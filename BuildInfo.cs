using System.Reflection;

namespace Herald;

/// <summary>
/// The version, commit and build time stamped into Herald.exe when it was built
/// (the StampBuildVersion target in Herald.csproj).
/// </summary>
public static class BuildInfo
{
    private static readonly Assembly Herald = typeof(BuildInfo).Assembly;

    private static readonly (string Version, string Commit) Parts =
        Split(Herald.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Like "1.0.14".</summary>
    public static string Version => Parts.Version;

    /// <summary>Short commit the build came from, with "*" if it had uncommitted changes; empty without git.</summary>
    public static string Commit => Parts.Commit;

    /// <summary>Like "2026-09-29 17:50"; empty if unknown.</summary>
    public static string BuildTime =>
        Herald.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "BuildTime")?.Value ?? String.Empty;

    /// <summary>Shown in the main window: "v1.0.14 · 29f2105".</summary>
    public static string Label => Describe(Version, Commit);

    /// <summary>The label's tooltip: when the build was made, and what a "*" means.</summary>
    public static string Details => Explain(Commit, BuildTime);

    /// <summary>"1.0.14+29f2105*" into its version and commit.</summary>
    public static (string Version, string Commit) Split(string? informational)
    {
        if (String.IsNullOrEmpty(informational)) return ("0.0.0", String.Empty);
        var plus = informational.IndexOf('+');
        return plus < 0 ? (informational, String.Empty) : (informational[..plus], informational[(plus + 1)..]);
    }

    public static string Describe(string version, string commit) =>
        commit.Length > 0 ? $"v{version} · {commit}" : $"v{version}";

    public static string Explain(string commit, string buildTime)
    {
        var lines = new List<string> { buildTime.Length > 0 ? $"Built {buildTime}" : "Build time unknown" };
        if (commit.EndsWith('*')) lines.Add("* with changes that weren't committed yet");
        return String.Join("\n", lines);
    }
}
