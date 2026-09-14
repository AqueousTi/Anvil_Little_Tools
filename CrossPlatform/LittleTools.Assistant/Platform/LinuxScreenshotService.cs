using Avalonia.Controls;
using System.Diagnostics;

namespace LittleTools.Assistant.Platform;

internal sealed class LinuxScreenshotService : IScreenshotService
{
    public async Task<byte[]?> CaptureRegionAsync(Window owner, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"little-tools-{Guid.NewGuid():N}.png");
        owner.Hide();
        await Task.Delay(120, cancellationToken);
        try
        {
            var exitCode = await CaptureAsync(path, cancellationToken);
            if (exitCode != 0 || !File.Exists(path)) return null;
            return await File.ReadAllBytesAsync(path, cancellationToken);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            owner.Show();
            owner.Activate();
        }
    }

    private static async Task<int> CaptureAsync(string outputPath, CancellationToken cancellationToken)
    {
        if (FindExecutable("gnome-screenshot") is not null)
            return await RunAsync("gnome-screenshot", ["-a", "-f", outputPath], cancellationToken);
        if (FindExecutable("spectacle") is not null)
            return await RunAsync("spectacle", ["-r", "-b", "-n", "-o", outputPath], cancellationToken);
        if (FindExecutable("slurp") is not null && FindExecutable("grim") is not null)
        {
            var geometry = await CaptureOutputAsync("slurp", [], cancellationToken);
            if (string.IsNullOrWhiteSpace(geometry)) return 1;
            return await RunAsync("grim", ["-g", geometry.Trim(), outputPath], cancellationToken);
        }
        throw new InvalidOperationException("没有找到区域截图工具。Ubuntu 可安装 gnome-screenshot；Wayland 也可使用 grim + slurp。");
    }

    private static string? FindExecutable(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator)
            .Select(folder => Path.Combine(folder, name))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<int> RunAsync(string file, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = Create(file, arguments, redirectOutput: false);
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static async Task<string> CaptureOutputAsync(string file, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = Create(file, arguments, redirectOutput: true);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0 ? output : string.Empty;
    }

    private static Process Create(string file, IReadOnlyList<string> arguments, bool redirectOutput)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }
}
