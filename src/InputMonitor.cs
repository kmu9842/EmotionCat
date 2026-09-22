using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace EmotionCat
{
    // Global keyboard characters in bounded RAM. Editor text is never read.
    public sealed class InputMonitor : IDisposable
    {
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly object lifecycle = new object();
        readonly ConcurrentQueue<Stroke> events = new ConcurrentQueue<Stroke>();
        readonly byte[] keys = new byte[256];
        Thread hookThread, readerThread;
        HookProc keyboardCallback, mouseCallback;
        volatile bool running, disposed, enabled = true;
        volatile int debounceMilliseconds = 500;
        volatile string inputMode = "auto";
        volatile string[] excludedProcesses = new string[0];
        int generation, queuedCount, queuedAnimation, latestAnimationKey, wakeDisposed;
        uint hookThreadId;
        bool capsOn;
        string lastStatus;
        public event Action<int> KeyPressed;
        public event Action<string> TextReady;
        public event Action<string, int> TextReadyWithContext;
        public event Action<int> TextObserved;
        public event Action<string> StatusChanged;
        public event Action ContextCleared;
        public event Action<int> ContextClearedWithVersion;
        internal int CaptureProcessIdFilter { get; set; }
        public int ContextVersion { get { return Volatile.Read(ref generation); } }
        public bool Enabled { get { return enabled; } set { if (enabled != value) { enabled = value; Clear(); } } }
        // Persisted name retained for compatibility: one-shot collection window.
        public int DebounceMilliseconds { get { return debounceMilliseconds; } set { debounceMilliseconds = Math.Max(150, Math.Min(5000, value)); Signal(); } }
        public string InputMode { get { return inputMode; } set { string mode = value == "korean" || value == "latin" ? value : "auto"; if (inputMode != mode) { inputMode = mode; Clear(); } } }
        public string[] ExcludedProcesses
        {
            get { return (string[])excludedProcesses.Clone(); }
            set
            {
                var names = new List<string>();
                if (value != null) foreach (string name in value)
                {
                    string clean = (name ?? "").Trim();
                    if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(0, clean.Length - 4);
                    if (clean.Length > 0) names.Add(clean);
                }
                excludedProcesses = names.ToArray(); Clear();
            }
        }
        public void Start()
        {
            lock (lifecycle)
            {
                if (disposed) throw new ObjectDisposedException("InputMonitor");
                if (running) return;
                running = true;
                readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "EmotionCat character buffer" };
                readerThread.SetApartmentState(ApartmentState.MTA);
                hookThread = new Thread(HookLoop) { IsBackground = true, Name = "EmotionCat global keyboard" };
                hookThread.SetApartmentState(ApartmentState.STA);
                readerThread.Start(); hookThread.Start();
            }
        }
        public void Clear()
        {
            int version = Interlocked.Increment(ref generation);
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (disposed) return;
                var contextual = ContextClearedWithVersion; if (contextual != null) contextual(version);
                var callback = ContextCleared; if (callback != null && version == ContextVersion) callback();
            });
            Signal();
        }
        void Signal() { if (!disposed) try { wake.Set(); } catch (ObjectDisposedException) { } }
        void HookLoop()
        {
            IntPtr keyboard = IntPtr.Zero, mouse = IntPtr.Zero;
            try
            {
                hookThreadId = GetCurrentThreadId();
                NativeMessage message; PeekMessage(out message, IntPtr.Zero, 0, 0, 0);
                capsOn = (GetKeyState(20) & 1) != 0;
                for (int i = 0; i < 256; i++) keys[i] = (byte)((GetAsyncKeyState(i) & 0x8000) != 0 ? 128 : 0);
                keys[16] = keys[17] = keys[18] = 0;
                keyboardCallback = KeyboardHook; mouseCallback = MouseHook;
                keyboard = SetWindowsHookEx(13, keyboardCallback, GetModuleHandle(null), 0);
                mouse = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
                if (keyboard == IntPtr.Zero || mouse == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                SetStatus("전역 문자 입력 대기");
                while (running && GetMessage(out message, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref message); DispatchMessage(ref message); }
            }
            catch (Exception) { SetStatus("전역 키보드 감지를 시작하지 못했어요."); }
            finally { if (keyboard != IntPtr.Zero) UnhookWindowsHookEx(keyboard); if (mouse != IntPtr.Zero) UnhookWindowsHookEx(mouse); }
        }
        IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                int message = wParam.ToInt32();
                if (code >= 0 && running && (message == 0x100 || message == 0x104 || message == 0x101 || message == 0x105))
                {
                    KeyboardData data = (KeyboardData)Marshal.PtrToStructure(lParam, typeof(KeyboardData));
                    int key = (int)data.vkCode;
                    bool down = message == 0x100 || message == 0x104;
                    if (key < 256) keys[key] = (byte)(down ? 128 : 0);
                    if (key == 160 || key == 161) keys[16] = 0;
                    if (key == 162 || key == 163) keys[17] = 0;
                    if (key == 164 || key == 165) keys[18] = 0;
                    bool shift = IsDown(16) || IsDown(160) || IsDown(161);
                    bool ctrl = IsDown(17) || IsDown(162) || IsDown(163);
                    bool alt = IsDown(18) || IsDown(164) || IsDown(165);
                    if (down)
                    {
                        QueueAnimation(key);
                        if (key == 20) capsOn = !capsOn;
                        if (enabled && !IsModifier(key))
                        {
                            IntPtr window = GetForegroundWindow();
                            uint pid; uint thread = GetWindowThreadProcessId(window, out pid);
                            if (CaptureProcessIdFilter == 0 || pid == CaptureProcessIdFilter)
                                Enqueue(new Stroke { Key = key, Scan = data.scanCode, Window = window,
                                    Layout = GetKeyboardLayout(thread), Process = (int)pid, Thread = thread, Version = ContextVersion,
                                    Shift = shift, Control = ctrl, Alt = alt, AltGr = alt && ctrl && IsDown(165),
                                    Windows = IsDown(91) || IsDown(92), Caps = capsOn, Time = Stopwatch.GetTimestamp() });
                        }
                    }
                }
            }
            catch { }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
        bool IsDown(int key) { return (keys[key] & 128) != 0; }
        static bool IsModifier(int key) { return key == 16 || key == 17 || key == 18 || key == 20 || key == 91 || key == 92 || (key >= 160 && key <= 165); }
        IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            int message = wParam.ToInt32();
            if (code >= 0 && running && enabled && (message == 0x201 || message == 0x204 || message == 0x207))
                Enqueue(new Stroke { Break = true, Version = ContextVersion });
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }
        void Enqueue(Stroke stroke)
        {
            if (Interlocked.Increment(ref queuedCount) > 512) { Interlocked.Decrement(ref queuedCount); Clear(); return; }
            events.Enqueue(stroke); Signal();
        }
        void QueueAnimation(int key)
        {
            Interlocked.Exchange(ref latestAnimationKey, key);
            if (Interlocked.CompareExchange(ref queuedAnimation, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { var handler = KeyPressed; if (handler != null && running && !disposed) handler(Interlocked.Exchange(ref latestAnimationKey, 0)); }
                catch { }
                finally { Interlocked.Exchange(ref queuedAnimation, 0); }
            });
        }
        void ReadLoop()
        {
            var buffer = new TypedTextBuffer();
            int version = ContextVersion;
            string pending = "", emitted = "", deadAccent = "";
            bool fresh = true, korean = false, blocked = false;
            long due = 0, lastCharacter = 0, checkedAt = 0, modeAt = 0;
            IntPtr window = IntPtr.Zero, focus = IntPtr.Zero, layout = IntPtr.Zero;
            try
            {
                while (running)
                {
                    try
                    {
                    if (version != ContextVersion)
                    {
                        version = ContextVersion; buffer.Clear(); pending = emitted = deadAccent = "";
                        due = 0; fresh = true; window = IntPtr.Zero; ReportObserved(0);
                    }
                    Stroke stroke;
                    while (events.TryDequeue(out stroke))
                    {
                        Interlocked.Decrement(ref queuedCount);
                        if (!enabled || stroke.Version != version) continue;
                        if (stroke.Break) { fresh = true; checkedAt = modeAt = 0; continue; }
                        if (stroke.Control && !stroke.AltGr || stroke.Alt && !stroke.AltGr || stroke.Windows ||
                            stroke.Key == 9 || stroke.Key == 27 || stroke.Key == 46 || stroke.Key >= 33 && stroke.Key <= 40)
                        { fresh = true; checkedAt = modeAt = 0; deadAccent = ""; continue; }
                        if (stroke.Window == IntPtr.Zero) continue;
                        var gui = new GuiThreadInfo { Size = Marshal.SizeOf(typeof(GuiThreadInfo)) };
                        IntPtr nextFocus = GetGUIThreadInfo(stroke.Thread, ref gui) ? gui.Focus : stroke.Window;
                        bool changed = window != stroke.Window || focus != nextFocus || layout != stroke.Layout;
                        if (changed)
                        {
                            window = stroke.Window; focus = nextFocus; layout = stroke.Layout; fresh = true; checkedAt = modeAt = 0; deadAccent = "";
                        }
                        if (checkedAt == 0 || Elapsed(checkedAt) >= 750)
                        {
                            blocked = IsExcluded(stroke.Process) || IsProtectedField(window, focus);
                            checkedAt = Stopwatch.GetTimestamp();
                        }
                        if (blocked)
                        {
                            buffer.Clear(); pending = emitted = ""; due = 0; fresh = true; ReportObserved(0);
                            SetStatus("암호 입력칸 또는 제외 앱 · 문자를 기록하지 않음"); continue;
                        }
                        if (stroke.Key == 21) { buffer.Commit(); korean = !korean; modeAt = 0; continue; }
                        if (inputMode == "korean") korean = true;
                        else if (inputMode == "latin") korean = false;
                        else if (modeAt == 0 || Elapsed(modeAt) >= 500)
                        {
                            bool native;
                            if (TryReadKoreanMode(focus, window, layout, out native)) korean = native;
                            modeAt = Stopwatch.GetTimestamp();
                        }
                        if (stroke.Key == 13) { buffer.Commit(); fresh = true; continue; }
                        if (stroke.Key == 8)
                        {
                            buffer.Backspace();
                            if (due != 0) { pending = buffer.Text.Trim(); if (pending.Length == 0) due = 0; }
                            ReportObserved(buffer.Text.Length); continue;
                        }
                        string characters = Characters(stroke, korean, ref deadAccent);
                        if (characters.Length == 0) continue;
                        if (fresh || lastCharacter != 0 && Milliseconds(lastCharacter, stroke.Time) > 1800)
                        { buffer.Clear(); emitted = ""; fresh = false; }
                        buffer.Append(characters, korean && stroke.Key != 231);
                        lastCharacter = stroke.Time;
                        pending = buffer.Text.Trim();
                        ReportObserved(pending.Length);
                        if (pending.Length > 0 && pending != emitted && due == 0)
                            due = stroke.Time + debounceMilliseconds * Stopwatch.Frequency / 1000;
                        SetStatus("전역 문자 입력 · " + (korean ? "한글 두벌식" : "현재 키보드 문자") + " · " + pending.Length + "자 기록");
                    }
                    if (due != 0 && Stopwatch.GetTimestamp() >= due)
                    {
                        due = 0;
                        if (enabled && version == ContextVersion && pending.Length > 0 && pending != emitted)
                        {
                            emitted = pending;
                            var contextual = TextReadyWithContext; if (contextual != null) contextual(pending, version);
                            var callback = TextReady; if (callback != null) callback(pending);
                        }
                    }
                    if (!running) break;
                    int wait = due == 0 ? Timeout.Infinite : Math.Max(1, (int)Math.Min(5000, (due - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency));
                    wake.WaitOne(wait);
                    }
                    catch (Exception)
                    {
                        buffer.Clear(); pending = emitted = deadAccent = ""; due = 0; fresh = true;
                        SetStatus("입력 처리 오류 · 다음 문자 입력 대기");
                        if (running) wake.WaitOne();
                    }
                }
            }
            finally { buffer.Clear(); if (disposed) DisposeWake(); }
        }
        static string Characters(Stroke stroke, bool korean, ref string deadAccent)
        {
            int key = stroke.Key;
            if (key == 229) key = (int)MapVirtualKeyEx(stroke.Scan, 3, stroke.Layout);
            if (key == 231) return Char.IsControl((char)stroke.Scan) ? "" : ((char)stroke.Scan).ToString();
            if (key < 0 || key > 255) return "";
            if (korean && key >= 65 && key <= 90) return ((char)(stroke.Shift ? key : key + 32)).ToString();
            var state = new byte[256]; state[key] = 128;
            if (stroke.Shift) state[16] = 128;
            if (stroke.Control) state[17] = 128;
            if (stroke.Alt) state[18] = 128;
            if (stroke.Caps) state[20] = 1;
            var output = new StringBuilder(16);
            int count = ToUnicodeEx((uint)key, stroke.Scan, state, output, output.Capacity, 4, stroke.Layout);
            if (count < 0) { deadAccent = output.ToString(); return ""; }
            if (count == 0) return "";
            string text = output.ToString(0, Math.Min(count, output.Length));
            if (deadAccent.Length > 0)
            {
                string accent = deadAccent == "´" ? "\u0301" : deadAccent == "\u0060" ? "\u0300" : deadAccent == "^" ? "\u0302" : deadAccent == "~" ? "\u0303" : deadAccent == "¨" ? "\u0308" : null;
                text = text == " " ? deadAccent : accent == null ? deadAccent + text : (text + accent).Normalize(); deadAccent = "";
            }
            var clean = new StringBuilder(); foreach (char c in text) if (!Char.IsControl(c)) clean.Append(c);
            return clean.ToString();
        }
        static bool TryReadKoreanMode(IntPtr focus, IntPtr window, IntPtr layout, out bool korean)
        {
            korean = false;
            if ((layout.ToInt64() & 0xffff) != 0x0412) return true;
            IntPtr ime = ImmGetDefaultIMEWnd(focus == IntPtr.Zero ? window : focus);
            if (ime == IntPtr.Zero) ime = ImmGetDefaultIMEWnd(window);
            UIntPtr mode;
            if (ime == IntPtr.Zero || SendMessageTimeout(ime, 0x283, (IntPtr)1, IntPtr.Zero, 2, 25, out mode) == IntPtr.Zero) return false;
            korean = (mode.ToUInt64() & 1) != 0; return true;
        }
        bool IsExcluded(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) foreach (string name in excludedProcesses) if (String.Equals(name, process.ProcessName, StringComparison.OrdinalIgnoreCase)) return true; }
            catch { return true; }
            return false;
        }
        static bool IsProtectedField(IntPtr window, IntPtr focus)
        {
            var className = new StringBuilder(128);
            if (focus != IntPtr.Zero && GetClassName(focus, className, 128) > 0 &&
                className.ToString().IndexOf("edit", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (GetWindowLong(focus, -16).ToInt64() & 0x20) != 0) return true;
            // Optional password metadata only. Unsupported controls still work.
            if (GetForegroundWindow() != window) return false;
            try
            {
                var element = AutomationElement.FocusedElement;
                for (int i = 0; element != null && i < 8; i++)
                {
                    var info = element.Current;
                    if (info.IsPassword) return true;
                    if (info.NativeWindowHandle == window.ToInt64()) break;
                    element = TreeWalker.ControlViewWalker.GetParent(element);
                }
            }
            catch { }
            return false;
        }
        void ReportObserved(int count) { var handler = TextObserved; if (handler != null) handler(count); }
        void SetStatus(string value) { if (lastStatus == value) return; lastStatus = value; var handler = StatusChanged; if (handler != null) handler(value); }
        static int Elapsed(long since) { return Milliseconds(since, Stopwatch.GetTimestamp()); }
        static int Milliseconds(long since, long now) { return (int)Math.Min(Int32.MaxValue, Math.Max(0, (now - since) * 1000 / Stopwatch.Frequency)); }
        public void Dispose()
        {
            lock (lifecycle)
            {
                if (disposed) return;
                enabled = false; running = false; Interlocked.Increment(ref generation); Signal(); disposed = true;
            }
            if (hookThreadId != 0) PostThreadMessage(hookThreadId, 0x12, IntPtr.Zero, IntPtr.Zero);
            if (hookThread != null && hookThread != Thread.CurrentThread) hookThread.Join(400);
            if (readerThread == null || readerThread != Thread.CurrentThread && readerThread.Join(400)) DisposeWake();
        }
        void DisposeWake() { if (Interlocked.Exchange(ref wakeDisposed, 1) == 0) wake.Dispose(); }
        sealed class Stroke
        {
            public int Key, Process, Version; public uint Scan, Thread;
            public IntPtr Window, Layout; public bool Shift, Control, Alt, AltGr, Windows, Caps, Break; public long Time;
        }
        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] struct KeyboardData { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct NativePoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct NativeMessage { public IntPtr H; public uint Message; public UIntPtr W; public IntPtr L; public uint Time; public NativePoint Point; public uint Private; }
        [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct GuiThreadInfo { public int Size; public uint Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public NativeRect CaretRect; }
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] static extern short GetKeyState(int key);
        [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder output, int length, uint flags, IntPtr layout);
        [DllImport("user32.dll")] static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr layout);
        [DllImport("imm32.dll")] static extern IntPtr ImmGetDefaultIMEWnd(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int count);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref NativeMessage message);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref NativeMessage message);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
    }
}
