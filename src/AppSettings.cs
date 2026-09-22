using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace EmotionCat
{
    public sealed class EmotionDefinition
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string ImagePath { get; set; }
        public string LeftImagePath { get; set; }
        public string RightImagePath { get; set; }
        public string BothImagePath { get; set; }

        public EmotionDefinition() { }

        public EmotionDefinition(string id, string name, string description)
        {
            Id = id;
            Name = name;
            Description = description;
            ImagePath = "assets/frames/" + id + "_idle.png";
            LeftImagePath = "assets/frames/" + id + "_left.png";
            RightImagePath = "assets/frames/" + id + "_right.png";
            BothImagePath = "assets/frames/" + id + "_both.png";
        }
    }

    public sealed class AppSettings
    {
        public int Size { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public bool AlwaysOnTop { get; set; }
        public bool InputEnabled { get; set; }
        public bool ClickThrough { get; set; }
        public bool SnapToTaskbar { get; set; }
        public string Model { get; set; }
        public string PythonPath { get; set; }
        public int SettingsVersion { get; set; }
        public string Device { get; set; }
        public string InputMode { get; set; }
        public string ClassificationPrompt { get; set; }
        public double MinConfidence { get; set; }
        public const string DefaultPrompt = "문장을 쓴 사람은 어떤 감정을 느끼고 있나요?";
        private const string LegacyPrompt = "Which emotion is expressed by the writer? Choose neutral for factual statements, ordinary questions, commands, or unclear feelings. Classify the tone of the input, not emotion words mentioned as a topic. Use sadness only for clearly sad feelings.";
        private static readonly string[] LegacyDescriptions = {
            "Neutral, calm, ordinary conversation without a strong emotion.",
            "Anger, irritation, frustration, annoyance, or rage.",
            "Love, affection, fondness, gratitude, or warmth toward someone.",
            "Joy, excitement, happiness, celebration, amusement, or enthusiasm.",
            "Sadness, disappointment, loneliness, sorrow, or feeling down.",
            "Surprise, amazement, shock, or an unexpected discovery.",
            "Sleepiness, tiredness, exhaustion, boredom, or needing rest.",
            "Confusion, uncertainty, puzzlement, doubt, or not understanding."
        };
        public int DebounceMilliseconds { get; set; }
        public int HoldSeconds { get; set; }
        public string[] ExcludedProcesses { get; set; }
        public List<EmotionDefinition> Emotions { get; set; }

        public static string BaseDirectory
        {
            get { return AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar); }
        }

        public static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmotionCat"); }
        }

        public AppSettings()
        {
            Size = 360;
            X = -1;
            Y = -1;
            AlwaysOnTop = true;
            InputEnabled = true;
            ClickThrough = false;
            SnapToTaskbar = true;
            Model = "multilingual";
            PythonPath = "";
            SettingsVersion = 4;
            Device = "auto";
            InputMode = "auto";
            ClassificationPrompt = DefaultPrompt;
            MinConfidence = 0.25;
            DebounceMilliseconds = 1000;
            HoldSeconds = 4;
            ExcludedProcesses = new[] { "1Password", "Bitwarden", "KeePass", "KeePassXC", "LastPass", "Dashlane", "NordPass" };
            Emotions = DefaultEmotions();
        }

        public static List<EmotionDefinition> DefaultEmotions()
        {
            return new List<EmotionDefinition>
            {
                new EmotionDefinition("neutral", "평온", "무감정"),
                new EmotionDefinition("angry", "화남", "분노"),
                new EmotionDefinition("love", "하트", "사랑"),
                new EmotionDefinition("excited", "신남", "기쁨"),
                new EmotionDefinition("sad", "슬픔", "슬픔"),
                new EmotionDefinition("surprised", "놀람", "놀람"),
                new EmotionDefinition("sleepy", "졸림", "졸림"),
                new EmotionDefinition("confused", "혼란", "혼란")
            };
        }

        public static AppSettings Load()
        {
            string path = Path.Combine(DataDirectory, "settings.json");
            if (!File.Exists(path)) return new AppSettings();
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 1024 * 1024) throw new InvalidDataException("Settings file is too large.");
                var serializer = new JavaScriptSerializer { MaxJsonLength = 1024 * 1024 };
                string json = File.ReadAllText(path, Encoding.UTF8);
                var settings = serializer.Deserialize<AppSettings>(json);
                if (settings == null) throw new InvalidDataException("Settings are empty.");
                var fields = serializer.Deserialize<Dictionary<string, object>>(json);
                if (!fields.ContainsKey("SettingsVersion") || settings.SettingsVersion < 2)
                {
                    settings.Model = "multilingual";
                    if (settings.DebounceMilliseconds == 1000 || settings.DebounceMilliseconds == 300) settings.DebounceMilliseconds = 150;
                    if (settings.HoldSeconds == 8) settings.HoldSeconds = 4;
                    settings.SettingsVersion = 2;
                }
                if (!fields.ContainsKey("SettingsVersion") || settings.SettingsVersion < 3)
                {
                    settings.DebounceMilliseconds = 1000;
                    if (settings.ClassificationPrompt == LegacyPrompt) settings.ClassificationPrompt = DefaultPrompt;
                    if (settings.MinConfidence == 0.5) settings.MinConfidence = 0.35;
                    var defaults = DefaultEmotions();
                    if (settings.Emotions != null) for (int i = 0; i < defaults.Count; i++)
                    {
                        var existing = settings.Emotions.Find(e => e != null && e.Id == defaults[i].Id);
                        if (existing != null && existing.Description == LegacyDescriptions[i]) existing.Description = defaults[i].Description;
                    }
                    settings.SettingsVersion = 3;
                }
                if (!fields.ContainsKey("SettingsVersion") || settings.SettingsVersion < 4)
                {
                    if (settings.ClassificationPrompt == "문장에서 드러나는 화자의 감정은 무엇인가요? 감정이 드러나지 않으면 무감정입니다.") settings.ClassificationPrompt = DefaultPrompt;
                    if (settings.MinConfidence == 0.35) settings.MinConfidence = 0.25;
                    settings.SettingsVersion = 4;
                }
                settings.Normalize();
                return settings;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidOperationException || ex is FormatException || ex is OverflowException)) throw;
                // Keep a readable copy for recovery without blocking startup.
                try { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"), false); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                return new AppSettings();
            }
        }

        public void Save()
        {
            Normalize();
            Directory.CreateDirectory(DataDirectory);
            string path = Path.Combine(DataDirectory, "settings.json");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                string json = new JavaScriptSerializer().Serialize(this);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", true);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        internal void Normalize()
        {
            if (InputMode != "korean" && InputMode != "latin") InputMode = "auto";
            Size = Math.Max(180, Math.Min(800, Size));
            DebounceMilliseconds = Math.Max(150, Math.Min(5000, DebounceMilliseconds));
            HoldSeconds = Math.Max(2, Math.Min(120, HoldSeconds));
            Model = "multilingual";
            if (PythonPath == null) PythonPath = "";
            if (Device != "cuda" && Device != "cpu" && Device != "mps") Device = "auto";
            if (String.IsNullOrWhiteSpace(ClassificationPrompt)) ClassificationPrompt = DefaultPrompt;
            ClassificationPrompt = Limit(ClassificationPrompt, 1000);
            if (Double.IsNaN(MinConfidence) || Double.IsInfinity(MinConfidence)) MinConfidence = 0.25;
            MinConfidence = Math.Max(0, Math.Min(1, MinConfidence));
            if (ExcludedProcesses == null) ExcludedProcesses = new AppSettings().ExcludedProcesses;
            ExcludedProcesses = ExcludedProcesses.Where(p => !String.IsNullOrWhiteSpace(p))
                .Select(p => Path.GetFileNameWithoutExtension(p.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();
            if (Emotions == null || Emotions.Count == 0) Emotions = DefaultEmotions();
            var defaults = DefaultEmotions().ToDictionary(e => e.Id, StringComparer.Ordinal);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var valid = new List<EmotionDefinition>();
            foreach (var emotion in Emotions.Take(16))
            {
                if (emotion == null || !IsValidId(emotion.Id) || !ids.Add(emotion.Id)) continue;
                EmotionDefinition original;
                defaults.TryGetValue(emotion.Id, out original);
                if (String.IsNullOrWhiteSpace(emotion.Name)) emotion.Name = original == null ? emotion.Id : original.Name;
                if (String.IsNullOrWhiteSpace(emotion.Description)) emotion.Description = original == null ? emotion.Name : original.Description;
                if (String.IsNullOrWhiteSpace(emotion.ImagePath)) emotion.ImagePath = original == null ? "assets/sprites/neutral.png" : original.ImagePath;
                if (original != null)
                {
                    if (emotion.ImagePath == "assets/sprites/" + emotion.Id + ".png") emotion.ImagePath = original.ImagePath;
                    if (String.IsNullOrWhiteSpace(emotion.LeftImagePath)) emotion.LeftImagePath = original.LeftImagePath;
                    if (String.IsNullOrWhiteSpace(emotion.RightImagePath)) emotion.RightImagePath = original.RightImagePath;
                    if (String.IsNullOrWhiteSpace(emotion.BothImagePath)) emotion.BothImagePath = original.BothImagePath;
                }
                try { ResolveAssetPath(emotion.ImagePath); }
                catch (Exception ex)
                {
                    if (!(ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)) throw;
                    emotion.ImagePath = original == null ? "assets/sprites/neutral.png" : original.ImagePath;
                }
                emotion.LeftImagePath = ValidateOptionalPath(emotion.LeftImagePath);
                emotion.RightImagePath = ValidateOptionalPath(emotion.RightImagePath);
                emotion.BothImagePath = ValidateOptionalPath(emotion.BothImagePath);
                emotion.Name = Limit(emotion.Name, 80);
                emotion.Description = Limit(emotion.Description, 400);
                valid.Add(emotion);
            }
            // The idle timer always selects neutral. Repair external/corrupt configs
            // that omit it, so the idle face never becomes an arbitrary emotion.
            if (!valid.Any(e => e.Id == "neutral"))
            {
                if (valid.Count == 16) valid.RemoveAt(valid.Count - 1);
                valid.Insert(0, defaults["neutral"]);
            }
            if (valid.Count < 2) valid = DefaultEmotions();
            Emotions = valid;
        }

        private static bool IsValidId(string id)
        {
            if (String.IsNullOrWhiteSpace(id) || id.Length > 48) return false;
            foreach (char c in id)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
            return true;
        }
        static string ValidateOptionalPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            try { ResolveAssetPath(path); return path; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private static string Limit(string value, int length)
        {
            return value.Length > length ? value.Substring(0, length) : value;
        }

        public static string ResolveAssetPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) path = "assets/sprites/neutral.png";
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(BaseDirectory, path.Replace('/', Path.DirectorySeparatorChar)));
        }

        public static string ImportImage(string sourcePath)
        {
            if (String.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("이미지 파일을 선택하세요.");
            var source = new FileInfo(sourcePath);
            if (!source.Exists) throw new FileNotFoundException("이미지 파일을 찾을 수 없습니다.", sourcePath);
            if (source.Length > 16 * 1024 * 1024) throw new InvalidDataException("이미지는 16 MB 이하여야 합니다.");
            string directory = Path.Combine(DataDirectory, "assets");
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".png");
            // Decode, validate and normalize to PNG; a settings entry never relies on an external file.
            using (var stream = new FileStream(source.FullName, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var image = Image.FromStream(stream, true, true))
            {
                if (image.Width > 4096 || image.Height > 4096 || image.Width < 1 || image.Height < 1)
                    throw new InvalidDataException("이미지 크기는 가로·세로 4096 픽셀 이하여야 합니다.");
                using (var bitmap = new Bitmap(512, 384, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    float ratio = Math.Min(512f / image.Width, 384f / image.Height);
                    float width = image.Width * ratio, height = image.Height * ratio;
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(image, (512 - width) / 2, (384 - height) / 2, width, height);
                    }
                    bitmap.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
            return target;
        }
    }
}
