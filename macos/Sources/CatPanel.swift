import AppKit

/// Drawing is event-driven: two one-shot resets per burst, no display timer at idle.
final class CatView: NSView {
    let store: SettingsStore
    let sprites: SpriteCache
    var emotion = "neutral" { didSet { needsDisplay = true } }
    var onMoved: (() -> Void)?
    var onOpenSettings: (() -> Void)?
    private var leftUp = false
    private var rightUp = false
    private var nextRight = false
    private var leftReset: DispatchWorkItem?
    private var rightReset: DispatchWorkItem?
    private var lastTap = Date.distantPast
    override var isFlipped: Bool { true }

    init(store: SettingsStore, sprites: SpriteCache) {
        self.store = store; self.sprites = sprites
        super.init(frame: .zero)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is unavailable") }

    func tap() {
        let right = nextRight
        nextRight.toggle()
        // Key-repeat bursts must still alternate; otherwise both 100 ms timers can
        // continually renew and leave the cat frozen in the both-up frame.
        if Date().timeIntervalSince(lastTap) < 0.06 {
            if right { leftReset?.cancel(); leftUp = false }
            else { rightReset?.cancel(); rightUp = false }
        }
        lastTap = Date()
        let reset = DispatchWorkItem { [weak self] in
            guard let self = self else { return }
            if right { self.rightUp = false } else { self.leftUp = false }
            self.needsDisplay = true
        }
        if right {
            rightReset?.cancel(); rightReset = reset; rightUp = true
        } else {
            leftReset?.cancel(); leftReset = reset; leftUp = true
        }
        needsDisplay = true
        DispatchQueue.main.asyncAfter(deadline: .now() + .milliseconds(100), execute: reset)
    }
    func resetPaws() {
        leftReset?.cancel(); rightReset?.cancel()
        leftUp = false; rightUp = false; needsDisplay = true
    }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.clear.setFill(); dirtyRect.fill(using: .copy)
        let definition = store.settings.emotions.first(where: { $0.id == emotion })
            ?? store.settings.emotions.first(where: { $0.id == "neutral" })
            ?? EmotionDefinition.defaults[0]
        let pose = leftUp && rightUp ? "both" : leftUp ? "left" : rightUp ? "right" : "idle"
        NSColor(calibratedRed: 0.20, green: 0.16, blue: 0.17, alpha: 1).setStroke()
        let line = NSBezierPath()
        line.lineWidth = 3.2 * bounds.width / 512
        line.lineCapStyle = .round
        line.move(to: NSPoint(x: 12 * bounds.width / 512, y: 310 * bounds.height / 384))
        line.line(to: NSPoint(x: 500 * bounds.width / 512, y: 310 * bounds.height / 384))
        line.stroke()
        guard let image = sprites.image(definition.path(for: pose)) ?? sprites.image(definition.imagePath) else { return }
        NSGraphicsContext.current?.imageInterpolation = .high
        image.draw(in: bounds, from: .zero, operation: .sourceOver, fraction: 1, respectFlipped: true, hints: nil)
    }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { onOpenSettings?(); return }
        window?.performDrag(with: event)
        onMoved?()
    }
    override func rightMouseDown(with event: NSEvent) { onOpenSettings?() }
}

final class CatPanel: NSPanel {
    let cat: CatView
    let store: SettingsStore
    var onPositionChanged: (() -> Void)?
    private var screenObserver: NSObjectProtocol?
    override var canBecomeKey: Bool { false }
    override var canBecomeMain: Bool { false }

    init(store: SettingsStore, sprites: SpriteCache) {
        self.store = store
        cat = CatView(store: store, sprites: sprites)
        let width = CGFloat(store.settings.size)
        super.init(contentRect: NSRect(x: 0, y: 0, width: width, height: width * 0.75),
                   styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false; backgroundColor = .clear; hasShadow = false
        level = .floating
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
        contentView = cat
        cat.onMoved = { [weak self] in self?.recordDrag() }
        screenObserver = NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                                               object: nil, queue: .main) { [weak self] _ in self?.applySettings() }
        applySettings()
    }
    deinit { if let observer = screenObserver { NotificationCenter.default.removeObserver(observer) } }

    private var activeScreen: NSScreen {
        // NSScreen.screens is nonempty in a normal GUI login session.
        return screen ?? NSScreen.main ?? NSScreen.screens[0]
    }
    func applySettings() {
        let width = CGFloat(store.settings.size)
        setContentSize(NSSize(width: width, height: width * 0.75))
        if store.settings.docked {
            if let x = store.settings.x, let y = store.settings.y {
                let saved = NSRect(x: x, y: y, width: width, height: width * 0.75)
                if NSScreen.screens.contains(where: { $0.frame.intersects(saved) }) { setFrameOrigin(saved.origin) }
            }
            snapToDock(centerX: store.settings.x.map { CGFloat($0) + width / 2 }, save: false)
        } else if let x = store.settings.x, let y = store.settings.y {
            let wanted = NSRect(x: CGFloat(x), y: CGFloat(y), width: width, height: width * 0.75)
            if NSScreen.screens.contains(where: { $0.visibleFrame.intersects(wanted.insetBy(dx: width * 0.2, dy: width * 0.2)) }) {
                setFrameOrigin(wanted.origin)
            } else { snapToDock(centerX: nil, save: false) }
        } else { snapToDock(centerX: nil, save: false) }
        cat.needsDisplay = true
    }
    func snapToDock(centerX: CGFloat? = nil, save: Bool = true) {
        let visible = activeScreen.visibleFrame
        let width = frame.width
        let desired = centerX ?? (store.settings.x == nil ? visible.maxX - width / 2 - 30 : frame.midX)
        let x = max(visible.minX, min(visible.maxX - width, desired - width / 2))
        // Frames use top-left image coordinates. Desk is y=310, not image bottom=384.
        // Its screen point is originY + (384-310)*scale, exactly the Dock's top edge.
        let y = visible.minY - (384 - 310) * width / 512
        setFrameOrigin(NSPoint(x: x, y: y))
        store.settings.docked = true
        if save { storePosition() }
    }
    private func recordDrag() {
        let visible = activeScreen.visibleFrame
        let deskY = frame.minY + 74 * frame.width / 512
        store.settings.docked = abs(deskY - visible.minY) < 24
        if store.settings.docked { snapToDock(centerX: frame.midX, save: false) }
        storePosition()
    }
    private func storePosition() {
        store.settings.x = Double(frame.minX); store.settings.y = Double(frame.minY)
        onPositionChanged?()
    }
}
