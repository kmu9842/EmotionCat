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
        Check(missingNeutral.Emotions.Count == 2 && missingNeutral.Emotions.Exists(e => e.Id == "neutral")
            && missingNeutral.Emotions.Exists(e => e.Id == "custom"), "Neutral fallback must be repaired without losing a custom emotion.");
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
        Console.WriteLine("Core tests passed: " + checks);
        int golden = Array.IndexOf(args, "--golden");
        if (golden >= 0) RunGolden(args[golden + 1]);
        if (Array.IndexOf(args, "--integration") >= 0) RunIntegration().GetAwaiter().GetResult();
        return 0;
    }

    /// The native tokenizer, sequence builder and ONNX run must reproduce tools/onnx/make_golden.py.
    private static void RunGolden(string path)
    {
        var cases = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<List<Dictionary<string, object>>>(System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8));
        var times = new List<double>();
        using (var engine = new LayaEngine(LayaEngine.ModelDirectory, 2))
        {
            foreach (var item in cases)
            {
                string text = (string)item["text"];
                var emotions = new List<EmotionDefinition>();
                foreach (Dictionary<string, object> e in (System.Collections.ArrayList)item["emotions"])
                    emotions.Add(new EmotionDefinition((string)e["id"], (string)e["name"], (string)e["description"]));
                LayaSequence sequence = engine.Build(text, emotions, (string)item["instructions"]);
                var ids = ((System.Collections.ArrayList)item["input_ids"]).Cast<object>().Select(Convert.ToInt64).ToArray();
                var markers = ((System.Collections.ArrayList)item["marker_pos"]).Cast<object>().Select(Convert.ToInt64).ToArray();
                Check(ids.SequenceEqual(sequence.InputIds), "Token ids differ for: " + text.Substring(0, Math.Min(30, text.Length)) + " (expected " + ids.Length + ", got " + sequence.InputIds.Length + ")");
                Check(markers.SequenceEqual(sequence.MarkerPositions), "Option markers differ for: " + text.Substring(0, Math.Min(30, text.Length)));
                var watch = System.Diagnostics.Stopwatch.StartNew();
                double[] probabilities = engine.Classify(sequence);
                times.Add(watch.Elapsed.TotalMilliseconds);
                var expected = ((System.Collections.ArrayList)item["probabilities"]).Cast<object>().Select(Convert.ToDouble).ToArray();
                for (int i = 0; i < expected.Length; i++)
                    Check(Math.Abs(expected[i] - probabilities[i]) <= 0.02, "Probability differs for: " + text.Substring(0, Math.Min(30, text.Length)));
                var sorted = expected.OrderByDescending(v => v).ToArray();
                int best = Array.IndexOf(probabilities, probabilities.Max());
                Check(emotions[best].Id == (string)item["emotion"] || sorted[0] - sorted[1] < 0.05, "Emotion differs for: " + text.Substring(0, Math.Min(30, text.Length)));
            }
        }
        times.Sort();
        Console.WriteLine("Golden ONNX cases passed: " + cases.Count + ", median " + times[times.Count / 2].ToString("0.0") + " ms");
    }

    private static async Task RunIntegration()
    {
        var settings = new AppSettings();
        using (var client = new LayaClient())
        {
            client.StatusChanged += state => Console.WriteLine(state);
            await client.StartAsync(settings);
            DateTime deadline = DateTime.UtcNow.AddMinutes(2);
            while (!client.IsReady && DateTime.UtcNow < deadline) await Task.Delay(500);
            Check(client.IsReady, "Installed local Laya worker did not become ready: " + client.Status);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await client.ClassifyAsync("와 신난다! 너무 행복하고 기뻐!", settings.Emotions, settings.ClassificationPrompt, "test");
            watch.Stop();
            Check(result != null && settings.Emotions.Exists(e => e.Id == result.Emotion), "Live classifier did not return a configured emotion.");
            Check(result.Emotion == "excited", "Synthetic Korean joy must reach a matching configured expression.");
            Console.WriteLine("Live Laya result: " + result.Emotion + ", model " + result.ElapsedMs + " ms, C# roundtrip " + watch.ElapsedMilliseconds + " ms, device " + result.Device);
            client.Stop();
            Check(!client.IsReady, "Stopped worker must not remain ready.");
        }
        Console.WriteLine("Integration tests passed.");
    }
}
