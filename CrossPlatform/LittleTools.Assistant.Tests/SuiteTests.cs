using LittleTools.Assistant;
using LittleTools.Common;

internal static class SuiteTests
{
    public static void Run(Action<string, bool> check)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "LittleTools-suite-test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var manager = Path.Combine(root, "LittleTools", "bin", "LittleTools.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(manager)!);
            File.WriteAllText(manager, "test placeholder");
            foreach (var layout in new[] { "Assistant", "CrossPlatform/artifacts/win-x64", "CrossPlatform/LittleTools.Assistant/bin/Release/net10.0", "." })
            {
                var path = Path.Combine(root, layout);
                Directory.CreateDirectory(path);
                check("find manager in " + layout, SuiteLauncher.FindManager(path) == manager);
            }
            File.Delete(manager);
            check("standalone assistant has no host", SuiteLauncher.FindManager(root) is null);
        }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(root, true);
        }

        check("chat forwarding", SuiteLauncher.ArgumentFor(AppCommand.ShowChat) == "--chat");
        check("screenshot forwarding", SuiteLauncher.ArgumentFor(AppCommand.Screenshot) == "--screenshot");
        check("translation forwarding", SuiteLauncher.ArgumentFor(AppCommand.ShowTranslation) == "--translate");
        check("background forwarding", SuiteLauncher.ArgumentFor(AppCommand.Background) == "--background");

        var pipeName = "LittleTools.Tests." + Guid.NewGuid().ToString("N");
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using (var server = new CommandPipe(pipeName, received.Enqueue))
        {
            check("IPC reports actual owner PID", CommandPipe.Send(pipeName, "managed:ShowChat", 2000) == Environment.ProcessId);
            SpinWait.SpinUntil(() => !received.IsEmpty, 2000);
            check("IPC forwards managed command", received.TryDequeue(out var text) && text == "managed:ShowChat");
            check("IPC ping reuses server", CommandPipe.Send(pipeName, "ping", 2000) == Environment.ProcessId);
            check("IPC ping does not open UI", received.IsEmpty);
            check("IPC subsequent request", CommandPipe.Send(pipeName, "--screenshot", 2000) == Environment.ProcessId);
            SpinWait.SpinUntil(() => !received.IsEmpty, 2000);
            check("IPC preserves command", received.TryDequeue(out text) && text == "--screenshot");
        }
        check("IPC absent host times out", CommandPipe.Send(pipeName + ".absent", "ping", 50) == 0);
    }
}
