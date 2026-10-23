using System.Text.Json;
using System.Text.Json.Nodes;

namespace Outspread.Core;

/// <summary>一条改名标记：一块板从 <see cref="From"/> 这个名字搬到了 <see cref="To"/>。</summary>
/// <param name="At">发起改名那台设备的 UTC 时刻，写成定宽的 <c>yyyy-MM-ddTHH:mm:ssZ</c>：
/// 比较它就是 Ordinal 比字符串，而定宽保证 Ordinal 与时间同序。换格式等于换裁决。</param>
public sealed record RenameEntry(string From, string To, string By, string At);

/// <summary>
/// <c>boards/.sync/renames.json</c> —— 改名标记。它是进仓、两端都读的文件，所以它的**解析规则**
/// 是契约的一部分：规矩与决胜写在 <c>docs/sync-contract.md</c>「改名怎么跨端跟上」，两端各自回放
/// <c>golden/renames/vectors.json</c> 钉住同一批答案。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要有这份东西：一块板改了名，同伴设备看到的只是"我那份远端文件不见了"。今天它会把这读成
/// 一次删除（安卓拿空文档去合并，把本地整块清空再推回去；桌面把这块当"第一次上传"重新发一份），
/// 两种都是把改名变成了丢内容。
/// </para>
/// <para>
/// 存的是**板名**（文件名去掉 <c>.canvas</c>），不是路径：路径由 <see cref="SyncPaths.ForBoard"/>
/// 从名字派生，写两遍就是第二个真相。
/// </para>
/// </remarks>
public sealed class RenameLog
{
    public const string RepoPath = "boards/.sync/renames.json";

    /// <summary>条数上限：与 <c>droppedAssets</c> 同一档。挤出去的是最旧的——改名是低频动作，
    /// 64 条已经跨过"一台设备关机几周再回来"那格。</summary>
    public const int MaxEntries = 64;

    /// <summary>链最多走几步。八步之外还不收敛的东西不可能是改名史，是环或脏数据 ⇒ 不跟。</summary>
    public const int MaxChainHops = 8;

    public int Schema { get; set; } = Ledger.Schema;

    public List<RenameEntry> Entries { get; set; } = [];

    /// <summary>
    /// 标记上那个时刻的**线上格式**：定宽 UTC、秒级。两端必须写成同一个形状——决胜就是 Ordinal 比
    /// 字符串，而 <c>...Z</c> 与 <c>...+00:00</c> 混在一起比，同一秒的两条会判出反的胜负。
    /// 秒级平手由 <c>by</c> 决胜（契约第 3 条），所以精度到这里够用。
    /// </summary>
    public static string Stamp(DateTimeOffset at)
        => at.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static byte[] ToBytes(RenameLog log)
        => Ledger.Encode(JsonSerializer.SerializeToNode(log, Ledger.Options)!.ToJsonString(Ledger.Options));

    /// <summary>
    /// 读不懂就是"没有标记"，不是"出错了要停下来"：这一份只用来**多给一条出路**，读不出来的后果
    /// 是退回今天已知的行为（把远端不见了当删除），而不是把同步卡死。调用方要把这句报出来。
    /// </summary>
    public static RenameLog? Parse(string? text, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return new RenameLog();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<RenameLog>(text, Ledger.Options);

            if (parsed is null)
            {
                problem = "改名标记是空的";

                return new RenameLog();
            }

            parsed.Entries.RemoveAll(entry => entry is null || string.IsNullOrEmpty(entry.From) || string.IsNullOrEmpty(entry.To));

            return parsed;
        }
        catch (JsonException e)
        {
            problem = e.Message;

            return new RenameLog();
        }
    }

    public static RenameLog? Parse(string? text) => Parse(text, out _);

    /// <summary>这条改名是不是已经在这份日志里了？补第 ④ 步的那一发用它：同一条不重写第二遍。</summary>
    public bool Has(string from, string to, string by)
        => Entries.Any(entry => entry.From == from && entry.To == to && entry.By == by);

    /// <summary>
    /// 追加一条，旧在前、超上限挤最旧的（与 <c>droppedAssets</c> 同一口味）。
    /// 同一条重复追加（重试那一发）不会留两份。
    /// </summary>
    public void Append(RenameEntry entry)
    {
        Entries.RemoveAll(kept => kept.From == entry.From && kept.To == entry.To && kept.By == entry.By);
        Entries.Add(entry);

        while (Entries.Count > MaxEntries)
        {
            Entries.RemoveAt(0);
        }
    }

    /// <summary>
    /// 我的板名 <paramref name="stem"/> 是不是被别人改走了？是就给出**最终**那个名字，否则 null。
    /// <paramref name="remoteHasBoard"/> 用来问"这个名字的 <c>boards/&lt;名&gt;.canvas</c> 还在仓里吗"
    /// ——由调用方拿本轮那发 tree 回答，这里不碰网络。
    /// </summary>
    /// <remarks>
    /// 三条会答 null（＝不跟，退回已知的删除语义）：链成环、超过 <see cref="MaxChainHops"/> 步、
    /// 走到尽头那个名的文件其实不在仓里。最后这条是"宁可保守"：半截发布的状态不该由改名标记来兜。
    /// </remarks>
    public string? Follow(string stem, Func<string, bool> remoteHasBoard)
    {
        var current = stem;
        var seen = new HashSet<string>(StringComparer.Ordinal) { stem };

        for (var hop = 0; hop < MaxChainHops; hop++)
        {
            var next = NewestFor(current);

            if (next is null)
            {
                return current == stem ? null : (remoteHasBoard(current) ? current : null);
            }

            if (!seen.Add(next.To))
            {
                return null;
            }

            current = next.To;
        }

        return null;
    }

    /// <summary>
    /// 同一个 <c>from</c> 有多条（两端同时把它改成了不同名字）：取 <c>at</c> 最大，平手取 <c>by</c>
    /// 的 Ordinal 大者；<c>at</c> 与 <c>by</c> 都平手（同一台设备反悔重发）取**列在后面的那条**——
    /// 同一份字节、同一个顺序读，两端才会同答。用 <c>&gt;=</c> 而不是 <c>&gt;</c> 就是为了这一格。
    /// </summary>
    private RenameEntry? NewestFor(string from)
    {
        RenameEntry? best = null;

        foreach (var entry in Entries)
        {
            if (!string.Equals(entry.From, from, StringComparison.Ordinal))
            {
                continue;
            }

            if (best is null || Compare(entry, best) >= 0)
            {
                best = entry;
            }
        }

        return best;
    }

    private static int Compare(RenameEntry left, RenameEntry right)
    {
        var byTime = string.CompareOrdinal(left.At, right.At);

        return byTime != 0 ? byTime : string.CompareOrdinal(left.By, right.By);
    }
}
