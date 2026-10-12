namespace Outspread.Core;

/// <summary>
/// The only thing the sync engine is allowed to talk to: a store of files that
/// answers "what is on the far side right now" and "replace it, but only if it is
/// still the version I was told about".
/// </summary>
/// <remarks>
/// Deliberately not an HTTP client. A host that hands back a compare-and-set token
/// per file is all the sync protocol needs, which is what makes a second host a
/// class instead of a rewrite — and it is what lets the whole engine be tested
/// against <see cref="IRemoteFiles"/> without a network or a cent spent.
/// </remarks>
public interface IRemoteFiles
{
    /// <summary>
    /// Every path and its token in one call, or null when the far side is gone
    /// (repository deleted, branch renamed). One request is the point: a caller
    /// that finds the tree unchanged must not read a single file.
    /// </summary>
    Task<RemoteTree?> ReadTreeAsync(CancellationToken cancel);

    /// <summary>The bytes plus the token needed to replace them. Null when the path is absent.</summary>
    Task<RemoteFile?> ReadAsync(string path, CancellationToken cancel);

    /// <summary>
    /// Publishes <paramref name="content"/>. A non-null <paramref name="expectToken"/>
    /// must be refused with <see cref="WriteOutcome.Conflict"/> if the far side no
    /// longer holds exactly that version. Null means "create; refuse if it exists".
    /// </summary>
    Task<WriteResult> WriteAsync(string path, byte[] content, string? expectToken, CancellationToken cancel);

    Task<WriteOutcome> DeleteAsync(string path, string expectToken, CancellationToken cancel);
}

public sealed record RemoteEntry(string Path, string Token, int Bytes);

public sealed record RemoteTree(string TreeToken, IReadOnlyList<RemoteEntry> Entries)
{
    public string? TokenFor(string path)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Path, path, StringComparison.Ordinal))
            {
                return entry.Token;
            }
        }

        return null;
    }
}

public sealed record RemoteFile(byte[] Content, string Token);

public enum WriteOutcome
{
    Written,

    /// <summary>The token handed over was no longer current. Nothing changed on the far side.</summary>
    Conflict,

    /// <summary>Refused for shape, not for timing: too large, wrong type. Retrying the same bytes will not help.</summary>
    Rejected,

    /// <summary>The path or its repository is not there.</summary>
    Missing,

    /// <summary>The account cannot write there. Only a human fixes this one.</summary>
    Unauthorized,

    /// <summary>Rate limit or transport failure; the same call may succeed later.</summary>
    RetryLater,
}

/// <summary>
/// <paramref name="Token"/> is the new token after a write, or the current one after
/// a conflict — a conflict response is the cheapest possible "here is what you were
/// wrong about" payload, and the engine re-merges from it without a second read.
/// </summary>
public sealed record WriteResult(WriteOutcome Outcome, string? Token, string? Detail = null);
