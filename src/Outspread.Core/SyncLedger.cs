using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>
/// What one device remembers about the last state all sides agreed on.
/// </summary>
/// <remarks>
/// This never goes in the repository. Two devices that shared a view of the ancestor
/// would have to agree about it over the same channel they are arguing about, and the
/// ancestor is exactly the thing a merge needs from outside the document — JSON Canvas
/// 1.0 carries no revision or timestamp to recover it from.
/// </remarks>
public sealed class SyncState
{
    public int Schema { get; set; } = Ledger.Schema;

    /// <summary>Stable per install. It settles ties, so it must not change between merges.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Pushes this device has made. Compared against the far side's, never against a clock.</summary>
    public int Clock { get; set; }

    /// <summary>Token the far file carried last time we saw it: the compare-and-set argument for the next write.</summary>
    public string? DocToken { get; set; }

    public string? ManifestToken { get; set; }

    /// <summary>Revision the ancestor and the far file agreed on last time.</summary>
    public int Rev { get; set; }

    /// <summary>Digest of the far document's bytes at that revision, so a "did it change" answer does not depend on a token format.</summary>
    public string? ContentSha { get; set; }

    /// <summary>File name of the ancestor's full text inside the ledger folder.</summary>
    public string? BaseFile { get; set; }

    /// <summary>Digest of the bytes this device last published, so a pending change is measurable rather than assumed.</summary>
    public string? PublishedSha { get; set; }

    public string? LastError { get; set; }

    public string? LastErrorKind { get; set; }
}

/// <summary>
/// Where this device keeps what sync needs. Computed in one place because the command line and
/// the window must land on the same folder: two answers for one board means two ledgers, and a
/// ledger is the ancestor a merge is not allowed to guess at.
/// </summary>
public static class SyncLayout
{
    /// <summary>The device-wide folder holding <c>remote.json</c> and one subfolder per board.</summary>
    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Outspread",
        "sync");

    public static string LedgerFolder(string boardPath)
        => Path.Combine(Root, Path.GetFileNameWithoutExtension(boardPath).ToLowerInvariant());
}

/// <summary>
/// Which far repository this device pushes to, kept in the ledger folder and never in the
/// board folder: a board copied somewhere else must not carry the address of the place it
/// syncs with, let alone the key that can overwrite it.
/// </summary>
public sealed class SyncConfig
{
    public const string FileName = "remote.json";

    public string Schema { get; set; } = "1";

    /// <summary>"github" today; a second host is a value here plus a branch in <see cref="OpenRemote"/>.</summary>
    public string Host { get; set; } = "github";

    public string Owner { get; set; } = string.Empty;

    public string Repo { get; set; } = string.Empty;

    /// <summary>
    /// Given, not discovered. Resolving it costs one extra request on every round that would
    /// otherwise be a single tree call — measured 2 against 1 on the live API.
    /// </summary>
    public string Branch { get; set; } = "main";

    public static SyncConfig? Load(string ledgerFolder)
    {
        var path = Path.Combine(ledgerFolder, FileName);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SyncConfig>(File.ReadAllText(path, Encoding.UTF8), Ledger.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string ledgerFolder)
    {
        Directory.CreateDirectory(ledgerFolder);

        File.WriteAllText(
            Path.Combine(ledgerFolder, FileName),
            JsonSerializer.Serialize(this, Ledger.Options) + Environment.NewLine,
            new UTF8Encoding(false));
    }

    public string ShortName => $"{Owner}/{Repo}";

    public bool IsSet => !string.IsNullOrEmpty(Owner) && !string.IsNullOrEmpty(Repo);

    /// <summary>Builds the transport this config points at, with the stored token attached.</summary>
    /// <exception cref="SyncSetupException">No repository configured, or no token stored.</exception>
    public IRemoteFiles OpenRemote()
    {
        if (!IsSet)
        {
            throw new SyncSetupException($"还没定远端仓库（{Host} 的 owner/repo 是空的）；先跑 outspread sync link --repo 名字");
        }

        var token = SyncCredentials.Read();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new SyncSetupException(
                $"没有找到 {ShortName} 的令牌。在凭据管理器里存一条：cmdkey /generic:{SyncCredentials.Target} /user:{Owner} /pass:<令牌>，" +
                $"或者跑 outspread sync link --token-stdin 从标准输入喂进来");
        }

        return Host.ToLowerInvariant() switch
        {
            "github" => GitHubContents.Create(Owner, Repo, token, Branch),
            _ => throw new SyncSetupException($"这台机器还不认识「{Host}」这个远端；现在只接了 github"),
        };
    }
}

/// <summary>A configuration problem is a sentence to read, not a stack trace. Named apart from the CLI's own
/// usage error so a message says which layer gave up.</summary>
public sealed class SyncSetupException : Exception
{
    public SyncSetupException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The one non-secret file at the repository root: what manages this folder and which format
/// version the boards inside it speak. Deliberately it does NOT list the boards — a list read
/// from the tree is the fact of this moment, while a list written into a file goes stale, and
/// a stale inventory is worse than none.
/// </summary>
public sealed class SyncWorkspace
{
    public const string RepoPath = "outspread.sync.json";

    public int Schema { get; set; } = Ledger.Schema;

    /// <summary>What the `<名字>.canvas` files inside are. Bump only when the two ends must disagree.</summary>
    public string Format { get; set; } = "jsoncanvas-1.0";

    public string? WrittenBy { get; set; }

    public string? At { get; set; }

    /// <summary>
    /// LF, closed with exactly one: the bytes of this file get compared by two
    /// implementations, and a platform-dependent line ending would make the same content
    /// produce two different digests.
    /// </summary>
    public byte[] ToBytes()
        => Ledger.Encode(JsonText.NormalizeNewLines(JsonSerializer.Serialize(this, Ledger.Options)) + "\n");

    public static SyncWorkspace? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncWorkspace>(text, Ledger.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Where a board lives on the far side. Derived from the file name so several boards can share one folder.</summary>
public sealed record SyncPaths(string DocPath, string ManifestPath)
{
    public static SyncPaths ForBoard(string boardPath)
    {
        var stem = Path.GetFileNameWithoutExtension(boardPath);

        if (string.IsNullOrEmpty(stem))
        {
            stem = "scratch";
        }

        return new SyncPaths($"boards/{stem}.canvas", $"boards/.sync/{stem}.manifest.json");
    }
}

/// <summary>
/// The small document both ends read to answer "whose word is newer" and "how far
/// apart are we". Kept separate from the board so the board stays pure JSON Canvas.
/// </summary>
public sealed class SyncManifest
{
    public int Schema { get; set; } = Ledger.Schema;

    public int Rev { get; set; }

    /// <summary>Digest of the board bytes this revision describes.</summary>
    public string? ContentSha { get; set; }

    /// <summary>The device that published it, and its clock at that moment.</summary>
    public string? By { get; set; }

    public int Clock { get; set; }

    public string? At { get; set; }

    public static byte[] ToBytes(SyncManifest manifest)
        => Ledger.Encode(JsonSerializer.SerializeToNode(manifest, Ledger.Options)!.ToJsonString(Ledger.Options));

    public static SyncManifest? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncManifest>(text, Ledger.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Reads and writes one device's ledger: the state it holds, and the full text of the
/// ancestor it merges against.
/// </summary>
public static class Ledger
{
    public const int Schema = 1;

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public const string StateFileName = "state.json";

    private static readonly UTF8Encoding NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static byte[] Encode(string text) => NoBom.GetBytes(text);

    public static string Digest(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

public sealed class SyncLedger
{
    public SyncLedger(string folder)
    {
        Folder = folder;
        Directory.CreateDirectory(folder);
    }

    public string Folder { get; }

    public string StatePath => Path.Combine(Folder, Ledger.StateFileName);

    public SyncState Load()
    {
        var minted = false;

        if (!File.Exists(StatePath))
        {
            minted = true;

            return Persist(NewState());
        }

        try
        {
            var state = JsonSerializer.Deserialize<SyncState>(
                File.ReadAllText(StatePath, Encoding.UTF8),
                Ledger.Options);

            if (state is null)
            {
                minted = true;

                return Persist(NewState());
            }

            if (string.IsNullOrEmpty(state.DeviceId))
            {
                // Minting is not a read-only act: the device name settles merge ties, so a
                // status line that showed a different one on every call would be reporting a
                // fact that was never a fact. It goes to disk the moment it exists.
                state.DeviceId = MintDeviceId();
                minted = true;
            }

            return minted ? Persist(state) : state;
        }
        catch (JsonException)
        {
            // A ledger that cannot be read is not a licence to merge against nothing: the
            // ancestor comes back null and the first round adopts the far document instead of
            // guessing. The unreadable state file itself is left alone for a human to look at.
            return NewState();
        }
    }

    private SyncState NewState() => new() { DeviceId = MintDeviceId() };

    private SyncState Persist(SyncState state)
    {
        Save(state);

        return state;
    }

    public void Save(SyncState state)
    {
        var text = JsonSerializer.Serialize(state, Ledger.Options);
        File.WriteAllText(StatePath, text + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// Stores the ancestor's full text and returns its file name. Only the digest is
    /// never enough: a three-way merge needs the fields as they were, or it becomes a
    /// two-way argument where the newest file wins every line.
    /// </summary>
    public string KeepBase(CanvasDocument ancestor)
    {
        var text = ancestor.ToJson();
        var digest = Ledger.Digest(Ledger.Encode(text));
        var name = $"base-{digest[..12]}.canvas";

        File.WriteAllText(Path.Combine(Folder, name), text, new UTF8Encoding(false));

        return name;
    }

    public CanvasDocument? ReadBase(SyncState state)
    {
        if (string.IsNullOrEmpty(state.BaseFile))
        {
            return null;
        }

        var path = Path.Combine(Folder, state.BaseFile);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return CanvasDocument.Parse(File.ReadAllText(path, Encoding.UTF8), path);
        }
        catch (CanvasDocumentException)
        {
            return null;
        }
    }

    private static string MintDeviceId()
    {
        var name = new string(
            (Environment.MachineName ?? string.Empty)
                .Where(char.IsLetterOrDigit)
                .Take(8)
                .ToArray());

        var bytes = RandomNumberGenerator.GetBytes(3);

        return $"{(name.Length > 0 ? name.ToLowerInvariant() : "device")}-{Convert.ToHexString(bytes).ToLowerInvariant()}";
    }
}
