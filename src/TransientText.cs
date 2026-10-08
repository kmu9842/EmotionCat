using System;
using System.Diagnostics;
using System.Threading;

namespace EmotionCat
{
    // The queue owns this object, never a plaintext string. Expiry also works while
    // the UI or the model is busy. Take transfers the text once and wipes our copy.
    public sealed class TransientText : IDisposable
    {
        public const int LifetimeMilliseconds = 1000;
        readonly object gate = new object();
        public readonly long ExpiresAt;
        char[] characters;
        Timer expiry;

        public TransientText(string text, long expiresAt = 0)
        {
            ExpiresAt = expiresAt == 0 ? Stopwatch.GetTimestamp() + Stopwatch.Frequency : expiresAt;
            characters = (text ?? "").ToCharArray();
            expiry = new Timer(delegate { Dispose(); }, null, Timeout.Infinite, Timeout.Infinite);
            expiry.Change(Math.Max(0, (int)Math.Min(LifetimeMilliseconds, (ExpiresAt - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency)), Timeout.Infinite);
        }

        public string Take()
        {
            lock (gate)
            {
                try { return characters == null || Stopwatch.GetTimestamp() >= ExpiresAt ? "" : new string(characters); }
                finally { Dispose(); }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (characters != null) { Array.Clear(characters, 0, characters.Length); characters = null; }
                if (expiry != null) { expiry.Dispose(); expiry = null; }
            }
        }
    }
}
