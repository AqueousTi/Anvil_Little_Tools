using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LittleTools.Manager
{
    internal sealed class MouseAutoClicker : NativeWindow, IDisposable
    {
        private const int HotkeyId = 1;
        private const int StopHotkeyId = 2;
        public const int DefaultIntervalMilliseconds = 100;
        public const int MinimumIntervalMilliseconds = 20;
        public const int MaximumIntervalMilliseconds = 10000;
        private readonly Timer timer;
        private readonly INPUT[] click;
        private bool disposed;
        private bool stopHotkeyRegistered;

        public bool IsRunning { get; private set; }
        public bool IsHotkeyRegistered { get; private set; }
        public int HotkeyRegistrationError { get; private set; }
        public int IntervalMilliseconds
        {
            get { return timer.Interval; }
            set
            {
                if (value < MinimumIntervalMilliseconds || value > MaximumIntervalMilliseconds)
                    throw new ArgumentOutOfRangeException("value");
                timer.Interval = value;
            }
        }

        public static int NormalizeInterval(int value)
        {
            return value >= MinimumIntervalMilliseconds && value <= MaximumIntervalMilliseconds
                ? value : DefaultIntervalMilliseconds;
        }
        public event Action StateChanged;
        public event Action<string> Failed;

        public MouseAutoClicker()
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            IsHotkeyRegistered = RegisterHotKey(Handle, HotkeyId, 0x4002, 0x71); // Ctrl+F2, no repeat
            if (!IsHotkeyRegistered) HotkeyRegistrationError = Marshal.GetLastWin32Error();
            click = new INPUT[]
            {
                new INPUT { Type = 0, Mouse = new MOUSEINPUT { Flags = 0x0002 } },
                new INPUT { Type = 0, Mouse = new MOUSEINPUT { Flags = 0x0004 } }
            };
            timer = new Timer { Interval = DefaultIntervalMilliseconds };
            timer.Tick += delegate { Tick(); };
        }

        public void Toggle()
        {
            if (disposed) return;
            if (IsRunning) { Stop(); return; }
            IsRunning = true;
            // Reserve Escape only while active, so even a brief press stops before the next click.
            stopHotkeyRegistered = RegisterHotKey(Handle, StopHotkeyId, 0x4000, 0x1B);
            timer.Start();
            if (StateChanged != null) StateChanged();
        }

        public void Stop()
        {
            if (!IsRunning) return;
            timer.Stop();
            if (stopHotkeyRegistered) UnregisterHotKey(Handle, StopHotkeyId);
            stopHotkeyRegistered = false;
            IsRunning = false;
            if (StateChanged != null) StateChanged();
        }

        private void Tick()
        {
            if (disposed || !IsRunning) return;
            if (Down(0x1B)) { Stop(); return; }
            // Never turn a held shortcut into Ctrl/Shift/Alt/Win+click, or interfere with dragging.
            if (Down(0x71) || Down(0x11) || Down(0x10) || Down(0x12)
                || Down(0x5B) || Down(0x5C) || Down(1) || Down(2) || Down(4)) return;
            uint sent = SendInput((uint)click.Length, click, Marshal.SizeOf(typeof(INPUT)));
            if (sent != click.Length)
            {
                // A partially inserted pair must not leave the left button held down.
                if (sent == 1) SendInput(1, new INPUT[] { click[1] }, Marshal.SizeOf(typeof(INPUT)));
                Stop();
                if (Failed != null) Failed("无法发送鼠标点击。目标窗口可能以管理员身份运行，请检查权限后重试。");
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x312 && message.WParam.ToInt32() == HotkeyId) Toggle();
            else if (message.Msg == 0x312 && message.WParam.ToInt32() == StopHotkeyId) Stop();
            base.WndProc(ref message);
        }

        private static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Stop();
            timer.Dispose();
            if (IsHotkeyRegistered) UnregisterHotKey(Handle, HotkeyId);
            IsHotkeyRegistered = false;
            DestroyHandle();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public MOUSEINPUT Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int X, Y;
            public uint MouseData, Flags, Time;
            public UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr handle, int id);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    }
}
