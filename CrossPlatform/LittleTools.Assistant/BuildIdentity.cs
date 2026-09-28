using System.Reflection;
using System.Security.Cryptography;

namespace LittleTools.Assistant;

/// <summary>
/// Identifies the running build, so the single-instance coordinator can tell a
/// stale copy from an identical one.
///
/// The assembly version alone is not enough here: this project never bumps it, so
/// the informational version is <c>1.0.0</c> plus whatever the SDK appended. Inside
/// this repository that is the git commit (<c>1.0.0+&lt;40 hex&gt;</c>), which
/// already separates two commits, but it says nothing about a working tree that was
/// edited and rebuilt without committing - exactly the "I changed something and
/// nothing took effect" case this exists for. The identity therefore appends a
/// short SHA-256 of the file that actually changes between builds: the managed
/// assembly for a framework-dependent run (<c>dotnet LittleTools.Assistant.dll</c>),
/// or the apphost itself for the single-file publish the installer ships
/// (everything is bundled into that one executable).
///
/// <c>LITTLETOOLS_BUILD_VERSION</c> replaces the whole identity. It exists so the
/// takeover path can be driven with two copies of one binary; production never
/// sets it.
/// </summary>
internal static class BuildIdentity
{
    /// <summary>Overrides the identity for a run; used by the takeover verification.</summary>
    public const string VersionVariable = "LITTLETOOLS_BUILD_VERSION";

    /// <summary>This build's identity, e.g. <c>1.0.0+3f9c2ab41d07</c>.</summary>
    public static string Current { get; } = Resolve();

    /// <summary>True when the other identity names exactly this build.</summary>
    public static bool IsSame(string? other) =>
        !string.IsNullOrWhiteSpace(other) && string.Equals(Current, other.Trim(), StringComparison.Ordinal);

    private static string Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(VersionVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden.Trim();

        var assembly = typeof(BuildIdentity).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version)) version = assembly.GetName().Version?.ToString();
        if (string.IsNullOrWhiteSpace(version)) version = "0.0.0";
        return version + "+" + Fingerprint(assembly);
    }

    private static string Fingerprint(Assembly assembly)
    {
        foreach (var path in CandidatePaths(assembly))
        {
            try
            {
                if (!File.Exists(path)) continue;
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream))[..12].ToLowerInvariant();
            }
            catch
            {
                // An unreadable file only costs the finer identity; the version part
                // of the identity still separates releases.
            }
        }
        return "unknown";
    }

    private static IEnumerable<string> CandidatePaths(Assembly assembly)
    {
        // Framework-dependent runs (`dotnet LittleTools.Assistant.dll`, the test host,
        // the IDE output) keep the managed assembly next to the app directory. The
        // single-file publish the installer ships has no such file - everything is
        // bundled into the apphost - so the apphost is the file that changes between
        // builds there. Reading Assembly.Location is avoided on purpose: the
        // single-file analyzer flags it, and it is empty inside a bundle anyway.
        var name = assembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(name)) yield return Path.Combine(AppContext.BaseDirectory, name + ".dll");
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)) yield return Environment.ProcessPath;
    }
}
