namespace Outspread.Core;

/// <summary>
/// Watches one file and raises a single notification per logical write.
/// </summary>
/// <remarks>
/// Writers here publish by renaming a temporary file over the target, so one save can
/// surface as a Changed plus a Renamed plus a Created, and antivirus or the search
/// indexer adds touches of its own. Without the debounce a board reloads three times per
/// agent edit, which is visible as flicker and, worse, as a merge racing itself.
///
/// The buffer-overflow path also raises the event: a dropped notification must degrade
/// into an extra reload, never into a stale board.
/// </remarks>
public sealed class FileWatcher : IDisposable
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(400);

    private readonly FileSystemWatcher _watcher;

    private readonly CancellationTokenSource _closed = new();

    private Dictionary<string, CancellationTokenSource> _pending = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _gate = new();

    private bool _disposed;

    public FileWatcher(string filePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));

        if (string.IsNullOrEmpty(directory))
        {
            throw new ArgumentException("the watched file must have a directory", nameof(filePath));
        }

        Directory.CreateDirectory(directory);

        _watcher = new FileSystemWatcher(directory, Path.GetFileName(filePath))
        {
            // LastWrite alone is not enough: an atomic replace arrives as a rename.
            NotifyFilter = NotifyFilters.LastWrite
                           | NotifyFilters.FileName
                           | NotifyFilters.Size
                           | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Error += OnError;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Raised on a thread-pool thread once the file has been quiet for the debounce
    /// window. Listeners must marshal to their own UI thread.
    /// </summary>
    public event Action? Changed;

    private void OnFileEvent(object sender, FileSystemEventArgs e) => Schedule(e.FullPath);

    private void OnError(object sender, ErrorEventArgs e)
    {
        Changed?.Invoke();
    }

    private void Schedule(string path)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource source;

        lock (_gate)
        {
            if (_pending.TryGetValue(path, out var previous))
            {
                previous.Cancel();
                _pending.Remove(path);
            }

            source = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
            _pending[path] = source;
        }

        _ = FireAfterDelayAsync(path, source);
    }

    private async Task FireAfterDelayAsync(string path, CancellationTokenSource source)
    {
        try
        {
            await Task.Delay(DebounceDelay, source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (_pending.TryGetValue(path, out var current) && ReferenceEquals(current, source))
            {
                _pending.Remove(path);
            }
        }

        if (_disposed || source.IsCancellationRequested)
        {
            return;
        }

        Changed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileEvent;
        _watcher.Created -= OnFileEvent;
        _watcher.Renamed -= OnFileEvent;
        _watcher.Deleted -= OnFileEvent;
        _watcher.Error -= OnError;
        _watcher.Dispose();

        _closed.Cancel();

        lock (_gate)
        {
            foreach (var source in _pending.Values)
            {
                source.Cancel();
                source.Dispose();
            }

            _pending.Clear();
        }

        _closed.Dispose();
    }
}
