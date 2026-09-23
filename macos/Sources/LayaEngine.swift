import Foundation

struct LayaLabel {
    var id: String
    var name: String
    var description: String
}

struct LayaSequence {
    var inputIDs: [Int64]
    var markerPositions: [Int64]
}

enum LayaEngineError: LocalizedError {
    case missingFile(String)
    case runtimeUnavailable
    case runtime(String)
    case invalidInput
    case unexpectedOutput

    var errorDescription: String? {
        switch self {
        case .missingFile(let name): return "감정 모델 파일(\(name))을 찾을 수 없습니다. 앱을 다시 설치해 주세요."
        case .runtimeUnavailable: return "ONNX Runtime을 불러올 수 없습니다. 앱을 다시 설치해 주세요."
        case .runtime(let message): return "감정 모델 실행 오류: \(message)"
        case .invalidInput: return "입력 문장과 감정 레이블을 확인해 주세요."
        case .unexpectedOutput: return "감정 모델 출력 형식이 올바르지 않습니다."
        }
    }
}

/// In-process Laya multilingual (int8 ONNX) inference through the ONNX Runtime C API.
/// Not thread-safe: the owner serialises every call on one queue.
final class LayaEngine {
    static let modelFileName = "laya-multilingual-int8.onnx"
    static let tokenizerFileName = "tokenizer.bin"

    private static let cls: Int64 = 2, sep: Int64 = 1, mask: Int64 = 4, maxLength = 1024

    let tokenizer: LayaTokenizer
    private let api: UnsafePointer<OrtApi>
    private var env: OpaquePointer?
    private var session: OpaquePointer?
    private var memoryInfo: OpaquePointer?

    init(modelDirectory: URL) throws {
        let modelURL = modelDirectory.appendingPathComponent(Self.modelFileName)
        let tokenizerURL = modelDirectory.appendingPathComponent(Self.tokenizerFileName)
        for url in [modelURL, tokenizerURL] where !FileManager.default.fileExists(atPath: url.path) {
            throw LayaEngineError.missingFile(url.lastPathComponent)
        }
        guard let base = OrtGetApiBase(), let getApi = base.pointee.GetApi,
              let api = getApi(UInt32(ORT_API_VERSION)) else { throw LayaEngineError.runtimeUnavailable }
        self.api = api
        tokenizer = try LayaTokenizer(url: tokenizerURL)

        do {
            try check(api.pointee.CreateEnv!(ORT_LOGGING_LEVEL_ERROR, "EmotionCat", &env))
            var options: OpaquePointer?
            try check(api.pointee.CreateSessionOptions!(&options))
            defer { if let options = options { api.pointee.ReleaseSessionOptions!(options) } }
            try check(api.pointee.SetIntraOpNumThreads!(options, 2))
            try check(api.pointee.SetInterOpNumThreads!(options, 1))
            try check(api.pointee.SetSessionGraphOptimizationLevel!(options, ORT_ENABLE_ALL))
            // A background typing companion must not keep cores spinning between inferences.
            try check(api.pointee.AddSessionConfigEntry!(options, "session.intra_op.allow_spinning", "0"))
            try check(api.pointee.CreateSession!(env, modelURL.path, options, &session))
            try check(api.pointee.CreateCpuMemoryInfo!(OrtArenaAllocator, OrtMemTypeDefault, &memoryInfo))
        } catch {
            releaseRuntime()
            throw error
        }
    }

    deinit { releaseRuntime() }

    private func releaseRuntime() {
        if let memoryInfo = memoryInfo { api.pointee.ReleaseMemoryInfo!(memoryInfo) }
        if let session = session { api.pointee.ReleaseSession!(session) }
        if let env = env { api.pointee.ReleaseEnv!(env) }
        memoryInfo = nil; session = nil; env = nil
    }

    /// Throws (and releases) a non-nil OrtStatus.
    private func check(_ status: OpaquePointer?) throws {
        guard let status = status else { return }
        let message = api.pointee.GetErrorMessage!(status).map { String(cString: $0) } ?? "unknown"
        api.pointee.ReleaseStatus!(status)
        throw LayaEngineError.runtime(message)
    }

    // MARK: Model input sequence

    /// Laya choice-question sequence: head, one [MASK] option per label, then the typed text.
    func buildSequence(text: String, labels emotions: [LayaLabel], instructions: String) -> LayaSequence {
        let labels = Self.labels(emotions)
        let encode = { (scalars: [Unicode.Scalar]) in self.tokenizer.encode(scalars).map(Int64.init) }
        let optionCost = labels.reduce(0) { $0 + min(49, 1 + encode($1).count) }
        let headLength = min(Self.maxLength - 128, max(192, encode(Array(instructions.unicodeScalars)).count + optionCost + 12))
        let head = encode(Self.clean(Array("choice question: ".unicodeScalars) + Array(instructions.unicodeScalars)))
        var options = labels.map { [Self.mask] + encode([" "] + Self.clean($0)).prefix(48) }
        var budget = headLength - options.reduce(0) { $0 + $1.count }
        if budget < 16 {
            let per = max(4, (headLength - 16) / options.count)
            options = options.map { Array($0.prefix(per)) }
            budget = headLength - options.reduce(0) { $0 + $1.count }
        }
        var ids = [Self.cls] + head.prefix(max(8, budget)) + [Self.sep]
        var markers: [Int64] = []
        for option in options {
            markers.append(Int64(ids.count))
            ids.append(contentsOf: option)
        }
        ids.append(Self.sep)
        var state = encode(Self.clean(Array(text.unicodeScalars)))
        let keep = Self.maxLength - headLength - 8
        state = keep > 0 ? Array(state.suffix(keep)) : []
        state = Array(state.prefix(max(0, Self.maxLength - ids.count - 1)))
        ids = Array((ids + state + [Self.sep]).prefix(Self.maxLength))
        return LayaSequence(inputIDs: ids, markerPositions: markers.filter { $0 < Int64(Self.maxLength) })
    }

    /// Softmax over the marker logits; one probability per label, in label order.
    func probabilities(for sequence: LayaSequence) throws -> [Float] {
        let tokens = sequence.inputIDs, markers = sequence.markerPositions
        guard !tokens.isEmpty, !markers.isEmpty, let session = session, let memoryInfo = memoryInfo else {
            throw LayaEngineError.invalidInput
        }
        let maskValues = [UInt8](repeating: 1, count: markers.count)  // ONNX bool is one byte
        var values: [OpaquePointer?] = [nil, nil, nil]
        var output: OpaquePointer?
        defer {
            for value in values { if let value = value { api.pointee.ReleaseValue!(value) } }
            if let output = output { api.pointee.ReleaseValue!(output) }
        }
        let tokenBuffer = UnsafeMutablePointer<Int64>.allocate(capacity: tokens.count)
        let markerBuffer = UnsafeMutablePointer<Int64>.allocate(capacity: markers.count)
        let maskBuffer = UnsafeMutablePointer<UInt8>.allocate(capacity: markers.count)
        defer { tokenBuffer.deallocate(); markerBuffer.deallocate(); maskBuffer.deallocate() }
        tokenBuffer.initialize(from: tokens, count: tokens.count)
        markerBuffer.initialize(from: markers, count: markers.count)
        maskBuffer.initialize(from: maskValues, count: markers.count)

        var tokenShape: [Int64] = [1, Int64(tokens.count)]
        var markerShape: [Int64] = [1, Int64(markers.count)]
        try check(api.pointee.CreateTensorWithDataAsOrtValue!(memoryInfo, tokenBuffer, tokens.count * 8, &tokenShape, 2,
                                                             ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &values[0]))
        try check(api.pointee.CreateTensorWithDataAsOrtValue!(memoryInfo, markerBuffer, markers.count * 8, &markerShape, 2,
                                                             ONNX_TENSOR_ELEMENT_DATA_TYPE_INT64, &values[1]))
        try check(api.pointee.CreateTensorWithDataAsOrtValue!(memoryInfo, maskBuffer, markers.count, &markerShape, 2,
                                                             ONNX_TENSOR_ELEMENT_DATA_TYPE_BOOL, &values[2]))

        let cName = { (name: String) -> UnsafePointer<CChar>? in UnsafePointer(strdup(name)) }
        let inputNames = ["input_ids", "marker_pos", "marker_mask"].map(cName)
        let outputNames = [cName("logits")]
        defer { (inputNames + outputNames).forEach { free(UnsafeMutablePointer(mutating: $0)) } }
        try inputNames.withUnsafeBufferPointer { names in
            try outputNames.withUnsafeBufferPointer { outNames in
                try values.withUnsafeBufferPointer { inputValues in
                    try check(api.pointee.Run!(session, nil, names.baseAddress, inputValues.baseAddress, 3,
                                              outNames.baseAddress, 1, &output))
                }
            }
        }
        guard let result = output else { throw LayaEngineError.unexpectedOutput }

        var info: OpaquePointer?
        try check(api.pointee.GetTensorTypeAndShape!(result, &info))
        var count = 0
        let countStatus = api.pointee.GetTensorShapeElementCount!(info, &count)
        if let info = info { api.pointee.ReleaseTensorTypeAndShapeInfo!(info) }
        try check(countStatus)
        guard count == markers.count else { throw LayaEngineError.unexpectedOutput }
        var raw: UnsafeMutableRawPointer?
        try check(api.pointee.GetTensorMutableData!(result, &raw))
        guard let data = raw else { throw LayaEngineError.unexpectedOutput }
        let logits = Array(UnsafeBufferPointer(start: data.assumingMemoryBound(to: Float.self), count: count))
        guard logits.allSatisfy({ $0.isFinite }) else { throw LayaEngineError.unexpectedOutput }
        let peak = logits.max() ?? 0
        let exps = logits.map { exp($0 - peak) }
        let total = exps.reduce(0, +)
        return exps.map { $0 / total }
    }

    // MARK: Text helpers (Python semantics on Unicode scalars)

    /// labels[i] = (description.strip() or name.strip() or id)[:160]; duplicates get " (id)".
    static func labels(_ emotions: [LayaLabel]) -> [[Unicode.Scalar]] {
        var seen = Set<[UInt32]>()
        var out: [[Unicode.Scalar]] = []
        for emotion in emotions {
            let description = strip(Array(emotion.description.unicodeScalars))
            let name = strip(Array(emotion.name.unicodeScalars))
            var label = Array((!description.isEmpty ? description : !name.isEmpty ? name : Array(emotion.id.unicodeScalars)).prefix(160))
            if seen.contains(label.map { $0.value }) { label += Array(" (\(emotion.id))".unicodeScalars) }
            while seen.contains(label.map { $0.value }) { label.append(" ") }
            seen.insert(label.map { $0.value })
            out.append(label)
        }
        return out
    }

    /// Python `str.strip()` whitespace set.
    static func isPythonSpace(_ scalar: Unicode.Scalar) -> Bool {
        switch scalar.value {
        case 0x09...0x0D, 0x1C...0x20, 0x85, 0xA0, 0x1680, 0x2000...0x200A, 0x2028, 0x2029, 0x202F, 0x205F, 0x3000: return true
        default: return false
        }
    }

    static func strip(_ scalars: [Unicode.Scalar]) -> [Unicode.Scalar] {
        guard let first = scalars.firstIndex(where: { !isPythonSpace($0) }),
              let last = scalars.lastIndex(where: { !isPythonSpace($0) }) else { return [] }
        return Array(scalars[first...last])
    }

    /// `s.replace("<mask>", " ")` on code points.
    static func clean(_ scalars: [Unicode.Scalar]) -> [Unicode.Scalar] {
        let pattern = Array("<mask>".unicodeScalars)
        var out: [Unicode.Scalar] = []
        out.reserveCapacity(scalars.count)
        var index = 0
        while index < scalars.count {
            if scalars[index] == "<", index + pattern.count <= scalars.count,
               Array(scalars[index..<index + pattern.count]) == pattern {
                out.append(" ")
                index += pattern.count
            } else {
                out.append(scalars[index])
                index += 1
            }
        }
        return out
    }
}
