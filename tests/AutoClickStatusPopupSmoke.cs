using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LittleTools.Manager;
using Drawing = System.Drawing;

internal static class AutoClickStatusPopupSmoke
{
    private static int checks;
    private static readonly BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var area = new Drawing.Rectangle(-1920, -200, 1920, 1080);
            var size = new Drawing.Size(280, 44);
            foreach (var cursor in new Drawing.Point[]
            {
                new Drawing.Point(-1900, -190), new Drawing.Point(-10, -190),
                new Drawing.Point(-1900, 870), new Drawing.Point(-10, 870), new Drawing.Point(-900, 400)
            })
            {
                var bounds = AutoClickStatusPopup.GetPopupBounds(area, size, cursor);
                Check(area.Contains(bounds), "Popup stays inside work area, including negative screen coordinates");
                Check(!bounds.Contains(cursor), "Popup is offset from cursor");
            }
            Directory.CreateDirectory(args[0]);
            using (var popup = new AutoClickStatusPopup())
            {
                var window = (Window)typeof(AutoClickStatusPopup).GetField("window", Instance).GetValue(popup);
                var label = (TextBlock)typeof(AutoClickStatusPopup).GetField("message", Instance).GetValue(popup);
                IntPtr foreground = GetForegroundWindow();
                popup.ShowStatus(true, 50);
                Check(window.IsVisible && label.Text.Contains("20") && label.Text.Contains("已开启"), "Started popup includes current speed");
                Check(GetForegroundWindow() == foreground, "Popup must not take foreground focus");
                Check(!window.ShowInTaskbar && !window.ShowActivated && !window.IsHitTestVisible, "Non-interactive window flags");
                IntPtr handle = new WindowInteropHelper(window).Handle;
                int styles = GetWindowLong(handle, -20);
                Check((styles & 0x080000A0) == 0x080000A0, "Native transparent, no-activate and tool-window styles");
                Check(SendMessage(handle, 0x84, IntPtr.Zero, IntPtr.Zero).ToInt32() == -1, "Native hit-test passes through");
                Check(SendMessage(handle, 0x21, IntPtr.Zero, IntPtr.Zero).ToInt32() == 3, "Mouse cannot activate popup");
                Snapshot(window, Path.Combine(args[0], "enabled.png"));
                Pump(550);
                popup.ShowStatus(false, 50);
                Check(window.IsVisible && label.Text.Contains("已停止"), "Stop replaces start message");
                Snapshot(window, Path.Combine(args[0], "stopped.png"));
                Check(GetForegroundWindow() == foreground, "Updating popup preserves foreground focus");
                Pump(700);
                Check(window.IsVisible && window.Opacity > 0.95, "New message restarts lifetime");
                Pump(330);
                Check(window.IsVisible && window.Opacity < 0.95 && window.Opacity > 0, "Popup fades after hold");
                Pump(250);
                Check(!window.IsVisible, "Popup hides after fade");
                popup.ShowStatus(true, 125);
                Check(label.Text.Contains("8"), "Fractional interval uses selected speed");
                popup.Dispose();
                Check(!window.IsVisible, "Disposal hides active popup");
                popup.ShowStatus(true, 100);
                Check(!window.IsVisible, "Disposed popup cannot reappear");
            }
            Console.WriteLine("AUTO_CLICK_STATUS_POPUP_OK checks=" + checks);
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }

    private static void Snapshot(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth), (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate { frame.Continue = false; }));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam);
}
