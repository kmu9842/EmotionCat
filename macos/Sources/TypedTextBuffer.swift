import Foundation

// Bounded keystroke-only buffer, including standard Korean two-set composition.
final class TypedTextBuffer {
    private var committed = ""
    private var jamo: [Character] = []
    private static let leads = Array("ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ")
    private static let vowels = Array("ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ")
    private static let tails = Array(" ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ")
    private static let keys = Array("rRseEfaqQtTdwWczxvgkoiOjpuPhynbml")
    private static let letters = Array("ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎㅏㅐㅑㅒㅓㅔㅕㅖㅗㅛㅜㅠㅡㅣ")
    var text: String { String((committed + Self.compose(jamo)).suffix(240)) }
    func clear() { committed = ""; jamo.removeAll(keepingCapacity: true) }
    func commit() {
        committed = String((committed + Self.compose(jamo)).suffix(240))
        jamo.removeAll(keepingCapacity: true)
    }
    func append(_ value: String, korean: Bool) {
        for character in value {
            let spelling = String(character)
            let key = "REQTWOP".contains(character) ? character : (spelling.lowercased().first ?? character)
            if korean, let index = Self.keys.firstIndex(of: key) {
                jamo.append(Self.letters[index])
                if jamo.count > 1024 { jamo.removeFirst(256) }
            } else {
                commit()
                committed = String((committed + spelling).suffix(240))
            }
        }
    }
    func backspace() {
        if !jamo.isEmpty { jamo.removeLast() }
        else if !committed.isEmpty { committed.removeLast() }
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
    private static func compose(_ source: [Character]) -> String {
        var output = "", lead = -1, vowel = -1, tail = 0
        func flush(_ l: Int, _ v: Int, _ t: Int) {
            if l >= 0 && v >= 0 { output.unicodeScalars.append(UnicodeScalar(0xAC00 + (l * 21 + v) * 28 + t)!) }
            else if l >= 0 { output.append(leads[l]) }
            else if v >= 0 { output.append(vowels[v]) }
        }
        for character in source {
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
