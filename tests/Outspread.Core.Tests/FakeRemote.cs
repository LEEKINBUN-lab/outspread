using System.Net.Http;
using System.Text;

namespace Outspread.Core.Tests;

/// <summary>
/// An in-process far side: files with a compare-and-set token each.
/// </summary>
/// <remarks>
/// The engine only ever needs "what is there now" and "replace it if it is still the
/// version I was told about", so the whole protocol can be tested against this without a
/// network, an account or a cent. The refusal paths — a write blocked by a stale token, an
/// account that cannot write, a folder that vanished, a document that arrived half-written
/// — are the ones a live service makes hardest to arrange on purpose, which is precisely why
/// they get arranged here.
///
/// Tokens are digests of content. Nothing in the engine is allowed to depend on that; a real
/// host hands back opaque git blob shas and the engine only ever compares them.
/// </remarks>
public sealed class FakeRemote : IRemoteFiles
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    private string? _divergePath;
    private string? _divergeText;
    private Func<string>? _divergeFactory;

    /// <summary>
    /// Runs while a write to this path is in flight. Stands in for the agent, or the CLI,
    /// writing into the local board between this round's read of it and its publish.
    /// </summary>
    public Action<string>? OnWrite { get; set; }

    private string? _refusePath;
    private string? _refuseDetail;
    private WriteOutcome _refuseOutcome;
    private bool _refuseArmed;

    private bool _offline;

    public int TreeCalls { get; private set; }

    public int ReadCalls { get; private set; }

    public int WriteCalls { get; private set; }

    /// <summary>What the far folder holds right now.</summary>
    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public string? Text(string path)
        => _files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;

    public string? Token(string path)
        => _files.TryGetValue(path, out var bytes) ? Ledger.Digest(bytes) : null;

    /// <summary>
    /// Puts text on the far side bypassing every check, the way a second device would.
    /// </summary>
    public void Publish(string path, string text) => _files[path] = Ledger.Encode(text);

    public void PublishRaw(string path, byte[] content) => _files[path] = content;

    public void Remove(string path) => _files.Remove(path);

    /// <summary>
    /// Arms a second-device write that lands between the engine's read of this path and its
    /// write to it, so a real compare-and-set refusal happens on the first attempt.
    /// </summary>
    public void ArmDiverge(string path, string text)
    {
        _divergePath = path;
        _divergeText = text;
    }

    /// <summary>
    /// Keeps a second device writing to this path on every attempt. A refusal once is a race;
    /// a refusal that never stops is a third writer, and the two need different answers.
    /// </summary>
    public void DivergeEveryWrite(string path, Func<string> textFactory)
    {
        _divergePath = path;
        _divergeFactory = textFactory;
    }

    /// <summary>Refuse the next write to this path without changing what it holds.</summary>
    public void FailNextWrite(string path, WriteOutcome outcome, string? detail = null)
    {
        _refusePath = path;
        _refuseOutcome = outcome;
        _refuseDetail = detail;
        _refuseArmed = true;
    }

    /// <summary>Replace what is stored at a path with bytes that are not a readable board.</summary>
    public void Corrupt(string path, string text = "not a canvas at all, and not JSON")
        => Publish(path, text);

    /// <summary>Takes the far side away, the way this laptop losing WiFi does.</summary>
    public void DropConnection() => _offline = true;

    public void RestoreConnection() => _offline = false;

    public void ResetCounters()
    {
        TreeCalls = 0;
        ReadCalls = 0;
        WriteCalls = 0;
    }

    public Task<RemoteTree?> ReadTreeAsync(CancellationToken cancel)
    {
        TreeCalls++;

        Refuse();

        var entries = _files
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new RemoteEntry(pair.Key, Ledger.Digest(pair.Value), pair.Value.Length))
            .ToList();

        var joined = string.Join('|', entries.Select(entry => $"{entry.Path}:{entry.Token}"));

        return Task.FromResult<RemoteTree?>(new RemoteTree(Ledger.Digest(Ledger.Encode(joined)), entries));
    }

    public Task<RemoteFile?> ReadAsync(string path, CancellationToken cancel)
    {
        ReadCalls++;

        Refuse();

        if (!_files.TryGetValue(path, out var bytes))
        {
            return Task.FromResult<RemoteFile?>(null);
        }

        return Task.FromResult<RemoteFile?>(new RemoteFile(bytes, Ledger.Digest(bytes)));
    }

    public Task<WriteResult> WriteAsync(string path, byte[] content, string? expectToken, CancellationToken cancel)
    {
        WriteCalls++;

        Refuse();

        if (_refuseArmed && string.Equals(_refusePath, path, StringComparison.Ordinal))
        {
            _refuseArmed = false;

            OnWrite?.Invoke(path);

            return Task.FromResult(new WriteResult(
                _refuseOutcome,
                Token(path),
                _refuseDetail ?? "refused"));
        }

        OnWrite?.Invoke(path);

        if (_divergePath is not null && string.Equals(_divergePath, path, StringComparison.Ordinal))
        {
            Publish(path, _divergeFactory is null ? _divergeText! : _divergeFactory());

            if (_divergeFactory is null)
            {
                _divergePath = null;
                _divergeText = null;
            }
        }

        var current = Token(path);

        if (expectToken is null)
        {
            // "Create" against a path that already exists is a refusal, not an overwrite:
            // a device that never saw the far file must not be able to erase it blindly.
            if (current is not null)
            {
                return Task.FromResult(new WriteResult(WriteOutcome.Conflict, current, "already there"));
            }

            PublishRaw(path, content);

            return Task.FromResult(new WriteResult(WriteOutcome.Written, Token(path)));
        }

        if (!string.Equals(current, expectToken, StringComparison.Ordinal))
        {
            return Task.FromResult(new WriteResult(
                WriteOutcome.Conflict,
                current,
                "the token handed over is not what the far side holds now"));
        }

        PublishRaw(path, content);

        return Task.FromResult(new WriteResult(WriteOutcome.Written, Token(path)));
    }

    public Task<WriteOutcome> DeleteAsync(string path, string expectToken, CancellationToken cancel)
    {
        var current = Token(path);

        if (current is null)
        {
            return Task.FromResult(WriteOutcome.Missing);
        }

        if (!string.Equals(current, expectToken, StringComparison.Ordinal))
        {
            return Task.FromResult(WriteOutcome.Conflict);
        }

        Remove(path);

        return Task.FromResult(WriteOutcome.Written);
    }

    private void Refuse()
    {
        if (_offline)
        {
            throw new HttpRequestException("no route to the far side");
        }
    }
}
