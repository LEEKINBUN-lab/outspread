using System.Diagnostics;
using System.IO;

namespace Outspread.Spike;

/// <summary>spike 自己的读数去处：不进产品的 board.log，免得把两臂的账混在一本里。</summary>
internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetEnvironmentVariable("TEMP") ?? ".", "outspread-spike", "spike.log");

    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    public static string Where => Path;
}
