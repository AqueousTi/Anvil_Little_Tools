using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LittleTools.Startup
{
    internal static class Program
    {
        private const string DeferredTaskName = "Little Tools Deferred Start";

        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length == 1 && string.Equals(args[0], "--deferred", StringComparison.OrdinalIgnoreCase))
            {
                StartSuiteAfterDelay();
                return;
            }

            try
            {
                using (Process process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"),
                    Arguments = "/Run /TN \"" + DeferredTaskName + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process != null) process.WaitForExit(2500);
                }
            }
            catch
            {
                // Startup must stay silent and cheap; the tray menu can retry next time.
            }
        }

        private static void StartSuiteAfterDelay()
        {
            try
            {
                Thread.Sleep(TimeSpan.FromSeconds(30));
                string launcherDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                string root = Directory.GetParent(Directory.GetParent(launcherDir).FullName).FullName;
                string suitePath = Path.Combine(root, "LittleTools", "bin", "LittleTools.exe");
                if (File.Exists(suitePath)) Process.Start(suitePath);
            }
            catch
            {
            }
        }
    }
}
