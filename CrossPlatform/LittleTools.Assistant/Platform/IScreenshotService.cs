using Avalonia.Controls;

namespace LittleTools.Assistant.Platform;

internal interface IScreenshotService
{
    Task<byte[]?> CaptureRegionAsync(Window owner, CancellationToken cancellationToken);
}

internal static class ScreenshotServiceFactory
{
    public static IScreenshotService Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsScreenshotService();
        if (OperatingSystem.IsLinux()) return new LinuxScreenshotService();
        return new UnsupportedScreenshotService();
    }
}

internal sealed class UnsupportedScreenshotService : IScreenshotService
{
    public Task<byte[]?> CaptureRegionAsync(Window owner, CancellationToken cancellationToken) =>
        throw new PlatformNotSupportedException("当前平台尚未配置区域截图服务。");
}
