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
        var titleId = NodeIds.New(NodeIds.AgentNamespace);
        var groupId = NodeIds.New(NodeIds.AgentNamespace);
        var members = new List<string> { titleId };

        // 先按"要他动作的在前"排队（决定→风险→待办→结论），再像写字一样从左往右、满一行换行。
        // 10-27 之前是按类型各占一整行：四件事就排成一条竖长蛇，他明确否掉了。
        var ordered = cards.Where(card => !string.IsNullOrWhiteSpace(card.Text)).ToList();
        var prepared = new List<(string Id, string Kind, string Text, string Label, double Height)>();

        foreach (var entry in ordered)
        {
            var kind = KindOf(entry.Kind);
            var body = entry.Text.Trim();

            // 标记行放最前面：他板子上那套约定是"@n8 @b0 换行 正文"，牌子才会落到卡片左下角。
            // 每一格都要有自己的号——结论也要，因为他想引用"你写的那条结论"时只能靠 @n。
            // @b 只给要他动作的三档挂：结论挂上牌子，"还欠几格"就会把无关的结论算进他的负担。
            var stamped = !Tags.LooksLikePropertyLine(body.Split('\n')[0]);
            var marks = string.Empty;
            int? ordinal = null;

            if (stamped)
            {
                ordinal = ++seq;
                marks = kind == "note" ? $"{tag} @n{ordinal}" : $"{tag} @n{ordinal} @b0";
            }

            // 高度按**折行后**的行数估（一行按 15px 一个汉字算），再多留两行：
            // 只数硬换行会把一句长句当一行，卡片短一截，多出来的那行被渐隐吃掉——看着像话没说完。
            var wrapped = body.Split('\n')
                .Sum(line => Math.Max(1, (int)Math.Ceiling(line.Length / (CardWidth / 15.0))));

            prepared.Add((
                NodeIds.New(NodeIds.AgentNamespace),
                kind,
                marks.Length == 0 ? body : $"{marks}\n{body}",
                ordinal is { } number ? $"@n{number}" : "@n—（沿用你写的标记行）",
                Math.Max(CardHeight, 44 + (22 * (wrapped + 2)))));
        }

        var additions = new List<AddedNode>
        {
            Card(titleId, left, top, $"### {title}\n\n{tag}\n{DateTime.Now:yyyy-MM-dd HH:mm}", rowWidth, 88),
        };

        var rowTop = top + 88 + ColumnGap;

        for (var index = 0; index < prepared.Count; index += across)
        {
            var row = prepared.Skip(index).Take(across).ToList();
            var rowTallest = row.Max(item => item.Height);

            for (var slot = 0; slot < row.Count; slot++)
            {
                var item = row[slot];
                var x = left + (slot * (CardWidth + ColumnGap));

                additions.Add(Card(item.Id, x, rowTop, item.Text, CardWidth, item.Height, ColourOf(item.Kind)));
                members.Add(item.Id);
                written.Add($"{item.Label} {item.Kind} {x:0},{rowTop:0} {item.Height:0}");
            }

            rowTop += rowTallest + ColumnGap;
        }

        // 一批一格组：同一批写下的东西在板上自成一块带着名字的方框（他 10-27 要的）。
        // 组先放进去、卡片压在它上面，免得框线盖住正文。等他的卡可以在这个组里，但绝不被叠起来。
        patch.Additions.Add(new AddedNode
        {
            Id = groupId,
            Json = new JsonObject
            {
                ["id"] = groupId,
                ["type"] = "group",
                // 组名带日期和时间：只写标题，以后要按轮次排序就是灾难（他 10-27）。
                ["label"] = $"{DateTime.Now:MM-dd HH:mm} · {title.Trim()}",
                ["x"] = (long)Math.Floor(left - 12),
                ["y"] = (long)Math.Floor(top - 12),
                ["width"] = (long)Math.Ceiling((double)(rowWidth + 24)),
                ["height"] = (long)Math.Ceiling((double)(rowTop - ColumnGap - top + 24)),
                ["members"] = new JsonArray(members.Select(member => (JsonNode)JsonValue.Create(member)!).ToArray()),
            },
        });

        patch.Additions.AddRange(additions);

        CanvasStore.SavePatch(path, patch);

        return $"已写到 {path}\n标签：{tag}\n分组：「{title.Trim()}」（{members.Count} 格）\n落了 {written.Count} 条 + 1 枚标题卡（一行 {across} 张，满行换行）：\n{string.Join("\n", written)}";
    }

    [McpServerTool(Name = "board_archive"), Description(
        "把**不用他再看的**格子收进一个命名分组并叠成一摞（堆叠前先把这堆统一成最小框）。"
        + "红线：还在等他的特殊卡（红／橙且没勾掉的）绝不收进去——除非他已经回答、勾了 @b。"
        + "归档组的名字要写清是哪一轮、什么内容，他以后按名字找。")]
    public static string BoardArchive(
        [Description("分组的名字，会成为组名牌，例如「已归档·MCP 那轮叙述」")] string name,
        [Description("要收进去的格子编号。不填就把板上所有没在组里、且不需要他动作的文字格收进去。")] string[]? ids = null,
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null,
        [Description("已有组的编号：给了就复用这枚组（改名、重新框住、换成员），不再新建一枚。")] string? into_group = null)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path))
        {
            return $"{path}：没这块板。";
        }

        var document = CanvasStore.Load(path);

        // 编号对不上必须说出来：10-27 我喂了一串截断的编号，工具一声不吭只收了 3/5 格，
        // 于是"这一轮已收全"是句假话。宁可整发不动，也不要在少收几格的时候报成功。
        var missing = ids is { Length: > 0 }
            ? ids.Where(id => document.Find(id) is null).ToList()
            : [];

        if (missing.Count > 0)
        {
            return $"{path} 里找不到这些编号：{string.Join("、", missing)}——这一发没动盘。";
        }

        var wanted = ids is { Length: > 0 }
            ? ids.Select(document.Find).Where(node => node is not null).Select(node => node!).ToList()
            : document.Nodes
                .Where(node => node.Kind == CanvasNodeKind.Text)
                .Where(node => !InsideAnyGroup(document, node))
                .Where(node => !AwaitsHim(node))
                .ToList();

        if (wanted.Count == 0)
        {
            return $"{path}：没有可收的格子，这一发没动盘。";
        }

        var blocked = document.Nodes
            .Where(node => node.Kind == CanvasNodeKind.Text && !InsideAnyGroup(document, node) && AwaitsHim(node))
            .Select(node => node.Id)
            .ToList();

        var settled = wanted.Where(node => !AwaitsHim(node)).ToList();

        // 叠之前先统一框：一摞里高低不齐就叠不拢。与界面上「按最小的统一」同一条规矩——
        // 宽、高各取**这一摞里**最小，允许裁字（他 10-26 22:5x 定的：堆叠换的就是占得少）。
        // 这一侧不需要字体引擎：min 宽 × min 高，两边算出来的框一模一样。
        var width = settled.Count > 0 ? settled.Min(node => node.Width) : 0;
        var height = settled.Count > 0 ? settled.Min(node => node.Height) : 0;

        // 一轮 = 一个组；组里已勾的叠成一摞，没勾的平铺在下面（他 10-27 定的摆法）。
        // 平铺的用自己原来的尺寸：它们还在等他看，被收成最小反而不好读。
        var stillOpen = wanted.Where(node => AwaitsHim(node)).ToList();
        var patch = new DocPatch();
        var placed = new List<Aligning.Placed>();

        // 尺寸先统一、再叠：标题卡常常比卡片宽得多（1128 vs 360），叠放那套要求 80% 重叠，
        // 宽的那张一脚踩空、留在原地还把组框撑破（10-27 实测：规矩那组的标题就露在摞外面）。
        foreach (var node in settled)
        {
            patch.SetNumber(node.Id, "width", width);
            patch.SetNumber(node.Id, "height", height);
        }

        if (settled.Count >= 2)
        {
            var stack = Aligning.Stack(settled
                .Select(node => new Aligning.Placed(node.Id, node.X, node.Y, width, height))
                .ToList());

            if (!stack.Ok)
            {
                return $"堆叠没算成（需 {stack.Needed}，有 {stack.Have}），这一发没动盘。";
            }

            foreach (var move in stack.Moves)
            {
                patch.SetNumber(move.Id, "width", width);
                patch.SetNumber(move.Id, "height", height);
                patch.SetNumber(move.Id, "x", move.X);
                patch.SetNumber(move.Id, "y", move.Y);
                placed.Add(new Aligning.Placed(move.Id, move.X, move.Y, width, height));
            }
        }
        else if (settled.Count == 1)
        {
            // 只有一格"已结"：不叠（叠不出来），就把它摆在组的左上角，免得它孤零零杵在别处。
            var only = settled[0];
            var x = Math.Min(only.X, stillOpen.Count > 0 ? stillOpen.Min(node => node.X) : only.X);
            var y = Math.Min(only.Y, stillOpen.Count > 0 ? stillOpen.Min(node => node.Y) : only.Y);

            patch.SetNumber(only.Id, "x", x);
            patch.SetNumber(only.Id, "y", y);
            placed.Add(new Aligning.Placed(only.Id, x, y, only.Width, only.Height));
        }

        var stackLeft = placed.Count > 0 ? placed.Min(item => item.X) : 0;
        var stackBottom = placed.Count > 0 ? placed.Max(item => item.Y + item.Height) : 0;
        var openLeft = stackLeft;
        var openTop = placed.Count > 0 ? stackBottom + ColumnGap : 0;

        foreach (var node in stillOpen)
        {
            patch.SetNumber(node.Id, "x", openLeft);
            patch.SetNumber(node.Id, "y", openTop);
            placed.Add(new Aligning.Placed(node.Id, openLeft, openTop, node.Width, node.Height));
            openLeft += node.Width + ColumnGap;
        }

        if (placed.Count == 0)
        {
            return $"{path}：这批没有可摆的格子，这一发没动盘。";
        }

        var groupId = into_group is { Length: > 0 } ? into_group : NodeIds.New(NodeIds.AgentNamespace);
        var frame = new JsonObject
        {
            ["label"] = name,
            ["x"] = (long)Math.Floor(placed.Min(item => item.X) - 12),
            ["y"] = (long)Math.Floor(placed.Min(item => item.Y) - 12),
            ["width"] = (long)Math.Ceiling(placed.Max(item => item.X + item.Width) - placed.Min(item => item.X) + 24),
            ["height"] = (long)Math.Ceiling(placed.Max(item => item.Y + item.Height) - placed.Min(item => item.Y) + 24),
            ["members"] = new JsonArray(placed.Select(item => (JsonNode)JsonValue.Create(item.Id)!).ToArray()),
        };

        if (into_group is { Length: > 0 })
        {
            // 复用他那枚组：他把组名写成了给我的话（"改成合适的名字"），这里就地改名换成员，
            // 不再甩一枚新组、把他画的那个框留在原地。
            if (document.Find(into_group) is null)
            {
                return $"{path} 里没有 {into_group} 这枚组，这一发没动盘。";
            }

            foreach (var (field, value) in frame)
            {
                patch.Set(groupId, field, value);
            }
        }
        else
        {
            patch.Additions.Add(new AddedNode
            {
                Id = groupId,
                Json = new JsonObject
                {
                    ["id"] = groupId,
                    ["type"] = "group",
                    ["label"] = name,
                    ["x"] = frame["x"]!.DeepClone(),
                    ["y"] = frame["y"]!.DeepClone(),
                    ["width"] = frame["width"]!.DeepClone(),
                    ["height"] = frame["height"]!.DeepClone(),
                    ["members"] = frame["members"]!.DeepClone(),
                },
            });
        }

        CanvasStore.SavePatch(path, patch);

        // 高度得钉住：板子那套「随文字」会把没有手动标记的格子重新按正文量一遍，
        // 一量，同一摞就又高低不齐（10-27 实测：统一成 119 的那摞，有一格被板子量回 140，
        // 组框也跟着短了 9px）。所以归档同时把这些格子记进侧车的手动尺寸表。
        var sidecar = path + ".view.json";
        var (view, viewProblem) = BoardViewState.TryLoad(sidecar);

        if (viewProblem is null)
        {
            // 只钉"叠成一摞"的那些：平铺的未结卡还归「随文字」管，改了字仍跟着长。
            foreach (var node in settled)
            {
                view.MarkManualSize(node.Id);
            }

            view.Save(sidecar);
        }

        return $"一轮一格组：「{name}」（{placed.Count} 格）"
            + (settled.Count > 0 ? $"\n已结的 {settled.Count} 格收成组内最窄最矮的 {width:0}×{height:0}，叠成一摞" : "\n这一轮还没有已结的格子，没有可叠的")
            + (stillOpen.Count > 0 ? $"\n还没结的 {stillOpen.Count} 格留在同一个组里平铺：{string.Join("、", stillOpen.Select(node => node.Id.Length > 8 ? node.Id[..8] : node.Id))}" : "");
    }

    [McpServerTool(Name = "board_tidy"), Description(
        "把整块板收紧：每个组（和每张散卡）当成一块，按原来的阅读顺序从上往下摞起来，块与块之间只留一条缝。"
        + "不改组内摆法、不改正文、不删任何东西，只挪位置。他 10-27 说过两次：组与组之间空出半屏，可读性太差。")]
    public static string BoardTidy(
        [Description("块与块之间的缝，像素，默认 48")] int gap = 48,
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path))
        {
            return $"{path} 还没有这块板。";
        }

        var document = CanvasStore.Load(path);
        var byId = document.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var blocks = new List<(List<string> Ids, double X, double Y, double Width, double Height)>();

        foreach (var group in document.Nodes.Where(node => node.Kind == CanvasNodeKind.Group))
        {
            var ids = new List<string> { group.Id };
            ids.AddRange((group.GroupMemberIds ?? []).Where(byId.ContainsKey));
            var members = ids.Select(id => byId[id]).ToList();

            blocks.Add((
                ids,
                members.Min(node => node.X),
                members.Min(node => node.Y),
                members.Max(node => node.X + node.Width) - members.Min(node => node.X),
                members.Max(node => node.Y + node.Height) - members.Min(node => node.Y)));
        }

        var inGroup = document.Nodes
            .Where(node => node.Kind == CanvasNodeKind.Group)
            .SelectMany(node => node.GroupMemberIds ?? [])
            .ToHashSet(StringComparer.Ordinal);

        foreach (var node in document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group && !inGroup.Contains(node.Id)))
        {
            blocks.Add(([node.Id], node.X, node.Y, node.Width, node.Height));
        }

        if (blocks.Count == 0)
        {
            return $"{path}：板上没有可收的东西。";
        }

        // 他 10-27：横着排、满一行换行，不要一路竖下去；顺序按每块里那枚标题卡的时间戳，
        // 没时间戳的排在后面（老内容）。同一时刻再按位置定先后。
        var ordered = blocks
            .Select(block => (Block: block, Time: BlockTime(byId, block.Ids)))
            .OrderBy(item => item.Time ?? DateTime.MaxValue)
            .ThenBy(item => item.Block.Y)
            .ThenBy(item => item.Block.X)
            .Select(item => item.Block)
            .ToList();
        var patch = new DocPatch();
        // 左边对齐到 0：负数坐标虽然能画，但他的板从 0 开始看最顺。
        const double left = 0;
        var top = ordered.Min(block => block.Y);

        var rowWidth = 0.0;
        var rowTop = top;
        var rowTallest = 0.0;

        foreach (var block in ordered)
        {
            // 横排：这一行放不下就换行（他 10-27 的 1-2-3 / 4-5-6 / 7…）。
            if (rowWidth > 0 && rowWidth + block.Width > TileRowWidth)
            {
                rowTop += rowTallest + Math.Max(8, gap);
                rowWidth = 0;
                rowTallest = 0;
            }

            var dx = left + rowWidth - block.X;
            var dy = rowTop - block.Y;

            foreach (var id in block.Ids)
            {
                patch.SetNumber(id, "x", byId[id].X + dx);
                patch.SetNumber(id, "y", byId[id].Y + dy);
            }

            rowWidth += block.Width + Math.Max(8, gap);
            rowTallest = Math.Max(rowTallest, block.Height);
        }

        top = rowTop + rowTallest;

        CanvasStore.SavePatch(path, patch);

        return $"已收紧 {ordered.Count} 块（组和散卡各算一块），块间缝 {Math.Max(8, gap)}px，整块板高 {top - Math.Max(8, gap):0}px。组内摆法一个字没动。";
    }

    [McpServerTool(Name = "board_inbox"), Description(
        "他要你做的事都在板上：这枚工具对比「上次 agent 读到的那一版」，列出**新加的**和**他改过的**格子，"
        + "外加所有 @a 工单。判据是正文哈希，不是「他没说话所以没改」。"
        + "每轮开头叫一次；处理完再叫一次 ack=true 把账抹平——账不抹平，你会一直看到同一批。")]
    public static string BoardInbox(
        [Description("true＝把当前这一版记成「已读」；不填只读不动账")] bool ack = false,
        [Description("画板文件路径。不填就用你的「Agent 出口」板。")] string? board = null)
    {
        var path = ResolveBoard(board);

        if (!File.Exists(path))
        {
            return $"{path} 还没有这块板。";
        }

        var markerPath = path + ".inbox.json";
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        try
        {
            if (File.Exists(markerPath)
                && JsonNode.Parse(File.ReadAllText(markerPath)) is JsonObject recorded)
            {
                foreach (var (id, value) in recorded)
                {
                    seen[id] = value?.GetValue<string>() ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            seen.Clear();
        }

        var now = CanvasStore.Load(path).Nodes
            .Where(node => node.Kind != CanvasNodeKind.Group)
            .ToDictionary(node => node.Id, node => Convert.ToHexString(CanvasStore.Sha256Of(System.Text.Encoding.UTF8.GetBytes(node.Text ?? string.Empty))), StringComparer.Ordinal);

        var lines = new List<string>();

        foreach (var node in CanvasStore.Load(path).Nodes.Where(node => node.Kind != CanvasNodeKind.Group))
        {
            var hash = now[node.Id];
            var isTicket = Tags.Of(node.Text).Any(tag => tag.Equals("#a", StringComparison.OrdinalIgnoreCase));
            var added = !seen.ContainsKey(node.Id);
            var edited = !added && seen[node.Id] != hash;

            if (!added && !edited && !isTicket)
            {
                continue;
            }

            var first = (node.Text ?? string.Empty).Split('\n')
                .FirstOrDefault(line => !Tags.LooksLikePropertyLine(line) && line.Trim().Length > 0) ?? string.Empty;
            var why = added ? "新" : edited ? "改" : "工单";

            lines.Add($"{why} {node.Id} | {first[..Math.Min(50, first.Length)]}");
        }

        if (ack)
        {
            var json = new JsonObject();

            foreach (var (id, hash) in now.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                json[id] = hash;
            }

            try
            {
                File.WriteAllText(markerPath, json.ToJsonString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return $"账没抹上（{ex.Message}）：下一轮还会看到同一批。";
            }
        }

        return lines.Count == 0
            ? "没有新东西：他没动板，也没有工单。"
            : $"{lines.Count} 处要你看：" + (ack ? "（已抹平这次读到的这一版）" : string.Empty) + "\n" + string.Join("\n", lines);
    }

    [McpServerTool(Name = "board_tickets"), Description(
        "列出他在板上打的 @a 工单。判据：格子里有一个**恰好叫 a 的标签**——他打 @a，板子会存成 @ta（标签 a），"
        + "所以按字面找 \"@a\" 会全漏掉（10-27 实测漏了三张）。收尾前叫一次：有工单就开工单，别等他踢。")]
    public static string BoardTickets(
        [Description("画板文件路径。不填就扫所有开着的板。")] string? board = null)
    {
        var paths = board is { Length: > 0 }
            ? [ResolveBoard(board)]
            : new[] { ResolveBoard(null) };

        var lines = new List<string>();

        foreach (var path in paths.Distinct())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            foreach (var node in CanvasStore.Load(path).Nodes)
            {
                if (node.Kind == CanvasNodeKind.Group)
                {
                    continue;
                }

                if (!Tags.Of(node.Text).Any(tag => tag.Equals("#a", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var first = (node.Text ?? string.Empty).Split('\n')
                    .FirstOrDefault(line => !Tags.LooksLikePropertyLine(line) && line.Trim().Length > 0) ?? string.Empty;

                lines.Add($"{node.Id} | {first[..Math.Min(60, first.Length)]} | {Path.GetFileName(path)}");
            }
        }

        return lines.Count == 0 ? "没有工单。" : $"工单 {lines.Count} 张：\n" + string.Join("\n", lines);
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
        // 标签读出来是规范形 #xxx：板上写 @tagent出口，存的就是 #agent出口。
        // 所以两种写法都认（带 t 的那条是 @t 这个键省下来的），不然按标签搜永远搜不到东西。
        var wanted = byTag
            ? new[] { "#" + needle[1..], needle.StartsWith("@t", StringComparison.Ordinal) ? "#" + needle[2..] : "#" + needle[1..] }
            : [];

        var hits = CanvasStore.Load(path).Nodes
            .Where(node => byTag
                ? Tags.Of(node.Text).Any(tag => wanted.Contains(tag, StringComparer.OrdinalIgnoreCase))
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
    /// <summary>
    /// 他 10-27 定的：红（1）要他拍、橙（2）风险——这两档保留原样；**其余回答一律用蓝**，
    /// 一眼分得出"这是 agent 写的"。JSON Canvas 1.0 的六档预设是 红/橙/黄/绿/青/紫，没有纯蓝，
    /// 最接近的是 5（青）——所以"蓝"落在 5 上；他真要自定义 hex 再说。
    /// </summary>
    private static string? ColourOf(string kind) => kind switch
    {
        "decision" => "1",
        "risk" => "2",
        _ => "5",
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

    /// <summary>横排一行最多多宽；超过就换行。</summary>
    private const double TileRowWidth = 1680;

    /// <summary>一块里那枚标题卡写着落板时间（yyyy-MM-dd HH:mm）；没写就是老内容，返回 null。</summary>
    private static DateTime? BlockTime(Dictionary<string, CanvasNode> byId, List<string> ids)
    {
        foreach (var id in ids)
        {
            var text = byId[id].Text ?? string.Empty;

            foreach (var line in text.Split('\n'))
            {
                if (DateTime.TryParseExact(line.Trim(), "yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var stamp))
                {
                    return stamp;
                }
            }
        }

        return null;
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
