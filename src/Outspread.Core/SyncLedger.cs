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

    /// <summary>
    /// Pictures this device tried to take away and could not finish — the far side refused, the
    /// local file was locked, a board beside it was mid-write. The far ledger keeps the
    /// deletion for everyone else, but a round that finds the far side unmoved never reads it,
    /// so this is where this device's own unfinished business waits.
    /// </summary>
    public List<string> PendingDeletions { get; set; } = [];
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

    /// <summary>
    /// The folder this subsystem used before the product was renamed. It is not hypothetical:
    /// the rename moved <see cref="Root"/>, which silently hid the far-repository pointer and,
    /// worse, the ancestor text — and a merge without its ancestor stops being a merge.
    /// </summary>
    public static string LegacyRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TanKai",
        "sync");

    public static string LedgerFolder(string boardPath)
        => Path.Combine(Root, Path.GetFileNameWithoutExtension(boardPath).ToLowerInvariant());

    private static string? migratedOnce;

    /// <summary>
    /// Moves the old folder aside-and-back into the new one, once per process. Nothing is
    /// deleted: the old path is renamed, so if the new location already had content the move
    /// is refused and reported rather than swallowing a ledger.
    /// </summary>
    /// <returns>A sentence saying what happened, or null when there was nothing to do.</returns>
    public static string? Migrate()
    {
        if (migratedOnce is not null)
        {
            return migratedOnce.Length == 0 ? null : migratedOnce;
        }

        var legacy = LegacyRoot;

        if (!Directory.Exists(legacy) || Directory.Exists(Root))
        {
            migratedOnce = string.Empty;

            return null;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Root)!);
            Directory.Move(legacy, Root);

            migratedOnce = $"账本从旧名字搬过来了：{legacy} → {Root}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            migratedOnce = $"旧账本 {legacy} 没能搬到 {Root}（{ex.Message}）；同步先按空配置处理，旧文件一个字没动";
        }

        return migratedOnce;
    }
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

        File.WriteAllBytes(
            Path.Combine(ledgerFolder, FileName),
            Ledger.Encode(JsonSerializer.Serialize(this, Ledger.Options) + Environment.NewLine));
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

    /// <summary>
    /// The name this file had before the product was renamed. <c>link</c> replaces it on the far
    /// side once; leaving both would mean two answers to "which format is this folder".
    /// </summary>
    public const string LegacyRepoPath = "tankan.sync.json";

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

    /// <summary>
    /// Pictures this board stopped pointing at, oldest first. A board file can only say what
    /// it uses, never what it removed, so a deletion has to be written down somewhere the
    /// other ends will read: this is that somewhere. An entry fires only while nothing
    /// references the path — a picture that comes back into use moots its own tombstone.
    /// </summary>
    public List<string> DroppedAssets { get; set; } = [];

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

    /// <summary>
    /// The single place text becomes bytes for the sync layer, and it normalises line endings
    /// on the way through. The board file already had to be LF for the same reason: the ledger
    /// entry travels to the far side and the phone reads it as LF, so a CRLF from .NET's
    /// indented writer would make one content answer as two different digests.
    /// </summary>
    public static byte[] Encode(string text) => NoBom.GetBytes(JsonText.NormalizeNewLines(text));

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
        // Trailing newline, like the phone writes: these two are device-local, but an
        // unnecessary byte difference between the two implementations is a bug waiting for
        // the day one of them starts comparing them. Encode normalises, so the platform
        // ending here still lands as LF in the file.
        File.WriteAllBytes(StatePath, Ledger.Encode(JsonSerializer.Serialize(state, Ledger.Options) + Environment.NewLine));
    }

    /// <summary>
    /// Stores the ancestor's full text and returns its file name. Only the digest is
    /// never enough: a three-way merge needs the fields as they were, or it becomes a
    /// two-way argument where the newest file wins every line.
    /// </summary>
    public string KeepBase(CanvasDocument ancestor)
    {
        var bytes = Ledger.Encode(ancestor.ToJson());
        var name = $"base-{Ledger.Digest(bytes)[..12]}.canvas";

        // The name claims to be the hash of this file, so the bytes written are the bytes that
        // were hashed — not the text before normalisation.
        File.WriteAllBytes(Path.Combine(Folder, name), bytes);

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
