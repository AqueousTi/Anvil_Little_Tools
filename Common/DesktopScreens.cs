using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace LittleTools.Common
{
    internal static class DesktopScreens
    {
        public static Rect GetWorkingArea(Window window)
        {
            Matrix toDevice = GetTransformToDevice(window);
            Forms.Screen screen = IsFinite(window.Left) && IsFinite(window.Top)
                ? Forms.Screen.FromRectangle(ToDeviceBounds(window.Left, window.Top, window.Width, window.Height, toDevice))
                : Forms.Screen.PrimaryScreen;
            Matrix fromDevice = toDevice;
            fromDevice.Invert();
            System.Drawing.Rectangle area = screen.WorkingArea;
            return new Rect(fromDevice.Transform(new Point(area.Left, area.Top)),
                fromDevice.Transform(new Point(area.Right, area.Bottom)));
        }

        public static bool IsOnScreen(Window window, double left, double top, double width, double height)
        {
            if (!IsFinite(left) || !IsFinite(top)) return false;
            System.Drawing.Rectangle bounds = ToDeviceBounds(left, top, width, height, GetTransformToDevice(window));
            foreach (Forms.Screen screen in Forms.Screen.AllScreens)
                if (screen.WorkingArea.IntersectsWith(bounds)) return true;
            return false;
        }

        private static System.Drawing.Rectangle ToDeviceBounds(double left, double top, double width, double height, Matrix transform)
        {
            Point start = transform.Transform(new Point(left, top));
            Point end = transform.Transform(new Point(left + width, top + height));
            return System.Drawing.Rectangle.FromLTRB((int)Math.Floor(start.X), (int)Math.Floor(start.Y),
                (int)Math.Ceiling(end.X), (int)Math.Ceiling(end.Y));
        }

        private static Matrix GetTransformToDevice(Window window)
        {
            PresentationSource source = PresentationSource.FromVisual(window);
            if (source != null && source.CompositionTarget != null)
                return source.CompositionTarget.TransformToDevice;

            // Restored positions are checked before WPF creates the window handle.
            IntPtr dc = GetDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) return Matrix.Identity;
            try { return new Matrix(GetDeviceCaps(dc, 88) / 96.0, 0, 0, GetDeviceCaps(dc, 90) / 96.0, 0, 0); }
            finally { ReleaseDC(IntPtr.Zero, dc); }
        }

        private static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr dc, int index);
    }
}
