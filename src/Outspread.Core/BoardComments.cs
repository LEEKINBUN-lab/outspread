using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Outspread.Core;

/// <summary>一条消息。</summary>
/// <param name="Author">显示名（他自己写的没有署名时是「我」）。</param>
/// <param name="Text">正文。</param>
/// <param name="Time">写下的时刻，<c>HH:mm</c>。</param>
/// <param name="Agent">是不是 agent 留的（徽标与线程里给一枚淡色 tag）。</param>
public sealed class CommentMessage
{
    public string Author { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Time { get; set; } = string.Empty;

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

    /// <summary>他没看过的新消息（打开线程即清）。</summary>
    public bool Unread { get; set; }

    public List<CommentMessage> Messages { get; set; } = [];
}

/// <summary>
/// 一块板上的全部评论，存成 <c>&lt;板名&gt;.canvas.comments.json</c>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是旁挂文件而不是 `.canvas` 里的节点：这条判据在原型里定死了——**评论是对话，便签是内容**。
/// 写进文档就会跟便签抢位置、被同步合并当成几何去判、被别的工具画成一张卡；旁挂之后评论既占不到
/// 板面，也不会让 JSON Canvas 1.0 多出任何私有字段。
/// </para>
/// <para>
/// 读的一侧刻意宽容：文件缺了、坏了、某条线程的锚点已经不在了，都只是少显示几条，绝不拦着板子打开。
/// 写的一侧先落临时文件再原子改名，半截文件不会留在盘上。
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

    public List<CommentThread> Threads { get; set; } = [];

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
            var text = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<BoardComments>(text, Json);

            if (loaded is null)
            {
                return new BoardComments { Problem = "评论文件是空的" };
            }

            // 坏数据不进界面：没有 id 或没有锚点的线程直接丢掉，别让它变成一枚点不开的徽标。
            loaded.Threads.RemoveAll(thread => string.IsNullOrEmpty(thread.Id) || string.IsNullOrEmpty(thread.Node));

            foreach (var thread in loaded.Threads)
            {
                thread.Messages ??= [];
            }

            return loaded;
        }
        catch (Exception ex)
        {
            return new BoardComments { Problem = $"评论文件读不出：{ex.Message}" };
        }
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";

        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, path, overwrite: true);

        Revision++;
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
        var thread = new CommentThread
        {
            Id = threadId ?? NewId(),
            Node = nodeId,
            Messages = [new CommentMessage { Author = author, Text = text, Agent = agent, Time = time ?? Clock() }],
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

        var message = new CommentMessage { Author = author, Text = text, Agent = agent, Time = time ?? Clock() };

        thread.Messages.Add(message);

        // 回复一条已经结掉的线程＝它又活了。
        thread.Resolved = false;
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

        if (resolved)
        {
            thread.Unread = false;
        }

        Revision++;

        return true;
    }

    /// <summary>
    /// 整条线程连它的每一句话一起删掉。
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

    private static string NewId() => "c" + Guid.NewGuid().ToString("n")[..12];

    private static string Clock() => DateTime.Now.ToString("HH:mm");
}
