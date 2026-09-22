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
    private var installing = false
    private var emotionReset: DispatchWorkItem?
    private var previewSequence = 0
    private var revision = 0
    private var session = 0
    private var inferenceBusy = false
    private var pending: (text: String, test: Bool, revision: Int)?

    init(resources: URL) {
        store = SettingsStore(resources: resources)
        sprites = SpriteCache(store: store)
        worker = LayaWorker(resourceRoot: resources, dataDirectory: store.directory)
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
        preferences.onInstall = { [weak self] in self?.installModel() }
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
        // An unconfigured install must expose the one-click model setup and permissions.
        let modelMarker = store.directory.appendingPathComponent("inference/models/\(store.settings.model)/emotioncat-model.json")
        if !FileManager.default.fileExists(atPath: modelMarker.path) { preferences.open() }
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
            if store.settings.captureText && !paused && !installing { worker.start(model: store.settings.model) }
            else if !installing {
                worker.stop()
                preferences.setStatus("감정 인식이 꺼져 있습니다. 발 동작은 계속 작동합니다.")
            }
        }
    }
    private func invalidateInference() {
        session += 1; revision += 1; pending = nil; inferenceBusy = false
    }
    private func restartModel() {
        guard !installing else { return }
        invalidateInference()
        worker.stop()
        if !paused && store.settings.captureText { worker.start(model: store.settings.model) }
        else if !paused { preferences.setStatus("감정 인식이 꺼져 있습니다. Laya 메모리를 사용하지 않습니다.") }
    }
    private func installModel() {
        guard !installing else { return }
        installing = true
        invalidateInference()
        preferences.setInstalling(true)
        worker.install(model: store.settings.model) { [weak self] success in
            guard let self = self else { return }
            self.installing = false
            self.preferences.setInstalling(false)
            if success && !self.paused && self.store.settings.captureText {
                self.worker.start(model: self.store.settings.model)
            }
        }
    }
    private func enqueue(_ text: String, test: Bool) {
        guard !paused else {
            if test { preferences.setTestResult("메뉴 막대에서 다시 시작을 눌러 주세요.") }
            return
        }
        if test && !worker.isReady {
            preferences.setTestResult("Laya 연결 대기")
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
let appDelegate = EmotionCatApp(resources: resourceRoot)
application.delegate = appDelegate
application.run()
