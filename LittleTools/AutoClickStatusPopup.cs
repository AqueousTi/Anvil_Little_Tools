using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace LittleTools.Manager
{
    internal sealed class AutoClickStatusPopup : IDisposable
    {
        private readonly Window window;
        private readonly TextBlock message;
        private readonly Border surface;
        private readonly DispatcherTimer timer;
        private readonly Stopwatch elapsed = new Stopwatch();
        private bool disposed;

        public AutoClickStatusPopup()
        {
            message = new TextBlock { FontSize = 13, FontFamily = new FontFamily("Microsoft YaHei UI") };
            surface = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(245, 23, 28, 36)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
                Padding = new Thickness(14, 10, 14, 10), Child = message
            };
            window = new Window
            {
                Title = "Little Tools · 连点提示", Content = surface,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true, Background = Brushes.Transparent,
                ShowActivated = false, ShowInTaskbar = false, Topmost = true,
                Focusable = false, IsHitTestVisible = false,
                SizeToContent = SizeToContent.WidthAndHeight, Opacity = 0
            };
            window.SourceInitialized += delegate
            {
                var source = (HwndSource)PresentationSource.FromVisual(window);
                int styles = GetWindowLong(source.Handle, -20);
                SetWindowLong(source.Handle, -20, styles | 0x20 | 0x80 | 0x08000000);
                source.AddHook(WindowMessage);
            };
            timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
            timer.Tick += delegate
            {
                double age = elapsed.Elapsed.TotalMilliseconds;
                if (age >= 1150)
                {
                    timer.Stop();
                    window.Hide();
                }
                else window.Opacity = age <= 900 ? 1 : (1150 - age) / 250;
            };
        }

        public void ShowStatus(bool running, int intervalMilliseconds)
        {
            if (disposed) return;
            message.Text = running
                ? "▶ 连点已开启 · 约 " + (1000.0 / intervalMilliseconds).ToString("0.##") + " 次/秒"
                : "■ 连点已停止";
            message.Foreground = new SolidColorBrush(running ? Color.FromRgb(110, 231, 165) : Color.FromRgb(194, 202, 214));
            surface.BorderBrush = new SolidColorBrush(running ? Color.FromArgb(150, 66, 153, 108) : Color.FromArgb(150, 91, 103, 120));

            Drawing.Point cursor = Forms.Cursor.Position;
            // Position the native window in pixels so mixed-DPI and negative-coordinate screens work.
            window.Opacity = 0;
            window.Show();
            window.UpdateLayout();
            IntPtr handle = new WindowInteropHelper(window).Handle;
            RECT rectangle;
            if (GetWindowRect(handle, out rectangle))
            {
                Drawing.Rectangle bounds = GetPopupBounds(Forms.Screen.FromPoint(cursor).WorkingArea,
                    new Drawing.Size(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top), cursor);
                SetWindowPos(handle, new IntPtr(-1), bounds.X, bounds.Y, 0, 0, 0x0011); // no size, no activate
            }
            window.Opacity = 1;
            elapsed.Restart();
            timer.Start();
        }

        internal static Drawing.Rectangle GetPopupBounds(Drawing.Rectangle area, Drawing.Size size, Drawing.Point cursor)
        {
            const int gap = 18, margin = 6;
            int x = cursor.X + gap;
            int y = cursor.Y - gap - size.Height;
            if (x + size.Width > area.Right - margin) x = cursor.X - gap - size.Width;
            if (y < area.Top + margin) y = cursor.Y + gap;
            x = Math.Max(area.Left + margin, Math.Min(x, area.Right - margin - size.Width));
            y = Math.Max(area.Top + margin, Math.Min(y, area.Bottom - margin - size.Height));
            return new Drawing.Rectangle(x, y, size.Width, size.Height);
        }

        private static IntPtr WindowMessage(IntPtr handle, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (message == 0x84) { handled = true; return new IntPtr(-1); } // HTTRANSPARENT
            if (message == 0x21) { handled = true; return new IntPtr(3); } // MA_NOACTIVATE
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            window.Close();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr handle, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr handle, int index, int value);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out RECT rectangle);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
