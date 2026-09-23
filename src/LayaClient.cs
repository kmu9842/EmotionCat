using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

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
    }

    public sealed class LayaClient : IDisposable
    {
        private const int Port = 47821;
        private readonly object gate = new object();
        private Process process;
        private CancellationTokenSource session;
        private string token;
        private string status = "Laya 준비 전";
        private volatile bool ready;
        private int classifying;
        private bool disposed;

        public event Action<string> StatusChanged;
        public event Action HealthChanged;
        public string DeviceDescription { get; private set; }
        public string Diagnostics { get; private set; }
        public string Status { get { lock (gate) return status; } }
        public bool IsReady { get { return ready; } }

        public async Task StartAsync(AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            CancellationTokenSource current;
            string currentToken;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("LayaClient");
                if (session != null) return;
                session = new CancellationTokenSource();
                token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                current = session;
                currentToken = token;
            }
            try
            {
                if (await Task.Run(() => IsPortOccupied()))
                    throw new InvalidOperationException("Laya 포트 47821이 사용 중입니다. 다른 EmotionCat을 종료하세요.");
                current.Token.ThrowIfCancellationRequested();
                string server = Path.Combine(AppSettings.BaseDirectory, "inference", "server.py");
                if (!File.Exists(server)) throw new FileNotFoundException("inference/server.py를 찾을 수 없습니다.");
                string python = FindPython(settings.PythonPath);
                var start = new ProcessStartInfo
                {
                    FileName = python,
                    Arguments = "-u " + Quote(server) + " --host 127.0.0.1 --port " + Port.ToString(CultureInfo.InvariantCulture)
                        + " --token " + currentToken + " --model multilingual --threads 2 --device " + (new[] { "cuda", "cpu", "mps" }.Contains(settings.Device) ? settings.Device : "auto"),
                    WorkingDirectory = AppSettings.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                start.EnvironmentVariables["PYTHONUTF8"] = "1";
                start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                start.EnvironmentVariables["TOKENIZERS_PARALLELISM"] = "false";
                var child = new Process { StartInfo = start, EnableRaisingEvents = true };
                // Drain output without retaining typed text or model prompts in application logs.
                child.OutputDataReceived += delegate { };
                child.ErrorDataReceived += delegate { };
                lock (gate)
                {
                    if (session != current || current.IsCancellationRequested) { child.Dispose(); return; }
                    process = child;
                    child.Start();
                    child.BeginOutputReadLine();
                    child.BeginErrorReadLine();
                }
                SetStatus("Laya 모델 불러오는 중…", false, current);
                // Loading a model may take time. It never blocks the settings or overlay window.
                Task ignored = MonitorAsync(current, currentToken, child);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                string message = ex is System.ComponentModel.Win32Exception
                    ? "감정 모델이 설치되지 않았습니다. 설정에서 모델 설치를 눌러 주세요."
                    : ShortError(ex.Message);
                SetStatus(message, false, current);
                ReleaseFailedSession(current);
            }
        }

        private async Task MonitorAsync(CancellationTokenSource current, string currentToken, Process child)
        {
            int failedHealthChecks = 0;
            try
            {
                while (!current.IsCancellationRequested)
                {
                    if (child.HasExited)
                    {
                        SetStatus("Laya가 종료되었습니다. 모델 설치 상태를 확인하세요.", false, current);
                        ReleaseFailedSession(current);
                        return;
                    }
                    try
                    {
                        Dictionary<string, object> health = await RequestAsync("health", null, currentToken, current.Token, 3000);
                        object stateValue;
                        string state = health.TryGetValue("status", out stateValue) ? Convert.ToString(stateValue, CultureInfo.InvariantCulture) : "";
                        failedHealthChecks = 0;
                        UpdateHealth(health);
                        if (state == "ready") SetStatus("연결됨 · " + DeviceDescription, true, current);
                        else if (state == "loading") SetStatus("Laya 모델 불러오는 중…", false, current);
                        else if (state == "error")
                        {
                            object error;
                            string details = health.TryGetValue("error", out error) ? ShortError(Convert.ToString(error, CultureInfo.InvariantCulture)) : "모델을 불러오지 못했습니다.";
                            SetStatus("Laya 오류: " + details, false, current);
                            ReleaseFailedSession(current);
                            return;
                        }
                        else SetStatus("Laya 응답 형식을 확인하세요.", false, current);
                    }
                    catch (OperationCanceledException) { if (current.IsCancellationRequested) return; }
                    catch (Exception ex)
                    {
                        if (!(ex is WebException || ex is IOException || ex is InvalidOperationException || ex is ArgumentException)) throw;
                        var webError = ex as WebException;
                        if (webError != null && webError.Response != null) webError.Response.Close();
                        failedHealthChecks++;
                        if (failedHealthChecks > 8) SetStatus("Laya 응답 대기 중… 모델 설치 상태를 확인하세요.", false, current);
                    }
                    await Task.Delay(ready ? 5000 : 1000, current.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception)
            {
                SetStatus("Laya 연결이 끊겼습니다. 설정에서 다시 연결하세요.", false, current);
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
            if (Interlocked.CompareExchange(ref classifying, 1, 0) != 0) return null;
            CancellationTokenSource current;
            string currentToken;
            lock (gate) { current = session; currentToken = token; }
            try
            {
                if (current == null || current.IsCancellationRequested) return null;
                string clippedText = text.Length > 1000 ? text.Substring(text.Length - 1000) : text;
                var choices = emotions.Select(e => new { id = e.Id, name = e.Name, description = e.Description }).ToArray();
                var serializer = new JavaScriptSerializer();
                string body = serializer.Serialize(new { text = clippedText, emotions = choices, instructions = instructions, source = source });
                var response = await RequestAsync("classify", body, currentToken, current.Token, 20000);
                ClassificationResult result = ParseClassification(response, emotions);
                if (current.IsCancellationRequested) return null;
                return result;
            }
            catch (OperationCanceledException) { return null; }
            catch (WebException ex)
            {
                if (current == null || current.IsCancellationRequested) return null;
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    HttpStatusCode code = response.StatusCode;
                    response.Close();
                    if (code == HttpStatusCode.ServiceUnavailable) return null;
                }
                SetStatus("Laya 분석 응답을 받지 못했습니다. 잠시 후 다시 시도합니다.", false, current);
                return null;
            }
            catch (IOException)
            {
                SetStatus("Laya 분석 연결이 끊겼습니다.", false, current);
                return null;
            }
            catch (InvalidOperationException)
            {
                SetStatus("Laya 분석 응답을 확인하지 못했습니다.", false, current);
                return null;
            }
            catch (ArgumentException)
            {
                SetStatus("Laya 분석 응답이 올바르지 않습니다.", false, current);
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
        void UpdateHealth(Dictionary<string, object> health)
        {
            Func<string, string> read = key => { object value; return health.TryGetValue(key, out value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : ""; };
            string device = read("device");
            DeviceDescription = device.StartsWith("cuda") ? "GPU · " + read("device_name") : device == "cpu" ? "CPU" : device;
            Diagnostics = "서버 접수 " + read("requests") + " · 완료 " + read("successes") + " · 최근 " + read("last_input_chars") + "자 / " + read("last_elapsed_ms") + " ms";
            var handler = HealthChanged; if (handler != null) handler();
        }

        private static async Task<Dictionary<string, object>> RequestAsync(string endpoint, string body, string authToken, CancellationToken cancellation, int timeout)
        {
            var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "/" + endpoint);
            request.Proxy = null;
            // Avoid the legacy 100-Continue wait and Nagle delay on tiny loopback POSTs.
            request.ServicePoint.Expect100Continue = false;
            request.ServicePoint.UseNagleAlgorithm = false;
            request.AllowAutoRedirect = false;
            request.Method = body == null ? "GET" : "POST";
            request.Headers["X-EmotionCat-Token"] = authToken;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.KeepAlive = false;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(timeout);
                using (deadline.Token.Register(request.Abort))
                {
                    if (body != null)
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(body);
                        if (bytes.Length > 32768) throw new ArgumentException("Classification payload is too large.");
                        request.ContentType = "application/json; charset=utf-8";
                        request.ContentLength = bytes.Length;
                        using (var stream = await request.GetRequestStreamAsync()) await stream.WriteAsync(bytes, 0, bytes.Length, deadline.Token);
                    }
                    using (var response = (HttpWebResponse)await request.GetResponseAsync())
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        char[] buffer = new char[2048];
                        var json = new StringBuilder();
                        int read;
                        while ((read = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                        {
                            json.Append(buffer, 0, read);
                            if (json.Length > 65536) throw new InvalidOperationException("Response exceeded size limit.");
                        }
                        var parsed = new JavaScriptSerializer { MaxJsonLength = 65536 }.Deserialize<Dictionary<string, object>>(json.ToString());
                        if (parsed == null) throw new InvalidOperationException("Response was empty.");
                        return parsed;
                    }
                }
            }
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

        private void ReleaseFailedSession(CancellationTokenSource current)
        {
            lock (gate)
            {
                if (session != current) return;
                session.Cancel();
                session = null;
                token = null;
                ready = false;
                if (process != null)
                {
                    try { if (!process.HasExited) process.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                    process.Dispose();
                    process = null;
                }
            }
        }

        public void Stop()
        {
            CancellationTokenSource current;
            lock (gate) current = session;
            if (current != null) ReleaseFailedSession(current);
            lock (gate) { status = "Laya 연결 해제됨"; ready = false; }
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }

        private static string FindPython(string configured)
        {
            string[] bundled =
            {
                Path.Combine(AppSettings.BaseDirectory, "inference", ".venv", "Scripts", "python.exe"),
                Path.Combine(AppSettings.BaseDirectory, "runtime", "python", "python.exe"),
                Path.Combine(AppSettings.BaseDirectory, "runtime", "python.exe")
            };
            foreach (string path in bundled) if (File.Exists(path)) return path;
            if (!String.IsNullOrWhiteSpace(configured)) return configured.Trim().Trim('"');
            return "python";
        }

        private static bool IsPortOccupied()
        {
            using (var client = new TcpClient())
            {
                try
                {
                    IAsyncResult connection = client.BeginConnect(IPAddress.Loopback, Port, null, null);
                    using (connection.AsyncWaitHandle)
                    {
                        if (!connection.AsyncWaitHandle.WaitOne(500)) return false;
                        client.EndConnect(connection);
                        return true;
                    }
                }
                catch (SocketException) { return false; }
            }
        }

        private static string Quote(string argument)
        {
            return "\"" + argument.Replace("\"", "\\\"") + "\"";
        }

        private static string ShortError(string error)
        {
            if (String.IsNullOrWhiteSpace(error)) return "모델 설치 상태를 확인하세요.";
            error = error.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return error.Length > 240 ? error.Substring(0, 240) + "…" : error;
        }
    }
}
