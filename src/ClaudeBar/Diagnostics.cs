using System.IO;

namespace ClaudeBar;

/// <summary>
/// Opt-in tracing, off unless CLAUDEBAR_DIAG=1 is set. Placement across a mixed-DPI,
/// multi-monitor desk is the thing most likely to misbehave on a machine that is not
/// in front of us, so it needs to be answerable without attaching a debugger.
/// Never logs anything from the credentials file.
/// </summary>
internal static class Diagnostics
{
    private static readonly bool Enabled =
        Environment.GetEnvironmentVariable("CLAUDEBAR_DIAG") == "1";

    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ClaudeBar", "diag.log");

    private static readonly object Gate = new();

    public static void Log(Func<string> message)
    {
        if (!Enabled) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"{DateTime.Now:HH:mm:ss.fff} {message()}{Environment.NewLine}");
            }
        }
        catch { /* diagnostics must never be the reason anything fails */ }
    }
}
