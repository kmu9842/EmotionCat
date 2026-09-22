import Foundation
import Darwin

/// Owns the optional local inference process. Typed text lives only in the request body.
final class LayaWorker {
    var onStatus: ((String) -> Void)?
    var isReady: Bool {
        if Thread.isMainThread { return ready }
        return DispatchQueue.main.sync { ready }
    }

    private let resourceRoot: URL
    private let inferenceDirectory: URL
    private let session: URLSession
    private var process: Process?
    private var processPipe: Pipe?
    private var installation: Process?
    private var installationPipe: Pipe?
    private var installationCompletion: ((Bool) -> Void)?
    private var healthTimer: DispatchSourceTimer?
    private var healthTask: URLSessionDataTask?
    private var classificationTask: URLSessionDataTask?
    private var classificationCompletion: ((Result<String, Error>) -> Void)?
    private var generation = UUID()
    private var installationGeneration = UUID()
    private var token = ""
    private var port = 0
    private var ready = false
    private var lastStatus = ""
    private var startupDeadline = Date.distantPast
    private var lastInstallUpdate = Date.distantPast

    init(resourceRoot: URL, dataDirectory: URL) {
        self.resourceRoot = resourceRoot
        self.inferenceDirectory = dataDirectory.appendingPathComponent("inference", isDirectory: true)
        let configuration = URLSessionConfiguration.ephemeral
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.timeoutIntervalForRequest = 60
        configuration.timeoutIntervalForResource = 65
        configuration.waitsForConnectivity = false
        configuration.connectionProxyDictionary = ["HTTPEnable": 0, "HTTPSEnable": 0, "SOCKSEnable": 0]
        session = URLSession(configuration: configuration, delegate: LoopbackRedirectBlocker(), delegateQueue: nil)
    }

    deinit {
        healthTimer?.cancel()
        healthTask?.cancel()
        classificationTask?.cancel()
        processPipe?.fileHandleForReading.readabilityHandler = nil
        installationPipe?.fileHandleForReading.readabilityHandler = nil
        if let child = process { Self.terminate(child) }
        if let child = installation { Self.terminate(child) }
        session.invalidateAndCancel()
        let pending = classificationCompletion
        let installPending = installationCompletion
        DispatchQueue.main.async {
            pending?(.failure(WorkerError.cancelled))
            installPending?(false)
        }
    }

    func start(model: String) {
        onMain { [weak self] in self?.startOnMain(model: model) }
    }

    /// Cancellation invalidates every callback from the previous model generation.
    func stop() {
        onMain { [weak self] in
            guard let self = self else { return }
            self.stopRuntime()
            self.stopInstallation()
            self.report("Laya 꺼짐")
        }
    }

    func classify(text: String, emotions: [EmotionDefinition], instructions: String, source: String = "typing", completion: @escaping (Result<String, Error>) -> Void) {
        onMain { [weak self] in
            guard let self = self, self.ready else {
                completion(.failure(WorkerError.notReady))
                return
            }
            guard self.classificationTask == nil else {
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
            let labels = emotions.map { ["id": $0.id, "name": $0.name, "description": $0.description] }
            let payload: [String: Any] = ["text": recentText, "emotions": labels, "instructions": instructions, "source": source]
            guard let body = try? JSONSerialization.data(withJSONObject: payload) else {
                completion(.failure(WorkerError.invalidInput))
                return
            }
            let currentGeneration = self.generation
            var request = self.request(path: "classify", timeout: 60)
            request.httpMethod = "POST"
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = body
            self.classificationCompletion = completion
            let task = self.session.dataTask(with: request) { [weak self] data, response, error in
                DispatchQueue.main.async {
                    guard let self = self, self.generation == currentGeneration else { return }
                    self.classificationTask = nil
                    let callback = self.classificationCompletion
                    self.classificationCompletion = nil
                    guard error == nil, let response = response as? HTTPURLResponse,
                          response.statusCode == 200, let data = data, data.count <= 65536,
                          let result = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                          let emotion = result["emotion"] as? String, allowedIDs.contains(emotion) else {
                        callback?(.failure(WorkerError.inferenceFailed))
                        return
                    }
                    let confidence = (result["confidence"] as? Double) ?? 0.0
                    let chosen = confidence < 0.25 && allowedIDs.contains("neutral") ? "neutral" : emotion
                    self.report("입력 \(recentText.count)자 → \(chosen) · \(result["elapsed_ms"] ?? "?") ms · \(result["device"] ?? "?")")
                    callback?(.success(chosen))
                }
            }
            self.classificationTask = task
            task.resume()
        }
    }

    func install(model: String, completion: @escaping (Bool) -> Void) {
        onMain { [weak self] in
            guard let self = self else { completion(false); return }
            guard Self.validModel(model) else {
                self.report("지원하지 않는 Laya 모델입니다.")
                completion(false)
                return
            }
            self.stopRuntime()
            self.stopInstallation()
            let script = self.resourceRoot.appendingPathComponent("scripts/setup-laya.sh")
            guard FileManager.default.fileExists(atPath: script.path) else {
                self.report("Laya 설치 스크립트를 찾을 수 없습니다. 앱을 다시 설치해 주세요.")
                completion(false)
                return
            }
            do {
                try FileManager.default.createDirectory(at: self.inferenceDirectory, withIntermediateDirectories: true)
            } catch {
                self.report("사용자 데이터 폴더를 만들 수 없습니다.")
                completion(false)
                return
            }
            let child = Process()
            child.executableURL = URL(fileURLWithPath: "/bin/bash")
            // The installer validates Python against the actual Python architecture,
            // including the tighter Intel Mac version range and Rosetta installs.
            child.arguments = [script.path, "--model", model, "--data-dir", self.inferenceDirectory.path]
            child.currentDirectoryURL = self.resourceRoot
            child.environment = Self.environment()
            child.standardInput = FileHandle.nullDevice
            let pipe = Pipe()
            child.standardOutput = pipe
            child.standardError = pipe
            let output = BoundedProcessOutput()
            let installGeneration = UUID()
            self.installationGeneration = installGeneration
            self.installation = child
            self.installationPipe = pipe
            self.installationCompletion = completion
            self.lastInstallUpdate = .distantPast
            pipe.fileHandleForReading.readabilityHandler = { [weak self] handle in
                let data = handle.availableData
                guard !data.isEmpty else { return }
                output.append(data)
                let line = output.latestLine()
                DispatchQueue.main.async {
                    guard let self = self, self.installationGeneration == installGeneration,
                          Date().timeIntervalSince(self.lastInstallUpdate) >= 0.4 else { return }
                    self.lastInstallUpdate = Date()
                    if !line.isEmpty { self.report("Laya 설치 · " + line) }
                }
            }
            child.terminationHandler = { [weak self] ended in
                DispatchQueue.main.async {
                    guard let self = self, self.installationGeneration == installGeneration else { return }
                    self.installationPipe?.fileHandleForReading.readabilityHandler = nil
                    self.installationPipe = nil
                    self.installation = nil
                    let callback = self.installationCompletion
                    self.installationCompletion = nil
                    let success = ended.terminationReason == .exit && ended.terminationStatus == 0
                    if success {
                        self.report("Laya 설치 완료")
                    } else {
                        let detail = output.latestLine()
                        self.report(detail.isEmpty ? "Laya 설치 실패. Python 버전과 인터넷 연결을 확인해 주세요." : "Laya 설치 실패 · " + detail)
                    }
                    callback?(success)
                }
            }
            self.report("Laya 설치 중 · 첫 설치에는 몇 분이 걸릴 수 있습니다.")
            do {
                try child.run()
            } catch {
                self.installation = nil
                self.installationPipe?.fileHandleForReading.readabilityHandler = nil
                self.installationPipe = nil
                self.installationCompletion = nil
                self.report("Laya 설치 프로그램을 실행할 수 없습니다.")
                completion(false)
            }
        }
    }

    private func startOnMain(model: String) {
        guard installation == nil else {
            report("Laya 설치가 끝날 때까지 기다려 주세요.")
            return
        }
        stopRuntime()
        guard Self.validModel(model) else {
            report("지원하지 않는 Laya 모델입니다.")
            return
        }
        let python = inferenceDirectory.appendingPathComponent(".venv/bin/python3")
        let marker = inferenceDirectory.appendingPathComponent("models/\(model)/emotioncat-model.json")
        guard FileManager.default.isExecutableFile(atPath: python.path),
              FileManager.default.fileExists(atPath: marker.path) else {
            report("Laya가 설치되지 않았습니다. 설정에서 모델을 설치해 주세요.")
            return
        }
        let script = resourceRoot.appendingPathComponent("inference/server.py")
        guard FileManager.default.fileExists(atPath: script.path) else {
            report("Laya 실행 파일을 찾을 수 없습니다. 앱을 다시 설치해 주세요.")
            return
        }
        startupDeadline = Date().addingTimeInterval(180)
        launch(model: model, attempt: 0, python: python, script: script)
    }

    private func launch(model: String, attempt: Int, python: URL, script: URL) {
        generation = UUID()
        let runGeneration = generation
        port = Int.random(in: 49152...65535)
        token = UUID().uuidString + UUID().uuidString
        let child = Process()
        child.executableURL = python
        child.arguments = ["-u", script.path, "--data-dir", inferenceDirectory.path,
                           "--port", String(port), "--token", token, "--model", model, "--threads", "2"]
        child.currentDirectoryURL = resourceRoot
        child.environment = Self.environment()
        child.standardInput = FileHandle.nullDevice
        let pipe = Pipe()
        child.standardOutput = pipe
        child.standardError = pipe
        // Consume output to prevent a full pipe blocking Python. Never retain or log it.
        pipe.fileHandleForReading.readabilityHandler = { handle in _ = handle.availableData }
        process = child
        processPipe = pipe
        child.terminationHandler = { [weak self] ended in
            DispatchQueue.main.async {
                guard let self = self, self.generation == runGeneration, self.process === ended else { return }
                let wasReady = self.ready
                self.ready = false
                self.stopHealthChecks()
                self.process = nil
                self.processPipe?.fileHandleForReading.readabilityHandler = nil
                self.processPipe = nil
                if !wasReady && attempt < 2 && Date() < self.startupDeadline {
                    self.launch(model: model, attempt: attempt + 1, python: python, script: script)
                } else {
                    self.finishClassification(.failure(WorkerError.notReady))
                    self.report("Laya가 종료되었습니다. 설정에서 다시 켜거나 모델을 재설치해 주세요.")
                }
            }
        }
        do {
            try child.run()
            report("Laya 모델 불러오는 중…")
            startHealthChecks(generation: runGeneration)
        } catch {
            process = nil
            processPipe?.fileHandleForReading.readabilityHandler = nil
            processPipe = nil
            report("Laya를 실행할 수 없습니다. 설정에서 모델을 재설치해 주세요.")
        }
    }

    private func startHealthChecks(generation runGeneration: UUID) {
        stopHealthChecks()
        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + 0.3, repeating: 1.25, leeway: .milliseconds(150))
        timer.setEventHandler { [weak self] in
            guard let self = self, self.generation == runGeneration else { return }
            guard Date() < self.startupDeadline else {
                self.stopRuntime()
                self.report("Laya 시작 시간이 초과되었습니다. 모델 설치 상태를 확인해 주세요.")
                return
            }
            guard self.healthTask == nil else { return }
            let request = self.request(path: "health", timeout: 3)
            self.healthTask = self.session.dataTask(with: request) { [weak self] data, response, error in
                DispatchQueue.main.async {
                    guard let self = self, self.generation == runGeneration else { return }
                    self.healthTask = nil
                    guard error == nil, let response = response as? HTTPURLResponse,
                          response.statusCode == 200, let data = data, data.count < 16384,
                          let body = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                          let status = body["status"] as? String else { return }
                    if status == "ready" {
                        self.ready = true
                        self.stopHealthChecks()
                        self.report("Laya 준비됨 · 로컬 감정 분석")
                    } else if status == "error" {
                        self.stopRuntime()
                        self.report("Laya 모델을 불러오지 못했습니다. 설정에서 모델을 재설치해 주세요.")
                    }
                }
            }
            self.healthTask?.resume()
        }
        healthTimer = timer
        timer.resume()
    }

    private func stopHealthChecks() {
        healthTimer?.cancel()
        healthTimer = nil
        healthTask?.cancel()
        healthTask = nil
    }

    private func stopRuntime() {
        generation = UUID()
        ready = false
        stopHealthChecks()
        finishClassification(.failure(WorkerError.cancelled))
        processPipe?.fileHandleForReading.readabilityHandler = nil
        processPipe = nil
        let oldProcess = process
        process = nil
        token = ""
        if let oldProcess = oldProcess { Self.terminate(oldProcess) }
    }

    private func stopInstallation() {
        installationGeneration = UUID()
        installationPipe?.fileHandleForReading.readabilityHandler = nil
        installationPipe = nil
        let oldProcess = installation
        installation = nil
        let callback = installationCompletion
        installationCompletion = nil
        if let oldProcess = oldProcess { Self.terminate(oldProcess) }
        callback?(false)
    }

    private func finishClassification(_ result: Result<String, Error>) {
        classificationTask?.cancel()
        classificationTask = nil
        let callback = classificationCompletion
        classificationCompletion = nil
        DispatchQueue.main.async { callback?(result) }
    }

    private func request(path: String, timeout: TimeInterval) -> URLRequest {
        var request = URLRequest(url: URL(string: "http://127.0.0.1:\(port)/\(path)")!, timeoutInterval: timeout)
        request.setValue(token, forHTTPHeaderField: "X-EmotionCat-Token")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.cachePolicy = .reloadIgnoringLocalCacheData
        return request
    }

    private func report(_ message: String) {
        guard message != lastStatus else { return }
        lastStatus = message
        onStatus?(message)
    }

    private func onMain(_ action: @escaping () -> Void) {
        if Thread.isMainThread { action() } else { DispatchQueue.main.async(execute: action) }
    }

    private static func validModel(_ model: String) -> Bool { model == "multilingual" }

    private static func terminate(_ child: Process) {
        guard child.isRunning else { return }
        child.terminate()
        DispatchQueue.global(qos: .utility).asyncAfter(deadline: .now() + 2) {
            // Process retains its own child state, so an exited child's PID is never targeted.
            if child.isRunning { _ = Darwin.kill(child.processIdentifier, SIGKILL) }
        }
    }

    private static func environment() -> [String: String] {
        var values = ProcessInfo.processInfo.environment
        var paths = ["/opt/homebrew/bin", "/usr/local/bin"]
        for version in ["3.11", "3.10", "3.12", "3.13"] {
            paths.append("/Library/Frameworks/Python.framework/Versions/\(version)/bin")
            paths.append("/opt/homebrew/opt/python@\(version)/bin")
            paths.append("/usr/local/opt/python@\(version)/bin")
        }
        paths += ["/usr/bin", "/bin", values["PATH"] ?? ""]
        values["PATH"] = paths.joined(separator: ":")
        values["PYTHONUTF8"] = "1"
        values["PYTHONUNBUFFERED"] = "1"
        values["PYTHONDONTWRITEBYTECODE"] = "1"
        values["HF_HUB_DISABLE_TELEMETRY"] = "1"
        values["TOKENIZERS_PARALLELISM"] = "false"
        return values
    }

}

private enum WorkerError: LocalizedError {
    case notReady, busy, cancelled, invalidInput, inferenceFailed
    var errorDescription: String? {
        switch self {
        case .notReady: return "Laya가 아직 준비되지 않았습니다."
        case .busy: return "Laya가 이전 문장의 감정을 분석 중입니다."
        case .cancelled: return "Laya 분석이 취소되었습니다."
        case .invalidInput: return "입력 문장과 감정 레이블을 확인해 주세요."
        case .inferenceFailed: return "Laya 감정 분석에 실패했습니다. 잠시 후 다시 입력해 주세요."
        }
    }
}

/// A local worker never redirects typed text to another endpoint.
private final class LoopbackRedirectBlocker: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}

private final class BoundedProcessOutput {
    private let lock = NSLock()
    private var bytes = Data()

    func append(_ data: Data) {
        lock.lock()
        bytes.append(data.suffix(8192))
        if bytes.count > 8192 { bytes.removeFirst(bytes.count - 8192) }
        lock.unlock()
    }

    func latestLine() -> String {
        lock.lock()
        let copy = bytes
        lock.unlock()
        let decoded = String(decoding: copy, as: UTF8.self)
        let cleaned = decoded.replacingOccurrences(of: "\u{001B}\\[[0-9;?]*[A-Za-z]", with: "", options: .regularExpression)
        let line = cleaned.components(separatedBy: .newlines).last { !$0.trimmingCharacters(in: .whitespaces).isEmpty } ?? ""
        return line.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }
            .prefix(220).map { String($0) }.joined()
    }
}
