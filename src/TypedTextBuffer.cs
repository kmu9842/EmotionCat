using System;
using System.Collections.Generic;
using System.Text;
using System.Diagnostics;
using System.Threading;

namespace EmotionCat
{
    // Only characters supplied by keyboard events enter this bounded RAM buffer.
    // A two-set Hangul run is recomposed so backspace and final-consonant splits
    // work without asking another application to expose an editable text field.
    internal sealed class TypedTextBuffer
    {
        readonly char[] committed = new char[240];
        readonly char[] jamo = new char[1024];
        int committedCount, jamoCount;
        readonly object gate = new object();
        Timer expiry;
        int expiryVersion;
        long expiresAt;
        const string Leads = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
        const string Vowels = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
        const string Tails = " ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";
        const string Keys = "rRseEfaqQtTdwWczxvgkoiOjpuPhynbml";
        const string Letters = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎㅏㅐㅑㅒㅓㅔㅕㅖㅗㅛㅜㅠㅡㅣ";

        public string Text { get { lock (gate) { if (Expired()) return ""; return Tail(new string(committed, 0, committedCount) + Compose(new ArraySegment<char>(jamo, 0, jamoCount)), 240); } } }
        bool Expired() { if (expiresAt != 0 && Stopwatch.GetTimestamp() >= expiresAt) { Clear(); return true; } return false; }
        public void Clear()
        {
            lock (gate)
            {
                expiryVersion++;
                if (expiry != null) { expiry.Dispose(); expiry = null; }
                Array.Clear(committed, 0, committed.Length); Array.Clear(jamo, 0, jamo.Length); committedCount = jamoCount = 0;
            }
        }
        public void ExpireAt(long deadline)
        {
            lock (gate)
            {
                if (expiry != null) expiry.Dispose();
                expiresAt = deadline;
                int version = ++expiryVersion;
                expiry = new Timer(delegate { lock (gate) { if (version == expiryVersion) Clear(); } }, null,
                    Math.Max(0, (int)Math.Min(1000, (deadline - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency)), Timeout.Infinite);
            }
        }
        public void Commit()
        {
            lock (gate)
            {
            if (Expired()) return;
            foreach (char c in Compose(new ArraySegment<char>(jamo, 0, jamoCount))) AppendCommitted(c);
            Array.Clear(jamo, 0, jamo.Length); jamoCount = 0;
            }
        }
        void AppendCommitted(char c)
        {
            if (committedCount == committed.Length)
            {
                int discard = Char.IsHighSurrogate(committed[0]) && Char.IsLowSurrogate(committed[1]) ? 2 : 1;
                Array.Copy(committed, discard, committed, 0, committedCount - discard);
                committedCount -= discard;
                Array.Clear(committed, committedCount, discard);
            }
            committed[committedCount++] = c;
        }
        public void Append(string text, bool korean)
        {
            lock (gate)
            {
            if (Expired()) return;
            foreach (char c in text)
            {
                char key = c;
                if (Char.IsUpper(key) && "REQTWOP".IndexOf(key) < 0) key = Char.ToLowerInvariant(key);
                int index = Keys.IndexOf(key);
                if (korean && index >= 0)
                {
                    if (jamoCount == jamo.Length)
                    {
                        Array.Copy(jamo, 256, jamo, 0, jamoCount - 256);
                        jamoCount -= 256; Array.Clear(jamo, jamoCount, 256);
                    }
                    jamo[jamoCount++] = Letters[index];
                }
                else if (!Char.IsControl(c)) { Commit(); AppendCommitted(c); }
            }
            }
        }
        public void Backspace()
        {
            lock (gate)
            {
            if (Expired()) return;
            if (jamoCount > 0) jamo[--jamoCount] = '\0';
            else if (committedCount > 0)
            {
                int count = committedCount > 1 && Char.IsLowSurrogate(committed[committedCount - 1]) && Char.IsHighSurrogate(committed[committedCount - 2]) ? 2 : 1;
                committedCount -= count; Array.Clear(committed, committedCount, count);
            }
            }
        }
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
        internal static string Compose(IEnumerable<char> source)
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
