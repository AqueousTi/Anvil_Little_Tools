using System.Diagnostics;

namespace LittleTools.Assistant.Services;

/// <summary>
/// Desktop notifications. Windows used tray balloons; Linux uses the freedesktop
/// notification daemon so the same "state changed" messages reach the user.
/// </summary>
internal interface INotificationService
{
    bool IsAvailable { get; }
    void Show(string title, string body, int timeoutMs = 4500);
}

internal static class NotificationServiceFactory
{
    public static INotificationService Create() => OperatingSystem.IsLinux()
        ? new LinuxNotificationService()
        : new NullNotificationService();
}

internal sealed class NullNotificationService : INotificationService
{
    public bool IsAvailable => false;
    public void Show(string title, string body, int timeoutMs = 4500) { }
}

internal sealed class LinuxNotificationService : INotificationService
{
    private readonly string? _executable = ExecutableLocator.Find("notify-send");

    public bool IsAvailable => _executable is not null;

    public void Show(string title, string body, int timeoutMs = 4500)
    {
        if (_executable is null) return;
        try
        {
            var start = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("--app-name=Little Tools");
            start.ArgumentList.Add("--expire-time=" + Math.Max(1000, timeoutMs));
            start.ArgumentList.Add(title);
            start.ArgumentList.Add(body);
            using var process = Process.Start(start);
            process?.WaitForExit(2000);
        }
        catch
        {
            // A missing or failing notification daemon must never break the app.
        }
    }
}
