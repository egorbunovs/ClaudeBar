using System.Reflection;

namespace ClaudeBar;

/// <summary>
/// What this build calls itself: <c>1.0.&lt;commit count&gt;</c>, stamped into the assembly by
/// the csproj at build time (see the <c>StampVersion</c> target) and read back here.
///
/// The commit count is the version because it needs no bookkeeping: it goes up on its own,
/// every build of a given commit gets the same number, and the number maps straight back to
/// a commit — `git rev-list --count HEAD` on the tag gives it again.
/// </summary>
internal static class BuildInfo
{
    /// <summary>e.g. "1.0.11". Never empty; falls back to the assembly version.</summary>
    public static string Version { get; } = Read();

    /// <summary>What the menu shows, with a marker when the numbers on screen are invented.</summary>
    public static string MenuLabel => $"ClaudeBar {Version}" + (Demo.Enabled ? " — demo data" : "");

    private static string Read()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // InformationalVersion is the one that carries 1.0.N verbatim; it can pick up a
        // "+<sha>" suffix from SourceLink, which is not for a menu.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (informational is { Length: > 0 })
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
