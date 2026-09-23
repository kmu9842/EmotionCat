using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using EmotionCat;

internal static class GpuTests
{
    static int Main()
    {
        try
        {
            Directory.CreateDirectory(".test-output");
            string prefix = Path.GetFullPath(".test-output/gpu-" + Guid.NewGuid().ToString("N"));
            var settings = new AppSettings();
            var durations = new List<double>();
            using (var engine = new LayaEngine(LayaEngine.ModelDirectory, 1, prefix))
            {
                var sequence = engine.Build("와 신난다! 너무 행복하고 기뻐!", settings.Emotions, settings.ClassificationPrompt);
                engine.Classify(sequence);
                for (int i = 0; i < 20; i++)
                {
                    var clock = Stopwatch.StartNew();
                    var result = engine.Classify(sequence);
                    durations.Add(clock.Elapsed.TotalMilliseconds);
                    if (Math.Abs(result.Sum() - 1) > 1e-6) throw new Exception("Invalid GPU output.");
                }
                var process = Process.GetCurrentProcess();
                double before = process.TotalProcessorTime.TotalSeconds;
                var idle = Stopwatch.StartNew(); Thread.Sleep(8000); process.Refresh();
                double cpu = 100 * (process.TotalProcessorTime.TotalSeconds - before) / idle.Elapsed.TotalSeconds / Environment.ProcessorCount;
                Console.WriteLine("Idle CPU with GPU session loaded: " + cpu.ToString("0.000") + "%");
                if (cpu > 0.5) throw new Exception("Idle inference session uses more than 0.5% CPU.");
            }
            var profile = Directory.GetFiles(".test-output", Path.GetFileName(prefix) + "*.json").Single();
            var rows = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(profile));
            int gpuNodes = 0;
            foreach (var row in rows)
            {
                object args;
                if (!row.TryGetValue("args", out args)) continue;
                var properties = args as Dictionary<string, object>;
                object provider;
                if (properties == null || !properties.TryGetValue("provider", out provider)) continue;
                if ((string)provider != "DmlExecutionProvider") throw new Exception("Non-GPU execution detected: " + provider);
                gpuNodes++;
            }
            if (gpuNodes == 0) throw new Exception("Profiling did not record GPU work.");
            durations.Sort();
            Console.WriteLine("PASS DirectML nodes: " + gpuNodes + "; CPU nodes: 0; median: " + durations[durations.Count / 2].ToString("0.0") + " ms");
            Console.WriteLine("Profile: " + profile);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
