using System.Diagnostics;
using System.Text;
using LittleTools.Assistant.Services;

namespace LittleTools.Assistant.Todo;

/// <summary>
/// Focus timer sounds. Windows used <c>SystemSounds</c> and <c>SoundPlayer</c>,
/// neither of which exists on Linux, so the samples are synthesised here and sent
/// to whichever player the desktop provides. Any failure degrades to silence and
/// must never reach the UI thread as an exception.
/// </summary>
internal interface ITodoSoundService
{
    bool IsAvailable { get; }
    void Tick();
    void FocusFinished();
}

internal static class TodoSoundServiceFactory
{
    public static ITodoSoundService Create() =>
        OperatingSystem.IsWindows() ? new NullTodoSoundService() : new LinuxTodoSoundService();
}

internal sealed class NullTodoSoundService : ITodoSoundService
{
    public bool IsAvailable => false;
    public void Tick() { }
    public void FocusFinished() { }
}

internal sealed class LinuxTodoSoundService : ITodoSoundService
{
    private readonly string? _canberra = ExecutableLocator.Find("canberra-gtk-play");
    private readonly string? _player = ExecutableLocator.Find("paplay")
                                      ?? ExecutableLocator.Find("aplay")
                                      ?? ExecutableLocator.Find("pw-play");
    private readonly Lazy<string?> _tickFile;
    private readonly Lazy<string?> _finishedFile;

    public LinuxTodoSoundService()
    {
        _tickFile = new Lazy<string?>(() => WriteSample("todo-tick.wav", TodoSoundSynthesizer.ClickWav()));
        _finishedFile = new Lazy<string?>(() => WriteSample("todo-finished.wav", TodoSoundSynthesizer.ChimeWav()));
    }

    public bool IsAvailable => _canberra is not null || _player is not null;

    public void Tick() => Play(_tickFile, "audio-volume-change");

    public void FocusFinished() => Play(_finishedFile, "complete");

    private void Play(Lazy<string?> sample, string canberraId)
    {
        try
        {
            if (_canberra is not null)
            {
                Start(_canberra, ["--id=" + canberraId]);
                return;
            }
            var path = sample.Value;
            if (_player is not null && path is not null) Start(_player, [path]);
        }
        catch
        {
            // A missing audio device must not disturb the countdown.
        }
    }

    private static void Start(string file, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start);
        // Deliberately not waited on: the sound plays in the background.
    }

    private static string? WriteSample(string fileName, byte[] wav)
    {
        try
        {
            var path = Path.Combine(AppPaths.CacheDirectory, fileName);
            if (!File.Exists(path) || new FileInfo(path).Length != wav.Length)
                File.WriteAllBytes(path, wav);
            return path;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Generates the two short WAV samples as plain RIFF byte streams.</summary>
internal static class TodoSoundSynthesizer
{
    private const int SampleRate = 44100;
    private const short Channels = 1;
    private const short BitsPerSample = 16;

    /// <summary>A very short high click used for every dial detent.</summary>
    public static byte[] ClickWav()
    {
        const int samples = 900;
        var data = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            var t = i / (double)SampleRate;
            var envelope = Math.Exp(-t * 220);
            var tone = Math.Sin(2 * Math.PI * 2100 * t) * 0.45 + Math.Sin(2 * Math.PI * 3300 * t) * 0.2;
            data[i] = (short)(tone * envelope * short.MaxValue * 0.6);
        }
        return Build(data);
    }

    /// <summary>A short two tone chime played when the countdown reaches zero.</summary>
    public static byte[] ChimeWav()
    {
        const int samples = 16000;
        var data = new short[samples];
        for (var i = 0; i < samples; i++)
        {
            var t = i / (double)SampleRate;
            var envelope = Math.Exp(-t * 4.5);
            var first = Math.Sin(2 * Math.PI * 880 * t);
            var second = t > 0.18 ? Math.Sin(2 * Math.PI * 1174.7 * (t - 0.18)) : 0;
            data[i] = (short)((first * 0.35 + second * 0.35) * envelope * short.MaxValue * 0.7);
        }
        return Build(data);
    }

    private static byte[] Build(short[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        var dataBytes = samples.Length * sizeof(short);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write(Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * Channels * BitsPerSample / 8);
        writer.Write((short)(Channels * BitsPerSample / 8));
        writer.Write(BitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        foreach (var sample in samples) writer.Write(sample);
        writer.Flush();
        return stream.ToArray();
    }
}
