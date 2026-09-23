using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using EmotionCat;

internal static class KoreanTests
{
    public sealed class Case { public string split { get; set; } public string expected { get; set; } public string text { get; set; } }
    static string Decide(double[] scores, List<EmotionDefinition> emotions, double minimum)
    {
        string id = emotions[Array.IndexOf(scores, scores.Max())].Id;
        return LayaEngine.Confidence(scores) < minimum ? "neutral" : id;
    }
    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var json = new JavaScriptSerializer();
            var cases = json.Deserialize<List<Case>>(File.ReadAllText("tests/korean-emotion-cases.json", Encoding.UTF8));
            var settings = new AppSettings();
            bool compare = args.Contains("--compare-prompts");
            string[] prompts = compare ? new[] {
                "문장을 쓴 사람은 어떤 감정을 느끼고 있나요?",
                AppSettings.DefaultPrompt,
                "문장의 말투와 문맥에서 드러나는 감정은 무엇인가요? 욕설은 분노, 감사는 사랑, 피곤함은 졸림입니다. 일상적인 정보나 요청은 무감정입니다."
            } : new[] { AppSettings.DefaultPrompt };
            var results = new List<object>();
            string[][] descriptions = compare ? new[] {
                settings.Emotions.Select(e => e.Description).ToArray(),
                new[] { "무감정, 평온, 일상적인 질문이나 요청", "분노, 짜증, 화남", "사랑, 애정, 감사, 고마움", "기쁨, 행복, 신남, 웃음", "슬픔, 외로움, 서운함, 우울함", "놀람, 충격, 예상 못한 일", "졸림, 피로, 지침, 자고 싶음", "혼란, 헷갈림, 이해하지 못함" },
                new[] { "특별한 감정 없는 평범한 말", "화가 나고 짜증이 난다", "사랑하고 고맙다", "행복하고 즐겁고 신난다", "슬프고 외롭고 서운하다", "깜짝 놀랐다", "피곤하고 졸리고 쉬고 싶다", "헷갈리고 이해가 안 된다" }
            } : new[] { settings.Emotions.Select(e => e.Description).ToArray() };
            using (var engine = new LayaEngine(LayaEngine.ModelDirectory, 1))
            {
                for (int d = 0; d < descriptions.Length; d++)
                {
                for (int i = 0; i < settings.Emotions.Count; i++) settings.Emotions[i].Description = descriptions[d][i];
                for (int p = 0; p < prompts.Length; p++)
                {
                    int correct = 0, total = 0;
                    foreach (var item in cases.Where(c => !compare || c.split == "development"))
                    {
                        var text = KoreanEmotionRules.PrepareText(item.text);
                        string predicted = compare ? null : KoreanEmotionRules.ExplicitEmotion(item.text);
                        if (predicted == null) predicted = KoreanEmotionRules.HasProfanity(item.text) ? "angry"
                            : Decide(engine.Classify(engine.Build(text, settings.Emotions, prompts[p])), settings.Emotions, settings.MinConfidence);
                        if (predicted == item.expected) correct++;
                        total++;
                        results.Add(new { labels = d, prompt = p, split = item.split, text = item.text, expected = item.expected, predicted = predicted });
                        if (predicted != item.expected) Console.WriteLine("MISS [" + d + "/" + p + "] " + item.text + " : " + item.expected + " -> " + predicted);
                    }
                    Console.WriteLine("Korean labels " + d + " prompt " + p + ": " + correct + "/" + total);
                    if (!compare && correct < total * 0.85) throw new Exception("Korean accuracy below 85%.");
                }
                }
            }
            Directory.CreateDirectory(".test-output");
            File.WriteAllText(".test-output/korean-results.json", json.Serialize(results), Encoding.UTF8);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
    }
}
