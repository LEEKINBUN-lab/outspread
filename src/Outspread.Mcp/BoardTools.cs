using System.ComponentModel;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using Outspread.Core;

namespace Outspread.Mcp;

/// <summary>
/// 板子给 agent 用的那五枚工具。写盘全走 <see cref="CanvasStore.SavePatch"/>——
/// 和界面同一把命名互斥、同一套原子替换，所以 agent 和你可以在同一秒各写各的。
/// </summary>
[McpServerToolType]
public sealed class BoardTools
{
    private const int CardWidth = 360;

    private const int CardHeight = 90;

    private const int ColumnGap = 24;

    /// <summary>带与带之间的空隙：比同行的大一档，"要你拍的"和"结论"之间要有明显的缝。</summary>
    private const int BandGap = 64;

    [McpServerTool(Name = "board_post"), Description(
        "把**要他动作或要他知道的**东西落成卡片：一条一格，一格只装一件事。"
        + "四种 kind：decision＝等他拍板（红）、risk＝缺陷／没做完／值得特别告知（橙）、task＝待办、note＝结论。"
        + "判据只有一句：这条不需要他做什么、也不需要他知道，就别写进来——叙述留在会话里，抄上板等于占他眼睛。"
        + "工具会自动接上这块板已有的 @n 序号、给 decision/risk/task 挂 @b0（他点一下牌子＝已阅／已决），并统一打一个 @t轮次标签。")]
    public static string BoardPost(
        [Description("这一组的小标题，会成为横贯一行那枚卡；只写「这组是关于什么的」")] string title,
        [Description("要落的卡片，每条一个对象：{kind, text}。text 里只装这一件事。")] BoardCard[] cards,
        [Description("轮次短标签，几个字即可（会打成 @t标签，他用筛选面板按它只看这一轮）。不填＝agent。")] string? round = null,
        [Description("画板文件路径。不填就落到你的「Agent 出口」板（没有会自动建）。")] string? board = null,
        [Description("一行摆几张卡，1 到 4，默认 3。")] int columns = 3)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "标题不能空：这一发没写。";
        }

        if (cards.Length == 0)
        {
            return "一条都没有，这一发没写。";
        }

        var path = ResolveBoard(board);
        var tag = "@t" + NormaliseTag(round is { Length: 0 } or null ? "agent" : round!);
        var document = CanvasStore.LoadOrCreate(path);

        var (left, top) = FreeColumnBelow(document);
        var across = Math.Clamp(columns, 1, 4);
        var rowWidth = (across * CardWidth) + ((across - 1) * ColumnGap);

        var patch = new DocPatch();
        var written = new List<string>();
        var seq = NextSequence(document);

        patch.Additions.Add(Card(NodeIds.New(NodeIds.AgentNamespace), left, top, $"### {title}\n\n{tag}\n{DateTime.Now:yyyy-MM-dd HH:mm}", rowWidth, 88));

        var rowTop = top + 88 + ColumnGap;

        // 分带：要他拍的在最上面，然后风险、待办，结论永远垫底——他扫一眼看到的是"要我决定的"，
        // 不是 agent 干了什么。
        foreach (var band in cards.GroupBy(card => KindOf(card.Kind)).OrderBy(group => BandOrder(group.Key)))
        {
            var rowTallest = 0.0;
            var slot = 0;

            foreach (var entry in band)
            {
                var body = entry.Text.Trim();

                if (body.Length == 0)
                {
                    continue;
                }

                if (slot % across == 0 && slot > 0)
                {
                    rowTop += rowTallest + ColumnGap;
                    rowTallest = 0;
                }

                // 标记行放最前面：他板子上那套约定是"@n8 @b0 换行 正文"，牌子才会落到卡片左下角。
                var marks = Tags.LooksLikePropertyLine(body.Split('\n')[0])
                    ? string.Empty
                    : band.Key == "note" ? tag : $"{tag} @n{++seq} @b0";
                var text = marks.Length == 0 ? body : $"{marks}\n{body}";

                // 高度按**折行后**的行数估（一行按 15px 一个汉字算），再多留两行：
                // 只数硬换行会把一句长句当一行，卡片短一截，多出来的那行被渐隐吃掉——看着像话没说完。
                var wrapped = body.Split('\n')
                    .Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (CardWidth / 15.0))));
                var height = Math.Max(CardHeight, 44 + (22 * (wrapped + 2)));
                var x = left + ((slot % across) * (CardWidth + ColumnGap));

                patch.Additions.Add(Card(
                    NodeIds.New(NodeIds.AgentNamespace),
                    x,
                    rowTop,
                    text,
                    CardWidth,
                    height,
                    ColourOf(band.Key)));

                written.Add($"@n{seq} {band.Key} {x:0},{rowTop:0} {height:0}");
                rowTallest = Math.Max(rowTallest, height);
                slot++;
            }

            rowTop += rowTallest + BandGap;
        }

        CanvasStore.SavePatch(path, patch);

        return $"已写到 {path}\n标签：{tag}\n落了 {written.Count} 条 + 1 枚标题卡（一行 {across} 张，按类型分带）：\n{string.Join("\n", written)}";
    }

    [McpServerTool(Name = "board_archive"), Description(
        "把**不用他再看的**格子收进一个命名分组并叠成一摞（堆叠前先把这堆统一成最小框）。"
        + "红线：还在等他的特殊卡（红／橙且没勾掉的）绝不收进去——除非他已经回答、勾了 @b。"
        + "归档组的名字要写清是哪一轮、什么内容，他以后按名字找。")]
    public static string BoardArchive(
        [Description("分组的名字，会成为组名牌，例如「已归档·MCP 那轮叙述」")] string name,
        [Description("要收进去的格子编号。不填就把板上所有没在组里、且不需要他动作的文字格收进去。")] string[]? ids = null,
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path))
        {
            return $"{path}：没这块板。";
        }

        var document = CanvasStore.Load(path);
        var wanted = ids is { Length: > 0 }
            ? ids.Select(document.Find).Where(node => node is not null).Select(node => node!).ToList()
            : document.Nodes
                .Where(node => node.Kind == CanvasNodeKind.Text)
                .Where(node => !InsideAnyGroup(document, node))
                .Where(node => !AwaitsHim(node))
                .ToList();

        if (wanted.Count < 2)
        {
            return $"只有 {wanted.Count} 格可收：堆叠至少要两格，这一发没动盘。";
        }

        var blocked = document.Nodes
            .Where(node => node.Kind == CanvasNodeKind.Text && !InsideAnyGroup(document, node) && AwaitsHim(node))
            .Select(node => node.Id)
            .ToList();

        // 堆之前先统一框：一摞里高低不齐就叠不拢（界面那发「按最小的统一」量字体，这里量不到，
        // 所以取"最窄宽 × 最高框"——不裁字，但比按最大小一圈）。
        var width = wanted.Min(node => node.Width);
        var height = wanted.Max(node => node.Height);
        var boxes = wanted.Select(node => new Aligning.Placed(node.Id, node.X, node.Y, width, height)).ToList();
        var stacked = Aligning.Stack(boxes);

        if (!stacked.Ok)
        {
            return $"堆叠没算成（需 {stacked.Needed}，有 {stacked.Have}），这一发没动盘。";
        }

        var patch = new DocPatch();

        foreach (var move in stacked.Moves)
        {
            patch.SetNumber(move.Id, "width", width);
            patch.SetNumber(move.Id, "height", height);
            patch.SetNumber(move.Id, "x", move.X);
            patch.SetNumber(move.Id, "y", move.Y);
        }

        var moved = stacked.Moves
            .Select(move => new Aligning.Placed(move.Id, move.X, move.Y, width, height))
            .ToList();

        var groupId = NodeIds.New(NodeIds.AgentNamespace);

        patch.Additions.Add(new AddedNode
        {
            Id = groupId,
            Json = new JsonObject
            {
                ["id"] = groupId,
                ["type"] = "group",
                ["label"] = name,
                ["x"] = (long)Math.Floor(moved.Min(item => item.X) - 12),
                ["y"] = (long)Math.Floor(moved.Min(item => item.Y) - 12),
                ["width"] = (long)Math.Ceiling(moved.Max(item => item.X + item.Width) - moved.Min(item => item.X) + 24),
                ["height"] = (long)Math.Ceiling(moved.Max(item => item.Y + item.Height) - moved.Min(item => item.Y) + 24),
            },
        });

        CanvasStore.SavePatch(path, patch);

        return $"已把 {wanted.Count} 格统一成 {width:0}×{height:0} 并叠进分组「{name}」（组 {groupId}）"
            + (blocked.Count == 0 ? "。" : $"\n没收进去、还在等他的有 {blocked.Count} 格：{string.Join("、", blocked)}");
    }

    [McpServerTool(Name = "board_read"), Description(
        "读一块板：每格一行，给编号、首行、标签、位置。想知道板子上现在有什么就先叫它。"
        + "收尾的那一发就靠它当凭据：返回第二行按 @t轮次给出「还欠几格」，把那个数写进汇报。")]
    public static string BoardRead(
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null,
        [Description("最多列几格，默认 40。")] int limit = 40)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path))
        {
            return $"{path} 还没有这块板。";
        }

        var document = CanvasStore.Load(path);
        var lines = document.Nodes
            .Where(node => node.Kind != CanvasNodeKind.Group)
            .OrderBy(node => node.Y)
            .ThenBy(node => node.X)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(node => $"{node.Id} | {FirstLine(node.Text)} | {string.Join(",", Tags.Of(node.Text))} | {node.X:0},{node.Y:0} {node.Width:0}×{node.Height:0}");

        // 收尾那一发的凭据：按轮次标签数还有几格没结。他勾掉一格，这里的数就掉一格。
        // 标签读出来是规范形 #xxx（@t 只是写在板上的样子），所以这里认 # 开头，不是认 @t。
        var pending = document.Nodes
            .Where(node => node.Kind == CanvasNodeKind.Text && AwaitsHim(node))
            .SelectMany(node => Tags.Of(node.Text)
                .Where(tag => tag.StartsWith("#", StringComparison.Ordinal))
                .Select(tag => (tag, node)))
            .GroupBy(pair => pair.tag)
            .OrderByDescending(group => group.Count())
            .Take(6)
            .Select(group => $"{group.Key} 还欠 {group.Count()} 格")
            .ToList();

        return $"{path}\n未结：{(pending.Count == 0 ? "没有——他要看的都结清了" : string.Join(" / ", pending))}\n"
            + string.Join("\n", lines);
    }

    [McpServerTool(Name = "board_search"), Description("在一块板里找字（也认 @标签），返回命中的格子。回答\"上次那条写哪去了\"这类问题用它。")]
    public static string BoardSearch(
        [Description("要找的词，不分大小写；以 @ 开头就按标签找")] string query,
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path) || query.Trim().Length == 0)
        {
            return $"{path}：没这块板或没给词。";
        }

        var needle = query.Trim();
        var byTag = needle.StartsWith('@');

        var hits = CanvasStore.Load(path).Nodes
            .Where(node => byTag
                ? Tags.Of(node.Text).Any(tag => needle[1..].Equals(tag, StringComparison.OrdinalIgnoreCase))
                : (node.Text ?? string.Empty).Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Take(40)
            .Select(node => $"{node.Id} | {FirstLine(node.Text)} | {node.X:0},{node.Y:0}")
            .ToList();

        return hits.Count == 0 ? $"没找到「{needle}」。" : string.Join("\n", hits);
    }

    [McpServerTool(Name = "board_patch"), Description(
        "改一格：换正文、加一个 @标签、或把它标成已完成（@b1）。你用来收口，agent 用来更新同一件事而不是再贴一张新的。")]
    public static string BoardPatch(
        [Description("格子编号")] string id,
        [Description("新正文；不填就不动")] string? text = null,
        [Description("要加的 @标签（不带 @）")] string? add_tag = null,
        [Description("true＝标已完成，false＝标未完成，不填＝不动")] bool? done = null,
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path))
        {
            return $"{path}：没这块板。";
        }

        var document = CanvasStore.Load(path);
        var node = document.Find(id);

        if (node is null)
        {
            return $"{path} 里没有 {id} 这一格。";
        }

        var body = text ?? node.Text ?? string.Empty;

        if (add_tag is { Length: > 0 })
        {
            body = Tags.AddTag(body, add_tag!.TrimStart('@'));
        }

        if (done is { } finished)
        {
            body = Tags.SetDone(body, finished);
        }

        var patch = new DocPatch();

        patch.SetString(node.Id, "text", body);

        CanvasStore.SavePatch(path, patch);

        return $"{node.Id} 已更新：{FirstLine(body)}";
    }

    [McpServerTool(Name = "board_shot"), Description(
        "向**开着的那块板**要一张 PNG（它画的就是你屏幕上那张）。落完一批东西后用来自查。没人开着这块板会直接说没人，不会自己另画一张。")]
    public static async Task<string> BoardShot(
        [Description("PNG 落盘路径")] string @out,
        [Description("view=当前视野 / fit=整块板 / node=单格（要配 id）")] string mode = "view",
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null,
        [Description("mode=node 时要拍的那格编号")] string? id = null)
    {
        var path = ResolveBoard(board);

        var request = new ShotRequest(ShotProtocol.RequestedOp, @out, mode, id);

        var pipe = new NamedPipeClientStream(".", ShotProtocol.PipeName(path), PipeDirection.InOut, PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(2000);
        }
        catch (Exception ex)
        {
            return $"这块板没开着（{ex.GetType().Name}），没人能答这一发。";
        }

        try
        {
            await using (pipe)
            {
                await JsonSerializer.SerializeAsync(pipe, request);
                await pipe.FlushAsync();

                var reply = await JsonSerializer.DeserializeAsync<ShotReply>(pipe);

                return reply is null
                    ? "界面答了个空的。"
                    : reply.Ok
                        ? $"{reply.Path} · {reply.Width}×{reply.Height} · 缩放 {reply.Zoom:P0} · {reply.Nodes} 个节点"
                        : $"没拍成：{reply.Error}";
            }
        }
        catch (Exception ex)
        {
            return $"没拍成：{ex.Message}";
        }
    }

    private static AddedNode Card(string id, double x, double y, string text, double width, double height, string? colour = null)
    {
        var json = new JsonObject
        {
            ["id"] = id,
            ["type"] = "text",
            ["x"] = (long)Math.Round(x),
            ["y"] = (long)Math.Round(y),
            ["width"] = (long)Math.Round(width),
            ["height"] = (long)Math.Round(height),
            ["text"] = text,
        };

        if (colour is { Length: > 0 })
        {
            json["color"] = colour;
        }

        return new AddedNode { Id = id, Json = json };
    }

    /// <summary>kind 只认那四种；写错的一律按 note 落（灰、不占序号），不猜他想表达什么。</summary>
    private static string KindOf(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "decision" or "decide" or "拍板" => "decision",
        "risk" or "defect" or "风险" => "risk",
        "task" or "todo" or "待办" => "task",
        _ => "note",
    };

    /// <summary>带的顺序：要他拍的在最上面。</summary>
    private static int BandOrder(string kind) => kind switch
    {
        "decision" => 0,
        "risk" => 1,
        "task" => 2,
        _ => 3,
    };

    /// <summary>JSON Canvas 的预设色：1＝红、2＝橙；待办与结论不涂色（涂满色就等于没有色）。</summary>
    private static string? ColourOf(string kind) => kind switch
    {
        "decision" => "1",
        "risk" => "2",
        _ => null,
    };

    /// <summary>还在等他的格子：特殊色（红／橙）且没勾掉，或明写了 @b0。归档绝不碰这些。</summary>
    private static bool AwaitsHim(CanvasNode node)
    {
        var read = Tags.Read(node.Text);

        if (read.Done == false)
        {
            return true;
        }

        return read.Done is null && node.Color is "1" or "2";
    }

    private static bool InsideAnyGroup(CanvasDocument document, CanvasNode node)
    {
        var groups = document.Nodes
            .Where(group => group.Kind == CanvasNodeKind.Group)
            .Select(group => new Grouping.Rect(group.Id, group.X, group.Y, group.Width, group.Height))
            .ToList();

        return Grouping.GroupOf(new Grouping.Rect(node.Id, node.X, node.Y, node.Width, node.Height), groups) is not null;
    }

    /// <summary>@n 接在板上已有的最大序号后面：两批之间也能互相引用（他写"参考@n1"用的就是这个）。</summary>
    private static int NextSequence(CanvasDocument document)
        => document.Nodes
            .Select(node => Tags.Read(node.Text).Sequence)
            .Where(sequence => sequence is not null)
            .DefaultIfEmpty(0)
            .Max() ?? 0;

    /// <summary>
    /// 「Agent 出口」落在哪儿：贴着**他真实开着的板**放。闸门会把自检跑在 %TEMP% 的副本上并留下
    /// last-board，那块出口板不能落在临时目录——所以先翻 open-boards 里第一条不在 temp 的，
    /// 再看 last-board，都没有就退回应用的默认板目录。
    /// </summary>
    private static string ResolveBoard(string? board)
    {
        if (board is { Length: > 0 })
        {
            return Path.GetFullPath(board);
        }

        var prefs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Outspread");

        var anchors = new List<string>();

        foreach (var file in new[] { "open-boards.txt", "last-board.txt" })
        {
            var path = Path.Combine(prefs, file);

            if (File.Exists(path))
            {
                anchors.AddRange(File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0));
            }
        }

        var temp = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

        var directory = anchors
            .Select(candidate => Path.GetFullPath(candidate))
            .FirstOrDefault(candidate =>
                File.Exists(candidate)
                && !Path.TrimEndingDirectorySeparator(candidate)
                    .StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) is { } real
            ? Path.GetDirectoryName(real)!
            : Path.GetDirectoryName(BoardLocator.FallbackPath())!;

        return Path.Combine(directory, "Agent 出口.canvas");
    }

    /// <summary>新内容一律落在"最低那格下面"：他往上看就是历史，往下走就是最新。</summary>
    private static (double X, double Y) FreeColumnBelow(CanvasDocument document)
    {
        var bottom = document.Nodes
            .Where(node => node.Kind != CanvasNodeKind.Group)
            .Select(node => node.Y + node.Height)
            .DefaultIfEmpty(0);

        var lowest = bottom.Max();
        var left = document.Nodes.Count == 0 ? 0 : document.Nodes.Min(node => node.X);

        return (left, lowest + ColumnGap);
    }

    private static string NormaliseTag(string raw)
    {
        var builder = new StringBuilder();

        foreach (var character in raw.Trim())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            if (builder.Length >= 24)
            {
                break;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string FirstLine(string? text)
    {
        var line = (text ?? string.Empty)
            .Split('\n')
            .FirstOrDefault(candidate => candidate.Trim().Length > 0 && !Tags.LooksLikePropertyLine(candidate))?
            .Trim() ?? string.Empty;

        return line.Length <= 46 ? line : line[..46] + "…";
    }
}

/// <summary>一枚要落板的卡。一格只装一件事——两件事就传两条，他会想单独勾掉其中一条。</summary>
public sealed class BoardCard
{
    [Description("decision＝等他拍板（红）/ risk＝缺陷、没做完、值得特别告知（橙）/ task＝待办 / note＝结论（默认别上板）")]
    public string Kind { get; set; } = "note";

    [Description("卡片正文：只写这一件事，需要他知道的判据、选项、复现通道都写在这一条里")]
    public string Text { get; set; } = string.Empty;
}
