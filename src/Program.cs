using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EmotionCat
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (var singleton = new Mutex(true, "Local\\EmotionCat.Desktop.v1", out created))
            {
                if (!created) { MessageBox.Show("EmotionCat이 이미 실행 중이에요.\n작업 표시줄의 고양이 아이콘을 더블클릭해 주세요.", "EmotionCat"); return; }
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { MessageBox.Show("작업을 완료하지 못했어요.\n" + e.Exception.Message, "EmotionCat"); };
                using (var app = new AppController(args.Contains("--preview"), args.Contains("--settings"))) Application.Run(app);
                singleton.ReleaseMutex();
            }
        }
    }

    internal sealed class AppController : ApplicationContext
    {
        public readonly AppSettings Settings;
        public readonly SpriteCatalog Sprites = new SpriteCatalog();
        public readonly LayaClient Client = new LayaClient();
        public readonly Icon AppIcon;
        public string ModelStatus = "Laya 준비 중…", InputStatus = "입력 대기";
        public string LastInputText = "", DecisionSummary = "아직 분류 결과가 없어요.";
        public int CaptureCount, SentCount, ResponseCount, ObservedChars;
        public DateTime LastCapturedAt;
        public string PipelineSummary { get { return "감지 " + CaptureCount + " → 전달 " + SentCount + " → 결과 " + ResponseCount + " · 현재 " + ObservedChars + "자"; } }
        readonly CatOverlay overlay;
        readonly InputMonitor monitor = new InputMonitor();
        readonly NotifyIcon tray;
        readonly System.Windows.Forms.Timer idle = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly System.Windows.Forms.Timer saveDelay = new System.Windows.Forms.Timer { Interval = 400 };
        readonly bool previewOnly;
        readonly bool persistSettings;
        public string CurrentFramePath { get; private set; }
        public string CurrentEmotionId { get; private set; }
        SettingsForm form;
        DateTime lastEmotion = DateTime.UtcNow;
        string lastEmotionId = "neutral";
        long requestVersion;
        string queuedText;
        long queuedContext;
        long activeContext = -1;
        bool analyzing, restarting, exiting;
        readonly ToolStripMenuItem pauseItem, clickItem;

        public AppController(bool previewOnly, bool showSettings, bool renderOnly = false, int captureProcessIdFilter = 0, string inputModeOverride = null)
        {
            this.previewOnly = previewOnly;
            persistSettings = !renderOnly;
            bool firstRun = !File.Exists(Path.Combine(AppSettings.DataDirectory, "settings.json"));
            Settings = AppSettings.Load();
            monitor.CaptureProcessIdFilter = captureProcessIdFilter;
            if (inputModeOverride != null) Settings.InputMode = inputModeOverride;
            saveDelay.Tick += delegate { saveDelay.Stop(); SaveSettings(); };
            string iconPath = Path.Combine(AppSettings.BaseDirectory, "assets", "emotioncat.ico");
            AppIcon = File.Exists(iconPath) ? new Icon(iconPath) : (Icon)SystemIcons.Information.Clone();
            overlay = new CatOverlay(Settings, Sprites); if (!renderOnly) overlay.Show();
            var overlayHandle = overlay.Handle;
            overlay.FrameChanged += (path, id) => { CurrentFramePath = path; CurrentEmotionId = id; if (form != null && !form.IsDisposed) form.ShowLiveFrame(path, id); };
            overlay.OpenSettings += OpenSettings;
            overlay.Moved += SaveSettings;
            var menu = new ContextMenuStrip();
            menu.Items.Add("EmotionCat 설정", null, delegate { OpenSettings(); });
            pauseItem = new ToolStripMenuItem("감정 인식 일시정지", null, delegate { Settings.InputEnabled = !Settings.InputEnabled; ApplySettings(); }); menu.Items.Add(pauseItem);
            clickItem = new ToolStripMenuItem("마우스 통과", null, delegate { Settings.ClickThrough = !Settings.ClickThrough; ApplySettings(); }); menu.Items.Add(clickItem);
            menu.Items.Add("고양이 위치 초기화", null, delegate { Settings.X = -1; Settings.Y = -1; ApplySettings(); });
            menu.Items.Add(new ToolStripSeparator()); menu.Items.Add("종료", null, delegate { ExitThread(); });
            overlay.ContextMenuStrip = menu;
            tray = new NotifyIcon { Icon = AppIcon, Text = "EmotionCat · 로컬 감정 봉고캣", Visible = !renderOnly, ContextMenuStrip = menu };
            tray.DoubleClick += delegate { OpenSettings(); };
            monitor.KeyPressed += key => OnUI(delegate { if (!exiting) overlay.Hit(key); });
            monitor.TextReadyWithContext += (text, context) => OnUI(delegate { if (Settings.InputEnabled) QueueText(text, context); });
            monitor.ContextClearedWithVersion += context => OnUI(delegate
            {
                if (context != monitor.ContextVersion) return;
                if (queuedContext != context) queuedText = null;
                if (activeContext >= 0 && activeContext != context) requestVersion++;
            });
            monitor.TextObserved += count => OnUI(delegate { ObservedChars = count; RefreshStatus(); });
            monitor.StatusChanged += text => OnUI(delegate { InputStatus = text; RefreshStatus(); });
            Client.StatusChanged += text => OnUI(delegate { ModelStatus = text; RefreshStatus(); if (Client.IsReady && queuedText != null && !analyzing) ProcessQueue(); });
            Client.HealthChanged += delegate { OnUI(delegate { RefreshStatus(); }); };
            idle.Tick += delegate { idle.Stop(); ShowEmotion("neutral"); };
            ApplySettings();
            if (!previewOnly) { monitor.Start(); StartModel(); }
            else { InputStatus = "미리보기 모드 · 전역 입력 인식 꺼짐"; ModelStatus = "미리보기 모드 · 연결 버튼으로 Laya 테스트 가능"; }
            if (!renderOnly && (firstRun || showSettings || previewOnly)) OpenSettings();
        }
        public void OpenSettings()
        {
            if (form == null || form.IsDisposed) { form = new SettingsForm(this); form.FormClosed += delegate { form = null; }; form.Show(); }
            else { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); }
        }
        public void ApplySettings(bool persist = true)
        {
            monitor.Enabled = Settings.InputEnabled && !previewOnly;
            monitor.DebounceMilliseconds = Settings.DebounceMilliseconds;
            monitor.InputMode = Settings.InputMode;
            if (!monitor.ExcludedProcesses.SequenceEqual(Settings.ExcludedProcesses)) monitor.ExcludedProcesses = Settings.ExcludedProcesses;
            pauseItem.Checked = !Settings.InputEnabled; clickItem.Checked = Settings.ClickThrough;
            if (!Settings.InputEnabled) { InvalidateRequests(); ShowEmotion("neutral"); }
            overlay.ApplySettings();
            if (persist) SaveSettings(); else { saveDelay.Stop(); saveDelay.Start(); }
            RefreshStatus();
        }
        public void SaveSettings()
        {
            if (!persistSettings) return;
            try { Settings.Save(); }
            catch (Exception ex) { if (ex is IOException || ex is UnauthorizedAccessException) { ModelStatus = "설정을 저장하지 못했어요: " + ex.Message; RefreshStatus(); } else throw; }
        }
        void OnUI(Action action)
        {
            if (exiting || overlay.IsDisposed || !overlay.IsHandleCreated) return;
            try { if (overlay.InvokeRequired) overlay.BeginInvoke(action); else action(); }
            catch (InvalidOperationException) { }
        }
        void RefreshStatus() { if (form != null && !form.IsDisposed) form.UpdateStatus(); }
        async void StartModel() { await RestartModel(); }
        void InvalidateRequests() { requestVersion++; queuedText = null; }
        void QueueText(string text, long context)
        {
            if (!Settings.InputEnabled || context != monitor.ContextVersion || String.IsNullOrWhiteSpace(text)) return;
            LastInputText = text; LastCapturedAt = DateTime.Now; CaptureCount++;
            queuedText = text; queuedContext = context; requestVersion++;
            RefreshStatus();
            if (Client.IsReady && !analyzing) ProcessQueue();
        }
        async void ProcessQueue()
        {
            analyzing = true;
            try
            {
                while (queuedText != null && !exiting && Client.IsReady)
                {
                    string text = queuedText; long context = queuedContext; long version = requestVersion; queuedText = null;
                    if (context != monitor.ContextVersion) continue;
                    activeContext = context; SentCount++; DecisionSummary = "Laya가 최근 " + text.Length + "자를 분석 중…"; RefreshStatus();
                    var choices = Settings.Emotions.Select(x => new EmotionDefinition { Id = x.Id, Name = x.Name, Description = x.Description, ImagePath = x.ImagePath }).ToList();
                    var result = await Client.ClassifyAsync(text, choices, Settings.ClassificationPrompt, "typing"); text = null;
                    if (result != null) { ResponseCount++; RecordDecision(result); }
                    else { DecisionSummary = "분류 응답을 받지 못했어요. 설정에서 감정 모델을 다시 시작해 주세요."; RefreshStatus(); }
                    if (!exiting && result != null && version == requestVersion && context == monitor.ContextVersion && Settings.InputEnabled && Settings.Emotions.Any(x => x.Id == result.Emotion)) ShowEmotion(result.Emotion);
                }
            }
            catch (Exception) { ModelStatus = "감정을 분석하지 못했어요. Laya 연결을 확인해 주세요."; RefreshStatus(); }
            finally { analyzing = false; activeContext = -1; }
        }
        public async Task<ClassificationResult> Analyze(string text)
        {
            if (!Client.IsReady) throw new InvalidOperationException("Laya가 연결되지 않았어요. 먼저 모델을 연결해 주세요.");
            InvalidateRequests();
            activeContext = -1; LastInputText = text; LastCapturedAt = DateTime.Now; SentCount++; RefreshStatus();
            long version = requestVersion;
            var choices = Settings.Emotions.Select(x => new EmotionDefinition { Id = x.Id, Name = x.Name, Description = x.Description, ImagePath = x.ImagePath }).ToList();
            var result = await Client.ClassifyAsync(text, choices, Settings.ClassificationPrompt, "manual");
            if (result == null) throw new InvalidOperationException("모델이 다른 문장을 분석 중이거나 응답하지 못했어요. 잠시 뒤 다시 시도해 주세요.");
            ResponseCount++; RecordDecision(result);
            if (version == requestVersion && !exiting) ShowEmotion(result.Emotion);
            return result;
        }
        void RecordDecision(ClassificationResult result)
        {
            string raw = result.RawEmotion ?? result.Emotion;
            if (result.Confidence < Settings.MinConfidence && Settings.Emotions.Any(x => x.Id == "neutral")) result.Emotion = "neutral";
            var label = Settings.Emotions.Find(x => x.Id == result.Emotion);
            DecisionSummary = (label == null ? result.Emotion : label.Name) + " · " + result.Confidence.ToString("P0") + " · " + result.ElapsedMs.ToString("0") + " ms · " + result.Device;
            if (raw != result.Emotion) DecisionSummary += "\n확신이 낮아 평온으로 표시 (모델 선택: " + raw + ")";
            RefreshStatus();
        }
        public void PreviewEmotion(string id) { InvalidateRequests(); ShowEmotion(id); }
        public async Task DemoPaws()
        {
            for (int i = 0; i < 12 && !exiting; i++) { overlay.Hit(32); await Task.Delay(140); }
        }
        public void PruneSprites()
        {
            Sprites.Prune(Settings.Emotions.SelectMany(x => new[] { x.ImagePath, x.LeftImagePath, x.RightImagePath, x.BothImagePath }));
        }
        void ShowEmotion(string id)
        {
            lastEmotionId = id; lastEmotion = DateTime.UtcNow; overlay.SetEmotion(id);
            idle.Stop();
            if (id != "neutral") { idle.Interval = Settings.HoldSeconds * 1000; idle.Start(); }
        }
        protected override void ExitThreadCore()
        {
            if (exiting) return;
            exiting = true; idle.Stop(); saveDelay.Stop(); InvalidateRequests();
            monitor.Dispose(); Client.Dispose(); tray.Visible = false; tray.Dispose();
            if (form != null) form.Close();
            Settings.X = overlay.Left; Settings.Y = overlay.Top; SaveSettings(); overlay.Close(); Sprites.Dispose(); AppIcon.Dispose(); idle.Dispose(); saveDelay.Dispose();
            base.ExitThreadCore();
        }
    }
}
