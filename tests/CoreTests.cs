using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using EmotionCat;

internal static class CoreTests
{
    private static int checks;

    private static void Check(bool condition, string message)
    {
        checks++;
        if (!condition) throw new Exception(message);
    }

    private static void MustReject(Action action, string message)
    {
        checks++;
        try { action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception(message);
    }

    public static int Main(string[] args)
    {
        var serializer = new JavaScriptSerializer();
        var defaults = new AppSettings();
        Check(defaults.Emotions.Count == 8, "Eight default expressions must be available.");
        Check(defaults.DebounceMilliseconds == 500, "Analyze recorded input half a second after characters arrive.");
        Check(defaults.Emotions.Find(e => e.Id == "angry").Description == "분노"
            && defaults.Emotions.Find(e => e.Id == "love").Description == "사랑", "Classification meanings must map to the intended sprites.");
        Check(defaults.Emotions.Exists(e => e.Id == "angry") && defaults.Emotions.Exists(e => e.Id == "love")
            && defaults.Emotions.Exists(e => e.Id == "sad") && defaults.Emotions.Exists(e => e.Id == "excited"), "Required expressions are missing.");
        var partial = serializer.Deserialize<AppSettings>("{\"Size\":480}");
        Check(partial.Size == 480 && partial.HoldSeconds == 1 && partial.Emotions.Count == 8 && partial.InputEnabled,
            "A partial settings file must preserve defaults for missing values.");
        var missingNeutral = new AppSettings { Emotions = new List<EmotionDefinition> { new EmotionDefinition("custom", "Custom", "Custom feeling") } };
        missingNeutral.Normalize();
        Check(missingNeutral.Emotions.Count == 3 && missingNeutral.Emotions.Exists(e => e.Id == "neutral")
            && missingNeutral.Emotions.Exists(e => e.Id == "angry") && missingNeutral.Emotions.Exists(e => e.Id == "custom"), "Required neutral/angry expressions must be repaired without losing a custom emotion.");
        var fullCustom = new AppSettings { Emotions = new List<EmotionDefinition>() };
        for (int i = 0; i < 16; i++) fullCustom.Emotions.Add(new EmotionDefinition("custom_" + i, "Custom", "Custom feeling"));
        fullCustom.Normalize();
        Check(fullCustom.Emotions.Count == 16 && fullCustom.Emotions[0].Id == "neutral", "Neutral repair must respect the model's 16-label maximum.");
        var invalidPath = new AppSettings();
        invalidPath.Emotions[0].ImagePath = "invalid\0image.png";
        invalidPath.Normalize();
        Check(invalidPath.Emotions[0].ImagePath == "assets/frames/neutral_idle.png", "A corrupt image path must not break the initial overlay render.");
        string saved = serializer.Serialize(defaults);
        Check(!saved.Contains("Text") && !saved.Contains("token") && !saved.Contains("DataDirectory"), "Settings should contain configuration only.");
        var response = new Dictionary<string, object> { { "emotion", "love" }, { "confidence", 0.91 }, { "elapsed_ms", 80.5 } };
        var result = LayaClient.ParseClassification(response, defaults.Emotions);
        Check(result.Emotion == "love" && result.Confidence == 0.91 && result.ElapsedMs == 80.5, "Valid result should retain classifier output.");
        response["emotion"] = "unconfigured";
        MustReject(() => LayaClient.ParseClassification(response, defaults.Emotions), "Unconfigured labels must never select an image.");
        response["emotion"] = "love";
        response["confidence"] = Double.NaN;
        MustReject(() => LayaClient.ParseClassification(response, defaults.Emotions), "NaN confidence must be rejected.");
        response["confidence"] = 1.01;
        MustReject(() => LayaClient.ParseClassification(response, defaults.Emotions), "Out-of-range confidence must be rejected.");
        response["confidence"] = 0.8;
        response["elapsed_ms"] = -1;
        MustReject(() => LayaClient.ParseClassification(response, defaults.Emotions), "Invalid timing must be rejected.");
        response.Remove("confidence");
        MustReject(() => LayaClient.ParseClassification(response, defaults.Emotions), "Missing confidence must be rejected.");
        Check(defaults.Device == "directml", "The runtime must require a GPU.");
        foreach (var text in new[] { "씨발", "씨 발", "시발", "시1발", "씨이발", "ㅆㅣ발", "개색기", "ㅅㅂ", "ㅆㅂ", "ㅂㅅ", "개새끼", "병신", "존나 좋다ㅋㅋ", "미친 최고야", "fuck", "씨\u200b발" })
            Check(KoreanEmotionRules.HasProfanity(text), "Profanity not detected: " + text);
        foreach (var text in new[] { "시발점", "시발역", "시바견", "사랑해", "고마워ㅠㅠ", "파일을 저장했어", "class", "assignment" })
            Check(!KoreanEmotionRules.HasProfanity(text), "Ordinary text matched profanity: " + text);
        Check(KoreanEmotionRules.ExplicitEmotion("고마워ㅠㅠ") == "love", "Gratitude must outweigh a crying emoticon.");
        Check(KoreanEmotionRules.ExplicitEmotion("졸려 죽겠다") == "sleepy", "A tired idiom must not become anger.");
        Check(KoreanEmotionRules.ExplicitEmotion("ㄱㅅㄱㅅ") == "love", "Repeated chat abbreviations must be understood.");
        Check(KoreanEmotionRules.ExplicitEmotion("사랑이라는 단어를 검색해 줘") == "neutral", "Mentioning an emotion is not expressing it.");
        Check(KoreanEmotionRules.ExplicitEmotion("별로 행복하지 않아") == null, "Negated happiness must reach the contextual model.");
        Check(KoreanEmotionRules.ExplicitEmotion("안 졸려") == null, "Negated sleepiness must not force sleepy.");
        Check(KoreanEmotionRules.ExplicitEmotion("슬펐지만 지금은 행복해") == "excited", "The latest explicit feeling wins in a mixed sentence.");
        Check(KoreanEmotionRules.PrepareText("\ud83d").Length == 1, "A partial emoji must not disable analysis.");
        RunUnavailable().GetAwaiter().GetResult();
        Console.WriteLine("Core tests passed: " + checks);
        int golden = Array.IndexOf(args, "--golden");
        if (golden >= 0) RunGolden(args[golden + 1]);
        if (Array.IndexOf(args, "--integration") >= 0) RunIntegration().GetAwaiter().GetResult();
        if (Array.IndexOf(args, "--expect-gpu-unavailable") >= 0)
        {
            using (var client = new LayaClient())
            {
                client.StartAsync(defaults).GetAwaiter().GetResult();
                Check(!client.IsReady && client.UnavailableReason != null, "A machine without a supported GPU must disable analysis.");
                Check(client.ClassifyAsync("씨발", defaults.Emotions).GetAwaiter().GetResult() == null, "No fallback is permitted, including rules.");
                Console.WriteLine("PASS unavailable GPU/runtime disables analysis: " + client.Status);
            }
        }
        return 0;
    }

    private static async Task RunUnavailable()
    {
        int failures = 0;
        var settings = new AppSettings();
        using (var client = new LayaClient(delegate { throw new InvalidOperationException("No hardware GPU available (test fixture)."); }))
        {
            client.InferenceUnavailable += reason => { failures++; Check(reason.Contains("감정 분석을 껐습니다"), "GPU failure needs a clear warning."); };
            await client.StartAsync(settings);
            Check(!client.IsReady && client.UnavailableReason != null && failures == 1, "GPU failure must disable inference and notify once.");
            Check(await client.ClassifyAsync("씨발", settings.Emotions) == null, "Profanity rules must not run when the GPU is unavailable.");
            Check(await client.ClassifyAsync("행복해", settings.Emotions) == null, "There must be no CPU fallback.");
        }
    }

    private static object Get(Dictionary<string, object> item, string key)
    {
        object value;
        if (!item.TryGetValue(key, out value)) throw new Exception("Golden case is missing '" + key + "'; keys: " + String.Join(",", item.Keys));
        return value;
    }

    /// Exact token ids / option markers for every golden case, and >= 80% emotion accuracy on labeled cases.
    /// Probabilities are not compared exactly: int8 kernels differ slightly per CPU (see tools/onnx/make_golden.py).
    private static void RunGolden(string path)
    {
        var cases = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<List<Dictionary<string, object>>>(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
        var times = new List<double>();
        int labeled = 0, correct = 0;
        using (var engine = new LayaEngine(LayaEngine.ModelDirectory, 2))
        {
            foreach (var item in cases)
            {
                string text = (string)Get(item, "text");
                string label = text.Substring(0, Math.Min(30, text.Length));
                var emotions = new List<EmotionDefinition>();
                foreach (Dictionary<string, object> e in (System.Collections.ArrayList)Get(item, "emotions"))
                    emotions.Add(new EmotionDefinition((string)Get(e, "id"), (string)Get(e, "name"), (string)Get(e, "description")));
                LayaSequence sequence = engine.Build(text, emotions, (string)Get(item, "instructions"));
                var ids = ((System.Collections.ArrayList)Get(item, "input_ids")).Cast<object>().Select(Convert.ToInt64).ToArray();
                var markers = ((System.Collections.ArrayList)Get(item, "marker_pos")).Cast<object>().Select(Convert.ToInt64).ToArray();
                Check(ids.SequenceEqual(sequence.InputIds), "Token ids differ for: " + label + " (expected " + ids.Length + ", got " + sequence.InputIds.Length + ")");
                Check(markers.SequenceEqual(sequence.MarkerPositions), "Option markers differ for: " + label);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                double[] probabilities = engine.Classify(sequence);
                times.Add(watch.Elapsed.TotalMilliseconds);
                Check(Math.Abs(probabilities.Sum() - 1) < 1e-6 && probabilities.All(v => v >= 0), "Invalid probabilities for: " + label);
                object expected;
                if (item.TryGetValue("expected", out expected))
                {
                    labeled++;
                    if (emotions[Array.IndexOf(probabilities, probabilities.Max())].Id == (string)expected) correct++;
                }
            }
        }
        times.Sort();
        Console.WriteLine("Golden ONNX: " + cases.Count + " sequences exact, accuracy " + correct + "/" + labeled + ", median " + times[times.Count / 2].ToString("0.0") + " ms");
        Check(correct * 10 >= labeled * 8, "Emotion accuracy fell below 80%.");
    }

    private static async Task RunIntegration()
    {
        var settings = new AppSettings();
        using (var client = new LayaClient())
        {
            client.StatusChanged += state => Console.WriteLine(state);
            await client.StartAsync(settings);
            Check(client.IsReady, "Installed local Laya worker did not become ready: " + client.Status);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await client.ClassifyAsync("와 신난다! 너무 행복하고 기뻐!", settings.Emotions, settings.ClassificationPrompt, "test");
            watch.Stop();
            Check(result != null && settings.Emotions.Exists(e => e.Id == result.Emotion), "Live classifier did not return a configured emotion.");
            Check(result.Emotion == "excited", "Synthetic Korean joy must reach a matching configured expression.");
            Check(result.Device == "directml", "The session must require DirectML.");
            foreach (string text in new[] { "씨발", "존나 행복해", "ㅂㅅ", "씨 발", "ㅆㅂ" })
            {
                var profanity = await client.ClassifyAsync(text, settings.Emotions);
                Check(profanity.Emotion == "angry" && profanity.Confidence == 1 && profanity.Source == "profanity-rule", "Profanity must force anger even in a positive sentence.");
            }
            Console.WriteLine("Live Laya result: " + result.Emotion + ", model " + result.ElapsedMs + " ms, C# roundtrip " + watch.ElapsedMilliseconds + " ms, device " + result.Device);
            client.Stop();
            Check(!client.IsReady, "Stopped worker must not remain ready.");
        }
        Console.WriteLine("Integration tests passed.");
    }
}
