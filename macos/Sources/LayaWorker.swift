import Foundation

/// Runs the bundled Laya model in-process on a private serial queue.
/// Typed text only lives in memory for the duration of one inference.
final class LayaWorker {
    var onStatus: ((String) -> Void)?
    var isReady: Bool {
        if Thread.isMainThread { return ready }
        return DispatchQueue.main.sync { ready }
    }

    private let modelDirectory: URL
    private let queue = DispatchQueue(label: "EmotionCat.LayaWorker", qos: .userInitiated)
    /// Touched only on `queue`; shared with queue blocks so they never retain the worker.
    private let holder = EngineHolder()
    /// Latest wanted generation, readable from `queue` to skip stale loads.
    private let wanted = GenerationFlag()
    private var classificationCompletion: ((Result<String, Error>) -> Void)?
    private var generation = 0
    private var ready = false
    private var lastStatus = ""

    init(resourceRoot: URL) {
        modelDirectory = resourceRoot.appendingPathComponent("model", isDirectory: true)
    }

    deinit {
        wanted.value = -1
        let holder = self.holder
        queue.async { holder.engine = nil }
        let pending = classificationCompletion
        DispatchQueue.main.async { pending?(.failure(WorkerError.cancelled)) }
    }

    func start(model: String) {
        onMain { [weak self] in self?.startOnMain(model: model) }
    }

    /// Cancellation invalidates every callback from the previous generation and frees the model.
    func stop() {
        onMain { [weak self] in
            guard let self = self else { return }
            self.stopRuntime()
            self.report("감정 인식 꺼짐 · 모델 메모리를 해제했습니다.")
        }
    }

    func classify(text: String, emotions: [EmotionDefinition], instructions: String, source: String = "typing", completion: @escaping (Result<String, Error>) -> Void) {
        onMain { [weak self] in
            guard let self = self, self.ready else {
                completion(.failure(WorkerError.notReady))
                return
            }
            guard self.classificationCompletion == nil else {
                completion(.failure(WorkerError.busy))
                return
            }
            let recentText = String(text.suffix(1000))
            let allowedIDs = Set(emotions.map { $0.id })
            guard !recentText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  (2...16).contains(emotions.count), allowedIDs.count == emotions.count else {
                completion(.failure(WorkerError.invalidInput))
                return
            }
            var prompt = String(instructions.trimmingCharacters(in: .whitespacesAndNewlines).prefix(1000))
            if prompt.isEmpty { prompt = AppSettings().classificationPrompt }
            let labels = emotions.map { LayaLabel(id: $0.id, name: $0.name, description: $0.description) }
            let currentGeneration = self.generation
            let holder = self.holder
            self.classificationCompletion = completion
            self.queue.async { [weak self] in
                let started = DispatchTime.now().uptimeNanoseconds
                let outcome: Result<[Float], Error>
                if let engine = holder.engine {
                    outcome = Result {
                        try engine.probabilities(for: engine.buildSequence(text: recentText, labels: labels, instructions: prompt))
                    }
                } else {
                    outcome = .failure(WorkerError.notReady)
                }
                let elapsed = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000
                DispatchQueue.main.async {
                    guard let self = self, self.generation == currentGeneration else { return }
                    let callback = self.classificationCompletion
                    self.classificationCompletion = nil
                    guard case .success(let probabilities) = outcome, probabilities.count == labels.count,
                          let best = probabilities.indices.max(by: { probabilities[$0] < probabilities[$1] }) else {
                        callback?(.failure(WorkerError.inferenceFailed))
                        return
                    }
                    let emotion = labels[best].id
                    let chosen = probabilities[best] < 0.25 && allowedIDs.contains("neutral") ? "neutral" : emotion
                    self.report("입력 \(recentText.count)자 → \(chosen) · \(Int(elapsed.rounded())) ms · CPU")
                    callback?(.success(chosen))
                }
            }
        }
    }

    private func startOnMain(model: String) {
        stopRuntime()
        guard model == "multilingual" else {
            report("지원하지 않는 감정 모델입니다.")
            return
        }
        let runGeneration = generation
        let holder = self.holder, wanted = self.wanted, directory = modelDirectory
        report("감정 모델 불러오는 중…")
        queue.async { [weak self] in
            guard wanted.value == runGeneration else { return }
            holder.engine = nil
            var failure: String?
            do {
                let engine = try LayaEngine(modelDirectory: directory)
                if wanted.value == runGeneration { holder.engine = engine }
            } catch {
                failure = error.localizedDescription
            }
            DispatchQueue.main.async {
                guard let self = self, self.generation == runGeneration else { return }
                if let failure = failure {
                    self.report("감정 모델을 불러오지 못했습니다. " + failure)
                } else {
                    self.ready = true
                    self.report("감정 인식 준비 완료 · 앱 내장 모델(CPU)")
                }
            }
        }
    }

    private func stopRuntime() {
        generation += 1
        wanted.value = generation
        ready = false
        let callback = classificationCompletion
        classificationCompletion = nil
        if let callback = callback { DispatchQueue.main.async { callback(.failure(WorkerError.cancelled)) } }
        let holder = self.holder
        queue.async { holder.engine = nil }
    }

    private func report(_ message: String) {
        guard message != lastStatus else { return }
        lastStatus = message
        onStatus?(message)
    }

    private func onMain(_ action: @escaping () -> Void) {
        if Thread.isMainThread { action() } else { DispatchQueue.main.async(execute: action) }
    }
}

private final class EngineHolder {
    var engine: LayaEngine?
}

private final class GenerationFlag {
    private let lock = NSLock()
    private var stored = 0
    var value: Int {
        get { lock.lock(); defer { lock.unlock() }; return stored }
        set { lock.lock(); stored = newValue; lock.unlock() }
    }
}

private enum WorkerError: LocalizedError {
    case notReady, busy, cancelled, invalidInput, inferenceFailed
    var errorDescription: String? {
        switch self {
        case .notReady: return "감정 모델이 아직 준비되지 않았습니다."
        case .busy: return "이전 문장의 감정을 분석 중입니다."
        case .cancelled: return "감정 분석이 취소되었습니다."
        case .invalidInput: return "입력 문장과 감정 레이블을 확인해 주세요."
        case .inferenceFailed: return "감정 분석에 실패했습니다. 잠시 후 다시 입력해 주세요."
        }
    }
}
