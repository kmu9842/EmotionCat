using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EmotionCat
{
    internal sealed class SettingsForm : Form
    {
        static readonly Color Ink = Color.FromArgb(54, 46, 49), Muted = Color.FromArgb(137, 119, 121), Accent = Color.FromArgb(193, 103, 115), Canvas = Color.FromArgb(249, 246, 243);
        readonly AppController app;
        readonly AppSettings settings;
        readonly Label status, current;
        readonly Panel pages;
        readonly Panel[] sections = new Panel[3];
        readonly Button[] navigation = new Button[3];
        readonly ListBox emotions;
        readonly CatPreview preview;
        readonly TextBox emotionName, description;
        readonly Label imageName;
        readonly ComboBox model;
        readonly ComboBox pose;
        readonly Label modelStatus, inputStatus, testResult;
        readonly Label pipeline;
        readonly TextBox testInput;
        readonly CheckBox enabled;
        readonly Button testButton;
        EmotionDefinition selected;
        bool loading;
        public SettingsForm(AppController app)
        {
            this.app = app; settings = app.Settings;
            Text = "EmotionCat · 설정";
            Font = new Font("Malgun Gothic", 9.5f);
            BackColor = Canvas; ForeColor = Ink;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(900, 650); MinimumSize = new Size(660, 440);
            AutoScroll = true; AutoScrollMinSize = new Size(900, 650);
            MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Icon = app.AppIcon;
            Label title = LabelAt(this, "EmotionCat", 28, 19, 300, 36, 23, true);
            title.Font = new Font("Segoe UI", 24, FontStyle.Bold);
            status = LabelAt(this, "●  연결 준비 중", 608, 31, 263, 27, 10, true); status.TextAlign = ContentAlignment.MiddleRight;
            string[] titles = { "01   표정 연결", "02   Laya · 입력", "03   모양" };
            for (int i = 0; i < 3; i++)
            {
                int index = i; navigation[i] = ButtonAt(this, titles[i], 28 + i * 281, 105, 270, 39, delegate { ShowPage(index); }, false);
            }
            pages = new Panel { Location = new Point(28, 164), Size = new Size(844, 426), BackColor = Canvas }; Controls.Add(pages);
            for (int i = 0; i < 3; i++) { sections[i] = new Panel { Dock = DockStyle.Fill, BackColor = Canvas }; pages.Controls.Add(sections[i]); }

            Panel left = Card(sections[0], 0, 0, 277, 426);
            LabelAt(left, "미리보기", 18, 17, 230, 24, 11, true);
            preview = new CatPreview { Location = new Point(13, 54), Size = new Size(251, 240) }; left.Controls.Add(preview);
            current = LabelAt(left, "기본", 18, 307, 241, 28, 14, true); current.TextAlign = ContentAlignment.MiddleCenter;
            ButtonAt(left, "양발 동작 확인", 26, 352, 225, 36, async delegate { await app.DemoPaws(); }, false);
            Panel right = Card(sections[0], 291, 0, 553, 426);
            LabelAt(right, "감정 이미지", 20, 17, 400, 28, 13, true);
            emotions = new ListBox { Location = new Point(20, 61), Size = new Size(139, 287), BorderStyle = BorderStyle.None, BackColor = Canvas, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 35, IntegralHeight = false };
            emotions.DrawItem += DrawEmotion; emotions.SelectedIndexChanged += delegate { LoadEmotion(); }; right.Controls.Add(emotions);
            ButtonAt(right, "+ 추가", 20, 365, 66, 34, delegate { AddEmotion(); }, false);
            ButtonAt(right, "삭제", 92, 365, 66, 34, delegate { RemoveEmotion(); }, false);
            LabelAt(right, "감정 이름", 181, 58, 330, 22, 9, true);
            emotionName = TextAt(right, 181, 84, 349, 30, false); emotionName.MaxLength = 40;
            LabelAt(right, "분류 기준", 181, 132, 330, 22, 9, true);
            description = TextAt(right, 181, 158, 349, 71, true); description.MaxLength = 400;
            pose = new ComboBox { Location = new Point(181, 263), Width = 151, DropDownStyle = ComboBoxStyle.DropDownList };
            pose.Items.AddRange(new object[] { "기본 · 양발 내려놓기", "왼발 들기", "오른발 들기", "양발 들기" }); pose.SelectedIndex = 0;
            pose.SelectedIndexChanged += delegate { ShowPosePreview(); }; right.Controls.Add(pose);
            imageName = LabelAt(right, "", 342, 267, 188, 25, 8.5f, false); imageName.AutoEllipsis = true;
            ButtonAt(right, "선택한 동작 이미지…", 181, 308, 168, 37, delegate { ChooseImage(); }, false);
            ButtonAt(right, "기본 이미지", 361, 308, 169, 37, delegate { ResetImage(); }, false);
            ButtonAt(right, "이 감정 저장", 181, 365, 349, 34, delegate { SaveEmotion(); }, true);
            right.AllowDrop = true;
            right.DragEnter += delegate(object sender, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            right.DragDrop += delegate(object sender, DragEventArgs e) { var files = (string[])e.Data.GetData(DataFormats.FileDrop); if (files.Length > 0) ImportImage(files[0]); };

            Panel connection = Card(sections[1], 0, 0, 410, 426);
            LabelAt(connection, "Laya", 20, 17, 367, 29, 13, true);
            model = new ComboBox { Location = new Point(20, 93), Width = 370, DropDownStyle = ComboBoxStyle.DropDownList };
            model.Width = 224;
            model.Items.Add("Laya multilingual · 322M"); model.SelectedIndex = 0; model.Enabled = false; connection.Controls.Add(model);
            var inputMode = new ComboBox { Location = new Point(253, 93), Width = 137, DropDownStyle = ComboBoxStyle.DropDownList };
            inputMode.Items.AddRange(new object[] { "입력: 한/영 자동", "입력: 한글 두벌식", "입력: 영문/키보드" });
            inputMode.SelectedIndex = settings.InputMode == "korean" ? 1 : settings.InputMode == "latin" ? 2 : 0;
            inputMode.SelectedIndexChanged += delegate { settings.InputMode = inputMode.SelectedIndex == 1 ? "korean" : inputMode.SelectedIndex == 2 ? "latin" : "auto"; app.ApplySettings(); };
            connection.Controls.Add(inputMode);
            modelStatus = LabelAt(connection, "모델 연결 대기", 20, 131, 370, 43, 9, false); modelStatus.ForeColor = Muted;
            ButtonAt(connection, "감정 모델 다시 시작", 20, 181, 220, 37, async delegate { settings.Model = "multilingual"; app.SaveSettings(); await app.RestartModel(); }, true);
            enabled = new CheckBox { Text = "다른 앱에서 친 글로 감정 바꾸기", Location = new Point(20, 246), Size = new Size(371, 27), Checked = settings.InputEnabled };
            enabled.CheckedChanged += delegate { if (!loading) { settings.InputEnabled = enabled.Checked; app.ApplySettings(); } }; connection.Controls.Add(enabled);
            inputStatus = LabelAt(connection, "입력 대기", 20, 278, 371, 37, 8.5f, false); inputStatus.ForeColor = Muted;
            ButtonAt(connection, "입력 확인 · 분류 지시문", 20, 321, 370, 35, delegate { new DiagnosticsForm(app).Show(this); }, false);
            LabelAt(connection, "암호 필드·제외 앱 제외 · 입력 내용 저장 안 함", 20, 374, 372, 44, 8.5f, false).ForeColor = Muted;
            Panel tester = Card(sections[1], 424, 0, 420, 426);
            LabelAt(tester, "분류 테스트", 20, 17, 380, 28, 13, true);
            testInput = TextAt(tester, 20, 93, 380, 92, true); testInput.MaxLength = 1000;
            testButton = ButtonAt(tester, "분류", 20, 200, 380, 36, async delegate { await TestSentence(); }, true);
            testResult = LabelAt(tester, "분류 대기", 20, 250, 380, 63, 10, false);
            pipeline = LabelAt(tester, "입력 감지 대기", 20, 325, 380, 64, 8.5f, false); pipeline.ForeColor = Muted;
            LabelAt(tester, "전역 문자 입력 → Laya → 표정", 20, 394, 386, 21, 8, false).ForeColor = Muted;

            Panel appearance = Card(sections[2], 0, 0, 410, 426);
            LabelAt(appearance, "표시", 20, 17, 370, 28, 13, true);
            Label sizeLabel = LabelAt(appearance, "고양이 크기  " + settings.Size + " px", 20, 71, 370, 25, 10, true);
            var size = new TrackBar { Minimum = 180, Maximum = 600, TickFrequency = 60, SmallChange = 10, LargeChange = 30, Value = Math.Max(180, Math.Min(600, settings.Size)), Location = new Point(14, 108), Size = new Size(381, 51) };
            size.ValueChanged += delegate { settings.Size = size.Value; sizeLabel.Text = "고양이 크기  " + size.Value + " px"; app.ApplySettings(false); }; appearance.Controls.Add(size);
            var top = CheckAt(appearance, "항상 다른 창 위에 표시", 20, 185, settings.AlwaysOnTop);
            top.CheckedChanged += delegate { settings.AlwaysOnTop = top.Checked; app.ApplySettings(); };
            var click = CheckAt(appearance, "고양이를 클릭해도 아래 창으로 통과", 20, 227, settings.ClickThrough);
            click.CheckedChanged += delegate { settings.ClickThrough = click.Checked; app.ApplySettings(); };
            var snap = CheckAt(appearance, "작업표시줄에 맞춤", 20, 269, settings.SnapToTaskbar);
            snap.CheckedChanged += delegate { settings.SnapToTaskbar = snap.Checked; app.ApplySettings(); };
            ButtonAt(appearance, "화면 오른쪽 아래로 이동", 20, 316, 370, 38, delegate { settings.X = -1; settings.Y = -1; app.ApplySettings(); }, false);
            Panel behavior = Card(sections[2], 424, 0, 420, 426);
            LabelAt(behavior, "입력", 20, 17, 380, 28, 13, true);
            LabelAt(behavior, "문자 입력 후 분석 대기", 20, 73, 220, 28, 10, false);
            var debounce = new NumericUpDown { Minimum = 150, Maximum = 5000, Increment = 50, Value = Math.Max(150, Math.Min(5000, settings.DebounceMilliseconds)), Location = new Point(247, 73), Width = 100 }; behavior.Controls.Add(debounce);
            LabelAt(behavior, "ms", 355, 74, 44, 25, 9, false);
            debounce.ValueChanged += delegate { settings.DebounceMilliseconds = (int)debounce.Value; app.ApplySettings(); };
            LabelAt(behavior, "표정 유지 시간", 20, 119, 220, 28, 10, false);
            var hold = new NumericUpDown { Minimum = 1, Maximum = 60, Value = Math.Max(1, Math.Min(60, settings.HoldSeconds)), Location = new Point(247, 119), Width = 100 }; behavior.Controls.Add(hold);
            LabelAt(behavior, "초", 355, 120, 44, 25, 9, false);
            hold.ValueChanged += delegate { settings.HoldSeconds = (int)hold.Value; app.ApplySettings(); };
            LabelAt(behavior, "입력을 읽지 않을 앱 (프로세스 이름)", 20, 180, 380, 25, 9, true);
            var excluded = TextAt(behavior, 20, 216, 380, 84, true); excluded.Text = String.Join(", ", settings.ExcludedProcesses);
            ButtonAt(behavior, "제외 목록 저장", 20, 315, 380, 37, delegate { settings.ExcludedProcesses = excluded.Text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray(); app.ApplySettings(); }, false);
            LabelAt(behavior, "예: 1Password, KeePass, WindowsTerminal", 20, 372, 385, 25, 8.5f, false).ForeColor = Muted;

            ButtonAt(this, "닫기", 754, 603, 118, 33, delegate { Close(); }, true);
            FormClosing += delegate { testInput.Clear(); };
            RefreshEmotions("neutral"); ShowPage(0); UpdateStatus();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (Height > area.Height - 20) Height = area.Height - 20;
            if (Width > area.Width - 20) Width = area.Width - 20;
            Top = Math.Max(area.Top, Math.Min(Top, area.Bottom - Height));
            Left = Math.Max(area.Left, Math.Min(Left, area.Right - Width));
        }
        void ShowPage(int index)
        {
            for (int i = 0; i < sections.Length; i++) sections[i].Visible = i == index;
            sections[index].BringToFront();
            for (int i = 0; i < navigation.Length; i++) { navigation[i].BackColor = i == index ? Ink : Color.White; navigation[i].ForeColor = i == index ? Color.White : Muted; }
        }
        void DrawEmotion(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool active = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(active ? Color.FromArgb(245, 222, 224) : Canvas)) e.Graphics.FillRectangle(b, e.Bounds);
            var emotion = (EmotionDefinition)emotions.Items[e.Index];
            TextRenderer.DrawText(e.Graphics, emotion.Name, Font, new Rectangle(e.Bounds.X + 12, e.Bounds.Y, e.Bounds.Width - 16, e.Bounds.Height), active ? Accent : Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        void RefreshEmotions(string id)
        {
            emotions.Items.Clear(); foreach (var emotion in settings.Emotions) emotions.Items.Add(emotion);
            int index = settings.Emotions.FindIndex(x => x.Id == id); emotions.SelectedIndex = Math.Max(0, index);
        }
        void LoadEmotion()
        {
            selected = emotions.SelectedItem as EmotionDefinition; if (selected == null) return;
            emotionName.Text = selected.Name; description.Text = selected.Description;
            current.Text = selected.Name;
            app.PreviewEmotion(selected.Id); ShowPosePreview();
        }
        string SelectedImage()
        {
            if (selected == null) return null;
            switch (pose.SelectedIndex) { case 1: return selected.LeftImagePath; case 2: return selected.RightImagePath; case 3: return selected.BothImagePath; default: return selected.ImagePath; }
        }
        void SetSelectedImage(string path)
        {
            switch (pose.SelectedIndex) { case 1: selected.LeftImagePath = path; break; case 2: selected.RightImagePath = path; break; case 3: selected.BothImagePath = path; break; default: selected.ImagePath = path; break; }
        }
        void ShowPosePreview()
        {
            if (selected == null) return;
            string path = SelectedImage();
            imageName.Text = String.IsNullOrWhiteSpace(path) ? "미지정 · 기본 이미지 사용" : Path.GetFileName(path);
            preview.Cat = app.Sprites.Get(path) ?? app.Sprites.Get(selected.ImagePath); preview.Invalidate();
        }
        public void ShowLiveFrame(string path, string id)
        {
            if (IsDisposed) return;
            preview.Cat = app.Sprites.Get(path); preview.Invalidate();
            var emotion = settings.Emotions.Find(x => x.Id == id); if (emotion != null) current.Text = emotion.Name;
        }
        void SaveEmotion()
        {
            if (selected == null) return;
            if (String.IsNullOrWhiteSpace(emotionName.Text) || String.IsNullOrWhiteSpace(description.Text)) { MessageBox.Show(this, "감정 이름과 느낌 설명을 입력해 주세요.", "표정 연결"); return; }
            selected.Name = emotionName.Text.Trim(); selected.Description = description.Text.Trim(); app.SaveSettings(); emotions.Invalidate(); current.Text = selected.Name;
        }
        void ChooseImage()
        {
            using (var dialog = new OpenFileDialog { Title = "이 감정에 사용할 이미지", Filter = "이미지|*.png;*.jpg;*.jpeg;*.bmp;*.gif", CheckFileExists = true }) if (dialog.ShowDialog(this) == DialogResult.OK) ImportImage(dialog.FileName);
        }
        void ImportImage(string file)
        {
            if (selected == null) return;
            try { SetSelectedImage(AppSettings.ImportImage(file)); app.SaveSettings(); LoadEmotion(); app.PruneSprites(); }
            catch (Exception ex) { MessageBox.Show(this, "이미지를 열 수 없어요.\n" + ex.Message, "이미지 가져오기"); }
        }
        void ResetImage()
        {
            if (selected == null) return;
            var original = AppSettings.DefaultEmotions().Find(x => x.Id == selected.Id);
            string path = "assets/frames/neutral_idle.png";
            if (original != null) { switch (pose.SelectedIndex) { case 1: path = original.LeftImagePath; break; case 2: path = original.RightImagePath; break; case 3: path = original.BothImagePath; break; default: path = original.ImagePath; break; } }
            SetSelectedImage(path); app.SaveSettings(); LoadEmotion(); app.PruneSprites();
        }
        void AddEmotion()
        {
            if (settings.Emotions.Count >= 16) { MessageBox.Show(this, "감정은 최대 16개까지 추가할 수 있어요.", "표정 연결"); return; }
            var emotion = new EmotionDefinition { Id = "custom_" + Guid.NewGuid().ToString("N").Substring(0, 8), Name = "새 감정", Description = "감정 설명", ImagePath = "assets/frames/neutral_idle.png", LeftImagePath = "assets/frames/neutral_left.png", RightImagePath = "assets/frames/neutral_right.png", BothImagePath = "assets/frames/neutral_both.png" };
            settings.Emotions.Add(emotion); app.SaveSettings(); RefreshEmotions(emotion.Id); emotionName.Focus(); emotionName.SelectAll();
        }
        void RemoveEmotion()
        {
            if (selected == null) return;
            if (selected.Id == "neutral") { MessageBox.Show(this, "기본 감정은 대기 표정으로 사용해요.", "표정 연결"); return; }
            if (settings.Emotions.Count <= 2) { MessageBox.Show(this, "Laya에는 감정이 2개 이상 필요해요.", "표정 연결"); return; }
            settings.Emotions.Remove(selected); app.SaveSettings(); RefreshEmotions("neutral");
        }
        async Task TestSentence()
        {
            if (String.IsNullOrWhiteSpace(testInput.Text)) { testResult.Text = "테스트할 문장을 입력해 주세요."; return; }
            testButton.Enabled = false; testResult.Text = "Laya가 표정을 고르고 있어요…";
            try
            {
                var result = await app.Analyze(testInput.Text);
                var emotion = settings.Emotions.Find(x => x.Id == result.Emotion);
                testResult.Text = "→  " + (emotion == null ? result.Emotion : emotion.Name) + "\n모델 점수 " + result.Confidence.ToString("P0") + "  ·  " + result.ElapsedMs.ToString("0") + " ms";
            }
            catch (Exception ex) { if (!IsDisposed) testResult.Text = "연결 상태를 확인해 주세요.\n" + ex.Message; }
            finally { if (!IsDisposed) testButton.Enabled = true; }
        }
        public void UpdateStatus()
        {
            if (IsDisposed) return;
            status.Text = app.Client.IsReady ? "●  Laya 연결됨 · 내 PC에서 실행" : "●  Laya 연결 대기";
            status.ForeColor = app.Client.IsReady ? Color.FromArgb(71, 128, 107) : Muted;
            modelStatus.Text = app.ModelStatus;
            inputStatus.Text = app.InputStatus;
            pipeline.Text = app.PipelineSummary + "\n" + app.DecisionSummary;
            if (app.Client.IsReady) status.Text = "●  " + app.Client.DeviceDescription;
            loading = true; enabled.Checked = settings.InputEnabled; loading = false;
        }
        static Panel Card(Control owner, int x, int y, int w, int h) { var panel = new Panel { Location = new Point(x, y), Size = new Size(w, h), BackColor = Color.White }; owner.Controls.Add(panel); return panel; }
        static Label LabelAt(Control owner, string text, int x, int y, int w, int h, float size, bool bold) { var label = new Label { Text = text, Location = new Point(x, y), Size = new Size(w, h), Font = new Font("Malgun Gothic", size, bold ? FontStyle.Bold : FontStyle.Regular), ForeColor = Ink }; owner.Controls.Add(label); return label; }
        static Button ButtonAt(Control owner, string text, int x, int y, int w, int h, EventHandler handler, bool primary) { var button = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Canvas, ForeColor = primary ? Color.White : Ink, Cursor = Cursors.Hand, UseVisualStyleBackColor = false }; button.FlatAppearance.BorderSize = 0; button.Click += handler; owner.Controls.Add(button); return button; }
        static TextBox TextAt(Control owner, int x, int y, int w, int h, bool multiline) { var input = new TextBox { Location = new Point(x, y), Size = new Size(w, h), Multiline = multiline, BorderStyle = BorderStyle.FixedSingle, BackColor = Canvas, ForeColor = Ink, ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None }; owner.Controls.Add(input); return input; }
        static CheckBox CheckAt(Control owner, string text, int x, int y, bool value) { var check = new CheckBox { Text = text, Checked = value, Location = new Point(x, y), Size = new Size(376, 29) }; owner.Controls.Add(check); return check; }
    }
}
