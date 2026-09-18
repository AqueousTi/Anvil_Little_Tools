using System.Runtime.InteropServices;

namespace LittleTools.Assistant.Platform;

internal interface IGlobalHotkeyService : IDisposable
{
    bool IsRegistered { get; }
    void Start(Action<AppCommand> callback);
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
    public void Start(Action<AppCommand> callback) { }
    public void Dispose() { }
}

internal sealed class WindowsGlobalHotkeyService : IGlobalHotkeyService
{
    private const int TranslateHotkeyId = 0x4C54;
    private const int ChatHotkeyId = 0x4C55;
    private const int ScreenshotHotkeyId = 0x4C56;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkBack = 0x08;
    private const uint VkX = 0x58;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private Thread? _thread;
    private uint _threadId;
    private Action<AppCommand>? _callback;
    private readonly List<int> _registeredIds = [];
    private readonly ManualResetEventSlim _started = new();
    private readonly ManualResetEventSlim _stopping = new();
    private KeyboardHookProc? _keyboardHookProc;
    private IntPtr _keyboardHook;
    private bool _screenshotPressed;
    private bool _translateFallback;
    private bool _chatFallback;
    private bool _screenshotFallback;
    private bool _backspacePressed;

    public bool IsRegistered { get; private set; }

    public void Start(Action<AppCommand> callback)
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
        _translateFallback = !Register(TranslateHotkeyId, ModShift | ModNoRepeat, VkBack);
        _chatFallback = !Register(ChatHotkeyId, ModControl | ModNoRepeat, VkBack);
        _screenshotFallback = !Register(ScreenshotHotkeyId, ModControl | ModAlt | ModNoRepeat, VkX);
        if (_translateFallback || _chatFallback || _screenshotFallback)
        {
            _keyboardHookProc = KeyboardHookCallback;
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardHookProc, IntPtr.Zero, 0);
        }
        IsRegistered = _registeredIds.Count > 0 || _keyboardHook != IntPtr.Zero;
        _started.Set();
        if (!IsRegistered) return;
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message != WmHotkey) continue;
            var command = message.wParam.ToInt32() switch
            {
                TranslateHotkeyId => AppCommand.ShowTranslation,
                ChatHotkeyId => AppCommand.ShowChat,
                ScreenshotHotkeyId => AppCommand.Screenshot,
                _ => AppCommand.Background
            };
            if (command != AppCommand.Background) _callback?.Invoke(command);
        }
        foreach (var id in _registeredIds) UnregisterHotKey(IntPtr.Zero, id);
        _registeredIds.Clear();
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        IsRegistered = false;
    }

    private bool Register(int id, uint modifiers, uint key)
    {
        if (!RegisterHotKey(IntPtr.Zero, id, modifiers, key)) return false;
        _registeredIds.Add(id);
        return true;
    }

    private IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var message = unchecked((uint)wParam.ToInt64());
            var data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
            if (data.vkCode == VkBack)
            {
                if (message is WmKeyUp or WmSysKeyUp)
                {
                    var handled = _backspacePressed;
                    _backspacePressed = false;
                    if (handled) return new IntPtr(1);
                }
                else if (message is WmKeyDown or WmSysKeyDown)
                {
                    if (_backspacePressed) return new IntPtr(1);
                    var shift = IsKeyDown(0x10);
                    var control = IsKeyDown(VkControl);
                    if (!IsKeyDown(VkMenu) && ((_translateFallback && shift && !control) || (_chatFallback && control && !shift)))
                    {
                        _backspacePressed = true;
                        _callback?.Invoke(shift ? AppCommand.ShowTranslation : AppCommand.ShowChat);
                        return new IntPtr(1);
                    }
                }
            }
            if (_screenshotFallback && data.vkCode == VkX)
            {
                if (message is WmKeyUp or WmSysKeyUp)
                {
                    var handled = _screenshotPressed;
                    _screenshotPressed = false;
                    if (handled) return new IntPtr(1);
                }
                else if (message is WmKeyDown or WmSysKeyDown &&
                         !_screenshotPressed && IsKeyDown(VkControl) && IsKeyDown(VkMenu))
                {
                    _screenshotPressed = true;
                    _callback?.Invoke(AppCommand.Screenshot);
                    return new IntPtr(1);
                }
            }
        }
        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Dispose()
    {
        _stopping.Set();
        if (_threadId != 0) PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(TimeSpan.FromSeconds(1));
        _started.Dispose();
        _stopping.Dispose();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr extraInfo;
    }

    private delegate IntPtr KeyboardHookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int hookId, KeyboardHookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
