using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace EmotionCat
{
    public sealed class LayaSequence
    {
        public long[] InputIds;
        public long[] MarkerPositions;
    }

    /// In-process GPU-only Laya inference. CPU EP fallback is forbidden by the runtime.
    public sealed class LayaEngine : IDisposable
    {
        public const string ModelFile = "laya-multilingual-gpu.onnx";
        public const string TokenizerFile = "tokenizer.bin";
        private const int Cls = 2, Sep = 1, Mask = 4, MaxLen = 1024, MaxOptions = 16;
        private static readonly string[] InputNames = { "input_ids", "marker_pos", "marker_mask", "attention_mask" };
        private readonly OnnxRuntime ort = OnnxRuntime.Instance;
        private readonly object gate = new object();
        private IntPtr env, session, memoryInfo;
        private bool disposed;
        private readonly IntPtr[] inputNames, outputNames;
        public LayaTokenizer Tokenizer { get; private set; }

        public static string ModelDirectory { get { return Path.Combine(AppSettings.BaseDirectory, "model"); } }
        public static bool IsInstalled
        {
            get { return File.Exists(Path.Combine(ModelDirectory, ModelFile)) && File.Exists(Path.Combine(ModelDirectory, TokenizerFile)); }
        }

        public LayaEngine(string directory, int threads, string profilePath = null)
        {
            inputNames = InputNames.Select(n => Marshal.StringToHGlobalAnsi(n)).ToArray();
            outputNames = new[] { Marshal.StringToHGlobalAnsi("logits") };
            try
            {
                Tokenizer = new LayaTokenizer(Path.Combine(directory, TokenizerFile));
                env = ort.CreateEnv();
                memoryInfo = ort.CreateCpuMemoryInfo();
                session = ort.CreateSession(env, Path.Combine(directory, ModelFile), threads, profilePath);
            }
            catch { Dispose(); throw; }
        }

        public static List<string> Labels(IList<EmotionDefinition> emotions)
        {
            var labels = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in emotions)
            {
                string label = PyStrip(e.Description);
                if (label.Length == 0) label = PyStrip(e.Name);
                if (label.Length == 0) label = e.Id ?? "";
                if (label.Length > 160) label = label.Substring(0, 160);
                if (seen.Contains(label)) label += " (" + e.Id + ")";
                while (seen.Contains(label)) label += " ";
                seen.Add(label);
                labels.Add(label);
            }
            return labels;
        }

        private static string PyStrip(string value) { return (value ?? "").Trim(); }
        private static string Clean(string value) { return value.Replace("<mask>", " "); }

        public LayaSequence Build(string text, IList<EmotionDefinition> emotions, string instructions)
        {
            List<string> labels = Labels(emotions);
            int labelTokens = labels.Sum(l => Math.Min(49, 1 + Tokenizer.Encode(l).Count));
            int headLen = Math.Min(MaxLen - 128, Math.Max(192, Tokenizer.Encode(instructions).Count + labelTokens + 12));
            List<int> head = Tokenizer.Encode(Clean("choice question: " + instructions));
            var options = labels.Select(l => new[] { Mask }.Concat(Tokenizer.Encode(" " + Clean(l)).Take(48)).ToList()).ToList();
            int budget = headLen - options.Sum(o => o.Count);
            if (budget < 16)
            {
                int per = Math.Max(4, (headLen - 16) / options.Count);
                options = options.Select(o => o.Take(per).ToList()).ToList();
                budget = headLen - options.Sum(o => o.Count);
            }
            var ids = new List<int> { Cls };
            ids.AddRange(head.Take(Math.Max(8, budget)));
            ids.Add(Sep);
            var markers = new List<int>();
            foreach (var option in options) { markers.Add(ids.Count); ids.AddRange(option); }
            ids.Add(Sep);
            List<int> state = Tokenizer.Encode(Clean(text));
            int keep = MaxLen - headLen - 8;
            state = keep > 0 ? state.Skip(Math.Max(0, state.Count - keep)).ToList() : new List<int>();
            state = state.Take(Math.Max(0, MaxLen - ids.Count - 1)).ToList();
            ids.AddRange(state);
            ids.Add(Sep);
            return new LayaSequence
            {
                InputIds = ids.Take(MaxLen).Select(x => (long)x).ToArray(),
                MarkerPositions = markers.Where(m => m < MaxLen).Select(x => (long)x).ToArray()
            };
        }

        /// Returns probabilities in emotion order.
        public double[] Classify(LayaSequence sequence)
        {
            lock (gate)
            {
                if (session == IntPtr.Zero) throw new ObjectDisposedException("LayaEngine");
                if (sequence == null || sequence.InputIds == null || sequence.MarkerPositions == null
                    || sequence.InputIds.Length < 1 || sequence.InputIds.Length > MaxLen
                    || sequence.MarkerPositions.Length < 2 || sequence.MarkerPositions.Length > MaxOptions
                    || sequence.MarkerPositions.Any(p => p < 0 || p >= sequence.InputIds.Length))
                    throw new ArgumentException("문장 또는 감정 선택지 길이가 올바르지 않습니다.", "sequence");
                int k = sequence.MarkerPositions.Length;
                var tokens = Enumerable.Repeat((long)Sep, MaxLen).ToArray();
                var markers = new long[MaxOptions];
                var mask = new byte[MaxOptions];
                var attentionMask = new long[MaxLen];
                Array.Copy(sequence.InputIds, tokens, sequence.InputIds.Length);
                Array.Copy(sequence.MarkerPositions, markers, k);
                for (int i = 0; i < k; i++) mask[i] = 1;
                for (int i = 0; i < sequence.InputIds.Length; i++) attentionMask[i] = 1;
                var pins = new[] { GCHandle.Alloc(tokens, GCHandleType.Pinned), GCHandle.Alloc(markers, GCHandleType.Pinned), GCHandle.Alloc(mask, GCHandleType.Pinned), GCHandle.Alloc(attentionMask, GCHandleType.Pinned) };
                var inputs = new IntPtr[4];
                IntPtr output = IntPtr.Zero;
                try
                {
                    inputs[0] = ort.CreateTensor(memoryInfo, pins[0].AddrOfPinnedObject(), MaxLen * 8L, new long[] { 1, MaxLen }, OnnxRuntime.TensorInt64);
                    inputs[1] = ort.CreateTensor(memoryInfo, pins[1].AddrOfPinnedObject(), MaxOptions * 8L, new long[] { 1, MaxOptions }, OnnxRuntime.TensorInt64);
                    inputs[2] = ort.CreateTensor(memoryInfo, pins[2].AddrOfPinnedObject(), MaxOptions, new long[] { 1, MaxOptions }, OnnxRuntime.TensorBool);
                    inputs[3] = ort.CreateTensor(memoryInfo, pins[3].AddrOfPinnedObject(), MaxLen * 8L, new long[] { 1, MaxLen }, OnnxRuntime.TensorInt64);
                    output = ort.Run(session, inputNames, inputs, outputNames);
                    var logits = new float[k];
                    Marshal.Copy(ort.TensorData(output), logits, 0, k);
                    if (logits.Any(v => Single.IsNaN(v) || Single.IsInfinity(v))) throw new InvalidOperationException("GPU 감정 모델이 유효하지 않은 점수를 반환했습니다.");
                    double max = logits.Max();
                    double[] exp = logits.Select(l => Math.Exp(l - max)).ToArray();
                    double sum = exp.Sum();
                    return exp.Select(v => v / sum).ToArray();
                }
                finally
                {
                    ort.ReleaseValue(output);
                    foreach (var value in inputs) ort.ReleaseValue(value);
                    foreach (var pin in pins) pin.Free();
                }
            }
        }

        public static double Confidence(double[] p)
        {
            if (p.Length < 2) return 1.0;
            double entropy = -p.Sum(v => v * Math.Log(Math.Min(1.0, Math.Max(1e-12, v))));
            return Math.Min(1.0, Math.Max(0.0, 1.0 - entropy / Math.Log(p.Length)));
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                ort.ReleaseSession(session); session = IntPtr.Zero;
                ort.ReleaseMemoryInfo(memoryInfo); memoryInfo = IntPtr.Zero;
                ort.ReleaseEnv(env); env = IntPtr.Zero;
                if (inputNames != null) foreach (var p in inputNames) Marshal.FreeHGlobal(p);
                if (outputNames != null) foreach (var p in outputNames) Marshal.FreeHGlobal(p);
            }
        }
    }
}
