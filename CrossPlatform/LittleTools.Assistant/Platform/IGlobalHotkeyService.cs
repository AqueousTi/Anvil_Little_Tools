using System.Runtime.InteropServices;

namespace LittleTools.Assistant.Platform;

internal interface IGlobalHotkeyService : IDisposable
{
    bool IsRegistered { get; }
    void Start(Action callback);
}

internal static class GlobalHotkeyServiceFactory
{
    public static IGlobalHotkeyService Create() => OperatingSystem.IsWindows()
        ? new WindowsGlobalHotkeyService()
        : new NullGlobalHotkeyService();
}

internal sealed class NullGlobalHotkeyService : IGlobalHotkeyService
{
    public bool IsRegistered => false;
    public void Start(Action callback) { }
    public void Dispose() { }
}

internal sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int HotkeyId = 0x4C54;
    private const uint ModShift = 0x0004;
    private const uint VkBack = 0x08;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private Thread? _thread;
    private uint _threadId;
    private Action? _callback;
    private readonly ManualResetEventSlim _started = new();

    public bool IsRegistered { get; private set; }

    public void Start(Action callback)
    {
        if (_thread is not null) return;
        _callback = callback;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "LittleTools hotkey" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(2));
    }

    private void MessageLoop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        IsRegistered = RegisterHotKey(IntPtr.Zero, HotkeyId, ModShift, VkBack);
        _started.Set();
        if (!IsRegistered) return;
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message == WmHotkey && message.wParam == (IntPtr)HotkeyId)
                _callback?.Invoke();
        }
        UnregisterHotKey(IntPtr.Zero, HotkeyId);
        IsRegistered = false;
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(1));
        _started.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point point;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
