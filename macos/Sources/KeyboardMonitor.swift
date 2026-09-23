import AppKit
import ApplicationServices
import Carbon
import Foundation

// Passive global characters; Accessibility is consulted only for password metadata.
final class KeyboardMonitor {
    private let onStroke: () -> Void
    private let onText: (String) -> Void
    private let onStatus: (String) -> Void
    private var eventTap: CFMachPort?
    private var eventSource: CFRunLoopSource?
    private var activationObserver: NSObjectProtocol?
    private var permissionTimer: Timer?
    private var pendingRead: DispatchWorkItem?
    private let systemElement = AXUIElementCreateSystemWide()
    private let buffer = TypedTextBuffer()
    private var pendingText = "", emittedText = "", lastStatus = ""
    private var fresh = true, blocked = false, isStarted = false
    private var lastCharacter = Date.distantPast, checkedAt = Date.distantPast
    private var focusedPID: pid_t = 0
    private static let movementKeys: Set<Int64> = [48, 53, 64, 79, 80, 90, 96, 97, 98,
        99, 100, 101, 103, 105, 106, 107, 109, 111, 113, 114, 115, 116, 117, 118,
        119, 120, 121, 122, 123, 124, 125, 126]
    private static let excludedApplications = ["1password", "onepassword", "bitwarden",
        "lastpass", "dashlane", "keepass", "enpass", "keeper", "keychain", "passwords"]
    private static let twoSetKeys: [Int64: String] = [0: "a", 1: "s", 2: "d", 3: "f", 4: "h", 5: "g",
        6: "z", 7: "x", 8: "c", 9: "v", 11: "b", 12: "q", 13: "w", 14: "e", 15: "r",
        16: "y", 17: "t", 31: "o", 32: "u", 34: "i", 35: "p", 37: "l", 38: "j",
        40: "k", 45: "n", 46: "m"]

    var enabled = true { didSet { if !enabled { clearCapture() }; reportStatus() } }
    var captureText = false { didSet { if captureText != oldValue { clearCapture() }; reportStatus() } }

    init(onStroke: @escaping () -> Void, onText: @escaping (String) -> Void,
         onStatus: @escaping (String) -> Void) {
        self.onStroke = onStroke; self.onText = onText; self.onStatus = onStatus
        AXUIElementSetMessagingTimeout(systemElement, 0.025)
    }
    func start() {
        isStarted = true
        if activationObserver == nil {
            activationObserver = NSWorkspace.shared.notificationCenter.addObserver(
                forName: NSWorkspace.didActivateApplicationNotification, object: nil, queue: .main
            ) { [weak self] _ in
                guard let self = self else { return }
                self.fresh = true; self.checkedAt = .distantPast
                if self.isStarted && self.eventTap == nil { self.installTap() }
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
        reportStatus()
    }
    func requestPermissions() {
        _ = CGRequestListenEventAccess()
        let promptKey = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        _ = AXIsProcessTrustedWithOptions([promptKey: true] as CFDictionary)
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
                else { monitor.fresh = true; monitor.checkedAt = .distantPast }
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
            fresh = true; return
        }
        guard let app = NSWorkspace.shared.frontmostApplication else { return }
        let now = Date()
        if focusedPID != app.processIdentifier { fresh = true; checkedAt = .distantPast; focusedPID = app.processIdentifier }
        if now.timeIntervalSince(checkedAt) > 0.5 {
            blocked = isExcluded(app) || isPasswordField(); checkedAt = now
        }
        if blocked { clearCapture(); publishStatus("암호 필드·제외 앱"); return }
        if code == 36 || code == 76 { buffer.commit(); fresh = true; return }
        if code == 51 {
            buffer.backspace()
            if pendingRead != nil {
                pendingText = buffer.text.trimmingCharacters(in: .whitespacesAndNewlines)
                if pendingText.isEmpty { pendingRead?.cancel(); pendingRead = nil }
            }
            return
        }
        let korean = isKoreanTwoSet()
        var characters = unicodeString(event)
        let physical = korean && event.flags.intersection([.maskAlternate]).isEmpty ? Self.twoSetKeys[code] : nil
        if let key = physical { characters = event.flags.contains(.maskShift) ? key.uppercased() : key }
        guard !characters.isEmpty else { return }
        if fresh || now.timeIntervalSince(lastCharacter) > 1.8 { buffer.clear(); emittedText = ""; fresh = false }
        buffer.append(characters, korean: physical != nil)
        lastCharacter = now
        pendingText = buffer.text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !pendingText.isEmpty, pendingText != emittedText else { return }
        publishStatus("전역 문자 입력 · \(pendingText.count)자 기록")
        if pendingRead == nil {
            let work = DispatchWorkItem { [weak self] in self?.emitRecentText() }
            pendingRead = work
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.5, execute: work)
        }
    }
    private func emitRecentText() {
        pendingRead = nil
        guard enabled, captureText, !pendingText.isEmpty, pendingText != emittedText else { return }
        emittedText = pendingText; onText(pendingText)
    }
    private func clearCapture() {
        pendingRead?.cancel(); pendingRead = nil; buffer.clear()
        pendingText = ""; emittedText = ""; fresh = true; lastCharacter = .distantPast
    }
    private func isKoreanTwoSet() -> Bool {
        guard let source = TISCopyCurrentKeyboardInputSource()?.takeRetainedValue(),
              let property = TISGetInputSourceProperty(source, kTISPropertyInputSourceID) else { return false }
        let identifier = Unmanaged<CFString>.fromOpaque(property).takeUnretainedValue() as String
        return identifier.lowercased().contains("korean.2set")
    }
    private func isExcluded(_ app: NSRunningApplication) -> Bool {
        let identity = ((app.bundleIdentifier ?? "") + " " + (app.localizedName ?? "")).lowercased()
        return Self.excludedApplications.contains { identity.contains($0) }
    }
    private func isPasswordField() -> Bool {
        guard AXIsProcessTrusted(), let value = attribute(systemElement, kAXFocusedUIElementAttribute),
              CFGetTypeID(value) == AXUIElementGetTypeID() else { return false }
        let element = value as! AXUIElement
        AXUIElementSetMessagingTimeout(element, 0.025)
        return (attribute(element, kAXSubroleAttribute) as? String) == "AXSecureTextField"
            || (attribute(element, "AXProtectedContent") as? Bool) == true
            || (attribute(element, "AXSecure") as? Bool) == true
    }
    private func attribute(_ element: AXUIElement, _ name: String) -> CFTypeRef? {
        var value: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, name as CFString, &value) == .success else { return nil }
        return value
    }
    private func unicodeString(_ event: CGEvent) -> String {
        var units = [UniChar](repeating: 0, count: 64), count = 0
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
