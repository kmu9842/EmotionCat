import Foundation

/// Native port of `Reference` in tools/onnx/tokenizer_pack.py (Gemma-style SentencePiece BPE).
/// Text is processed as Unicode scalars (Python code points); vocabulary lookups use exact
/// scalar values, never Swift `String` equality, which would merge canonically equivalent text.
final class LayaTokenizer {
    enum LoadError: LocalizedError {
        case unreadable, malformed(String)
        var errorDescription: String? {
            switch self {
            case .unreadable: return "토크나이저 파일을 읽을 수 없습니다."
            case .malformed(let detail): return "토크나이저 파일이 손상되었습니다 (\(detail))."
            }
        }
    }

    private static let space: Unicode.Scalar = "\u{2581}"
    private static let unknown: Int32 = 3

    /// Single-scalar vocabulary entries (the only ones `piece` looks up directly).
    private var scalarIDs: [UInt32: Int32] = [:]
    /// `<0xXX>` byte-fallback ids.
    private var byteIDs = [Int32](repeating: -1, count: 256)
    /// Added tokens bucketed by first scalar, each bucket in Python's `sorted(key=-len)` order.
    private var added: [UInt32: [(scalars: [Unicode.Scalar], id: Int32)]] = [:]
    /// Open-addressing table: (left << 32 | right) -> rank, merged id.
    private var mergeKeys: [UInt64] = []
    private var mergeRanks: [Int32] = []
    private var mergeIDs: [Int32] = []
    private var mergeMask = 0
    private(set) var vocabularySize = 0
    private(set) var mergeCount = 0

    init(url: URL) throws {
        guard let data = try? Data(contentsOf: url, options: .alwaysMapped) else { throw LoadError.unreadable }
        try data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in try parse(raw) }
    }

    private func parse(_ raw: UnsafeRawBufferPointer) throws {
        var offset = 0
        func need(_ count: Int) throws {
            guard count >= 0, offset + count <= raw.count else { throw LoadError.malformed("offset \(offset)") }
        }
        func u16() throws -> Int {
            try need(2)
            let value = Int(raw[offset]) | Int(raw[offset + 1]) << 8
            offset += 2
            return value
        }
        func u32() throws -> UInt32 {
            try need(4)
            let value = UInt32(raw[offset]) | UInt32(raw[offset + 1]) << 8 | UInt32(raw[offset + 2]) << 16 | UInt32(raw[offset + 3]) << 24
            offset += 4
            return value
        }
        func i32() throws -> Int32 { Int32(bitPattern: try u32()) }

        try need(5)
        guard Array(raw[0..<5]) == Array("ECTK1".utf8) else { throw LoadError.malformed("header") }
        offset = 5

        let vocabCount = Int(try u32())
        vocabularySize = vocabCount
        scalarIDs.reserveCapacity(vocabCount / 2)
        for id in 0..<vocabCount {
            let length = try u16()
            try need(length)
            let bytes = UnsafeRawBufferPointer(rebasing: raw[offset..<offset + length])
            offset += length
            guard length > 0 else { continue }
            if let scalar = Self.singleScalar(bytes) {
                scalarIDs[scalar] = Int32(id)  // later ids win, as in the Python dict comprehension
            } else if length == 6, bytes[0] == 0x3C, bytes[1] == 0x30, bytes[2] == 0x78, bytes[5] == 0x3E,
                      let high = Self.hexDigit(bytes[3]), let low = Self.hexDigit(bytes[4]),
                      Self.isUpperHex(bytes[3]), Self.isUpperHex(bytes[4]) {
                byteIDs[high << 4 | low] = Int32(id)
            }
        }

        let count = Int(try u32())
        try need(count * 12)
        mergeCount = count
        var capacity = 1
        while capacity < count * 2 { capacity <<= 1 }
        mergeMask = capacity - 1
        var keys = [UInt64](repeating: UInt64.max, count: capacity)
        var ranks = [Int32](repeating: 0, count: capacity)
        var merged = [Int32](repeating: 0, count: capacity)
        let base = offset
        keys.withUnsafeMutableBufferPointer { keyBuffer in
            ranks.withUnsafeMutableBufferPointer { rankBuffer in
                merged.withUnsafeMutableBufferPointer { mergedBuffer in
                    for rank in 0..<count {
                        let at = base + rank * 12
                        let left = raw.loadUnaligned(fromByteOffset: at, as: UInt32.self).littleEndian
                        let right = raw.loadUnaligned(fromByteOffset: at + 4, as: UInt32.self).littleEndian
                        let result = raw.loadUnaligned(fromByteOffset: at + 8, as: Int32.self).littleEndian
                        let key = UInt64(left) << 32 | UInt64(right)
                        var slot = Self.hash(key) & mergeMask
                        while keyBuffer[slot] != UInt64.max && keyBuffer[slot] != key { slot = (slot + 1) & mergeMask }
                        // Later ranks overwrite earlier ones, exactly like the reference dict.
                        keyBuffer[slot] = key
                        rankBuffer[slot] = Int32(rank)
                        mergedBuffer[slot] = result
                    }
                }
            }
        }
        offset += count * 12
        mergeKeys = keys; mergeRanks = ranks; mergeIDs = merged

        let addedCount = Int(try u32())
        var tokens: [(scalars: [Unicode.Scalar], id: Int32, order: Int)] = []
        for index in 0..<addedCount {
            let id = try i32()
            let length = try u16()
            try need(length)
            let text = String(decoding: UnsafeRawBufferPointer(rebasing: raw[offset..<offset + length]), as: UTF8.self)
            offset += length
            let scalars = Array(text.unicodeScalars)
            if !scalars.isEmpty { tokens.append((scalars, id, index)) }
        }
        tokens.sort { $0.scalars.count != $1.scalars.count ? $0.scalars.count > $1.scalars.count : $0.order < $1.order }
        for token in tokens { added[token.scalars[0].value, default: []].append((token.scalars, token.id)) }
    }

    func encode(_ text: String) -> [Int32] { encode(Array(text.unicodeScalars)) }

    func encode(_ text: [Unicode.Scalar]) -> [Int32] {
        var out: [Int32] = []
        out.reserveCapacity(text.count)
        var index = 0, start = 0
        while index < text.count {
            if let hit = addedToken(in: text, at: index) {
                if index > start { segment(text[start..<index], into: &out) }
                out.append(hit.id)
                index += hit.length
                start = index
            } else {
                index += 1
            }
        }
        if start < text.count { segment(text[start...], into: &out) }
        return out
    }

    private func addedToken(in text: [Unicode.Scalar], at index: Int) -> (id: Int32, length: Int)? {
        guard let bucket = added[text[index].value] else { return nil }
        for candidate in bucket where index + candidate.scalars.count <= text.count {
            var matches = true
            for (offset, scalar) in candidate.scalars.enumerated() where text[index + offset] != scalar {
                matches = false; break
            }
            if matches { return (candidate.id, candidate.scalars.count) }
        }
        return nil
    }

    private func segment(_ text: ArraySlice<Unicode.Scalar>, into out: inout [Int32]) {
        var scalars = text.map { $0 == " " ? Self.space : $0 }
        if scalars.first != Self.space { scalars.insert(Self.space, at: 0) }
        var start = 0
        for index in 1...scalars.count where index == scalars.count || scalars[index] == Self.space {
            piece(scalars[start..<index], into: &out)
            start = index
        }
    }

    private func piece(_ text: ArraySlice<Unicode.Scalar>, into out: inout [Int32]) {
        var ids: [Int32] = []
        ids.reserveCapacity(text.count)
        for scalar in text {
            if let id = scalarIDs[scalar.value] {
                ids.append(id)
            } else {
                // The reference raises on a missing byte token; this vocabulary lacks only <0x09>,
                // and tab is a regular token, so the <unk> fallback is unreachable in practice.
                for byte in String(scalar).utf8 { ids.append(byteIDs[Int(byte)] >= 0 ? byteIDs[Int(byte)] : Self.unknown) }
            }
        }
        guard ids.count > 1 else { out.append(contentsOf: ids); return }
        // ranks[p] caches the merge for (ids[p], ids[p+1]); only neighbours change after a merge.
        var ranks = [Int32](repeating: 0, count: ids.count - 1)
        var results = [Int32](repeating: 0, count: ids.count - 1)
        for p in 0..<ids.count - 1 { (ranks[p], results[p]) = lookup(ids[p], ids[p + 1]) }
        while ids.count > 1 {
            var best = -1
            var bestRank = Int32.max
            for p in 0..<ranks.count where ranks[p] < bestRank { bestRank = ranks[p]; best = p }
            guard best >= 0 else { break }
            ids[best] = results[best]
            ids.remove(at: best + 1)
            ranks.remove(at: best); results.remove(at: best)
            if best > 0 { (ranks[best - 1], results[best - 1]) = lookup(ids[best - 1], ids[best]) }
            if best < ranks.count { (ranks[best], results[best]) = lookup(ids[best], ids[best + 1]) }
        }
        out.append(contentsOf: ids)
    }

    /// Returns (rank, merged) or (Int32.max, 0) when the pair has no merge.
    private func lookup(_ left: Int32, _ right: Int32) -> (Int32, Int32) {
        let key = UInt64(UInt32(bitPattern: left)) << 32 | UInt64(UInt32(bitPattern: right))
        var slot = Self.hash(key) & mergeMask
        while true {
            let stored = mergeKeys[slot]
            if stored == key { return (mergeRanks[slot], mergeIDs[slot]) }
            if stored == UInt64.max { return (Int32.max, 0) }
            slot = (slot + 1) & mergeMask
        }
    }

    private static func hash(_ key: UInt64) -> Int {
        var x = key &* 0x9E37_79B9_7F4A_7C15
        x ^= x >> 29
        return Int(truncatingIfNeeded: x)
    }

    private static func singleScalar(_ bytes: UnsafeRawBufferPointer) -> UInt32? {
        let b0 = UInt32(bytes[0])
        switch bytes.count {
        case 1 where b0 < 0x80: return b0
        case 2 where b0 & 0xE0 == 0xC0: return (b0 & 0x1F) << 6 | UInt32(bytes[1] & 0x3F)
        case 3 where b0 & 0xF0 == 0xE0: return (b0 & 0x0F) << 12 | UInt32(bytes[1] & 0x3F) << 6 | UInt32(bytes[2] & 0x3F)
        case 4 where b0 & 0xF8 == 0xF0:
            return (b0 & 0x07) << 18 | UInt32(bytes[1] & 0x3F) << 12 | UInt32(bytes[2] & 0x3F) << 6 | UInt32(bytes[3] & 0x3F)
        default: return nil
        }
    }

    private static func hexDigit(_ byte: UInt8) -> Int? {
        switch byte {
        case 0x30...0x39: return Int(byte - 0x30)
        case 0x41...0x46: return Int(byte - 0x41 + 10)
        default: return nil
        }
    }

    private static func isUpperHex(_ byte: UInt8) -> Bool { hexDigit(byte) != nil }
}
