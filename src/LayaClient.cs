using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EmotionCat
{
    public sealed class ClassificationResult
    {
        public string Emotion { get; set; }
        public double Confidence { get; set; }
        public double ElapsedMs { get; set; }
        public string RawEmotion { get; set; }
        public Dictionary<string, double> Probabilities { get; set; }
        public string Device { get; set; }
        public string Model { get; set; }
        public string Source { get; set; }
    }

    /// Runs Laya inside this process with ONNX Runtime. No Python, no local server, no network.
    public sealed class LayaClient : IDisposable
    {
        private readonly object gate = new object();
        private LayaEngine engine;
        private CancellationTokenSource session;
        private string status = "Laya 준비 전";
        private volatile bool ready;
        private int classifying;
        private bool disposed;
        private int requests, successes;
        private readonly Func<LayaEngine> createEngine;

        public event Action<string> StatusChanged;
        public event Action HealthChanged;
        public event Action<string> InferenceUnavailable;
        public string DeviceDescription { get; private set; }
        public string Diagnostics { get; private set; }
        public string Status { get { lock (gate) return status; } }
        public bool IsReady { get { return ready; } }
        public string UnavailableReason { get; private set; }

        public LayaClient() : this(null) { }
        internal LayaClient(Func<LayaEngine> engineFactory) { createEngine = engineFactory; }

        public async Task StartAsync(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            CancellationTokenSource current;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("LayaClient");
                if (session != null) return;
                session = new CancellationTokenSource();
                current = session;
                UnavailableReason = null;
            }
            SetStatus("감정 모델 불러오는 중…", false, current);
            try
            {
                if (createEngine == null && !LayaEngine.IsInstalled) throw new FileNotFoundException("GPU 감정 모델 파일이 없습니다. EmotionCat을 다시 설치해 주세요.");
                var emotions = settings.Emotions.ToList();
                string prompt = settings.ClassificationPrompt;
                LayaEngine loaded = await Task.Run(() =>
                {
                    var created = createEngine == null ? new LayaEngine(LayaEngine.ModelDirectory, 1) : createEngine();
                    try { created.Classify(created.Build("안녕하세요", emotions, prompt)); }  // warm up kernels without user text
                    catch { created.Dispose(); throw; }
                    return created;
                });
                lock (gate)
                {
                    if (session != current || current.IsCancellationRequested) { loaded.Dispose(); return; }
                    engine = loaded;
                }
                DeviceDescription = "GPU · DirectML";
                Diagnostics = "요청 0 · 완료 0";
                SetStatus("연결됨 · " + DeviceDescription, true, current);
                var handler = HealthChanged; if (handler != null) handler();
            }
            catch (DllNotFoundException)
            {
                DisableInference("GPU 실행 파일을 불러올 수 없어 감정 분석을 껐습니다. 앱을 다시 설치해 주세요.", current);
            }
            catch (Exception ex)
            {
                DisableInference("GPU를 사용할 수 없어 감정 분석을 껐습니다. GPU·드라이버·모델을 확인해 주세요.\n" + ShortError(ex.Message), current);
            }
        }

        public Task<ClassificationResult> ClassifyAsync(string text, List<EmotionDefinition> emotions)
        {
            return ClassifyAsync(text, emotions, AppSettings.DefaultPrompt, "manual");
        }

        public async Task<ClassificationResult> ClassifyAsync(string text, List<EmotionDefinition> emotions, string instructions, string source = "manual")
        {
            if (!ready || String.IsNullOrWhiteSpace(text)) return null;
            if (emotions == null || emotions.Count < 2 || emotions.Count > 16)
                throw new ArgumentException("감정은 2~16개를 설정하세요.", "emotions");
            if (String.IsNullOrWhiteSpace(instructions)) instructions = AppSettings.DefaultPrompt;
            if (Interlocked.CompareExchange(ref classifying, 1, 0) != 0) return null;
            LayaEngine current;
            CancellationTokenSource currentSession;
            lock (gate) { current = engine; currentSession = session; }
            try
            {
                if (current == null || currentSession == null || currentSession.IsCancellationRequested) return null;
                string clipped = text.Length > 1000 ? text.Substring(text.Length - 1000) : text;
                var choices = emotions.ToList();
                string prompt = instructions.Trim();
                Interlocked.Increment(ref requests);
                var watch = Stopwatch.StartNew();
                bool profanity = KoreanEmotionRules.HasProfanity(clipped);
                if (profanity && !choices.Any(e => e.Id == "angry")) throw new InvalidOperationException("욕설에 사용할 화남 표정이 없습니다.");
                string explicitEmotion = KoreanEmotionRules.ExplicitEmotion(clipped);
                if (!choices.Any(e => e.Id == explicitEmotion)) explicitEmotion = null;
                double[] probabilities = explicitEmotion != null ? choices.Select(e => e.Id == explicitEmotion ? 1.0 : 0.0).ToArray()
                    : await Task.Run(() => current.Classify(current.Build(KoreanEmotionRules.PrepareText(clipped), choices, prompt)));
                watch.Stop();
                if (currentSession.IsCancellationRequested) return null;
                int best = 0;
                for (int i = 1; i < probabilities.Length; i++) if (probabilities[i] > probabilities[best]) best = i;
                var byId = new Dictionary<string, double>();
                for (int i = 0; i < probabilities.Length && i < choices.Count; i++) byId[choices[i].Id] = Math.Round(probabilities[i], 4);
                Interlocked.Increment(ref successes);
                double elapsed = Math.Round(watch.Elapsed.TotalMilliseconds, 1);
                Diagnostics = "요청 " + requests + " · 완료 " + successes + " · 최근 " + clipped.Length + "자 / " + elapsed.ToString(CultureInfo.InvariantCulture) + " ms";
                var handler = HealthChanged; if (handler != null) handler();
                return new ClassificationResult
                {
                    Emotion = choices[best].Id, RawEmotion = choices[best].Id, Confidence = Math.Round(LayaEngine.Confidence(probabilities), 4),
                    ElapsedMs = elapsed, Probabilities = byId, Device = "directml", Model = "multilingual", Source = profanity ? "profanity-rule" : explicitEmotion != null ? "korean-rule" : "gpu-model"
                };
            }
            catch (ObjectDisposedException) { return null; }
            catch (Exception ex)
            {
                if (currentSession != null && !currentSession.IsCancellationRequested)
                    DisableInference("GPU 분석에 실패하여 감정 분석을 껐습니다. 설정에서 GPU 연결을 다시 시도해 주세요.\n" + ShortError(ex.Message), currentSession);
                return null;
            }
            finally { Interlocked.Exchange(ref classifying, 0); }
        }

        internal static ClassificationResult ParseClassification(Dictionary<string, object> response, List<EmotionDefinition> emotions)
        {
            object value;
            string id = response.TryGetValue("emotion", out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : "";
            if (!emotions.Any(e => e.Id == id)) throw new InvalidOperationException("Unknown emotion returned.");
            double confidence;
            if (!response.TryGetValue("confidence", out value) || !Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out confidence)
                || Double.IsNaN(confidence) || Double.IsInfinity(confidence) || confidence < 0 || confidence > 1)
                throw new InvalidOperationException("Invalid confidence returned.");
            double elapsed = 0;
            if (response.TryGetValue("elapsed_ms", out value))
            {
                if (!Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out elapsed)
                    || Double.IsNaN(elapsed) || Double.IsInfinity(elapsed) || elapsed < 0)
                    throw new InvalidOperationException("Invalid elapsed time returned.");
            }
            var probabilities = new Dictionary<string, double>();
            object raw;
            if (response.TryGetValue("probabilities", out raw))
            {
                var values = raw as Dictionary<string, object>;
                if (values != null) foreach (var pair in values)
                {
                    double probability;
                    if (emotions.Any(e => e.Id == pair.Key) && Double.TryParse(Convert.ToString(pair.Value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out probability) && probability >= 0 && probability <= 1)
                        probabilities[pair.Key] = probability;
                }
            }
            return new ClassificationResult { Emotion = id, RawEmotion = id, Confidence = confidence, ElapsedMs = elapsed, Probabilities = probabilities, Device = response.TryGetValue("device", out value) ? Convert.ToString(value) : "", Model = response.TryGetValue("model", out value) ? Convert.ToString(value) : "" };
        }

        private void SetStatus(string value, bool isReady, CancellationTokenSource current)
        {
            Action<string> handler;
            lock (gate)
            {
                if (session != current) return;
                ready = isReady;
                if (status == value) return;
                status = value;
                handler = StatusChanged;
            }
            if (handler != null) handler(value);
        }

        private void ReleaseSession(CancellationTokenSource current)
        {
            LayaEngine released;
            lock (gate)
            {
                if (current != null && session != current) return;
                if (session != null) session.Cancel();
                session = null;
                ready = false;
                released = engine;
                engine = null;
            }
            // Waits for an in-flight inference; the model memory is freed immediately afterwards.
            if (released != null) released.Dispose();
        }

        private void DisableInference(string reason, CancellationTokenSource current)
        {
            lock (gate)
            {
                if (session != current || current.IsCancellationRequested) return;
                UnavailableReason = reason;
            }
            SetStatus(reason, false, current);
            ReleaseSession(current);
            var handler = InferenceUnavailable; if (handler != null) handler(reason);
        }

        public void Stop()
        {
            ReleaseSession(null);
            lock (gate) status = "Laya 연결 해제됨";
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }

        private static string ShortError(string error)
        {
            if (String.IsNullOrWhiteSpace(error)) return "EmotionCat을 다시 설치해 주세요.";
            error = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return error.Length > 240 ? error.Substring(0, 240) + "…" : error;
        }
    }
}
