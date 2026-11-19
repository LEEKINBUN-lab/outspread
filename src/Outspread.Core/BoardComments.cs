using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Outspread.Core;

/// <summary>一条消息。</summary>
/// <param name="Author">显示名（他自己写的没有署名时是「我」）。</param>
/// <param name="Text">正文。</param>
/// <param name="Time">写下的时刻，<c>HH:mm</c>，给人看。</param>
/// <param name="Agent">是不是 agent 留的（徽标与线程里给一枚淡色 tag）。</param>
public sealed class CommentMessage
{
    /// <summary>
    /// 稳定消息号。跨设备合并按它取并集；旧文件没有这一栏，读取时按内容现算一个
    /// （同一条消息在两端算出同一个号，所以不会被当成两条）。
    /// </summary>
    public string Id { get; set; } = string.Empty;

    public string Author { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Time { get; set; } = string.Empty;

    /// <summary>写下的那一刻（UTC ISO 8601）。合并要它排序与判新旧；旧消息是空串。</summary>
    public string At { get; set; } = string.Empty;

    public bool Agent { get; set; }
}

/// <summary>
/// 一条线程：锚在某一格上的一串消息。
/// </summary>
/// <remarks>
/// 线程不是板上的节点——它没有坐标、没有尺寸、不参与排版与同步合并。锚点只记 <see cref="Node"/>，
/// 那一格被删掉时线程跟着不显示（但文件里留着，撤销回来还能看见）。
/// </remarks>
public sealed class CommentThread
{
    public string Id { get; set; } = string.Empty;

    /// <summary>锚在哪一格的 id 上。</summary>
    public string Node { get; set; } = string.Empty;

    public bool Resolved { get; set; }

    /// <summary>他没看过的新消息（打开线程即清）。这是**本机视图**，同步时不传。</summary>
    public bool Unread { get; set; }

    /// <summary>
    /// 这条线程最后一次内容变化的时刻（UTC ISO 8601）：写消息、结掉、重开都会刷新。
    /// 两端状态不一致时，新的那一方赢；相等时"已结"优先，不让合并把结掉的线程又翻回来。
    /// </summary>
    public string UpdatedAt { get; set; } = string.Empty;

    public List<CommentMessage> Messages { get; set; } = [];
}

/// <summary>删掉的线程。没有它，合并只会做并集——删掉的线程会在下一轮从别的设备走回来。</summary>
public sealed class CommentTombstone
{
    public string Id { get; set; } = string.Empty;

    public string At { get; set; } = string.Empty;
}

/// <summary>
/// 一块板上的全部评论，存成 <c>&lt;板名&gt;.canvas.comments.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是旁挂文件而不是 `.canvas` 里的节点：**评论是对话，便签是内容**。写进文档就会跟便签
/// 抢位置、被同步合并当成几何去判、被别的工具画成一张卡；旁挂之后评论既占不到板面，也不会让
/// JSON Canvas 1.0 多出任何私有字段。
/// </para>
/// <para>
/// 写的一侧与 `.canvas` 同一把按板互斥：读当前盘上的那份、按消息号合并、再原子改名。少了这一步，
/// 两个 agent 同时回一条评论就是"后写的把先写的整份覆盖"（2026-11-19 实测 12 发只活下 3 条）。
/// </para>
/// <para>
/// 读的一侧刻意宽容：文件缺了、坏了、某条线程的锚点已经不在了，都只是少显示几条，绝不拦着板子打开。
/// </para>
/// </remarks>
public sealed class BoardComments
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 中文按字面写，不转 \uXXXX——与 `.canvas` 的既有约定一致。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(5);

    public List<CommentThread> Threads { get; set; } = [];

    /// <summary>删过的线程号。与线程一起走同步，合并时并集。</summary>
    public List<CommentTombstone> Tombstones { get; set; } = [];

    /// <summary>读的时候出了什么毛病（文件坏了／不是这个形状）。没有就是 null。</summary>
    [JsonIgnore]
    public string? Problem { get; private set; }

    /// <summary>每改一次加一。界面拿它判"要不要重铺徽标层"。</summary>
    [JsonIgnore]
    public int Revision { get; private set; }

    public static BoardComments Load(string path)
    {
        if (!File.Exists(path))
        {
            return new BoardComments();
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            return new BoardComments { Problem = $"评论文件读不出：{ex.Message}" };
        }
    }

    /// <summary>从文本读一份。同步那一侧读远端的字节也走这里，宽容规则与本地一字不差。</summary>
    public static BoardComments Parse(string text)
    {
        try
        {
            // 远端那份可能由别的工具写过、带 BOM；解析器不认它，去掉一个字符就够。
            text = text.TrimStart('\uFEFF');

            var loaded = JsonSerializer.Deserialize<BoardComments>(text, Json);

            if (loaded is null)
            {
                return new BoardComments { Problem = "评论文件是空的" };
            }

            loaded.Normalise();

            return loaded;
        }
        catch (Exception ex)
        {
            return new BoardComments { Problem = $"评论文件读不出：{ex.Message}" };
        }
    }

    /// <summary>
    /// 写这一份：在按板互斥里读当前盘上的、与内存这份合并、再原子落盘。
    /// </summary>
    /// <remarks>
    /// 合并而不是直接覆盖，是并发写不丢的那一半；互斥是另一半。两者少一个，
    /// 两路 agent 同时回复就有一路消失。文件坏了的、读不出的，这里**拒绝写**——
    /// 覆盖一份读不懂的旧账等于把证据抹掉。
    /// </remarks>
    public void Save(string path)
    {
        var boardPath = BoardPathFor(path);
        using var mutex = new Mutex(initiallyOwned: false, CanvasStore.MutexNameFor(boardPath));

        var held = false;

        try
        {
            try
            {
                held = mutex.WaitOne(LockTimeout);
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            if (!held)
            {
                throw new IOException(
                    $"评论文件正被另一处写，{(int)LockTimeout.TotalSeconds} 秒内没松手；这一发没存");
            }

            var disk = new BoardComments();

            if (File.Exists(path))
            {
                var current = Parse(File.ReadAllText(path));

                if (current.Problem is not null)
                {
                    throw new IOException(current.Problem);
                }

                disk = current;
            }

            var merged = Merge(this, disk);

            WriteAtomic(path, merged.ToJson(includeUnread: true));

            Threads = merged.Threads;
            Tombstones = merged.Tombstones;
            Revision++;
        }
        finally
        {
            if (held)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>落盘用的文本。<paramref name="includeUnread"/>＝false 时去掉每条的未读标记（同步用）。</summary>
    public string ToJson(bool includeUnread = true)
    {
        if (includeUnread)
        {
            return JsonSerializer.Serialize(this, Json);
        }

        var node = JsonSerializer.SerializeToNode(this, Json)!.AsObject();

        if (node["threads"] is JsonArray threads)
        {
            foreach (var thread in threads.OfType<JsonObject>())
            {
                thread.Remove("unread");
            }
        }

        return node.ToJsonString(Json);
    }

    /// <summary>
    /// 内容指纹（不含未读——那是本机视图，翻一下不该触发一次同步）。
    /// 同步拿它判"本地这份有没有变"。
    /// </summary>
    public static string ContentDigest(BoardComments comments)
        => Ledger.Digest(Ledger.Encode(comments.ToJson(includeUnread: false)));

    /// <summary>
    /// 合并两份：线程与消息按号取并集，墓碑取并集；结没结、重没重开按 <see cref="CommentThread.UpdatedAt"/>
    /// 判新旧（相等时"已结"优先）；未读只在本机有意义——本地已有的沿用本地，远端新来的算未读。
    /// </summary>
    /// <remarks>
    /// 并集天然幂等、可交换，所以评论不需要 `.canvas` 那样的三方祖先：两端谁先谁后都合到同一个结果。
    /// 删除是全量删线程（没有消息级删除），所以墓碑只记线程号就够。
    /// </remarks>
    public static BoardComments Merge(BoardComments local, BoardComments far)
    {
        var tombstones = new Dictionary<string, CommentTombstone>(StringComparer.Ordinal);

        foreach (var tombstone in local.Tombstones.Concat(far.Tombstones))
        {
            if (string.IsNullOrEmpty(tombstone.Id))
            {
                continue;
            }

            if (!tombstones.TryGetValue(tombstone.Id, out var kept)
                || string.CompareOrdinal(tombstone.At, kept.At) > 0)
            {
                tombstones[tombstone.Id] = tombstone;
            }
        }

        var farById = new Dictionary<string, CommentThread>(StringComparer.Ordinal);

        foreach (var thread in far.Threads)
        {
            farById[thread.Id] = thread;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new BoardComments { Tombstones = [.. tombstones.Values] };

        foreach (var thread in local.Threads)
        {
            if (tombstones.ContainsKey(thread.Id))
            {
                continue;
            }

            merged.Threads.Add(farById.TryGetValue(thread.Id, out var twin)
                ? MergeThread(thread, twin)
                : Clone(thread));

            seen.Add(thread.Id);
        }

        foreach (var thread in far.Threads)
        {
            if (tombstones.ContainsKey(thread.Id) || !seen.Add(thread.Id))
            {
                continue;
            }

            var pulled = Clone(thread);

            // 远端新来的线程＝本机还没见过：给它亮一枚橙点，和板上其它"新消息"同一档。
            pulled.Unread = true;
            merged.Threads.Add(pulled);
        }

        // 规范序：两端各自合并时会拿到同一份字节。少了这一步，本地先并谁就把谁排前面，
        // 两台设备会为了"顺序不同"互相重推，内容其实一个字没差。
        merged.Threads.Sort(CompareThreads);
        merged.Tombstones.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));

        return merged;
    }

    private static int CompareThreads(CommentThread left, CommentThread right)
    {
        var leftAt = left.Messages.Count == 0 ? string.Empty : left.Messages[0].At;
        var rightAt = right.Messages.Count == 0 ? string.Empty : right.Messages[0].At;
        var order = string.CompareOrdinal(leftAt, rightAt);

        return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
    }

    public IReadOnlyList<CommentThread> ForNode(string nodeId)
    {
        List<CommentThread>? hit = null;

        foreach (var thread in Threads)
        {
            if (!string.Equals(thread.Node, nodeId, StringComparison.Ordinal))
            {
                continue;
            }

            (hit ??= []).Add(thread);
        }

        return hit ?? [];
    }

    /// <summary>这一格上一共多少条消息（徽标上那个数）。<paramref name="unresolvedOnly"/> 只看未解决的线程。</summary>
    public int MessageCount(string nodeId, bool unresolvedOnly = false)
    {
        var count = 0;

        foreach (var thread in Threads)
        {
            if (!string.Equals(thread.Node, nodeId, StringComparison.Ordinal)
                || (unresolvedOnly && thread.Resolved))
            {
                continue;
            }

            count += thread.Messages.Count;
        }

        return count;
    }

    /// <summary>有多少条还没结掉。算出来的，不落盘——写进文件就多一个会骗人的真值来源。</summary>
    [JsonIgnore]
    public int UnresolvedThreads
    {
        get
        {
            var count = 0;

            foreach (var thread in Threads)
            {
                if (!thread.Resolved)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>一共多少条消息。同样算出来的，不落盘。</summary>
    [JsonIgnore]
    public int MessageTotal
    {
        get
        {
            var count = 0;

            foreach (var thread in Threads)
            {
                count += thread.Messages.Count;
            }

            return count;
        }
    }

    /// <summary>在这一格上开一条新线程；<paramref name="threadId"/> 不填就现铸一个。</summary>
    public CommentThread AddThread(string nodeId, string author, string text, bool agent, string? threadId = null, string? time = null)
    {
        var now = DateTimeOffset.UtcNow;

        var thread = new CommentThread
        {
            Id = threadId ?? NewId("c"),
            Node = nodeId,
            UpdatedAt = now.ToString("O"),
            Messages =
            [
                new CommentMessage
                {
                    Id = NewId("m"),
                    Author = author,
                    Text = text,
                    Agent = agent,
                    Time = time ?? now.ToLocalTime().ToString("HH:mm"),
                    At = now.ToString("O"),
                },
            ],
        };

        Threads.Add(thread);
        Revision++;

        return thread;
    }

    public CommentMessage? Reply(string threadId, string author, string text, bool agent, string? time = null)
    {
        var thread = Find(threadId);

        if (thread is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;

        var message = new CommentMessage
        {
            Id = NewId("m"),
            Author = author,
            Text = text,
            Agent = agent,
            Time = time ?? now.ToLocalTime().ToString("HH:mm"),
            At = now.ToString("O"),
        };

        thread.Messages.Add(message);

        // 回复一条已经结掉的线程＝它又活了。
        thread.Resolved = false;
        thread.UpdatedAt = now.ToString("O");
        Revision++;

        return message;
    }

    public bool SetResolved(string threadId, bool resolved)
    {
        var thread = Find(threadId);

        if (thread is null || thread.Resolved == resolved)
        {
            return false;
        }

        thread.Resolved = resolved;
        thread.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");

        if (resolved)
        {
            thread.Unread = false;
        }

        Revision++;

        return true;
    }

    /// <summary>
    /// 整条线程连它的每一句话一起删掉，并留一枚墓碑——不然下一轮合并会把它从别的设备带回来。
    /// </summary>
    /// <remarks>
    /// 这里没有撤销账（评论是旁挂的，不进文档那本账），所以调用方必须先问一句——
    /// 界面上那个"删掉这条线程？"就是在问这一句。
    /// </remarks>
    public bool RemoveThread(string threadId)
    {
        var index = Threads.FindIndex(thread => string.Equals(thread.Id, threadId, StringComparison.Ordinal));

        if (index < 0)
        {
            return false;
        }

        Threads.RemoveAt(index);
        Tombstones.Add(new CommentTombstone { Id = threadId, At = DateTimeOffset.UtcNow.ToString("O") });
        Revision++;

        return true;
    }

    public bool MarkRead(string threadId)
    {
        var thread = Find(threadId);

        if (thread is null || !thread.Unread)
        {
            return false;
        }

        thread.Unread = false;
        Revision++;

        return true;
    }

    public CommentThread? Find(string threadId)
    {
        foreach (var thread in Threads)
        {
            if (string.Equals(thread.Id, threadId, StringComparison.Ordinal))
            {
                return thread;
            }
        }

        return null;
    }

    private void Normalise()
    {
        Threads ??= [];
        Tombstones ??= [];

        Tombstones.RemoveAll(tombstone => string.IsNullOrEmpty(tombstone.Id));

        var gone = new HashSet<string>(
            Tombstones.Select(tombstone => tombstone.Id), StringComparer.Ordinal);

        // 坏数据不进界面：没有 id 或没有锚点的线程直接丢掉，别让它变成一枚点不开的徽标。
        Threads.RemoveAll(thread =>
            string.IsNullOrEmpty(thread.Id)
            || string.IsNullOrEmpty(thread.Node)
            || gone.Contains(thread.Id));

        foreach (var thread in Threads)
        {
            thread.Messages ??= [];

            for (var i = 0; i < thread.Messages.Count; i++)
            {
                var message = thread.Messages[i];

                if (string.IsNullOrEmpty(message.Id))
                {
                    // 旧文件没有消息号：按内容现算。两端各读一次会算出同一个号，合并不重复。
                    message.Id = LegacyMessageId(thread.Id, i, message);
                }

                if (string.IsNullOrEmpty(message.Author))
                {
                    message.Author = "我";
                }
            }

            var newest = thread.Messages
                .Select(message => message.At)
                .Where(at => !string.IsNullOrEmpty(at))
                .DefaultIfEmpty(string.Empty)
                .Max(StringComparer.Ordinal)!;

            if (string.CompareOrdinal(newest, thread.UpdatedAt) > 0)
            {
                thread.UpdatedAt = newest;
            }
        }
    }

    private static CommentThread MergeThread(CommentThread local, CommentThread far)
    {
        var order = string.CompareOrdinal(far.UpdatedAt, local.UpdatedAt);

        // 相等时"已结"优先：两端都没时钟的那一档，宁可保持结掉，也别凭空翻回未解决。
        var resolved = order == 0
            ? local.Resolved || far.Resolved
            : order > 0 ? far.Resolved : local.Resolved;

        var localIds = new HashSet<string>(local.Messages.Select(message => message.Id), StringComparer.Ordinal);
        var farOnly = far.Messages.Count(message => !localIds.Contains(message.Id));

        var messages = MergeMessages(local.Messages, far.Messages);
        var updated = string.CompareOrdinal(far.UpdatedAt, local.UpdatedAt) > 0 ? far.UpdatedAt : local.UpdatedAt;

        var newest = messages
            .Select(message => message.At)
            .Where(at => !string.IsNullOrEmpty(at))
            .DefaultIfEmpty(string.Empty)
            .Max(StringComparer.Ordinal)!;

        // 同一线程的锚点只该一致；万一两端各记了一份，按字典序取小的，保证两端算出同一个答案。
        var node = local.Node.Length == 0
            ? far.Node
            : far.Node.Length == 0
                ? local.Node
                : string.CompareOrdinal(local.Node, far.Node) <= 0 ? local.Node : far.Node;

        return new CommentThread
        {
            Id = local.Id,
            Node = node,
            Resolved = resolved,
            Unread = local.Unread || farOnly > 0,
            UpdatedAt = string.CompareOrdinal(newest, updated) > 0 ? newest : updated,
            Messages = messages,
        };
    }

    private static List<CommentMessage> MergeMessages(List<CommentMessage> local, List<CommentMessage> far)
    {
        var merged = new List<CommentMessage>(local.Count + far.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var message in local)
        {
            if (seen.Add(message.Id))
            {
                merged.Add(message);
            }
        }

        foreach (var message in far)
        {
            if (seen.Add(message.Id))
            {
                merged.Add(message);
            }
        }

        // 按写入时刻排；同一毫秒按消息号兜底。旧消息（没有 At）保持原有先后，排在有时刻的前面。
        return merged
            .OrderBy(message => string.IsNullOrEmpty(message.At) ? "0" : message.At, StringComparer.Ordinal)
            .ThenBy(message => message.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static CommentThread Clone(CommentThread thread)
        => new()
        {
            Id = thread.Id,
            Node = thread.Node,
            Resolved = thread.Resolved,
            Unread = thread.Unread,
            UpdatedAt = thread.UpdatedAt,
            Messages = thread.Messages.Select(Clone).ToList(),
        };

    private static CommentMessage Clone(CommentMessage message)
        => new()
        {
            Id = message.Id,
            Author = message.Author,
            Text = message.Text,
            Time = message.Time,
            At = message.At,
            Agent = message.Agent,
        };

    private static string LegacyMessageId(string threadId, int index, CommentMessage message)
    {
        var seed = $"{threadId}\u0001{index}\u0001{message.Author}\u0001{message.Time}\u0001{(message.Agent ? 1 : 0)}\u0001{message.Text}";

        return "m" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..12].ToLowerInvariant();
    }

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("n")[..12];

    /// <summary>
    /// 评论文件对应的板路径：同名的那么一份去掉尾巴。互斥按板名算，所以评论与 `.canvas`
    /// 两边的写入也互斥，而不是两套各锁各的。
    /// </summary>
    private static string BoardPathFor(string commentsPath)
        => commentsPath.EndsWith(".comments.json", StringComparison.OrdinalIgnoreCase)
            ? commentsPath[..^".comments.json".Length]
            : commentsPath;

    private static void WriteAtomic(string path, string text)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = $"{path}.{Environment.ProcessId}.tmp";

        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }
}
