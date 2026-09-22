using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace EmotionCat
{
    internal sealed class SpriteCatalog : IDisposable
    {
        readonly Dictionary<string, Bitmap> images = new Dictionary<string, Bitmap>();
        public Bitmap Get(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            path = AppSettings.ResolveAssetPath(path);
            Bitmap image;
            if (images.TryGetValue(path, out image)) return image;
            if (!File.Exists(path)) return null;
            try
            {
                using (var loaded = Image.FromFile(path))
                {
                    float scale = Math.Min(1f, Math.Min(512f / loaded.Width, 384f / loaded.Height));
                    image = new Bitmap(Math.Max(1, (int)(loaded.Width * scale)), Math.Max(1, (int)(loaded.Height * scale)), PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(image)) { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(loaded, 0, 0, image.Width, image.Height); }
                }
                images[path] = image;
                return image;
            }
            catch (ArgumentException) { return null; }
            catch (IOException) { return null; }
        }
        public void Clear() { foreach (var image in images.Values) image.Dispose(); images.Clear(); }
        public void Prune(IEnumerable<string> retained)
        {
            var keep = new HashSet<string>();
            foreach (var path in retained) if (!String.IsNullOrWhiteSpace(path)) keep.Add(AppSettings.ResolveAssetPath(path));
            var obsolete = new List<string>();
            foreach (var path in images.Keys) if (!keep.Contains(path)) obsolete.Add(path);
            foreach (var path in obsolete) { images[path].Dispose(); images.Remove(path); }
        }
        public void Dispose() { Clear(); }
    }

    internal static class CatPainter
    {
        public static void Draw(Graphics g, Rectangle bounds, Bitmap cat, Bitmap paw, float left, float right, bool desk)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Min(bounds.Width / 512f, bounds.Height / 384f);
            var state = g.Save();
            g.TranslateTransform(bounds.X + (bounds.Width - 512 * scale) / 2, bounds.Y + (bounds.Height - 384 * scale) / 2);
            g.ScaleTransform(scale, scale);
            if (desk) using (var pen = new Pen(Color.FromArgb(52, 43, 44), 3.2f)) { pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; g.DrawLine(pen, 12, 310, 500, 310); }
            if (cat != null)
            {
                g.DrawImage(cat, new RectangleF(0, 0, 512, 384));
            }
            else using (var font = new Font("Malgun Gothic", 15)) using (var brush = new SolidBrush(Color.FromArgb(128, 97, 104)))
                g.DrawString("이미지를 지정해 주세요", font, brush, 120, 200);
            g.Restore(state);
        }
    }

    internal sealed class CatPreview : Control
    {
        public Bitmap Cat;
        public CatPreview() { DoubleBuffered = true; BackColor = Color.FromArgb(247, 242, 239); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var dot = new SolidBrush(Color.FromArgb(228, 217, 213)))
                for (int y = 16; y < Height; y += 20) for (int x = 16; x < Width; x += 20) e.Graphics.FillEllipse(dot, x, y, 2, 2);
            CatPainter.Draw(e.Graphics, ClientRectangle, Cat, null, 0, 0, true);
        }
    }

    internal sealed class CatOverlay : Form
    {
        readonly AppSettings settings;
        readonly SpriteCatalog sprites;
        readonly Timer animation = new Timer { Interval = 16 };
        DateTime leftAt = DateTime.MinValue, rightAt = DateTime.MinValue;
        string emotion = "neutral";
        bool nextRight;
        public bool LastPaintSucceeded { get; private set; }
        public event Action OpenSettings;
        public event Action Moved;
        public event Action<string, string> FrameChanged;
        public CatOverlay(AppSettings settings, SpriteCatalog sprites)
        {
            this.settings = settings; this.sprites = sprites;
            Text = "EmotionCat"; FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            animation.Interval = 35;
            animation.Tick += delegate { if (!IsUp(leftAt) && !IsUp(rightAt)) { animation.Stop(); Render(); } };
            MouseDoubleClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && OpenSettings != null) OpenSettings(); };
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };
            MouseUp += delegate { SavePosition(); };
            ResizeEnd += delegate { SavePosition(); };
            ApplySettings();
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x80 | 0x08000000; if (settings != null && settings.ClickThrough) p.ExStyle |= 0x20; return p; }
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); Render(); }
        public void ApplySettings()
        {
            Size = new Size(settings.Size, settings.Size * 3 / 4);
            TopMost = settings.AlwaysOnTop;
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            int x = settings.X == -1 ? area.Right - Width - 30 : settings.X;
            int y = settings.Y == -1 ? area.Bottom - (int)Math.Round(310f * Width / 512f) : settings.Y;
            var desired = new Rectangle(x, y, Width, Height);
            bool visible = false;
            foreach (var screen in Screen.AllScreens) if (screen.WorkingArea.IntersectsWith(desired)) visible = true;
            if (!visible) { x = area.Right - Width - 30; y = area.Bottom - Height; }
            if (settings.SnapToTaskbar)
            {
                var screen = Screen.FromRectangle(new Rectangle(x, y, Width, Height));
                y = screen.WorkingArea.Bottom - (int)Math.Round(310f * Width / 512f);
            }
            Location = new Point(x, y);
            if (IsHandleCreated)
            {
                long style = GetWindowLong(Handle, -20).ToInt64();
                if (settings.ClickThrough) style |= 0x20; else style &= ~0x20;
                SetWindowLong(Handle, -20, (IntPtr)style); Render();
            }
        }
        void SavePosition()
        {
            settings.X = Left; settings.Y = Top;
            if (settings.SnapToTaskbar) ApplySettings();
            if (Moved != null) Moved();
        }
        public void SetEmotion(string id) { emotion = id; Render(); }
        public void Hit(int key)
        {
            bool right = nextRight; nextRight = !nextRight;
            if (right) { rightAt = DateTime.UtcNow; leftAt = DateTime.MinValue; }
            else { leftAt = DateTime.UtcNow; rightAt = DateTime.MinValue; }
            Render(); animation.Start();
        }
        bool IsUp(DateTime at) { return (DateTime.UtcNow - at).TotalMilliseconds < 105; }
        public void Render()
        {
            if (!IsHandleCreated || Width < 1) return;
            var definition = settings.Emotions.Find(x => x.Id == emotion) ?? settings.Emotions.Find(x => x.Id == "neutral") ?? settings.Emotions[0];
            bool left = IsUp(leftAt), right = IsUp(rightAt);
            string path = left && right ? definition.BothImagePath : left ? definition.LeftImagePath : right ? definition.RightImagePath : definition.ImagePath;
            var image = sprites.Get(path) ?? sprites.Get(definition.ImagePath);
            if (String.IsNullOrWhiteSpace(path) || sprites.Get(path) == null) path = definition.ImagePath;
            using (var frame = new Bitmap(Width, Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(frame)) CatPainter.Draw(g, new Rectangle(0, 0, Width, Height), image, null, 0, 0, true);
                SetBitmap(frame);
            }
            if (FrameChanged != null) FrameChanged(path, emotion);
        }
        void SetBitmap(Bitmap bitmap)
        {
            IntPtr screen = GetDC(IntPtr.Zero), memory = CreateCompatibleDC(screen), dib = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                dib = bitmap.GetHbitmap(Color.FromArgb(0)); old = SelectObject(memory, dib);
                var size = new NativeSize { X = Width, Y = Height }; var source = new NativePoint(); var dest = new NativePoint { X = Left, Y = Top };
                var blend = new BlendFunction { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                LastPaintSucceeded = UpdateLayeredWindow(Handle, screen, ref dest, ref size, memory, ref source, 0, ref blend, 2);
            }
            finally { if (old != IntPtr.Zero) SelectObject(memory, old); if (dib != IntPtr.Zero) DeleteObject(dib); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
        }
        protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct NativeSize { public int X, Y; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dc, ref NativePoint p, ref NativeSize s, IntPtr src, ref NativePoint sp, int key, ref BlendFunction b, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLong(IntPtr h, int index, IntPtr value);
    }
}
