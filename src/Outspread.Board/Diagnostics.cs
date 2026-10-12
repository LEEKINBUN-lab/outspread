namespace Outspread.Board;

/// <summary>
/// Tracing for the board process. On by default.
/// </summary>
/// <remarks>
/// A GUI-subsystem process cannot rely on an inherited stdout handle, so a
/// failure that only shows as "nothing happened" is unobservable unless it is
/// written down. The log lives outside the repository; set
/// <c>TANKAI_DIAGNOSTICS</c> to redirect it, or <c>TANKAI_DIAGNOSTICS=0</c>
/// to silence it.
/// </remarks>
internal static class Diagnostics
{
    private static readonly string? TargetPath = ResolveTarget();

    private static readonly object Gate = new();

    public static string? LogPath => TargetPath;

    public static void Write(string message)
    {
        if (TargetPath is null)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    TargetPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never take the board down with them.
        }
    }

    private static string? ResolveTarget()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("TANKAI_DIAGNOSTICS");

        if (string.Equals(fromEnvironment, "0", StringComparison.Ordinal))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Outspread");

        try
        {
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "board.log");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
