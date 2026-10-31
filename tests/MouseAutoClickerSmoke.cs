using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using LittleTools.Manager;

internal static class MouseAutoClickerSmoke
{
    private static Form target;
    private static MouseAutoClicker clicker;
    private static int clicks;
    private static int checks;

    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Length == 1 && arguments[0] == "--state-only") return TestState();
        Point originalCursor = Cursor.Position;
        IntPtr originalForeground = GetForegroundWindow();
        try
        {
            Application.EnableVisualStyles();
            using (target = new Form { Text = "Little Tools auto click test", Size = new Size(480, 260),
                StartPosition = FormStartPosition.CenterScreen, TopMost = true })
            using (clicker = new MouseAutoClicker())
            {
                Check(clicker.IsHotkeyRegistered, "Ctrl+F2 registration (exit the suite before running this test)");
                Check(!clicker.IsRunning, "Must start stopped");
                clicker.Failed += delegate(string message) { throw new Exception(message); };
                target.MouseClick += delegate(object sender, MouseEventArgs args)
                {
                    Check(args.Button == MouseButtons.Left, "Expected left clicks");
                    Check(Control.ModifierKeys == Keys.None, "Click must not contain modifiers");
                    clicks++;
                };
                target.Show();
                target.Activate();
                SetForegroundWindow(target.Handle);
                Cursor.Position = target.PointToScreen(new Point(180, 120));
                Pump(150);

                Key(0x11, false); Key(0x71, false);
                Pump(300);
                Check(clicker.IsRunning, "Real global Ctrl+F2 starts");
                Check(clicks == 0, "Holding shortcut must not click");
                Key(0x71, true); Key(0x11, true);
                Pump(550);
                Check(clicks >= 3 && clicks <= 7, "Actual native input clicks the target at about 10 Hz: " + clicks);

                Key(0x10, false);
                int before = clicks;
                Pump(300);
                Check(clicks == before, "Held Shift pauses clicks");
                Key(0x10, true);
                Pump(250);
                Check(clicks > before, "Clicks resume after releasing Shift");

                Key(0x11, false); Key(0x71, false);
                Pump(300);
                Check(!clicker.IsRunning, "Second Ctrl+F2 stops without repeat toggles");
                Key(0x71, true); Key(0x11, true);
                before = clicks;
                Pump(250);
                Check(clicks == before, "No clicks after stopping");

                Shortcut(); Pump(250);
                Check(clicker.IsRunning, "Restart with shortcut");
                Key(0x1B, false); Pump(200); Key(0x1B, true);
                Check(!clicker.IsRunning, "Escape stops");
                before = clicks; Pump(250);
                Check(clicks == before, "Escape leaves no pending clicks");

                Shortcut(); Pump(150);
                Check(clicker.IsRunning, "Start before disposal");
                clicker.Dispose();
                before = clicks; Pump(250);
                Check(!clicker.IsRunning && clicks == before, "Disposal stops input");
                using (var replacement = new MouseAutoClicker())
                    Check(replacement.IsHotkeyRegistered, "Disposal releases shortcut");
                Console.WriteLine("MOUSE_AUTO_CLICKER_SMOKE_OK checks=" + checks + " clicks=" + clicks);
            }
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally
        {
            if (clicker != null) clicker.Dispose();
            Key(0x71, true); Key(0x11, true); Key(0x10, true); Key(0x1B, true);
            Cursor.Position = originalCursor;
            if (originalForeground != IntPtr.Zero) SetForegroundWindow(originalForeground);
        }
    }

    private static int TestState()
    {
        try
        {
            int changes = 0;
            using (var service = new MouseAutoClicker())
            {
                Check(!service.IsRunning, "Initial stopped state");
                Check(service.IntervalMilliseconds == 100, "Existing default speed is preserved");
                Check(MouseAutoClicker.NormalizeInterval(0) == 100 && MouseAutoClicker.NormalizeInterval(-1) == 100
                    && MouseAutoClicker.NormalizeInterval(int.MaxValue) == 100, "Corrupt saved speed falls back to default");
                Check(MouseAutoClicker.NormalizeInterval(20) == 20 && MouseAutoClicker.NormalizeInterval(10000) == 10000,
                    "Custom speed boundaries are preserved");
                bool rejected = false;
                try { service.IntervalMilliseconds = 0; } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected && service.IntervalMilliseconds == 100, "Invalid update does not alter current speed");
                service.IntervalMilliseconds = 250;
                service.StateChanged += delegate { changes++; };
                // Dispatch the native hotkey messages synchronously; never pump a click timer.
                SendMessage(service.Handle, 0x312, new IntPtr(1), IntPtr.Zero);
                Check(service.IsRunning && changes == 1, "Hotkey starts and updates state");
                service.IntervalMilliseconds = 20;
                Check(service.IsRunning && service.IntervalMilliseconds == 20, "Changing speed preserves active state");
                SendMessage(service.Handle, 0x312, new IntPtr(1), IntPtr.Zero);
                Check(!service.IsRunning && changes == 2, "Same hotkey stops");
                Check(service.IntervalMilliseconds == 20, "Stopping preserves selected speed");
                service.Stop();
                Check(changes == 2, "Stopping twice is idempotent");
                SendMessage(service.Handle, 0x312, new IntPtr(1), IntPtr.Zero);
                SendMessage(service.Handle, 0x312, new IntPtr(2), IntPtr.Zero);
                Check(!service.IsRunning && changes == 4, "Escape message stops");
                service.Toggle();
                service.Dispose();
                Check(!service.IsRunning && changes == 6, "Disposal stops active service");
                service.Toggle(); service.Dispose();
                Check(!service.IsRunning && changes == 6, "Disposed service cannot restart");
            }
            Console.WriteLine("MOUSE_AUTO_CLICKER_STATE_OK checks=" + checks);
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Shortcut()
    {
        Key(0x11, false); Key(0x71, false); Pump(40);
        Key(0x71, true); Key(0x11, true);
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            // Abort before pumping click timers if the test target loses the cursor or focus.
            if (!target.RectangleToScreen(target.ClientRectangle).Contains(Cursor.Position)
                || GetForegroundWindow() != target.Handle)
            {
                clicker.Stop();
                throw new Exception("Test target lost cursor or focus; stopped input. Cursor=" + Cursor.Position
                    + " Bounds=" + target.RectangleToScreen(target.ClientRectangle)
                    + " Foreground=" + GetForegroundWindow() + " Target=" + target.Handle);
            }
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    private static void Key(byte key, bool up) { keybd_event(key, 0, up ? 2u : 0u, UIntPtr.Zero); }
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);
}
