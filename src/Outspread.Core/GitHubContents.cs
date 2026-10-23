using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Outspread.Core;

/// <summary>
/// The far side as a GitHub repository, reached through nothing but its file API.
/// </summary>
/// <remarks>
/// Shapes below were read back off the live API on 2026-10-11 rather than taken from the
/// docs, because two of them are not documented:
///
/// - a new file's token is <c>.content.sha</c> in the response; <c>.commit.content.sha</c> is
///   <c>null</c>, which is the first thing one would have guessed.
/// - <c>contents.sha</c> and the blob sha in <c>git/trees?recursive=1</c> are the same number,
///   so one tree call can hand out every per-file token.
/// - writing with a stale token is <b>409</b> with "…does not match &lt;sha&gt;", and the body
///   does not say what the current token is — so a refusal must be followed by a re-read.
/// - writing an existing file without a token is <b>422</b> "sha wasn't supplied": the platform
///   refuses to overwrite blindly, which is the same guarantee the engine wants.
/// - a missing path answers <b>404</b> with a body identical in shape to "repository not found",
///   so "this file is not there" and "the far side is gone" cannot be told apart at that call.
///   Only the tree call is allowed to report a gone remote.
/// - the base64 in <c>content</c> carries a newline every 60 characters, and files above 1 MB
///   come back with <c>content</c> empty — those are read with the <c>raw</c> media type instead.
/// </remarks>
public sealed class GitHubContents : IRemoteFiles, IDisposable
{
    /// <summary>Never logged, never put in a message: see <see cref="DescribeAsync"/>.</summary>
    private const string RateLimitHint = "rate limit";

    private readonly HttpClient _http;
    private readonly string _owner;
    private readonly string _repo;
    private readonly Uri _base = new("https://api.github.com/");
    private readonly bool _ownsHttp;
    private string? _branch;

    /// <summary>
    /// The last tree call's validator and its parsed answer. A poll that re-sends the validator
    /// and gets 304 costs no rate limit at all (measured 2026-10-23: a full GET moved
    /// <c>remaining</c> 4980→4979, the same call with <c>If-None-Match</c> answered 304 and the
    /// counter did not move), which is what lets the window ask every eight seconds. Kept per
    /// transport, so it dies with the board's window and one process start costs one full GET.
    /// </summary>
    private string? _treeEtag;
    private RemoteTree? _tree;

    public GitHubContents(HttpClient http, string owner, string repo, string? branch = null)
    {
        _http = http;
        _owner = owner;
        _repo = repo;
        _branch = branch;
    }

    /// <summary>Set only by <see cref="Create"/>, which is the one path that built the client.</summary>
    internal GitHubContents(HttpClient http, string owner, string repo, string? branch, bool ownsHttp)
        : this(http, owner, repo, branch)
    {
        _ownsHttp = ownsHttp;
    }

    /// <summary>A client this class built carries the device's token in its default headers, so it is this class's to close.</summary>
    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    /// <summary>
    /// The one place a token is attached, so it never has to appear in a path, a message or a
    /// log line. GitHub rejects a request with no User-Agent, so that goes here too.
    /// </summary>
    public static GitHubContents Create(string owner, string repo, string token, string? branch = null)
    {
        var http = new HttpClient();

        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "outspread-board-sync");
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return new GitHubContents(http, owner, repo, branch, ownsHttp: true);
    }

    /// <summary>The branch files are read from and written to. Resolved from the repository when not given.</summary>
    public string Branch => _branch ?? throw new InvalidOperationException("the branch has not been resolved yet");

    public async Task<RemoteTree?> ReadTreeAsync(CancellationToken cancel)
    {
        await EnsureBranchAsync(cancel);

        var response = await GetAsync($"git/trees/{Escape(_branch!)}?recursive=1", cancel, ifNoneMatch: _treeEtag);

        // "Nothing you wrote last time changed" — the body would be identical to the one already
        // parsed, and GitHub does not charge for finding that out.
        if (response.StatusCode == HttpStatusCode.NotModified && _tree is not null)
        {
            response.Dispose();

            return _tree;
        }

        // A repository with no commits has no tree at all. That is an empty far side, not a
        // missing one: reporting it gone would make the very first upload impossible.
        if (IsEmptyRepository(response))
        {
            return new RemoteTree("no-commits", []);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        // Anything else that is not an answer (403 rate limit, 5xx) is a round that did not
        // happen, not an empty folder: saying so here keeps the engine from uploading over a
        // far side it never actually saw.
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"the far folder did not answer: {await SafeMessageAsync(response, cancel)}");
        }

        using var document = await ReadJsonAsync(response, cancel);

        var root = document.RootElement;

        var entries = new List<RemoteEntry>();

        if (root.TryGetProperty("tree", out var tree))
        {
            foreach (var item in tree.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var kind)
                    && kind.GetString() == "blob")
                {
                    entries.Add(new RemoteEntry(
                        item.GetProperty("path").GetString()!,
                        item.GetProperty("sha").GetString()!,
                        item.TryGetProperty("size", out var size) ? size.GetInt32() : 0));
                }
            }
        }

        // Naming a folder that recursion could not finish is worse than not answering: the
        // engine would compare tokens for a folder it never saw whole.
        if (root.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
        {
            throw new IOException(
                $"the far folder is too large for one tree call ({entries.Count} entries shown, GitHub truncates the response); " +
                "refusing to sync against a list that is not complete");
        }

        var parsed = new RemoteTree(root.GetProperty("sha").GetString()!, entries);

        // Only a full answer updates the validator: a 304 leaves both alone, and a 409/404 round
        // must not remember a tree it never saw.
        _treeEtag = response.Headers.ETag?.Tag;
        _tree = parsed;

        return parsed;
    }

    public async Task<RemoteFile?> ReadAsync(string path, CancellationToken cancel)
    {
        var response = await GetAsync($"contents/{EscapePath(path)}?ref={Escape(_branch!)}", cancel);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"the far file did not answer: {await SafeMessageAsync(response, cancel)}");
        }

        using var document = await ReadJsonAsync(response, cancel);

        var root = document.RootElement;

        // A directory answers with an array; this store only carries files.
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("sha", out var sha))
        {
            return null;
        }

        var token = sha.GetString()!;

        var encoded = root.TryGetProperty("encoding", out var encoding) ? encoding.GetString() : null;

        if (encoded == "base64" && root.TryGetProperty("content", out var content))
        {
            // FromBase64String ignores the newlines GitHub puts every 60 characters.
            var text = content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : string.Empty;

            return new RemoteFile(Convert.FromBase64String(text), token);
        }

        // Past 1 MB the same call still gives the token but leaves the content empty. The raw
        // media type serves the bytes through api.github.com, which is the domain that is
        // reachable from here — download_url points at raw.githubusercontent.com, which is not.
        using var raw = await GetAsync($"contents/{EscapePath(path)}?ref={Escape(_branch!)}", cancel, acceptRaw: true);

        if (!raw.IsSuccessStatusCode)
        {
            throw new IOException($"the far file is too large for this endpoint and the raw read failed: {await SafeMessageAsync(raw, cancel)}");
        }

        return new RemoteFile(await raw.Content.ReadAsByteArrayAsync(cancel), token);
    }

    public async Task<WriteResult> WriteAsync(string path, byte[] content, string? expectToken, CancellationToken cancel)
    {
        await EnsureBranchAsync(cancel);

        var body = new Dictionary<string, string>
        {
            ["message"] = CommitMessage(path),
            ["content"] = Convert.ToBase64String(content),
            ["branch"] = _branch!,
        };

        if (expectToken is not null)
        {
            body["sha"] = expectToken;
        }

        var response = await SendAsync(HttpMethod.Put, $"contents/{EscapePath(path)}", Json(body), cancel);

        var status = (int)response.StatusCode;

        if (status is 200 or 201)
        {
            using var document = await ReadJsonAsync(response, cancel);

            var written = document.RootElement.TryGetProperty("content", out var created)
                ? created.GetProperty("sha").GetString()
                : null;

            return new WriteResult(WriteOutcome.Written, written);
        }

        return await DescribeAsync(response, expectToken, cancel);
    }

    public async Task<WriteOutcome> DeleteAsync(string path, string expectToken, CancellationToken cancel)
    {
        await EnsureBranchAsync(cancel);

        var response = await SendAsync(
            HttpMethod.Delete,
            $"contents/{EscapePath(path)}",
            Json(new Dictionary<string, string>
            {
                ["message"] = CommitMessage(path),
                ["sha"] = expectToken,
                ["branch"] = _branch!,
            }),
            cancel);

        var status = (int)response.StatusCode;

        if (status is 200 or 204)
        {
            return WriteOutcome.Written;
        }

        return (await DescribeAsync(response, expectToken, cancel)).Outcome;
    }

    /// <summary>
    /// Turns a refusal into one of the six answers the engine can act on. Only GitHub's own
    /// <c>message</c> and <c>status</c> fields are kept: a response body also carries
    /// <c>download_url</c>, and for a private repository that URL has a write-capable token in
    /// its query string, so the whole body must never be surfaced as an error.
    /// </summary>
    private static async Task<WriteResult> DescribeAsync(HttpResponseMessage response, string? expectToken, CancellationToken cancel)
    {
        var status = (int)response.StatusCode;

        var message = await SafeMessageAsync(response, cancel);

        var retryable = message.Contains(RateLimitHint, StringComparison.OrdinalIgnoreCase);

        var detail = $"HTTP {status}: {message}";

        // 409 says the token we handed over is not what the far side holds. It does not say what
        // the far side holds, so no token comes back and the engine has to re-read.
        if (status == 409 || (status == 422 && message.Contains("sha", StringComparison.OrdinalIgnoreCase)))
        {
            return new WriteResult(WriteOutcome.Conflict, null, detail);
        }

        if (status == 404)
        {
            return new WriteResult(WriteOutcome.Missing, null, detail);
        }

        if (status is 401 or 403 && !retryable)
        {
            return new WriteResult(WriteOutcome.Unauthorized, null, detail);
        }

        if (status is 403 or 429 or >= 500)
        {
            return new WriteResult(WriteOutcome.RetryLater, null, detail);
        }

        // 422 for anything else (content over the size this endpoint carries, invalid path) will
        // not improve by retrying.
        return new WriteResult(WriteOutcome.Rejected, null, detail);
    }

    private async Task EnsureBranchAsync(CancellationToken cancel)
    {
        if (_branch is not null)
        {
            return;
        }

        var response = await GetAsync(string.Empty, cancel);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"the far repository did not answer: {await SafeMessageAsync(response, cancel)}");
        }

        using var document = await ReadJsonAsync(response, cancel);

        _branch = document.RootElement.TryGetProperty("default_branch", out var name)
            ? name.GetString()
            : null;

        if (string.IsNullOrEmpty(_branch))
        {
            throw new IOException("the far repository answered without a default branch");
        }
    }

    private async Task<HttpResponseMessage> GetAsync(
        string suffix,
        CancellationToken cancel,
        bool acceptRaw = false,
        string? ifNoneMatch = null)
    {
        var url = suffix.Length == 0
            ? $"repos/{Escape(_owner)}/{Escape(_repo)}"
            : $"repos/{Escape(_owner)}/{Escape(_repo)}/{suffix}";

        var request = new HttpRequestMessage(HttpMethod.Get, Base(url));

        if (acceptRaw)
        {
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw"));
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }

        return await SendAsync(request, cancel);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string suffix, string json, CancellationToken cancel)
    {
        var request = new HttpRequestMessage(method, Base($"repos/{Escape(_owner)}/{Escape(_repo)}/{suffix}"))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        return await SendAsync(request, cancel);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
    {
        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);

        // A redirect that landed on a different host means we would be reading JSON from
        // somewhere the caller did not authorize a token for, so the answer is refused rather
        // than parsed.
        var landed = response.RequestMessage?.RequestUri?.Host;

        if (landed is not null && !string.Equals(landed, _base.Host, StringComparison.OrdinalIgnoreCase))
        {
            var safe = await SafeMessageAsync(response, cancel);
            response.Dispose();

            throw new HttpRequestException($"the far side redirected away from {_base.Host} ({safe})");
        }

        return response;
    }

    private Uri Base(string relative) => new(_base, relative);

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancel);

        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancel);
        }
        catch (JsonException e)
        {
            throw new IOException($"the far side answered HTTP {(int)response.StatusCode} without JSON: {e.Message}", e);
        }
    }

    /// <summary>Reads the two fields worth quoting, and never the rest of the body.</summary>
    private static async Task<string> SafeMessageAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        try
        {
            using var document = await ReadJsonAsync(response, cancel);
            var root = document.RootElement;

            var message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;

            return string.Join(" · ", new[] { message, status }.Where(part => !string.IsNullOrEmpty(part)));
        }
        catch
        {
            return $"no body ({(int)response.StatusCode} {response.ReasonPhrase})";
        }
    }

    private static bool IsEmptyRepository(HttpResponseMessage response)
        => (int)response.StatusCode == 409;

    private string CommitMessage(string path)
        => $"铺陈同步 {Path.GetFileName(path)}";

    /// <summary>Percent-encodes one path segment; the separators between them stay literal.</summary>
    private static string EscapePath(string path)
        => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Escape));

    private static string Escape(string value)
        => Uri.EscapeDataString(value);

    private static string Json(IReadOnlyDictionary<string, string> fields)
        => JsonSerializer.Serialize(fields);
}
