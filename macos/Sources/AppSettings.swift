import AppKit

struct EmotionDefinition: Codable {
    var id: String
    var name: String
    var description: String
    var imagePath: String
    var leftImagePath: String = ""
    var rightImagePath: String = ""
    var bothImagePath: String = ""

    func path(for pose: String) -> String {
        switch pose {
        case "left": return leftImagePath.isEmpty ? imagePath : leftImagePath
        case "right": return rightImagePath.isEmpty ? imagePath : rightImagePath
        case "both": return bothImagePath.isEmpty ? imagePath : bothImagePath
        default: return imagePath
        }
    }

    mutating func setPath(_ path: String, for pose: String) {
        switch pose {
        case "left": leftImagePath = path
        case "right": rightImagePath = path
        case "both": bothImagePath = path
        default: imagePath = path
        }
    }

    static let defaults: [EmotionDefinition] = [
        .init(id: "neutral", name: "평온", description: "무감정", imagePath: "assets/sprites/neutral.png"),
        .init(id: "angry", name: "화남", description: "분노", imagePath: "assets/sprites/angry.png"),
        .init(id: "love", name: "하트", description: "사랑", imagePath: "assets/sprites/love.png"),
        .init(id: "excited", name: "신남", description: "기쁨", imagePath: "assets/sprites/excited.png"),
        .init(id: "sad", name: "슬픔", description: "슬픔", imagePath: "assets/sprites/sad.png"),
        .init(id: "surprised", name: "놀람", description: "놀람", imagePath: "assets/sprites/surprised.png"),
        .init(id: "sleepy", name: "졸림", description: "졸림", imagePath: "assets/sprites/sleepy.png"),
        .init(id: "confused", name: "혼란", description: "혼란", imagePath: "assets/sprites/confused.png")
    ].map { (definition: EmotionDefinition) in
        var value = definition
        value.imagePath = "assets/frames/\(value.id)_idle.png"
        value.leftImagePath = "assets/frames/\(value.id)_left.png"
        value.rightImagePath = "assets/frames/\(value.id)_right.png"
        value.bothImagePath = "assets/frames/\(value.id)_both.png"
        return value
    }
}

struct AppSettings: Codable {
    var size: Double = 320
    var x: Double? = nil
    var y: Double? = nil
    var docked = true
    var captureText = true
    var model = "multilingual"
    var classificationPrompt = "문장을 쓴 사람은 어떤 감정을 느끼고 있나요?"
    var emotions = EmotionDefinition.defaults
}

final class SettingsStore {
    let resources: URL
    let directory: URL
    private let file: URL
    var settings: AppSettings

    init(resources: URL) {
        self.resources = resources
        directory = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("EmotionCat", isDirectory: true)
        file = directory.appendingPathComponent("mac-settings.json")
        if let data = try? Data(contentsOf: file), data.count < 1_048_576,
           let decoded = try? JSONDecoder().decode(AppSettings.self, from: data) {
            settings = decoded
        } else {
            settings = AppSettings()
        }
        settings.size = settings.size.isFinite ? min(700, max(180, settings.size)) : 320
        if let x = settings.x, !x.isFinite { settings.x = nil }
        if let y = settings.y, !y.isFinite { settings.y = nil }
        settings.model = "multilingual"
        var seen = Set<String>()
        settings.emotions = Array(settings.emotions.filter { item in
            item.id.range(of: "^[A-Za-z0-9_-]{1,64}$", options: .regularExpression) != nil && seen.insert(item.id).inserted
        }.prefix(16))
        if !settings.emotions.contains(where: { $0.id == "neutral" }) {
            settings.emotions = Array(settings.emotions.prefix(15))
            settings.emotions.insert(EmotionDefinition.defaults[0], at: 0)
        }
        if settings.emotions.count < 2 { settings.emotions = EmotionDefinition.defaults }
        for index in settings.emotions.indices {
            settings.emotions[index].name = String(settings.emotions[index].name.prefix(80))
            settings.emotions[index].description = String(settings.emotions[index].description.prefix(400))
            if settings.emotions[index].imagePath.isEmpty {
                settings.emotions[index].imagePath = "assets/sprites/\(settings.emotions[index].id).png"
            }
        }
    }

    func assetURL(_ path: String) -> URL {
        if path.hasPrefix("/") { return URL(fileURLWithPath: path) }
        return resources.appendingPathComponent(path)
    }

    func save() throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(settings).write(to: file, options: .atomic)
    }

    func importImage(_ source: URL) throws -> String {
        let values = try source.resourceValues(forKeys: [.fileSizeKey])
        guard (values.fileSize ?? Int.max) <= 16 * 1_024 * 1_024,
              let bitmap = NSBitmapImageRep(contentsOf: source),
              bitmap.pixelsWide > 0, bitmap.pixelsHigh > 0,
              bitmap.pixelsWide <= 4096, bitmap.pixelsHigh <= 4096,
              let png = bitmap.representation(using: .png, properties: [:]) else {
            throw NSError(domain: "EmotionCat", code: 1, userInfo: [NSLocalizedDescriptionKey: "이미지는 16 MB, 4096 × 4096 픽셀 이하여야 합니다."])
        }
        let folder = directory.appendingPathComponent("images", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let target = folder.appendingPathComponent(UUID().uuidString + ".png")
        try png.write(to: target, options: .atomic)
        return target.path
    }
}

final class SpriteCache {
    let store: SettingsStore
    private var images: [String: NSImage] = [:]
    init(store: SettingsStore) { self.store = store }
    func image(_ path: String) -> NSImage? {
        if let image = images[path] { return image }
        guard let image = NSImage(contentsOf: store.assetURL(path)) else { return nil }
        images[path] = image
        return image
    }
    func clear() { images.removeAll() }
}
