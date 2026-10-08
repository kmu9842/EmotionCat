import Foundation
import Darwin

/// Explicitly owned UTF-16 storage. Erasing uses memset_s, not an optimizable store.
final class WipingTextBuffer {
    let capacity: Int
    private(set) var count = 0
    private let storage: UnsafeMutablePointer<UInt16>
    init(capacity: Int) {
        self.capacity = max(2, capacity)
        storage = .allocate(capacity: self.capacity)
        storage.initialize(repeating: 0, count: self.capacity)
    }
    deinit { clear(); storage.deallocate() }
    var units: UnsafeBufferPointer<UInt16> { UnsafeBufferPointer(start: storage, count: count) }
    var text: String { String(utf16CodeUnits: storage, count: count) }
    func append(_ unit: UInt16) {
        if count == capacity {
            let pair = (0xD800...0xDBFF).contains(storage[0]) && (0xDC00...0xDFFF).contains(storage[1])
            removeFirst(pair ? 2 : 1)
        }
        storage[count] = unit; count += 1
    }
    func append(_ text: String) { for unit in text.utf16 { append(unit) } }
    func removeFirst(_ amount: Int) {
        let n = min(count, amount)
        memmove(storage, storage.advanced(by: n), (count - n) * 2)
        count -= n
        memset_s(storage.advanced(by: count), n * 2, 0, n * 2)
    }
    func backspace() {
        guard count > 0 else { return }
        let pair = count > 1 && (0xDC00...0xDFFF).contains(storage[count - 1]) && (0xD800...0xDBFF).contains(storage[count - 2])
        let n = pair ? 2 : 1
        count -= n
        memset_s(storage.advanced(by: count), n * 2, 0, n * 2)
    }
    func clear() { memset_s(storage, capacity * 2, 0, capacity * 2); count = 0 }
}

/// Queues retain this expiring owner, never an immutable plaintext String.
final class TransientText {
    private let lock = NSLock()
    private let storage: WipingTextBuffer
    let expiresAt: UInt64
    private var timer: DispatchSourceTimer?
    init(_ text: String, expiresAt: UInt64 = DispatchTime.now().uptimeNanoseconds + 1_000_000_000) {
        self.expiresAt = expiresAt
        storage = WipingTextBuffer(capacity: max(2, text.utf16.count))
        storage.append(text)
        let timer = DispatchSource.makeTimerSource(queue: .global(qos: .userInitiated))
        timer.schedule(deadline: DispatchTime(uptimeNanoseconds: expiresAt))
        timer.setEventHandler { [weak self] in self?.erase() }
        self.timer = timer
        timer.resume()
    }
    deinit { erase() }
    func take() -> String {
        lock.lock(); defer { eraseLocked(); lock.unlock() }
        return DispatchTime.now().uptimeNanoseconds < expiresAt ? storage.text : ""
    }
    func erase() { lock.lock(); defer { lock.unlock() }; eraseLocked() }
    private func eraseLocked() { storage.clear(); timer?.cancel(); timer = nil }
}
