using System;
using System.Text;
using System.Text.RegularExpressions;

namespace EmotionCat
{
    /// Small, explicit language rules supplement the GPU classifier. They never
    /// enable classification when the GPU session is unavailable.
    public static class KoreanEmotionRules
    {
        const RegexOptions Options = RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(50);
        static readonly Regex Profanity = new Regex(
            @"[씨씹쒸시][\s\p{P}\p{Cf}이1i]*[발빨](?!점|역)|씨[\s\p{P}]*바|ㅆ[\s\p{P}]*ㅣ[\s\p{P}]*발|" +
            @"[병븅][\s\p{P}]*신|[개걔][\s\p{P}]*[새쌔색][\s\p{P}]*[끼기]|" +
            @"[이저그][\s\p{P}]*새끼|새끼(?:야|들|가)|좆|좃|존나|" +
            @"지[\s\p{P}]*랄|엿[\s\p{P}]*먹|닥쳐|꺼져|미친|" +
            @"[ㅅㅆ][\s\p{P}]*[ㅂㅃ]|ㅂ[\s\p{P}]*ㅅ|ㅈ[\s\p{P}]*[ㄹㄴ]|" +
            @"(?<![a-z])(?:f+u+c+k+\w*|sh[i1]+t+\w*|b[i1]tch\w*|asshole\w*)(?![a-z])|🖕",
            Options, Timeout);
        static readonly Regex FormatCharacters = new Regex(@"\p{Cf}", Options, Timeout);
        static readonly Regex Mention = new Regex(@"(?:단어|표현|뜻|번역|예시|표정|이미지|감정).*(?:검색|설명|추가|교체|바꿔|분류)|(?:이라는|라는)\s*(?:단어|말|표현)|[\""'“‘].*[\""'”’]", Options, Timeout);
        static readonly Regex NegationBefore = new Regex(@"(?:안|못|별로|전혀|하나도)\s*$", Options, Timeout);
        static readonly Regex NegationAfter = new Regex(@"^(?:하)?(?:지|진|지는|지도)?\s*(?:않|안|없)", Options, Timeout);
        static readonly string[] EmotionIds = { "angry", "love", "excited", "sad", "sleepy", "surprised", "confused" };
        static readonly Regex[] Expressions = {
            new Regex(@"빡(?:쳐|친|치)|킹받|열받|짜증|화(?:나|난|났)|분노", Options, Timeout),
            new Regex(@"고마(?:워|웠|운)|고맙|감사(?:해|합|하)|사랑(?:해|하|합)|아껴|아끼|소중|보고\s*싶|안아\s*주고\s*싶", Options, Timeout),
            new Regex(@"신(?:난|나|남)|기쁘|기뻐|행복|즐겁|즐거|기분\s*(?:좋|최고)|웃겨|웃기(?:네|다)|개꿀|나이스|야호|해냈|합격했", Options, Timeout),
            new Regex(@"서운|속상|우울|슬프|슬퍼|외롭|외로|허전|울적|실망", Options, Timeout),
            new Regex(@"졸(?:려|리|린|립)|피곤|지쳤|지치|지친|하품|자고\s*싶|쉬고\s*싶|눈(?:꺼풀)?.{0,8}(?:감긴|무겁)", Options, Timeout),
            new Regex(@"깜짝|놀랐|놀랍|놀라(?:워|움)|충격|헐|헉|이럴\s*수가|예상(?:도)?\s*못", Options, Timeout),
            new Regex(@"헷갈|혼란|모르겠|모르겟|이해(?:가)?\s*안\s*(?:돼|되|가)|무슨\s*(?:뜻|소리)|뭔\s*소리", Options, Timeout)
        };

        public static bool HasProfanity(string text)
        {
            return !String.IsNullOrWhiteSpace(text) && Profanity.IsMatch(Clean(text));
        }

        static string Clean(string text)
        {
            if (text.Length > 1000) text = text.Substring(text.Length - 1000);
            // A just-captured UTF-16 fragment may end in half of an emoji. It must
            // not turn an input normalization error into a GPU failure.
            try { text = text.Normalize(NormalizationForm.FormC); } catch (ArgumentException) { }
            return FormatCharacters.Replace(text, "");
        }

        public static string PrepareText(string text)
        {
            string value = Clean(text ?? "");
            // Expand standalone chat abbreviations without rewriting ordinary words
            // or making a crying emoticon outweigh a sentence such as 고마워ㅠㅠ.
            value = Regex.Replace(value, @"(?<![가-힣ㄱ-ㅎㅏ-ㅣa-zA-Z])(?:ㄱㅅ|ㄳ)+(?![가-힣ㄱ-ㅎㅏ-ㅣa-zA-Z])", "고마워");
            value = Regex.Replace(value, @"(?<![가-힣ㄱ-ㅎㅏ-ㅣa-zA-Z])ㅅㄹㅎ(?![가-힣ㄱ-ㅎㅏ-ㅣa-zA-Z])", "사랑해");
            value = Regex.Replace(value, @"(?<![가-힣ㄱ-ㅎㅏ-ㅣa-zA-Z])ㅊㅋ(?![가-힣ㄱ-ㅎㅏ-ㅣa-zA-Z])", "축하해 기쁘다");
            if (Regex.IsMatch(value, @"^[\sㅋㅎ!~.]+$") && Regex.IsMatch(value, @"[ㅋㅎ]{2}")) return "정말 웃겨서 즐겁다! " + value;
            if (Regex.IsMatch(value, @"^[\sㅠㅜ!~.]+$") && Regex.IsMatch(value, @"[ㅠㅜ]{2}")) return "너무 슬퍼서 눈물이 나. " + value;
            return value;
        }

        /// Unambiguous self-expression beats a weak multilingual model prediction.
        /// Negated expressions are left to the contextual GPU model; explicit
        /// requests about words/images are neutral instead of reacting to a label.
        /// In mixed sentences the last explicit feeling wins; profanity always wins.
        public static string ExplicitEmotion(string text)
        {
            if (HasProfanity(text)) return "angry";
            string value = PrepareText(text);
            if (Mention.IsMatch(value)) return "neutral";
            string result = null;
            int last = -1;
            for (int i = 0; i < Expressions.Length; i++)
            {
                foreach (Match match in Expressions[i].Matches(value))
                {
                    string before = value.Substring(Math.Max(0, match.Index - 12), Math.Min(12, match.Index));
                    string after = value.Substring(match.Index + match.Length);
                    if (NegationBefore.IsMatch(before) || NegationAfter.IsMatch(after)) continue;
                    if (match.Index > last) { last = match.Index; result = EmotionIds[i]; }
                }
            }
            if (result != null) return result;
            if (Regex.IsMatch(value, @"눈물\s*(?:나|난|이\s*나)|울고\s*싶")) return "sad";
            if (Regex.IsMatch(value, @"(?:어디|언제|몇\s*시|얼마|무엇|어떤|어느).*(?:까|요|지|나|[?？])|(?:파일|자료|문서|메일|회의|점심|저녁|일정|시간|버튼|코드|함수|이미지).*(?:저장|보냈|보내|시작|바꿔|수정|교체|추가|메뉴|입니다|이야)")) return "neutral";
            return null;
        }
    }
}
