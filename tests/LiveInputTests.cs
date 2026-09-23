using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using EmotionCat;

internal static class LiveInputTests
{
    static int exitCode;
    [STAThread]
    static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        if (args.Length > 0) { RunFixture(args[0]); return 0; }
        string tag = "Local\\EmotionCat.GlobalTest." + Guid.NewGuid().ToString("N");
        using (var ready = new EventWaitHandle(false, EventResetMode.ManualReset, tag + ".ready"))
        using (var love = new EventWaitHandle(false, EventResetMode.AutoReset, tag + ".love"))
        using (var angry = new EventWaitHandle(false, EventResetMode.AutoReset, tag + ".angry"))
        using (var profanity = new EventWaitHandle(false, EventResetMode.AutoReset, tag + ".profanity"))
        using (var model = new EventWaitHandle(false, EventResetMode.AutoReset, tag + ".model"))
        using (var modifiers = new EventWaitHandle(false, EventResetMode.AutoReset, tag + ".modifiers"))
        using (var done = new EventWaitHandle(false, EventResetMode.AutoReset, tag + ".done"))
        using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "\"" + tag + "\"") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }))
        {
            if (!ready.WaitOne(8000)) { Console.WriteLine("FAIL fixture startup"); child.Kill(); return 1; }
            using (var app = new AppController(false, false, true, child.Id, "auto"))
            using (var starter = new System.Windows.Forms.Timer { Interval = 100 })
            {
                app.Settings.InputEnabled = true; app.Settings.DebounceMilliseconds = 500; app.ApplySettings();
                var cat = (CatOverlay)typeof(AppController).GetField("overlay", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(app);
                cat.Show();
                starter.Tick += async delegate
                {
                    starter.Stop();
                    try
                    {
                        await Until(() => app.Client.IsReady, 120000, "model startup");
                        Console.WriteLine("READY " + app.Client.DeviceDescription);
                        var clock = Stopwatch.StartNew();
                        AllowSetForegroundWindow((uint)child.Id); love.Set();
                        await Until(() => app.CaptureCount > 0, 5000, "global love capture");
                        long captureMs = clock.ElapsedMilliseconds;
                        if (captureMs < 450 || captureMs > 950) throw new Exception("half-second capture timing: " + captureMs);
                        if (app.LastInputText != "사랑해") throw new Exception("auto Korean capture expected 사랑해, got owned fixture: " + app.LastInputText);
                        await Until(() => app.ResponseCount > 0, 5000, "love response");
                        AssertFrame(app, cat, "love");
                        Console.WriteLine("PASS global keys -> 사랑해 -> Laya -> love PNG; capture=" + captureMs + "ms, total=" + clock.ElapsedMilliseconds + "ms; " + app.DecisionSummary);
                        int captures = app.CaptureCount, sends = app.SentCount, responses = app.ResponseCount;
                        await Task.Delay(2100);
                        if (app.CaptureCount != captures || app.SentCount != sends || app.ResponseCount != responses) throw new Exception("idle performed inference");
                        AllowSetForegroundWindow((uint)child.Id); modifiers.Set(); await Task.Delay(1500);
                        if (app.CaptureCount != captures || app.SentCount != sends) throw new Exception("modifier/navigation keys scheduled inference");
                        Console.WriteLine("PASS idle and modifier/navigation keys produce zero inference");
                        AllowSetForegroundWindow((uint)child.Id); angry.Set();
                        await Until(() => app.ResponseCount > responses, 5000, "anger response");
                        if (app.LastInputText != "화나") throw new Exception("auto Korean anger text mismatch: " + app.LastInputText);
                        AssertFrame(app, cat, "angry");
                        Console.WriteLine("PASS global keys -> 화나 -> Laya -> angry PNG; " + app.DecisionSummary);
                        responses = app.ResponseCount; AllowSetForegroundWindow((uint)child.Id); profanity.Set();
                        await Until(() => app.ResponseCount > responses, 5000, "profanity response");
                        if (app.LastInputText != "시발" || !app.DecisionSummary.Contains("욕설 우선 규칙")) throw new Exception("profanity did not force anger");
                        AssertFrame(app, cat, "angry");
                        Console.WriteLine("PASS global Korean profanity -> mandatory angry PNG");
                        responses = app.ResponseCount; AllowSetForegroundWindow((uint)child.Id); model.Set();
                        await Until(() => app.ResponseCount > responses, 5000, "GPU contextual response");
                        if (app.LastInputText != "안녕" || !app.DecisionSummary.Contains("directml")) throw new Exception("contextual input did not use the GPU model");
                        Console.WriteLine("PASS global Korean input -> actual DirectML model; " + app.DecisionSummary);
                        Console.WriteLine("PASS native UpdateLayeredWindow succeeded for both emotions; fixture has no editable controls");
                    }
                    catch (Exception ex) { exitCode = 1; Console.WriteLine("FAIL " + ex.Message + " | " + app.InputStatus + " | " + app.ModelStatus); }
                    finally { done.Set(); app.ExitThread(); }
                };
                starter.Start(); Application.Run(app);
            }
            done.Set(); if (!child.WaitForExit(3000)) child.Kill();
        }
        return exitCode;
    }
    static void AssertFrame(AppController app, CatOverlay cat, string emotion)
    {
        if (app.CurrentEmotionId != emotion || app.CurrentFramePath != "assets/frames/" + emotion + "_idle.png" || !cat.LastPaintSucceeded)
            throw new Exception("native frame mismatch " + app.CurrentEmotionId + " / " + app.CurrentFramePath + " / paint=" + cat.LastPaintSucceeded + " / " + app.DecisionSummary);
    }
    static async Task Until(Func<bool> predicate, int limit, string operation)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate()) { if (clock.ElapsedMilliseconds >= limit) throw new Exception("timeout " + operation); await Task.Delay(20); }
    }
    static void RunFixture(string tag)
    {
        using (var ready = EventWaitHandle.OpenExisting(tag + ".ready"))
        using (var love = EventWaitHandle.OpenExisting(tag + ".love"))
        using (var angry = EventWaitHandle.OpenExisting(tag + ".angry"))
        using (var profanity = EventWaitHandle.OpenExisting(tag + ".profanity"))
        using (var model = EventWaitHandle.OpenExisting(tag + ".model"))
        using (var modifiers = EventWaitHandle.OpenExisting(tag + ".modifiers"))
        using (var done = EventWaitHandle.OpenExisting(tag + ".done"))
        using (var window = new Form { Text = "EmotionCat input verification", Width = 420, Height = 110, TopMost = true, StartPosition = FormStartPosition.CenterScreen })
        using (var timer = new System.Windows.Forms.Timer { Interval = 40 })
        {
            var keys = new Queue<int>();
            IntPtr inputContext = IntPtr.Zero;
            window.Shown += delegate
            {
                // Own non-editable window: a real Korean layout/context, no UIA text.
                LoadKeyboardLayout("00000412", 1);
                inputContext = ImmCreateContext(); ImmAssociateContext(window.Handle, inputContext);
                ImmSetOpenStatus(inputContext, true); ImmSetConversionStatus(inputContext, 1, 0);
                window.Activate(); ready.Set();
            };
            timer.Tick += delegate
            {
                if (done.WaitOne(0)) { window.Close(); return; }
                string phrase = love.WaitOne(0) ? "tkfkdgo" : angry.WaitOne(0) ? "ghksk" : profanity.WaitOne(0) ? "tlqkf" : model.WaitOne(0) ? "dkssud" : null;
                if (phrase != null) { window.Activate(); SetForegroundWindow(window.Handle); keys.Enqueue(13); foreach (char c in phrase) keys.Enqueue(Char.ToUpperInvariant(c)); }
                if (modifiers.WaitOne(0)) { window.Activate(); SetForegroundWindow(window.Handle); keys.Enqueue(160); keys.Enqueue(162); keys.Enqueue(37); }
                if (keys.Count > 0)
                {
                    if (GetForegroundWindow() != window.Handle) { FocusFixture(window); return; }
                    // TSF can reset the conversion mode when this fixture regains
                    // focus after GPU startup. Establish it on the focused window.
                    LoadKeyboardLayout("00000412", 1);
                    ImmAssociateContext(window.Handle, inputContext);
                    ImmSetOpenStatus(inputContext, true); ImmSetConversionStatus(inputContext, 1, 0);
                    IntPtr ime = ImmGetDefaultIMEWnd(window.Handle);
                    if (ime != IntPtr.Zero) { SendMessage(ime, 0x283, (IntPtr)6, (IntPtr)1); SendMessage(ime, 0x283, (IntPtr)2, (IntPtr)1); }
                    int key = keys.Dequeue();
                    var input = new[] { new INPUT { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { Vk = (ushort)key, Scan = (ushort)MapVirtualKey((uint)key, 0) } } }, new INPUT { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT { Vk = (ushort)key, Scan = (ushort)MapVirtualKey((uint)key, 0), Flags = 2 } } } };
                    if (SendInput(2, input, Marshal.SizeOf(typeof(INPUT))) != 2) { window.Close(); return; }
                }
            };
            timer.Start(); Application.Run(window);
            if (inputContext != IntPtr.Zero) ImmDestroyContext(inputContext);
        }
    }
    static void FocusFixture(Form window)
    {
        uint process;
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out process);
        uint fixtureThread = GetCurrentThreadId();
        bool attached = foregroundThread != fixtureThread && AttachThreadInput(fixtureThread, foregroundThread, true);
        try { window.Activate(); SetForegroundWindow(window.Handle); }
        finally { if (attached) AttachThreadInput(fixtureThread, foregroundThread, false); }
    }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public KEYBDINPUT Keyboard; [FieldOffset(0)] public MOUSEINPUT Mouse; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
    [DllImport("user32.dll")] static extern uint SendInput(uint count, INPUT[] input, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint first, uint second, bool attach);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr LoadKeyboardLayout(string layout, uint flags);
    [DllImport("imm32.dll")] static extern IntPtr ImmCreateContext();
    [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("imm32.dll")] static extern bool ImmDestroyContext(IntPtr context);
    [DllImport("imm32.dll")] static extern IntPtr ImmAssociateContext(IntPtr window, IntPtr context);
    [DllImport("imm32.dll")] static extern bool ImmSetConversionStatus(IntPtr context, uint conversion, uint sentence);
    [DllImport("imm32.dll")] static extern bool ImmSetOpenStatus(IntPtr context, bool open);
}
