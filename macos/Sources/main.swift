import AppKit

final class EmotionCatApp: NSObject, NSApplicationDelegate {
    private let store: SettingsStore
    private let sprites: SpriteCache
    private let worker: LayaWorker
    private var cat: CatPanel!
    private var preferences: SettingsWindow!
    private var keyboard: KeyboardMonitor!
    private var tray: NSStatusItem!
    private var pauseItem: NSMenuItem!
    private var paused = false
    private var emotionReset: DispatchWorkItem?
    private var previewSequence = 0
    private var revision = 0
    private var session = 0
    private var inferenceBusy = false
    private var pending: (text: String, test: Bool, revision: Int)?

    init(resources: URL) {
        store = SettingsStore(resources: resources)
        sprites = SpriteCache(store: store)
        worker = LayaWorker(resourceRoot: resources)
        super.init()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        cat = CatPanel(store: store, sprites: sprites)
        preferences = SettingsWindow(store: store, sprites: sprites)
        keyboard = KeyboardMonitor(onStroke: { [weak self] in
            guard let self = self, !self.paused else { return }
            self.cat.cat.tap()
        }, onText: { [weak self] text in self?.enqueue(text, test: false) }, onStatus: { [weak self] status in
            self?.preferences.setInputStatus(status)
        })
        cat.cat.onOpenSettings = { [weak self] in self?.openSettings() }
        cat.onPositionChanged = { [weak self] in self?.saveSettings() }
        preferences.onChanged = { [weak self] in self?.settingsChanged() }
        preferences.onModelChanged = { [weak self] in self?.restartModel() }
        preferences.onPermissions = { [weak self] in self?.keyboard.requestPermissions() }
        preferences.onPreview = { [weak self] id in self?.showEmotion(id) }
        preferences.onTap = { [weak self] in self?.demoPaws() }
        preferences.onSnap = { [weak self] in self?.cat.snapToDock() }
        preferences.onTest = { [weak self] text in self?.enqueue(text, test: true) }
        worker.onStatus = { [weak self] value in
            self?.preferences.setStatus(value)
            self?.runPending()
        }
        createTray()
        keyboard.captureText = store.settings.captureText
        keyboard.start()
        cat.orderFrontRegardless()
        if store.settings.captureText { worker.start(model: store.settings.model) }
        else { preferences.setStatus("감정 인식이 꺼져 있습니다. Laya 메모리를 사용하지 않습니다.") }
    }

    private func createTray() {
        tray = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let image = NSImage(systemSymbolName: "pawprint.fill", accessibilityDescription: "EmotionCat") {
            image.isTemplate = true; tray.button?.image = image
        } else { tray.button?.title = "🐾" }
        tray.button?.toolTip = "EmotionCat"
        let menu = NSMenu()
        let settings = NSMenuItem(title: "EmotionCat 설정…", action: #selector(openSettings), keyEquivalent: ",")
        settings.target = self; menu.addItem(settings)
        pauseItem = NSMenuItem(title: "일시 정지", action: #selector(togglePause), keyEquivalent: "")
        pauseItem.target = self; menu.addItem(pauseItem)
        let snap = NSMenuItem(title: "Dock 위에 맞추기", action: #selector(snap), keyEquivalent: "")
        snap.target = self; menu.addItem(snap)
        menu.addItem(.separator())
        let quit = NSMenuItem(title: "종료", action: #selector(quit), keyEquivalent: "q")
        quit.target = self; menu.addItem(quit)
        tray.menu = menu
    }
    @objc private func openSettings() { preferences.open() }
    @objc private func snap() { cat.snapToDock() }
    @objc private func quit() { NSApp.terminate(nil) }
    @objc private func togglePause() {
        paused.toggle()
        pauseItem.title = paused ? "다시 시작" : "일시 정지"
        keyboard.enabled = !paused
        if paused {
            previewSequence += 1
            invalidateInference()
            worker.stop()
            cat.cat.resetPaws()
            emotionReset?.cancel()
            cat.cat.emotion = "neutral"
            preferences.setStatus("일시 정지 · 입력 감지와 Laya를 멈췄습니다.")
        } else if store.settings.captureText { worker.start(model: store.settings.model) }
    }

    private func saveSettings() {
        do { try store.save() }
        catch { preferences.setStatus("설정을 저장하지 못했습니다: " + error.localizedDescription) }
    }
    private func settingsChanged() {
        cat.applySettings()
        if keyboard.captureText != store.settings.captureText {
            keyboard.captureText = store.settings.captureText
            invalidateInference()
            if store.settings.captureText && !paused { worker.start(model: store.settings.model) }
            else {
                worker.stop()
                preferences.setStatus("감정 인식이 꺼져 있습니다. 발 동작은 계속 작동합니다.")
            }
        }
    }
    private func invalidateInference() {
        session += 1; revision += 1; pending = nil; inferenceBusy = false
    }
    private func restartModel() {
        invalidateInference()
        worker.stop()
        if !paused && store.settings.captureText { worker.start(model: store.settings.model) }
        else if !paused { preferences.setStatus("감정 인식이 꺼져 있습니다. Laya 메모리를 사용하지 않습니다.") }
    }
    private func enqueue(_ text: String, test: Bool) {
        guard !paused else {
            if test { preferences.setTestResult("메뉴 막대에서 다시 시작을 눌러 주세요.") }
            return
        }
        if test && !worker.isReady {
            preferences.setTestResult("감정 모델을 준비하고 있습니다. 잠시 후 다시 시도해 주세요.")
            return
        }
        revision += 1
        pending = (String(text.suffix(600)), test, revision)
        runPending()
    }
    private func runPending() {
        guard worker.isReady, !inferenceBusy, let job = pending else { return }
        pending = nil
        inferenceBusy = true
        let currentSession = session
        worker.classify(text: job.text, emotions: store.settings.emotions, instructions: store.settings.classificationPrompt, source: job.test ? "manual" : "typing") { [weak self] result in
            guard let self = self, self.session == currentSession else { return }
            self.inferenceBusy = false
            if job.revision == self.revision {
                switch result {
                case .success(let id):
                    self.showEmotion(id)
                    if job.test {
                        let name = self.store.settings.emotions.first(where: { $0.id == id })?.name ?? id
                        self.preferences.setTestResult("Laya가 고른 감정: \(name)")
                    }
                case .failure(let error):
                    if job.test { self.preferences.setTestResult(error.localizedDescription) }
                    else { self.preferences.setStatus(error.localizedDescription) }
                }
            }
            self.runPending()
        }
    }
    private func showEmotion(_ id: String) {
        guard store.settings.emotions.contains(where: { $0.id == id }) else { return }
        emotionReset?.cancel()
        cat.cat.emotion = id
        let reset = DispatchWorkItem { [weak self] in self?.cat.cat.emotion = "neutral" }
        emotionReset = reset
        DispatchQueue.main.asyncAfter(deadline: .now() + 1, execute: reset)
    }
    private func demoPaws() {
        previewSequence += 1
        let sequence = previewSequence
        for index in 0..<12 {
            DispatchQueue.main.asyncAfter(deadline: .now() + Double(index) * 0.16) { [weak self] in
                guard let self = self, self.previewSequence == sequence else { return }
                self.cat.cat.tap()
            }
        }
    }
    func applicationWillTerminate(_ notification: Notification) {
        keyboard?.stop()
        worker.stop()
        emotionReset?.cancel()
        previewSequence += 1
        saveSettings()
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
}

// libmalloc's large-allocation cache otherwise keeps ~550 MB of freed model-loading buffers
// resident, so turning emotion recognition off would not return memory to the system.
// The setting is read only at process start, hence one re-exec of the same signed binary.
if getenv("MallocLargeCache") == nil, let executable = Bundle.main.executablePath {
    setenv("MallocLargeCache", "0", 1)
    execv(executable, CommandLine.unsafeArgv)
    // execv only returns on failure; continue with the default allocator.
}

let arguments = CommandLine.arguments
let rootIndex = arguments.firstIndex(of: "--project-root")
let resourceRoot: URL
if let index = rootIndex, index + 1 < arguments.count {
    resourceRoot = URL(fileURLWithPath: arguments[index + 1], isDirectory: true)
} else if let bundled = Bundle.main.resourceURL {
    resourceRoot = bundled
} else {
    resourceRoot = URL(fileURLWithPath: FileManager.default.currentDirectoryPath, isDirectory: true)
}

/// `--verify-model <golden.json>`: exact input_ids/marker_pos for every case and >= 80% accuracy on labeled cases.
/// Probabilities are reference-only: int8 kernels differ slightly per CPU (see tools/onnx/make_golden.py).
func verifyModel(golden: URL, modelDirectory: URL) -> Int32 {
    func fail(_ message: String) -> Int32 {
        fputs("FAIL: \(message)\n", stderr)
        return 1
    }
    guard let data = try? Data(contentsOf: golden),
          let cases = (try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]], !cases.isEmpty else {
        return fail("cannot read golden cases from \(golden.path)")
    }
    let loadStart = DispatchTime.now().uptimeNanoseconds
    let engine: LayaEngine
    do { engine = try LayaEngine(modelDirectory: modelDirectory) }
    catch { return fail("engine load: \(error.localizedDescription)") }
    let loadMs = Double(DispatchTime.now().uptimeNanoseconds - loadStart) / 1_000_000
    var latencies: [Double] = []
    var worstDelta: Float = 0
    var labeled = 0, correct = 0
    for (index, item) in cases.enumerated() {
        guard let text = item["text"] as? String, let instructions = item["instructions"] as? String,
              let emotions = item["emotions"] as? [[String: String]],
              let expectedIDs = (item["input_ids"] as? [NSNumber])?.map({ $0.int64Value }),
              let expectedMarkers = (item["marker_pos"] as? [NSNumber])?.map({ $0.int64Value }),
              let expectedProbabilities = (item["probabilities"] as? [NSNumber])?.map({ $0.floatValue }) else {
            return fail("case \(index): malformed golden entry")
        }
        let labels = emotions.map { LayaLabel(id: $0["id"] ?? "", name: $0["name"] ?? "", description: $0["description"] ?? "") }
        let start = DispatchTime.now().uptimeNanoseconds
        let sequence = engine.buildSequence(text: text, labels: labels, instructions: instructions)
        let probabilities: [Float]
        do { probabilities = try engine.probabilities(for: sequence) }
        catch { return fail("case \(index): inference error \(error.localizedDescription)") }
        latencies.append(Double(DispatchTime.now().uptimeNanoseconds - start) / 1_000_000)
        if sequence.inputIDs != expectedIDs {
            let at = zip(sequence.inputIDs, expectedIDs).enumerated().first(where: { $0.element.0 != $0.element.1 })?.offset
                ?? min(sequence.inputIDs.count, expectedIDs.count)
            return fail("case \(index): input_ids differ at \(at) (got \(sequence.inputIDs.count) ids, expected \(expectedIDs.count)); "
                + "got \(Array(sequence.inputIDs.dropFirst(max(0, at - 3)).prefix(8))) expected \(Array(expectedIDs.dropFirst(max(0, at - 3)).prefix(8)))")
        }
        if sequence.markerPositions != expectedMarkers {
            return fail("case \(index): marker_pos \(sequence.markerPositions) != \(expectedMarkers)")
        }
        guard probabilities.count == expectedProbabilities.count, probabilities.count == labels.count else {
            return fail("case \(index): \(probabilities.count) probabilities, expected \(expectedProbabilities.count)")
        }
        for (got, want) in zip(probabilities, expectedProbabilities) { worstDelta = max(worstDelta, abs(got - want)) }
        if let expected = item["expected"] as? String {
            labeled += 1
            let best = probabilities.indices.max(by: { probabilities[$0] < probabilities[$1] }) ?? 0
            if labels[best].id == expected { correct += 1 }
        }
    }
    if correct * 10 < labeled * 8 {
        return fail("emotion accuracy \(correct)/\(labeled) is below 80%")
    }
    let sorted = latencies.sorted()
    let median = sorted.count % 2 == 1 ? sorted[sorted.count / 2] : (sorted[sorted.count / 2 - 1] + sorted[sorted.count / 2]) / 2
    #if arch(arm64)
    let architecture = "arm64"
    #else
    let architecture = "x86_64"
    #endif
    print(String(format: "PASS: %d golden cases (%@) · input_ids/marker_pos exact · accuracy %d/%d · max |Δp| vs fp32 %.3f · median %.1f ms · model load %.0f ms",
                 cases.count, architecture, correct, labeled, worstDelta, median, loadMs))
    return 0
}

let application = NSApplication.shared
if arguments.contains("--verify-assets") {
    var failures: [String] = []
    for definition in EmotionDefinition.defaults {
        for pose in ["idle", "left", "right", "both"] {
            let path = definition.path(for: pose)
            guard let data = try? Data(contentsOf: resourceRoot.appendingPathComponent(path)),
                  let bitmap = NSBitmapImageRep(data: data),
                  bitmap.pixelsWide == 512, bitmap.pixelsHigh == 384, bitmap.hasAlpha else {
                failures.append(path); continue
            }
        }
    }
    if failures.isEmpty { print("PASS: 8 emotions × 4 assigned alpha frames (512×384).") }
    else { fputs("Invalid/missing frames: \(failures.joined(separator: ", "))\n", stderr) }
    exit(failures.isEmpty ? 0 : 1)
}
if let index = arguments.firstIndex(of: "--verify-model") {
    guard index + 1 < arguments.count else {
        fputs("Usage: EmotionCat --verify-model <golden.json>\n", stderr)
        exit(2)
    }
    exit(verifyModel(golden: URL(fileURLWithPath: arguments[index + 1]),
                     modelDirectory: resourceRoot.appendingPathComponent("model", isDirectory: true)))
}
let appDelegate = EmotionCatApp(resources: resourceRoot)
application.delegate = appDelegate
application.run()
