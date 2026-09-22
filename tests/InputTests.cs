using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmotionCat;

internal static class InputTests
{
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static void Korean(string keys, string expected)
    {
        var buffer = new TypedTextBuffer(); buffer.Append(keys, true);
        Check(buffer.Text == expected, "Hangul composition: " + keys + " -> " + buffer.Text);
    }
    static int Main()
    {
        Korean("tkfkdgo", "사랑해"); Korean("ghksk", "화나"); Korean("Wkwmdsk", "짜증나");
        Korean("dkssudgktpdy", "안녕하세요"); Korean("gksrmf", "한글"); Korean("dml", "의");
        Korean("rkqt", "값"); Korean("rkqtk", "갑사"); Korean("ekfrdl", "닭이"); Korean("rnjf", "궐");
        var b = new TypedTextBuffer(); b.Append("tkfkdgo", true);
        b.Backspace(); Check(b.Text == "사랑ㅎ", "Backspace must undo a vowel.");
        b.Backspace(); Check(b.Text == "사랑", "Backspace must undo a leading consonant.");
        b.Backspace(); Check(b.Text == "사라", "Backspace must undo a final consonant.");
        b.Clear(); b.Append("Hello ", false); b.Append("tkfkdgo", true); b.Append("!", false);
        Check(b.Text == "Hello 사랑해!", "Mixed language composition must preserve both modes.");
        b.Clear(); b.Append("사랑해😀", false); b.Backspace(); Check(b.Text == "사랑해", "Backspace must not split a surrogate pair.");
        b.Append(new string('x', 600), false); Check(b.Text.Length <= 240, "The memory buffer must be bounded.");
        b.Clear(); Check(b.Text == "", "Clearing the buffer must forget text.");
        using (var monitor = new InputMonitor())
        {
            int before = monitor.ContextVersion; monitor.Clear(); Check(monitor.ContextVersion == before + 1, "Clear invalidates results.");
            monitor.Enabled = false; Check(!monitor.Enabled, "Pause recognition.");
            monitor.ExcludedProcesses = new[] { " KeePass.exe ", null, "", "1Password" };
            Check(monitor.ExcludedProcesses.Length == 2 && monitor.ExcludedProcesses[0] == "KeePass", "Normalize exclusion settings.");
            var clone = monitor.ExcludedProcesses; clone[0] = "changed"; Check(monitor.ExcludedProcesses[0] == "KeePass", "Clone exclusions.");
            monitor.DebounceMilliseconds = 0; Check(monitor.DebounceMilliseconds == 150, "Minimum delay.");
            monitor.DebounceMilliseconds = Int32.MaxValue; Check(monitor.DebounceMilliseconds == 5000, "Maximum delay.");
            monitor.InputMode = "invalid"; Check(monitor.InputMode == "auto", "Unknown mode defaults to auto.");
            before = monitor.ContextVersion; Parallel.For(0, 64, delegate(int unused) { monitor.Clear(); });
            Check(monitor.ContextVersion == before + 64, "Concurrent context clears must not be lost.");
        }
        var disposed = new InputMonitor(); Parallel.For(0, 16, delegate(int unused) { disposed.Dispose(); });
        bool rejected = false; try { disposed.Start(); } catch (ObjectDisposedException) { rejected = true; }
        Check(rejected, "Disposed monitor cannot start.");
        using (var paused = new InputMonitor())
        using (var delivered = new ManualResetEvent(false))
        {
            paused.Enabled = false; paused.KeyPressed += key => delivered.Set();
            typeof(InputMonitor).GetField("running", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(paused, true);
            typeof(InputMonitor).GetMethod("QueueAnimation", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(paused, new object[] { 65 });
            Check(delivered.WaitOne(2000), "Paws must work with recognition paused.");
        }
        Console.WriteLine("PASS " + checks + " global-character/Hangul checks; no hooks or live typing read.");
        return 0;
    }
}
