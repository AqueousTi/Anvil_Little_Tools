using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using LittleTools.Assistant.Services;
using SkiaSharp;
using System.Diagnostics;

namespace LittleTools.Assistant.Platform;

/// <summary>
/// Linux screen capture.
///
/// The region is chosen by our own <see cref="SelectionWindow"/>, never by a
/// desktop picker: the previous implementation ran <c>gnome-screenshot -a</c>,
/// which on a GNOME session delegates the selection to
/// <c>org.gnome.Shell.Screenshot.SelectArea</c>. When the shell does not answer,
/// the tool blocks forever, so the assistant stayed hidden with no feedback at
/// all (measured before the fix: <c>gnome-screenshot -a</c> alive after 60s, a
/// single unmapped 10x10 InputOnly window, no overlay, no output file).
///
/// Instead the whole desktop is captured with a non-interactive backend, the
/// overlay picks a rectangle on the owner's screen, and the region is cropped out
/// of that already captured image, so the overlay can never appear in the result.
///
/// The whole-desktop capture is a chain (see
/// <see cref="ScreenshotBackendProtocol.SelectBackends"/>): the flash-free GNOME
/// shell call first, then the official portal, then the desktop tools. Every step
/// is bounded and every failure is collected, so a broken desktop produces a
/// visible error instead of a hidden window.
/// </summary>
internal sealed class LinuxScreenshotService : IScreenshotService
{
    /// <summary>Longest a desktop capture command may run before it counts as broken.</summary>
    internal static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Test hook: when set, the cropped PNG of the last capture is also written to
    /// this path. The verification harnesses compare those bytes against the
    /// pattern they planted on screen, so "the crop really is the selected region"
    /// is asserted on a real display instead of only on a synthetic bitmap.
    /// </summary>
    internal const string CropOutVariable = "LITTLETOOLS_SCREENSHOT_CROP_OUT";

    /// <summary>
    /// Full-desktop capture commands, in preference order. None of them prompt:
    /// every one writes the whole screen to the path appended by the caller.
    ///
    /// The silent tools come first. <c>gnome-screenshot</c> is last (before the
    /// <c>xwd</c> dump) because every capture it makes fires a full-screen white
    /// flash: on a GNOME session its shell backend asks
    /// <c>org.gnome.Shell.Screenshot</c> for <c>flash=true</c> and GNOME Shell
    /// answers with a <c>Flashspot</c> lightbox, while the X11 fallback fires
    /// gnome-screenshot's own bundled <c>CheeseFlash</c> popup. Version 41.0 has no
    /// command-line flag, environment variable or gsettings key for it (the only
    /// knobs are the flash argument the tool hard-codes and the global
    /// <c>enable-animations</c> setting), so the fix is to prefer tools that never
    /// flash and to document "install maim" for users who only have gnome-screenshot.
    /// </summary>
    private static readonly (string Tool, string[] Arguments, bool IsXwd)[] CaptureCommands =
    [
        ("maim", [], false),
        ("scrot", ["-o"], false),
        ("import", ["-window", "root"], false),
        ("grim", [], false),
        // -b background, -n no notification, -f full screen, -o output path.
        ("spectacle", ["-b", "-n", "-f", "-o"], false),
        ("gnome-screenshot", ["-f"], false),
        ("xwd", ["-root", "-silent", "-out"], true)
    ];

    /// <summary>
    /// Shared across the assistant's windows: the D-Bus connections and the leased
    /// name are process wide, so a second window reuses them instead of opening a
    /// second connection and racing itself for the name.
    /// </summary>
    private static readonly GnomeShellScreenshotBackend ShellBackend = new();
    private static readonly PortalScreenshotBackend PortalBackend = new();

    /// <summary>
    /// The capture tools in the order they are tried, exposed so the tests can pin
    /// the silent-first order (a flashless tool must never be preceded by a
    /// flashing one).
    /// </summary>
    internal static IReadOnlyList<string> CaptureToolOrder =>
        CaptureCommands.Select(command => command.Tool).ToArray();

    public async Task<byte[]?> CaptureRegionAsync(Window owner, CancellationToken cancellationToken)
    {
        var screen = owner.Screens.ScreenFromWindow(owner) ?? owner.Screens.Primary
            ?? throw new InvalidOperationException("无法读取当前屏幕。");
        // The screen bounds are physical X coordinates and can be offset (a second
        // monitor sits at a non-zero, on some layouts negative, origin), so the
        // captured bitmap is indexed from the whole virtual desktop's origin.
        var desktop = VirtualDesktopBounds(owner.Screens.All, screen.Bounds);
        owner.Hide();
        try
        {
            await Task.Delay(120, cancellationToken);
            var captured = await CaptureFullScreenAsync(desktop, cancellationToken);
            using var full = captured.Bitmap;
            if (full is null)
                throw new InvalidOperationException("全屏截图失败。" + (captured.Failure ?? string.Empty));
            var overlay = new SelectionWindow(screen);
            // The overlay is the same managed, topmost window Windows uses: it
            // takes the pointer and the Esc key. GNOME keeps its 32px panel above
            // any managed window (measured: the panel strip is unchanged while the
            // rest of the screen is darkened), so that strip cannot be selected;
            // covering it would need an override-redirect window, which cannot
            // receive the Esc key.
            var rectangle = await overlay.SelectAsync();
            if (rectangle is null) return null;
            var bytes = Crop(full, rectangle.Value, desktop);
            WriteCropOut(bytes);
            return bytes;
        }
        finally
        {
            owner.Show();
            owner.Activate();
        }
    }

    /// <summary>Writes the harness-visible copy of the crop (see <see cref="CropOutVariable"/>).</summary>
    private static void WriteCropOut(byte[]? bytes)
    {
        var path = Environment.GetEnvironmentVariable(CropOutVariable);
        if (string.IsNullOrWhiteSpace(path) || bytes is null) return;
        try { File.WriteAllBytes(path, bytes); } catch { }
    }

    /// <summary>
    /// The bounding box of every reported screen. Both <c>gnome-screenshot -f</c>
    /// and <c>xwd -root</c> dump the entire X root window, which is exactly this
    /// rectangle, so it is the origin the capture's pixels are indexed from.
    /// </summary>
    internal static PixelRect VirtualDesktopBounds(IReadOnlyList<Screen> screens, PixelRect fallback)
    {
        if (screens.Count == 0) return fallback;
        var left = screens.Min(screen => screen.Bounds.X);
        var top = screens.Min(screen => screen.Bounds.Y);
        var right = screens.Max(screen => screen.Bounds.Right);
        var bottom = screens.Max(screen => screen.Bounds.Bottom);
        if (right <= left || bottom <= top) return fallback;
        return new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// Maps a selection in physical screen coordinates onto the captured bitmap.
    /// Handles a non-zero virtual desktop origin, a capture whose pixel size does
    /// not match the reported bounds (HiDPI), and a selection that reaches past
    /// the picture.
    /// </summary>
    internal static PixelRect MapToImage(PixelRect selection, PixelRect desktop, PixelSize image)
    {
        if (desktop.Width <= 0 || desktop.Height <= 0 || image.Width <= 0 || image.Height <= 0)
            return default;
        var scaleX = image.Width / (double)desktop.Width;
        var scaleY = image.Height / (double)desktop.Height;
        var left = (int)Math.Floor((selection.X - desktop.X) * scaleX);
        var top = (int)Math.Floor((selection.Y - desktop.Y) * scaleY);
        var right = (int)Math.Ceiling((selection.Right - desktop.X) * scaleX);
        var bottom = (int)Math.Ceiling((selection.Bottom - desktop.Y) * scaleY);
        left = Math.Clamp(left, 0, image.Width);
        top = Math.Clamp(top, 0, image.Height);
        right = Math.Clamp(right, 0, image.Width);
        bottom = Math.Clamp(bottom, 0, image.Height);
        return new PixelRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    /// <summary>Encodes the selected part of a captured desktop as a PNG.</summary>
    internal static byte[]? Crop(SKBitmap full, PixelRect selection, PixelRect desktop)
    {
        var region = MapToImage(selection, desktop, new PixelSize(full.Width, full.Height));
        if (region.Width < 1 || region.Height < 1) return null;
        using var subset = new SKBitmap();
        if (!full.ExtractSubset(subset, new SKRectI(region.X, region.Y, region.Right, region.Bottom)))
            return null;
        using var image = SKImage.FromBitmap(subset);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data?.ToArray();
    }

    /// <summary>
    /// True when every sampled pixel is opaque black, which is how a compositor
    /// answers <c>xwd -root</c> on GNOME: the visible picture lives in the
    /// compositor's own buffer, not in the X root window.
    /// </summary>
    internal static bool IsBlankRootWindow(SKBitmap bitmap)
    {
        const int steps = 16;
        for (var row = 0; row < steps; row++)
        {
            var y = (int)((row + 0.5) * bitmap.Height / steps);
            for (var column = 0; column < steps; column++)
            {
                var x = (int)((column + 0.5) * bitmap.Width / steps);
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red != 0 || pixel.Green != 0 || pixel.Blue != 0) return false;
            }
        }
        return true;
    }

    private static async Task<(SKBitmap? Bitmap, string? Failure)> CaptureFullScreenAsync(
        PixelRect desktop, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var backend in ScreenshotBackendProtocol.SelectBackends(
                     Environment.GetEnvironmentVariable(ScreenshotBackendProtocol.ForceBackendVariable)))
        {
            switch (backend)
            {
                case ScreenshotBackendProtocol.BackendShell:
                {
                    var (bitmap, failure) = await ShellBackend.CaptureAsync(desktop, cancellationToken);
                    if (bitmap is not null)
                    {
                        ScreenshotCaptureLog.Record(ScreenshotBackendProtocol.BackendShell, "flash=false");
                        return (bitmap, null);
                    }
                    failures.Add(failure ?? "shell：未生成截图");
                    break;
                }
                case ScreenshotBackendProtocol.BackendPortal:
                {
                    var (bitmap, failure) = await PortalBackend.CaptureAsync(desktop, cancellationToken);
                    if (bitmap is not null)
                    {
                        ScreenshotCaptureLog.Record(
                            ScreenshotBackendProtocol.BackendPortal, "GNOME 46 上会闪（portal 内部硬编码 flash=true）");
                        return (bitmap, null);
                    }
                    failures.Add(failure ?? "portal：未生成截图");
                    break;
                }
                default:
                {
                    var (bitmap, tool, toolFailures) = await CaptureWithToolsAsync(cancellationToken);
                    if (bitmap is not null)
                    {
                        ScreenshotCaptureLog.Record(tool ?? ScreenshotBackendProtocol.BackendTools, null);
                        return (bitmap, null);
                    }
                    failures.AddRange(toolFailures);
                    break;
                }
            }
        }
        if (failures.Count == 0)
            failures.Add("没有找到可用的全屏截图工具（maim、scrot、import、grim、spectacle、gnome-screenshot 或 xwd）。");
        ScreenshotCaptureLog.Record("none", string.Join("；", failures));
        return (null, string.Join("；", failures));
    }

    /// <summary>
    /// The historical tool chain. It is what runs when neither D-Bus backend can
    /// (no session bus, a desktop without GNOME Shell or a portal, a lease that
    /// could not be taken). The tool that produced the picture is returned so
    /// <c>--diagnose</c> can name it.
    /// </summary>
    private static async Task<(SKBitmap? Bitmap, string? Tool, List<string> Failures)> CaptureWithToolsAsync(
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        foreach (var (tool, arguments, isXwd) in CaptureCommands)
        {
            if (ExecutableLocator.Find(tool) is null) continue;
            var (bytes, failure) = await CaptureToTempFileAsync(tool, arguments, isXwd, cancellationToken);
            if (bytes is null)
            {
                failures.Add(tool + "：" + (failure ?? "未生成截图"));
                continue;
            }
            try
            {
                var bitmap = isXwd ? XwdImage.Decode(bytes) : SKBitmap.Decode(bytes);
                if (bitmap is { Width: > 0, Height: > 0 })
                {
                    if (isXwd && IsBlankRootWindow(bitmap))
                    {
                        // Measured on this GNOME/X11 session: xwd -root dumps an
                        // entirely black root window because the compositor owns
                        // the visible output, while gnome-screenshot -f sees the
                        // real screen. Refuse the dump instead of translating a
                        // black picture.
                        bitmap.Dispose();
                        failures.Add("xwd：根窗口是全黑的（合成器下 xwd 读不到画面）");
                        continue;
                    }
                    return (bitmap, tool, failures);
                }
                bitmap?.Dispose();
                failures.Add(tool + " 的截图无法解码");
            }
            catch (Exception exception)
            {
                failures.Add(tool + "：" + exception.Message);
            }
        }
        return (null, null, failures);
    }

    private static async Task<(byte[]? Bytes, string? Failure)> CaptureToTempFileAsync(
        string tool, IReadOnlyList<string> arguments, bool isXwd, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"little-tools-screen-{Guid.NewGuid():N}{(isXwd ? ".xwd" : ".png")}");
        try
        {
            var argv = new List<string>(arguments) { path };
            var result = await RunAsync(tool, argv, cancellationToken);
            if (result.ExitCode != 0)
                return (null, result.Error.Length > 0 ? result.Error : $"退出码 {result.ExitCode}");
            if (!File.Exists(path)) return (null, "没有写出文件");
            return (await File.ReadAllBytesAsync(path, cancellationToken), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return (null, exception.Message); }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static async Task<(int ExitCode, string Error)> RunAsync(
        string file, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = Create(file, arguments);
        process.Start();
        // No terminal is attached to the suite: close stdin so a tool that
        // unexpectedly asks for input sees EOF instead of waiting forever.
        try { process.StandardInput.Close(); } catch { }
        // stderr is drained while the tool runs: a capture tool may print a warning
        // but it must never fill the pipe buffer and deadlock the wait.
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CaptureTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // A capture tool that waits for input (or a dead desktop) must not keep
            // the window hidden: stop it and report the failure.
            try { process.Kill(entireProcessTree: true); } catch { }
            try { await error; } catch { }
            if (cancellationToken.IsCancellationRequested) throw;
            return (-1, $"超时（超过 {CaptureTimeout.TotalSeconds:0} 秒没有返回）");
        }
        var standardError = string.Empty;
        try { standardError = (await error).Trim(); } catch { }
        if (standardError.Length > 300) standardError = standardError[..300];
        return (process.ExitCode, standardError);
    }

    private static Process Create(string file, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // stdin is closed so a tool that unexpectedly wants input sees EOF and
            // fails instead of waiting for a terminal that is not there.
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }
}
