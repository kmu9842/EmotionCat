import Foundation

// Bounded keystroke-only buffer, including standard Korean two-set composition.
final class TypedTextBuffer {
    private let committed = WipingTextBuffer(capacity: 240)
    private let jamo = WipingTextBuffer(capacity: 1024)
    private let lock = NSRecursiveLock()
    private var expiry: DispatchSourceTimer?
    private var expiryVersion = 0
    private var expiresAt: UInt64 = 0
    private static let leads = Array("ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ")
    private static let vowels = Array("ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ")
    private static let tails = Array(" ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ")
    private static let keys = Array("rRseEfaqQtTdwWczxvgkoiOjpuPhynbml")
    private static let letters = Array("ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎㅏㅐㅑㅒㅓㅔㅕㅖㅗㅛㅜㅠㅡㅣ")
    deinit { expiry?.cancel() }
    var text: String {
        lock.lock(); defer { lock.unlock() }
        guard !expired() else { return "" }
        return String((committed.text + Self.compose(jamo.units)).suffix(240))
    }
    private func expired() -> Bool {
        if expiresAt != 0 && DispatchTime.now().uptimeNanoseconds >= expiresAt { clear(); return true }
        return false
    }
    func clear() {
        lock.lock(); defer { lock.unlock() }
        expiryVersion += 1; expiry?.cancel(); expiry = nil
        committed.clear(); jamo.clear()
    }
    func expire(at deadline: UInt64) {
        lock.lock(); defer { lock.unlock() }
        expiry?.cancel(); expiryVersion += 1
        expiresAt = deadline
        let version = expiryVersion
        let timer = DispatchSource.makeTimerSource(queue: .global(qos: .userInitiated))
        timer.schedule(deadline: DispatchTime(uptimeNanoseconds: deadline))
        timer.setEventHandler { [weak self] in
            guard let self = self else { return }
            self.lock.lock(); defer { self.lock.unlock() }
            if self.expiryVersion == version { self.clear() }
        }
        expiry = timer; timer.resume()
    }
    func commit() {
        lock.lock(); defer { lock.unlock() }
        guard !expired() else { return }
        committed.append(Self.compose(jamo.units))
        jamo.clear()
    }
    func append(_ value: String, korean: Bool) {
        lock.lock(); defer { lock.unlock() }
        guard !expired() else { return }
        for character in value {
            let spelling = String(character)
            let key = "REQTWOP".contains(character) ? character : (spelling.lowercased().first ?? character)
            if korean, let index = Self.keys.firstIndex(of: key) {
                if jamo.count == 1024 { jamo.removeFirst(256) }
                jamo.append(String(Self.letters[index]))
            } else {
                commit()
                committed.append(spelling)
            }
        }
    }
    func backspace() {
        lock.lock(); defer { lock.unlock() }
        guard !expired() else { return }
        if jamo.count > 0 { jamo.backspace() }
        else { committed.backspace() }
    }
    private static func compoundVowel(_ a: Int, _ b: Int) -> Int? {
        switch (a, b) {
        case (8, 0): return 9
        case (8, 1): return 10
        case (8, 20): return 11
        case (13, 4): return 14
        case (13, 5): return 15
        case (13, 20): return 16
        case (18, 20): return 19
        default: return nil
        }
    }
    private static func compoundTail(_ a: Int, _ b: Int) -> Int? {
        if a == 1 && b == 19 { return 3 }
        if a == 4 && b == 22 { return 5 }
        if a == 4 && b == 27 { return 6 }
        if a == 8, let index = [1, 16, 17, 19, 25, 26, 27].firstIndex(of: b) { return 9 + index }
        return a == 17 && b == 19 ? 18 : nil
    }
    private static func splitTail(_ value: Int) -> (Int, Character) {
        switch value {
        case 3: return (1, "ㅅ")
        case 5: return (4, "ㅈ")
        case 6: return (4, "ㅎ")
        case 9...15: return (8, Array("ㄱㅁㅂㅅㅌㅍㅎ")[value - 9])
        case 18: return (17, "ㅅ")
        default: return (0, tails[value])
        }
    }
    private static func compose(_ source: UnsafeBufferPointer<UInt16>) -> String {
        var output = "", lead = -1, vowel = -1, tail = 0
        func flush(_ l: Int, _ v: Int, _ t: Int) {
            if l >= 0 && v >= 0 { output.unicodeScalars.append(UnicodeScalar(0xAC00 + (l * 21 + v) * 28 + t)!) }
            else if l >= 0 { output.append(leads[l]) }
            else if v >= 0 { output.append(vowels[v]) }
        }
        for unit in source {
            let character = Character(UnicodeScalar(unit)!)
            if let nextVowel = vowels.firstIndex(of: character) {
                if vowel < 0 { vowel = nextVowel }
                else if tail > 0 {
                    let (first, second) = splitTail(tail)
                    flush(lead, vowel, first)
                    lead = leads.firstIndex(of: second) ?? -1; vowel = nextVowel; tail = 0
                } else if let combined = compoundVowel(vowel, nextVowel) { vowel = combined }
                else { flush(lead, vowel, tail); lead = -1; vowel = nextVowel; tail = 0 }
            } else if let nextLead = leads.firstIndex(of: character) {
                let nextTail = tails.firstIndex(of: character) ?? -1
                if lead >= 0 && vowel >= 0 && nextTail > 0 {
                    if tail == 0 { tail = nextTail }
                    else if let combined = compoundTail(tail, nextTail) { tail = combined }
                    else { flush(lead, vowel, tail); lead = nextLead; vowel = -1; tail = 0 }
                } else { flush(lead, vowel, tail); lead = nextLead; vowel = -1; tail = 0 }
            }
        }
        flush(lead, vowel, tail)
        return output
    }
}
