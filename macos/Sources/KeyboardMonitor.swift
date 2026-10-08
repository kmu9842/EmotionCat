import AppKit
import ApplicationServices
import Carbon
import Foundation

// Passive global characters, kept for at most one second. No editor inspection.
final class KeyboardMonitor {
    private let onStroke: () -> Void
    private let onText: (TransientText) -> Void
    private let onStatus: (String) -> Void
    private let onInvalidated: () -> Void
    private var eventTap: CFMachPort?
    private var eventSource: CFRunLoopSource?
    private var activationObserver: NSObjectProtocol?
    private var privacyObservers: [NSObjectProtocol] = []
    private var permissionTimer: Timer?
    private var pendingRead: DispatchWorkItem?
    private let buffer = TypedTextBuffer()
    private var lastStatus = ""
    private var isStarted = false
    private var captureDeadline: UInt64 = 0
    private var focusedPID: pid_t = 0
    private static let movementKeys: Set<Int64> = [48, 53, 64, 79, 80, 90, 96, 97, 98,
        99, 100, 101, 103, 105, 106, 107, 109, 111, 113, 114, 115, 116, 117, 118,
        119, 120, 121, 122, 123, 124, 125, 126]
    private static let twoSetKeys: [Int64: String] = [0: "a", 1: "s", 2: "d", 3: "f", 4: "h", 5: "g",
        6: "z", 7: "x", 8: "c", 9: "v", 11: "b", 12: "q", 13: "w", 14: "e", 15: "r",
        16: "y", 17: "t", 31: "o", 32: "u", 34: "i", 35: "p", 37: "l", 38: "j",
        40: "k", 45: "n", 46: "m"]

    var enabled = true { didSet { if !enabled { clearCapture() }; reportStatus() } }
    var captureText = false { didSet { if captureText != oldValue { clearCapture() }; reportStatus() } }

    init(onStroke: @escaping () -> Void, onText: @escaping (TransientText) -> Void,
         onStatus: @escaping (String) -> Void, onInvalidated: @escaping () -> Void) {
        self.onStroke = onStroke; self.onText = onText; self.onStatus = onStatus
        self.onInvalidated = onInvalidated
    }
    func start() {
        isStarted = true
        if activationObserver == nil {
            activationObserver = NSWorkspace.shared.notificationCenter.addObserver(
                forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
            ) { [weak self] _ in
                guard let self = self else { return }
                self.clearCapture()
                if self.isStarted && self.eventTap == nil { self.installTap() }
            }
        }
        if privacyObservers.isEmpty {
            for name in [NSWorkspace.willSleepNotification, NSWorkspace.sessionDidResignActiveNotification] {
                privacyObservers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in self?.clearCapture() })
            }
        }
        if eventTap == nil { installTap() }
        if eventTap == nil && !CGPreflightListenEventAccess() { requestPermissions() }
        reportStatus()
    }
    func stop() {
        isStarted = false; clearCapture()
        permissionTimer?.invalidate(); permissionTimer = nil
        if let tap = eventTap { CGEvent.tapEnable(tap: tap, enable: false); CFMachPortInvalidate(tap) }
        if let source = eventSource { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        eventSource = nil; eventTap = nil
        if let observer = activationObserver { NSWorkspace.shared.notificationCenter.removeObserver(observer); activationObserver = nil }
        for observer in privacyObservers { NSWorkspace.shared.notificationCenter.removeObserver(observer) }
        privacyObservers.removeAll()
        reportStatus()
    }
    func requestPermissions() {
        _ = CGRequestListenEventAccess()
        if isStarted && eventTap == nil { installTap() }
        waitForPermission()
        reportStatus()
    }
    /// Picks up a permission granted in System Settings without restarting the app.
    private func waitForPermission() {
        guard eventTap == nil, permissionTimer == nil else { return }
        permissionTimer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] timer in
            guard let self = self else { timer.invalidate(); return }
            if self.isStarted && self.eventTap == nil { self.installTap() }
            if self.eventTap != nil || !self.isStarted {
                timer.invalidate(); self.permissionTimer = nil; self.reportStatus()
            }
        }
    }
    deinit {
        permissionTimer?.invalidate()
        pendingRead?.cancel()
        if let tap = eventTap { CFMachPortInvalidate(tap) }
        if let source = eventSource { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        if let observer = activationObserver { NSWorkspace.shared.notificationCenter.removeObserver(observer) }
        for observer in privacyObservers { NSWorkspace.shared.notificationCenter.removeObserver(observer) }
    }
    private func installTap() {
        guard eventTap == nil, CGPreflightListenEventAccess() else { reportStatus(); return }
        let mask = (CGEventMask(1) << CGEventType.keyDown.rawValue)
            | (CGEventMask(1) << CGEventType.leftMouseDown.rawValue)
            | (CGEventMask(1) << CGEventType.rightMouseDown.rawValue)
        let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .tailAppendEventTap,
            options: .listenOnly, eventsOfInterest: mask, callback: { _, type, event, info in
                guard let info = info else { return Unmanaged.passUnretained(event) }
                let monitor = Unmanaged<KeyboardMonitor>.fromOpaque(info).takeUnretainedValue()
                if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
                    monitor.clearCapture()
                    if let tap = monitor.eventTap { CGEvent.tapEnable(tap: tap, enable: true) }
                } else if type == .keyDown { monitor.handleKey(event) }
                else { monitor.clearCapture() }
                return Unmanaged.passUnretained(event)
            }, userInfo: Unmanaged.passUnretained(self).toOpaque())
        guard let installedTap = tap, let source = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, installedTap, 0)
        else { reportStatus(); return }
        eventTap = installedTap; eventSource = source
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: installedTap, enable: true)
    }
    private func handleKey(_ event: CGEvent) {
        onStroke()
        guard enabled, captureText else { return }
        let code = event.getIntegerValueField(.keyboardEventKeycode)
        if !event.flags.intersection([.maskCommand, .maskControl]).isEmpty || Self.movementKeys.contains(code) {
            clearCapture(); return
        }
        guard let app = NSWorkspace.shared.frontmostApplication else { clearCapture(); return }
        if focusedPID != app.processIdentifier { clearCapture() }
        focusedPID = app.processIdentifier
        if code == 36 || code == 76 { emitRecentText(); return }
        if code == 51 {
            buffer.backspace()
            return
        }
        let korean = isKoreanTwoSet()
        var characters = unicodeString(event)
        let physical = korean && event.flags.intersection([.maskAlternate]).isEmpty ? Self.twoSetKeys[code] : nil
        if let key = physical { characters = event.flags.contains(.maskShift) ? key.uppercased() : key }
        guard !characters.isEmpty else { return }
        if captureDeadline != 0 && DispatchTime.now().uptimeNanoseconds >= captureDeadline { clearCapture(invalidate: false) }
        if pendingRead == nil {
            captureDeadline = DispatchTime.now().uptimeNanoseconds + 1_000_000_000
            buffer.expire(at: captureDeadline)
            let work = DispatchWorkItem { [weak self] in self?.emitRecentText() }
            pendingRead = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
        }
        buffer.append(characters, korean: physical != nil)
        characters = ""
        publishStatus("전역 문자 입력 · 분석 전달 후 입력 버퍼 폐기")
    }
    private func emitRecentText() {
        defer { clearCapture(invalidate: false) }
        guard enabled, captureText, captureDeadline != 0,
              DispatchTime.now().uptimeNanoseconds < captureDeadline,
              NSWorkspace.shared.frontmostApplication?.processIdentifier == focusedPID else { return }
        let text = buffer.text.trimmingCharacters(in: .whitespacesAndNewlines)
        buffer.clear()
        if !text.isEmpty { onText(TransientText(text, expiresAt: captureDeadline)) }
    }
    private func clearCapture(invalidate: Bool = true) {
        pendingRead?.cancel(); pendingRead = nil; buffer.clear()
        captureDeadline = 0
        if invalidate { onInvalidated() }
    }
    private func isKoreanTwoSet() -> Bool {
        guard let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
              let property = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { return false }
        let identifier = Unmanaged<CFString>.fromOpaque(property).takeUnretainedValue() as String
        return identifier.lowercased().contains("korean.2set")
    }
    private func unicodeString(_ event: CGEvent) -> String {
        var units = [UniChar](repeating: 0, count: 64), count = 0
        defer { units.withUnsafeMutableBytes { bytes in if let base = bytes.baseAddress { memset_s(base, bytes.count, 0, bytes.count) } } }
        units.withUnsafeMutableBufferPointer { buffer in
            event.keyboardGetUnicodeString(maxStringLength: buffer.count, actualStringLength: &count, unicodeString: buffer.baseAddress)
        }
        let text = String(utf16CodeUnits: units, count: min(count, units.count))
        return String(String.UnicodeScalarView(text.unicodeScalars.filter {
            !CharacterSet.controlCharacters.contains($0) && !(0xF700...0xF8FF).contains(Int($0.value))
        }))
    }
    private func reportStatus() {
        if !isStarted || !enabled { publishStatus("입력 일시정지") }
        else if eventTap == nil { publishStatus("입력 모니터링 권한 필요") }
        else { publishStatus(captureText ? "전역 문자 입력 대기 · 입력 후 0.5초 뒤 분석" : "감정 인식 꺼짐") }
    }
    private func publishStatus(_ value: String) { if value != lastStatus { lastStatus = value; onStatus(value) } }
}
