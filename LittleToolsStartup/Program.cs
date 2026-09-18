using System;
using System.Diagnostics;
using System.IO;

namespace LittleTools.Startup
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // Compatibility entry for old shortcuts; new startup entries use the manager directly.
            StartSuite();
        }

        private static void StartSuite()
        {
            try
            {
                string launcherDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                string root = Directory.GetParent(Directory.GetParent(launcherDir).FullName).FullName;
                string suitePath = Path.Combine(root, "LittleTools", "bin", "LittleTools.exe");
                if (File.Exists(suitePath)) Process.Start(new ProcessStartInfo(suitePath, "--background")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch
            {
            }
        }
    }
}
