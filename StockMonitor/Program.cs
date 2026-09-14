using System;
using System.Drawing;
using System.Threading;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace LittleTools.StockMonitor
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "LittleTools.StockMonitor.SingleInstance", out created))
            {
                if (!created) return;
                Forms.Application.EnableVisualStyles();
                var app = new WpfApplication { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                using (var controller = new StockController(app, true))
                {
                    controller.Window.Show();
                    app.Run();
                }
            }
        }
    }

    internal sealed class StockController : IDisposable
    {
        private readonly WpfApplication app;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ContextMenuStrip menu;
        private bool disposed;
        public StockWindow Window { get; private set; }
        public event Action<string, string> AlertRaised;

        public StockController(WpfApplication app, bool standaloneTray)
        {
            this.app = app;
            Window = new StockWindow();
            Window.AlertRaised += OnAlert;
            if (!standaloneTray) return;

            menu = new Forms.ContextMenuStrip();
            var show = new Forms.ToolStripMenuItem("显示 / 隐藏股票观察");
            show.Click += delegate { app.Dispatcher.BeginInvoke(new Action(Window.ToggleVisibility)); };
            menu.Items.Add(show);
            var exit = new Forms.ToolStripMenuItem("退出");
            exit.Click += delegate { app.Dispatcher.BeginInvoke(new Action(delegate { Dispose(); app.Shutdown(); })); };
            menu.Items.Add(exit);
            tray = new Forms.NotifyIcon
            {
                Icon = Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath) ?? SystemIcons.Application,
                Text = "Little Tools · 股票观察",
                Visible = true,
                ContextMenuStrip = menu
            };
            tray.DoubleClick += delegate { app.Dispatcher.BeginInvoke(new Action(Window.ToggleVisibility)); };
        }

        private void OnAlert(string title, string message)
        {
            Action<string, string> handler = AlertRaised;
            if (handler != null) handler(title, message);
            if (tray != null) tray.ShowBalloonTip(6000, title, message, Forms.ToolTipIcon.Info);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (Window != null)
            {
                Window.AlertRaised -= OnAlert;
                Window.ClosePermanently();
                Window = null;
            }
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (menu != null) menu.Dispose();
        }
    }
}
