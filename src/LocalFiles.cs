using System;
using System.IO;

namespace EmotionCat
{
    internal static class LocalFiles
    {
        // File APIs also open SMB/WebDAV paths. Validate before reading content.
        internal static string Require(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Replace('/', '\\').StartsWith("\\\\", StringComparison.Ordinal))
                throw new ArgumentException("네트워크 경로는 사용할 수 없습니다.");
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable && drive.DriveType != DriveType.CDRom && drive.DriveType != DriveType.Ram)
                throw new ArgumentException("로컬 드라이브의 파일만 사용할 수 있습니다.");
            string current = root;
            // Check parents before descendants: probing through a junction first
            // could already contact the network before discovering the link.
            foreach (string component in full.Substring(root.Length).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new ArgumentException("링크·리디렉션 경로는 사용할 수 없습니다.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
            return full;
        }
    }
}
