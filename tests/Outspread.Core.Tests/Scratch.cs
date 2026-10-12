namespace Outspread.Core.Tests;

/// <summary>
/// A throwaway board folder per test class, outside the repository.
/// </summary>
public sealed class Scratch : IDisposable
{
    public Scratch(string label)
    {
        Folder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "outspread-tests",
            $"{label}-{Guid.NewGuid().ToString("N")[..8]}");

        Directory.CreateDirectory(Folder);
        BoardPath = System.IO.Path.Combine(Folder, "scratch.canvas");
    }

    public string Folder { get; }

    public string BoardPath { get; }

    public string SidecarPath => CanvasStore.SidecarPath(BoardPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is noise; the test result is already recorded.
        }
    }
}
