using System.Runtime.InteropServices;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// X11 global hotkeys via <c>XGrabKey</c>. Windows uses <c>RegisterHotKey</c>; X11
/// offers the same capability, so on an X11 session Linux reaches feature parity.
/// Under Wayland a client cannot grab global keys, so the factory returns the null
/// service and the desktop's own custom shortcuts are used instead.
/// </summary>
internal sealed class LinuxGlobalHotkeyService : IGlobalHotkeyService
{
    private const int KeyPress = 2;
    private const int GrabModeAsync = 1;
    private const uint ShiftMask = 1;
    private const uint LockMask = 2;
    private const uint ControlMask = 4;
    private const uint Mod1Mask = 8;
    private const uint Mod2Mask = 16;
    private const int KeyBackspace = 0xFF08;
    private const int KeyX = 0x0078;
    /// <summary>Windows StockWindow registers Ctrl+Alt+Q for the stock capsule.</summary>
    private const int KeyQ = 0x0071;
    private const int EventBufferSize = 192;

    private static bool _grabDenied;

    private readonly List<(int Keycode, uint Modifiers)> _grabs = [];
    private readonly ManualResetEventSlim _started = new();
    private Thread? _thread;
    private IntPtr _display;
    private volatile bool _running;
    private Action<AppCommand>? _callback;
    private ErrorHandler? _errorHandler;
    private IntPtr _handlerPointer;
    private IntPtr _previousErrorHandler;

    public bool IsRegistered { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>Chords that were grabbed successfully, for diagnostics.</summary>
    public IReadOnlyList<string> RegisteredChords => _registeredChords;

    /// <summary>Chords the desktop refused to hand over, for diagnostics.</summary>
    public IReadOnlyList<string> FailedChords => _failedChords;

    private readonly List<string> _registeredChords = [];
    private readonly List<string> _failedChords = [];

    public void Start(Action<AppCommand> callback)
    {
        if (_thread is not null) return;
        _callback = callback;
        _thread = new Thread(Run) { IsBackground = true, Name = "LittleTools X11 hotkeys" };
        _thread.Start();
        _started.Wait(TimeSpan.FromSeconds(3));
    }

    private void Run()
    {
        try
        {
            _display = XOpenDisplay(null);
            if (_display == IntPtr.Zero)
            {
                FailureReason = "无法连接 X 服务器。";
                return;
            }

            _errorHandler = OnXError;
            _handlerPointer = Marshal.GetFunctionPointerForDelegate(_errorHandler);
            _previousErrorHandler = XSetErrorHandler(_handlerPointer);
            var root = XDefaultRootWindow(_display);

            Register(root, ShiftMask, KeyBackspace, AppCommand.ShowTranslation, "Shift+Backspace");
            Register(root, ControlMask, KeyBackspace, AppCommand.ShowChat, "Ctrl+Backspace");
            Register(root, ControlMask | Mod1Mask, KeyX, AppCommand.Screenshot, "Ctrl+Alt+X");
            // The stock capsule's own Windows hotkey (StockWindow.cs L148-L151).
            Register(root, ControlMask | Mod1Mask, KeyQ, AppCommand.ToggleStock, "Ctrl+Alt+Q");

            XSync(_display, false);
            XSetErrorHandler(_previousErrorHandler);
            _previousErrorHandler = IntPtr.Zero;
            _grabDenied = false;

            IsRegistered = _grabs.Count > 0;
            if (_failedChords.Count > 0)
                FailureReason = "以下快捷键已被其它程序占用：" + string.Join("、", _failedChords);
            if (!IsRegistered)
            {
                UngrabAll(root);
                FailureReason ??= "快捷键已被其它程序占用。";
                return;
            }

            _running = true;
            while (_running)
            {
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, out var xevent);
                    if (xevent.type != KeyPress) continue;
                    var state = xevent.state & ~(LockMask | Mod2Mask);
                    foreach (var (keycode, modifiers) in _grabs)
                    {
                        if (xevent.keycode != keycode || state != modifiers) continue;
                        _callback?.Invoke(CommandFor(modifiers, keycode));
                        break;
                    }
                }
                Thread.Sleep(20);
            }

            UngrabAll(root);
        }
        catch (Exception exception)
        {
            FailureReason = exception.Message;
        }
        finally
        {
            if (_display != IntPtr.Zero)
            {
                XCloseDisplay(_display);
                _display = IntPtr.Zero;
            }
            IsRegistered = false;
            _started.Set();
        }
    }

    /// <summary>
    /// Grabs one chord for every CapsLock/NumLock combination. A chord only counts
    /// as registered when all four variants were granted, otherwise the shortcut
    /// would stop working as soon as a lock modifier is active, and the partial
    /// grabs are rolled back so another application can still use the chord.
    /// </summary>
    private void Register(IntPtr root, uint modifiers, int keysym, AppCommand command, string name)
    {
        var keycode = XKeysymToKeycode(_display, (IntPtr)keysym);
        if (keycode == 0)
        {
            _failedChords.Add(name);
            return;
        }

        var granted = new List<(int Keycode, uint Modifiers)>();
        var denied = false;
        foreach (var variant in new[] { modifiers, modifiers | LockMask, modifiers | Mod2Mask, modifiers | LockMask | Mod2Mask })
        {
            _grabDenied = false;
            XGrabKey(_display, keycode, variant, root, 1, GrabModeAsync, GrabModeAsync);
            XSync(_display, false);
            if (_grabDenied)
            {
                denied = true;
                continue;
            }
            granted.Add((keycode, variant));
        }

        if (denied)
        {
            foreach (var (code, variant) in granted) XUngrabKey(_display, code, variant, root);
            XSync(_display, false);
            _failedChords.Add(name);
            return;
        }

        foreach (var (code, variant) in granted)
        {
            _grabs.Add((code, variant));
            _commands[(code, variant)] = command;
        }
        _registeredChords.Add(name);
    }

    private readonly Dictionary<(int Keycode, uint Modifiers), AppCommand> _commands = [];

    private AppCommand CommandFor(uint modifiers, int keycode) =>
        _commands.TryGetValue((keycode, modifiers), out var command) ? command : AppCommand.Background;

    private void UngrabAll(IntPtr root)
    {
        if (_display == IntPtr.Zero) return;
        foreach (var (keycode, modifiers) in _grabs) XUngrabKey(_display, keycode, modifiers, root);
        XSync(_display, false);
        _grabs.Clear();
    }

    private int OnXError(IntPtr display, IntPtr errorEvent)
    {
        try
        {
            var error = Marshal.PtrToStructure<XErrorEvent>(errorEvent);
            if (error.error_code == 10) _grabDenied = true; // BadAccess
        }
        catch
        {
            _grabDenied = true;
        }
        return 0;
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(1));
        _started.Dispose();
        _errorHandler = null;
        _commands.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int type;
        public IntPtr display;
        public nuint resourceid;
        public nuint serial;
        public byte error_code;
        public byte request_code;
        public byte minor_code;
    }

    [StructLayout(LayoutKind.Sequential, Size = EventBufferSize)]
    private struct XEvent
    {
        public int type;
        public ulong serial;
        public int send_event;
        public IntPtr display;
        public IntPtr window;
        public IntPtr root;
        public IntPtr subwindow;
        public long time;
        public int x;
        public int y;
        public int x_root;
        public int y_root;
        public uint state;
        public uint keycode;
        public int same_screen;
    }

    private delegate int ErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport("libX11.so.6")] private static extern IntPtr XOpenDisplay(string? display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(IntPtr display);
    [DllImport("libX11.so.6")] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport("libX11.so.6")] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport("libX11.so.6")] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr grabWindow);
    [DllImport("libX11.so.6")] private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("libX11.so.6")] private static extern int XPending(IntPtr display);
    [DllImport("libX11.so.6")] private static extern int XNextEvent(IntPtr display, out XEvent xevent);
    [DllImport("libX11.so.6")] private static extern IntPtr XSetErrorHandler(IntPtr handler);
}
