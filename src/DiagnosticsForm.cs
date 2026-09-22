using System;
using System.Drawing;
using System.Windows.Forms;

namespace EmotionCat
{
    internal sealed class DiagnosticsForm : Form
    {
        readonly Timer refresh = new Timer { Interval = 250 };
        public DiagnosticsForm(AppController app)
        {
            Text = "입력 확인 · 분류 지시문"; Font = new Font("Malgun Gothic", 9);
            ClientSize = new Size(620, 585); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent; BackColor = Color.FromArgb(249, 246, 243);
            var status = new Label { Bounds = new Rectangle(20, 15, 580, 66) }; Controls.Add(status);
            Controls.Add(new Label { Text = "최근 감지 / 전달 문장 (메모리에서만 표시)", Bounds = new Rectangle(20, 88, 580, 25) });
            var input = new TextBox { Bounds = new Rectangle(20, 118, 580, 79), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical }; Controls.Add(input);
            Controls.Add(new Label { Text = "Laya에 전달할 분류 지시문", Bounds = new Rectangle(20, 218, 580, 25) });
            var prompt = new TextBox { Bounds = new Rectangle(20, 250, 580, 153), Multiline = true, ScrollBars = ScrollBars.Vertical, Text = app.Settings.ClassificationPrompt, MaxLength = 1000 }; Controls.Add(prompt);
            Controls.Add(new Label { Text = "점수가 이 값보다 낮으면 평온으로 표시", Bounds = new Rectangle(20, 424, 395, 27) });
            var confidence = new NumericUpDown { Bounds = new Rectangle(442, 421, 90, 30), Minimum = 0, Maximum = 1, DecimalPlaces = 2, Increment = .05m, Value = (decimal)app.Settings.MinConfidence }; Controls.Add(confidence);
            Controls.Add(new Label { Text = "점수는 모델의 확신 정도이며 정답률이 아닙니다.\n감정 이름·설명도 각 요청에 함께 전달됩니다.", Bounds = new Rectangle(20, 465, 580, 45), ForeColor = Color.DimGray });
            var reset = new Button { Text = "기본 지시문", Bounds = new Rectangle(20, 525, 140, 36) }; reset.Click += delegate { prompt.Text = AppSettings.DefaultPrompt; confidence.Value = .25m; }; Controls.Add(reset);
            var save = new Button { Text = "저장", Bounds = new Rectangle(459, 525, 140, 36) }; save.Click += delegate { if (String.IsNullOrWhiteSpace(prompt.Text)) return; app.Settings.ClassificationPrompt = prompt.Text.Trim(); app.Settings.MinConfidence = (double)confidence.Value; app.SaveSettings(); Close(); }; Controls.Add(save);
            refresh.Tick += delegate
            {
                status.Text = app.Client.DeviceDescription + "  ·  " + app.PipelineSummary + "\n" + app.InputStatus + "\n" + app.DecisionSummary;
                if (input.Text != app.LastInputText) input.Text = app.LastInputText;
            };
            refresh.Start();
            FormClosed += delegate { refresh.Dispose(); input.Clear(); };
        }
    }
}
