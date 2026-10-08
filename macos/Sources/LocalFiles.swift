import Foundation
import Darwin

enum LocalFiles {
    static func require(_ url: URL) throws -> URL {
        guard url.isFileURL, url.host == nil || url.host == "" || url.host == "localhost",
              !url.pathComponents.contains("..") else { throw rejected }
        let path = url.path
        // Read the kernel's mount table without querying a remote filesystem.
        var mounts: UnsafeMutablePointer<statfs>?
        let count = getmntinfo(&mounts, MNT_NOWAIT)
        guard count > 0, let mounts = mounts else { throw rejected }
        var longest = -1, local = false
        for index in 0..<Int(count) {
            var name = mounts[index].f_mntonname
            let mount = withUnsafeBytes(of: &name) { String(cString: $0.baseAddress!.assumingMemoryBound(to: CChar.self)) }
            if (mount == "/" || path == mount || path.hasPrefix(mount + "/")), mount.count > longest {
                longest = mount.count; local = mounts[index].f_flags & UInt32(MNT_LOCAL) != 0
            }
        }
        guard local else { throw rejected }
        var current = ""
        for component in path.split(separator: "/") {
            current += "/" + component
            var info = stat()
            if lstat(current, &info) == 0 {
                guard info.st_mode & S_IFMT != S_IFLNK else { throw rejected }
            } else if errno != ENOENT { throw rejected }
        }
        return url
    }
    private static var rejected: NSError {
        NSError(domain: "EmotionCat", code: 2, userInfo: [NSLocalizedDescriptionKey: "네트워크·링크 경로는 사용할 수 없습니다. 로컬 파일을 선택해 주세요."])
    }
}
