using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;

namespace EmotionCat
{
    internal static class VisualTests
    {
        [STAThread]
        static int Main()
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            string output = Path.Combine(AppSettings.BaseDirectory, "output", "verification"); Directory.CreateDirectory(output);
            int checks = 0;
            foreach (var emotion in AppSettings.DefaultEmotions())
            {
                var hashes = new System.Collections.Generic.HashSet<string>();
                foreach (string path in new[] { emotion.ImagePath, emotion.LeftImagePath, emotion.RightImagePath, emotion.BothImagePath })
                {
                    string full = AppSettings.ResolveAssetPath(path);
                    if (!File.Exists(full)) throw new Exception("Missing mapped frame: " + path);
                    using (var image = new Bitmap(full))
                    {
                        if (image.Width != 512 || image.Height != 384) throw new Exception("Incorrect frame size: " + path);
                        if (image.GetPixel(0, 0).A != 0) throw new Exception("Frame is not transparent: " + path);
                    }
                    using (var hash = SHA256.Create()) hashes.Add(Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(full))));
                    checks++;
                }
                if (hashes.Count != 4) throw new Exception("Paw poses are not distinct: " + emotion.Id);
                checks++;
            }
            using (var catalog = new SpriteCatalog())
            {
                var settings = new AppSettings();
                using (var cat = new CatOverlay(settings, catalog))
                {
                    var handle = cat.Handle;
                    string framePath = null;
                    cat.FrameChanged += (path, id) => framePath = path;
                    cat.Hit(65);
                    if (framePath != "assets/frames/neutral_left.png") throw new Exception("First key did not raise left paw.");
                    checks++;
                    cat.Hit(65);
                    if (framePath != "assets/frames/neutral_right.png") throw new Exception("Second key did not raise right paw.");
                    checks++;
                    System.Threading.Thread.Sleep(130); cat.Render();
                    if (framePath != "assets/frames/neutral_idle.png") throw new Exception("Paws did not return to idle.");
                    checks++;
                    foreach (int width in new[] { 180, 360, 600 })
                    {
                        settings.Size = width; cat.ApplySettings();
                        int line = cat.Top + (int)Math.Round(310f * width / 512f);
                        if (line != Screen.FromControl(cat).WorkingArea.Bottom) throw new Exception("Desk is not aligned to taskbar for width " + width);
                        checks++;
                    }
                    cat.SetEmotion("angry");
                    if (framePath != "assets/frames/angry_idle.png") throw new Exception("Emotion does not select its mapped image.");
                    checks++;
                }
            }
            using (var app = new AppController(true, false, true))
            using (var window = new SettingsForm(app))
            {
                window.Opacity = 0;
                window.ShowInTaskbar = false;
                window.Show();
                Application.DoEvents();
                var select = typeof(SettingsForm).GetMethod("ShowPage", BindingFlags.NonPublic | BindingFlags.Instance);
                for (int page = 0; page < 3; page++)
                {
                    select.Invoke(window, new object[] { page });
                    Application.DoEvents();
                    using (var frame = new Bitmap(window.Width, window.Height))
                    {
                        window.DrawToBitmap(frame, new Rectangle(0, 0, window.Width, window.Height));
                        var colors = new System.Collections.Generic.HashSet<int>();
                        for (int y = 80; y < frame.Height - 30; y += 3) for (int x = 20; x < frame.Width - 20; x += 3) colors.Add(frame.GetPixel(x, y).ToArgb());
                        if (colors.Count < 30) throw new Exception("UI page did not render controls: " + page);
                        frame.Save(Path.Combine(output, "settings-" + page + ".png"), ImageFormat.Png);
                    }
                    checks++;
                }
                app.ExitThread();
            }
            Console.WriteLine("PASS " + checks + " frame mapping and UI render checks; no global input observed.");
            return 0;
        }
    }
}
