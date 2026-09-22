using System;
using System.Text;

namespace EmotionCat
{
    // Only characters supplied by keyboard events enter this bounded RAM buffer.
    // A two-set Hangul run is recomposed so backspace and final-consonant splits
    // work without asking another application to expose an editable text field.
    internal sealed class TypedTextBuffer
    {
        readonly StringBuilder committed = new StringBuilder();
        readonly StringBuilder jamo = new StringBuilder();
        const string Leads = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
        const string Vowels = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
        const string Tails = " ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";
        const string Keys = "rRseEfaqQtTdwWczxvgkoiOjpuPhynbml";
        const string Letters = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎㅏㅐㅑㅒㅓㅔㅕㅖㅗㅛㅜㅠㅡㅣ";

        public string Text { get { return Tail(committed.ToString() + Compose(jamo.ToString()), 240); } }
        public void Clear() { committed.Clear(); jamo.Clear(); }
        public void Commit() { committed.Append(Compose(jamo.ToString())); jamo.Clear(); Trim(); }
        public void Append(string text, bool korean)
        {
            foreach (char c in text)
            {
                char key = c;
                if (Char.IsUpper(key) && "REQTWOP".IndexOf(key) < 0) key = Char.ToLowerInvariant(key);
                int index = Keys.IndexOf(key);
                if (korean && index >= 0)
                {
                    jamo.Append(Letters[index]);
                    // Keep at most 1024 uncommitted physical jamo; output is 240 chars.
                    if (jamo.Length > 1024) jamo.Remove(0, 256);
                }
                else if (!Char.IsControl(c)) { Commit(); committed.Append(c); Trim(); }
            }
        }
        public void Backspace()
        {
            if (jamo.Length > 0) jamo.Length--;
            else if (committed.Length > 0)
            {
                int count = committed.Length > 1 && Char.IsLowSurrogate(committed[committed.Length - 1]) && Char.IsHighSurrogate(committed[committed.Length - 2]) ? 2 : 1;
                committed.Length -= count;
            }
        }
        void Trim() { if (committed.Length > 240) { string text = Tail(committed.ToString(), 240); committed.Clear(); committed.Append(text); } }
        static string Tail(string s, int n) { int start = Math.Max(0, s.Length - n); if (start > 0 && Char.IsLowSurrogate(s[start])) start++; return s.Substring(start); }
        static int CompoundVowel(int a, int b)
        {
            if (a == 8) { if (b == 0) return 9; if (b == 1) return 10; if (b == 20) return 11; }
            if (a == 13) { if (b == 4) return 14; if (b == 5) return 15; if (b == 20) return 16; }
            return a == 18 && b == 20 ? 19 : -1;
        }
        static int CompoundTail(int a, int b)
        {
            if (a == 1 && b == 19) return 3;
            if (a == 4) { if (b == 22) return 5; if (b == 27) return 6; }
            if (a == 8) { int i = Array.IndexOf(new[] { 1, 16, 17, 19, 25, 26, 27 }, b); if (i >= 0) return 9 + i; }
            return a == 17 && b == 19 ? 18 : -1;
        }
        static void SplitTail(int t, out int first, out char second)
        {
            first = 0; second = Tails[t];
            if (t == 3) { first = 1; second = 'ㅅ'; }
            else if (t == 5 || t == 6) { first = 4; second = t == 5 ? 'ㅈ' : 'ㅎ'; }
            else if (t >= 9 && t <= 15) { first = 8; second = "ㄱㅁㅂㅅㅌㅍㅎ"[t - 9]; }
            else if (t == 18) { first = 17; second = 'ㅅ'; }
        }
        static void Flush(StringBuilder output, int l, int v, int t)
        {
            if (l >= 0 && v >= 0) output.Append((char)(0xAC00 + (l * 21 + v) * 28 + t));
            else if (l >= 0) output.Append(Leads[l]);
            else if (v >= 0) output.Append(Vowels[v]);
        }
        internal static string Compose(string source)
        {
            var output = new StringBuilder(); int l = -1, v = -1, t = 0;
            foreach (char c in source)
            {
                int nextVowel = Vowels.IndexOf(c), nextLead = Leads.IndexOf(c), nextTail = Tails.IndexOf(c);
                if (nextVowel >= 0)
                {
                    if (v < 0) v = nextVowel;
                    else if (t > 0)
                    {
                        int first; char second; SplitTail(t, out first, out second);
                        Flush(output, l, v, first); l = Leads.IndexOf(second); v = nextVowel; t = 0;
                    }
                    else
                    {
                        int compound = CompoundVowel(v, nextVowel);
                        if (compound >= 0) v = compound;
                        else { Flush(output, l, v, t); l = -1; v = nextVowel; t = 0; }
                    }
                }
                else if (nextLead >= 0)
                {
                    if (l >= 0 && v >= 0 && nextTail > 0)
                    {
                        if (t == 0) t = nextTail;
                        else
                        {
                            int compound = CompoundTail(t, nextTail);
                            if (compound >= 0) t = compound;
                            else { Flush(output, l, v, t); l = nextLead; v = -1; t = 0; }
                        }
                    }
                    else { Flush(output, l, v, t); l = nextLead; v = -1; t = 0; }
                }
            }
            Flush(output, l, v, t); return output.ToString();
        }
    }
}
