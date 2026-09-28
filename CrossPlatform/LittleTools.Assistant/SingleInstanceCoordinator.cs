using System.Text.Json;
using LittleTools.Common;

namespace LittleTools.Assistant;

/// <summary>
/// One record describing the process that currently owns the command pipe. The
/// running primary writes it next to the pipe (i.e. in <c>TMPDIR</c>), the next
/// launch reads it to answer "is the copy that is running the same build as me?".
/// Older builds never wrote one, which is exactly the signal that they are stale.
/// </summary>
internal sealed class PeerIdentity
{
    public int Pid { get; set; }
    public string Version { get; set; } = string.Empty;
    public DateTime StartedUtc { get; set; }
}

/// <summary>
/// What a takeover replaced, written by the copy that took over. The coordinator's
/// own state dies with the process, and <c>--diagnose</c> usually runs against a
/// *running* instance, so this file is what lets a later diagnosis say "the
/// instance answering the pipe superseded <c>old-build-111</c>".
/// </summary>
internal sealed class TakeoverRecord
{
    public int FromPid { get; set; }
    public string? FromVersion { get; set; }
    public int ToPid { get; set; }
    public string ToVersion { get; set; } = string.Empty;
    public DateTime AtUtc { get; set; }

    public string Describe() => (FromVersion ?? "未知版本") + " (pid " + FromPid + ")";
}

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "LittleTools.Assistant.Singleton.v1";

    /// <summary>How long a superseded copy gets to leave before the launch gives up.</summary>
    internal static readonly TimeSpan SupersedeTimeout = TimeSpan.FromSeconds(6);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly Mutex _mutex;
    private CommandPipe? _server;
    private readonly bool _isolated;

    public SingleInstanceCoordinator(bool isolated = false)
    {
        _isolated = isolated;
        _mutex = new Mutex(true, isolated ? MutexName + ".Test." + Guid.NewGuid().ToString("N") : MutexName, out var created);
        IsPrimary = created;
    }

    public bool IsPrimary { get; private set; }

    /// <summary>This build's identity, for diagnostics and for the peer handshake.</summary>
    public string Version => BuildIdentity.Current;

    /// <summary>The build the superseded copy reported, or null when it was too old to say.</summary>
    public string? PeerVersion { get; private set; }

    /// <summary>The process that stepped aside, e.g. <c>1.0.0+old (pid 4711)</c>.</summary>
    public string? TookOverFrom { get; private set; }

    /// <summary>True when this launch replaced a differently versioned running copy.</summary>
    public bool SupersededPeer { get; private set; }

    /// <summary>Where the running primary publishes its identity.</summary>
    internal static string IdentityPath => Path.Combine(Path.GetTempPath(),
        "little-tools-assistant." + SafeUser() + ".identity.json");

    /// <summary>Where the last takeover is recorded, for the next <c>--diagnose</c>.</summary>
    internal static string TakeoverPath => Path.Combine(Path.GetTempPath(),
        "little-tools-assistant." + SafeUser() + ".takeover.json");

    /// <summary>
    /// The named mutex is scoped to the login session on Linux, so an instance
    /// started from another session (a launcher using setsid, a second TTY) would
    /// wrongly consider itself primary. The command pipe is machine wide, so it is
    /// the authoritative check. A "ping" is acknowledged without dispatching a
    /// command, so probing never opens a window.
    /// </summary>
    public bool HasLivePeer(string? pipeName = null) =>
        ProbePeer(pipeName) > 0;

    /// <summary>The pid of the live primary, or 0 when nobody owns the pipe.</summary>
    public int ProbePeer(string? pipeName = null) =>
        CommandPipe.Send(pipeName ?? CommandPipe.AssistantName, "ping", 800);

    public Task<bool> SendAsync(AppCommand command, bool managed = false) => Task.Run(() =>
        CommandPipe.Send(CommandPipe.AssistantName, (managed ? "managed:" : "") + command, 3000) > 0);

    /// <summary>
    /// The identity the live peer published, but only when it belongs to the pid
    /// that answered the pipe. Null means "an older build that never published
    /// one" (or a stale file from a crashed copy), which is treated as stale.
    /// </summary>
    public static string? ReadPeerVersion(int peerPid) => ReadPeerVersion(IdentityPath, peerPid);

    /// <summary>The same check against an explicit file; used by the unit tests.</summary>
    internal static string? ReadPeerVersion(string path, int peerPid)
    {
        var identity = ReadIdentity(path);
        return identity is not null && identity.Pid == peerPid ? identity.Version : null;
    }

    /// <summary>The published identity, whatever its pid; for diagnostics.</summary>
    public static PeerIdentity? ReadIdentity() => ReadIdentity(IdentityPath);

    /// <summary>Publishes this process as the pipe owner. Only the primary calls it.</summary>
    public void PublishIdentity()
    {
        // An isolated coordinator (a smoke run) must never touch the real file: a
        // short-lived process publishing there would make the next launch see a dead
        // pid and treat the live instance as stale.
        if (_isolated) return;
        WriteIdentity(IdentityPath, Environment.ProcessId, BuildIdentity.Current);
    }

    internal static void WriteIdentity(string path, int pid, string version)
    {
        var identity = new PeerIdentity { Pid = pid, Version = version, StartedUtc = DateTime.UtcNow };
        WriteAtomic(path, JsonSerializer.Serialize(identity));
    }

    internal static PeerIdentity? ReadIdentity(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PeerIdentity>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the file only when it still describes this process.
    /// </summary>
    internal static bool ClearIdentity(string path, int pid)
    {
        try
        {
            if (ReadIdentity(path) is not { } identity || identity.Pid != pid) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            // A leftover file is ignored by ReadPeerVersion because its pid is gone.
            return false;
        }
    }

    internal static void WriteTakeover(string path, TakeoverRecord record) =>
        WriteAtomic(path, JsonSerializer.Serialize(record));

    internal static TakeoverRecord? ReadTakeover(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TakeoverRecord>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Temp file plus a replace. A half written identity would deserialize to null
    /// and make a launching copy treat a perfectly healthy primary as stale (i.e.
    /// kick it out); a reader must only ever see nothing or the whole record.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        try
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, content);
            File.Move(temporary, path, true);
        }
        catch
        {
            // Diagnostics only; never a reason to fail a launch.
        }
    }

    /// <summary>The record, but only when it belongs to the instance answering now.</summary>
    public static TakeoverRecord? ReadTakeoverFor(int runningPid, string? path = null)
    {
        var record = ReadTakeover(path ?? TakeoverPath);
        return record is not null && record.ToPid == runningPid ? record : null;
    }

    /// <summary>
    /// Asks the running copy to leave and waits for the command pipe to go quiet.
    ///
    /// The request reuses the existing <c>Exit</c> command on the existing pipe
    /// protocol, so a build that knows nothing about takeover still understands it
    /// (a new command word would be silently ignored by exactly the process this
    /// has to move out of the way). Returns false when the peer is still answering
    /// after <see cref="SupersedeTimeout"/>, so the caller can tell the user
    /// instead of quietly handing the command to the stale copy.
    /// </summary>
    public bool SupersedePeer(int peerPid, string? peerVersion)
    {
        PeerVersion = peerVersion;
        TookOverFrom = (peerVersion ?? "未知版本") + " (pid " + peerPid + ")";
        CommandPipe.Send(CommandPipe.AssistantName, AppCommand.Exit.ToString(), 3000);

        var deadline = DateTime.UtcNow + SupersedeTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!HasLivePeer())
            {
                SupersededPeer = true;
                ClaimPrimary();
                if (!_isolated)
                    WriteTakeover(TakeoverPath, new TakeoverRecord
                    {
                        FromPid = peerPid,
                        FromVersion = peerVersion,
                        ToPid = Environment.ProcessId,
                        ToVersion = BuildIdentity.Current,
                        AtUtc = DateTime.UtcNow
                    });
                return true;
            }
            Thread.Sleep(PollInterval);
        }
        return false;
    }

    /// <summary>
    /// Takes the singleton after the previous owner left. The mutex may have been
    /// held by that process, so it is claimed now if possible; the pipe check above
    /// already proved nobody else owns the instance, so a still-busy mutex (a
    /// leftover handle, a copy in another session) does not veto this launch.
    /// </summary>
    private void ClaimPrimary()
    {
        if (IsPrimary) return;
        try
        {
            _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            // The previous owner died while holding it; that is exactly what we want.
        }
        catch
        {
            // Fall through: the pipe is authoritative.
        }
        IsPrimary = true;
    }

    public void StartListening(Action<AppCommand, bool> onCommand)
    {
        if (!IsPrimary || _isolated || _server is not null) return;
        _server = new CommandPipe(CommandPipe.AssistantName, text =>
        {
            var managed = text.StartsWith("managed:", StringComparison.Ordinal);
            if (managed) text = text[8..];
            if (Enum.TryParse<AppCommand>(text, true, out var command)) onCommand(command, managed);
        });
    }

    public void Dispose()
    {
        _server?.Dispose();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
        if (IsPrimary) ClearIdentity(IdentityPath, Environment.ProcessId);
    }

    private static string SafeUser()
    {
        var name = Environment.UserName;
        if (string.IsNullOrWhiteSpace(name)) return "user";
        var safe = new string(name.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return safe.Length == 0 ? "user" : safe;
    }
}
