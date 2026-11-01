using System.Text.Json.Nodes;
using Outspread.Core;
using Outspread.Mcp;

namespace Outspread.Mcp.Tests;

/// <summary>
/// 出口那几枚工具的写分支：落在 %TEMP% 的一块板上，绝不碰他真在用的那几块。
/// 2026-10-26 他勾 @n10 时我在自己机器上撞出来的两发都锁在这里：
/// 结论卡没号（他没法引用）、回执报了板上不存在的号（凭据是假的）。
/// </summary>
public sealed class BoardToolsWriteTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "outspread-mcp-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public BoardToolsWriteTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Every_kind_gets_its_own_number_and_the_receipt_shows_those_exact_ones()
    {
        var board = SeededBoard(ordinal: 7);

        var receipt = BoardTools.BoardPost(
            "编号",
            [
                Card("note", "这是一条结论"),
                Card("task", "这是一条待办"),
                Card("risk", "这是一处缺陷"),
                Card("decision", "这是要他拍的"),
            ],
            round: "编号",
            board: board);

        var ordinals = CanvasStore.Load(board).Nodes
            .Select(node => Tags.Read(node.Text).Sequence)
            .Where(sequence => sequence is not null)
            .Select(sequence => sequence!.Value)
            .ToList();

        // 四格各一枚号，接在板上已有的 7 后面且不与之相撞：他"@n10 已结"这样引用才有唯一落点。
        Assert.Equal([7, 8, 9, 10, 11], ordinals.Order().ToArray());

        // 回执是他读到的凭据：上面写的号必须就是板上的号，一个都不能是编出来的。
        foreach (var line in receipt.Split('\n').Where(line => line.StartsWith("@n", StringComparison.Ordinal)))
        {
            var shown = int.Parse(line.Split(' ')[0][2..]);

            Assert.Contains(shown, ordinals);
        }
    }

    [Fact]
    public void Only_the_three_kinds_that_ask_something_of_him_carry_a_tick_mark()
    {
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost(
            "负担",
            [
                Card("note", "结论不该算进他的待办"),
                Card("task", "待办要算"),
                Card("risk", "缺陷要算"),
                Card("decision", "要拍的更要算"),
            ],
            round: "负担",
            board: board);

        var notes = CanvasStore.Load(board).Nodes
            .Where(node => Tags.Read(node.Text).Done is not null)
            .ToList();

        // 结论挂上牌子，"还欠几格"就会把无关的结论算进他的负担——那一行读数就不是凭据了。
        Assert.Equal(3, notes.Count);
        Assert.All(notes, node => Assert.False(Tags.Read(node.Text).Done));

        var unread = BoardTools.BoardRead(board, limit: 40)!.Split('\n')[1];

        Assert.Contains("还欠 3 格", unread);
    }

    [Fact]
    public void A_card_that_already_carries_marks_keeps_yours_instead_of_getting_a_second_number()
    {
        var board = SeededBoard(ordinal: 3);

        var receipt = BoardTools.BoardPost(
            "沿用",
            [Card("note", "@t我自己标的 @n99 @b0\n这行本来就是他的标记行")],
            round: "沿用",
            board: board);

        var kept = CanvasStore.Load(board).Nodes
            .Select(node => Tags.Read(node.Text).Sequence)
            .Where(sequence => sequence is not null)
            .Select(sequence => sequence!.Value)
            .ToList();

        // 沿用他自己那行的号，不再叠第二个号，也不把 4 这个空号发出去。
        Assert.Equal([3, 99], kept.Order().ToArray());
        Assert.Contains("@n—（沿用你写的标记行）", receipt);
    }


    [Fact]
    public void A_batch_lands_as_a_named_group_in_a_grid_not_one_long_snake()
    {
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost(
            "一批四件",
            [
                Card("decision", "要他拍的"),
                Card("risk", "一处缺陷"),
                Card("task", "一条待办"),
                Card("note", "一条结论"),
            ],
            round: "编组",
            board: board,
            columns: 3);

        var document = CanvasStore.Load(board);
        var group = Assert.Single(document.Nodes.Where(node => node.Kind == CanvasNodeKind.Group));

        // 组名就是这一批的小标题，成员是标题卡 + 四张卡：他以后按名字找，不用靠位置。
        // 组名带日期和时间（他要按轮次排序，光一个标题排不了）。
        Assert.EndsWith("· 一批四件", group.Label);
        Assert.StartsWith(DateTime.Now.ToString("MM-dd"), group.Label);
        Assert.Equal(5, group.GroupMemberIds?.Count);

        var cards = document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();
        // 去掉种板子那一格（u1）和标题卡，只看这一批落下的四张。
        var body = cards
            .Where(node => node.Id != "u1")
            .Where(node => !(node.Text ?? string.Empty).StartsWith("### ", StringComparison.Ordinal))
            .ToList();

        // 四件事排成两行（3 + 1），不是四行——那正是他 10-27 否掉的一字长蛇阵。
        Assert.Equal(2, body.Select(node => node.Y).Distinct().Count());
        Assert.Equal(3, body.Count(node => Math.Abs(node.Y - body.Min(item => item.Y)) < 0.5));
    }

    [Fact]
    public void Archiving_can_reuse_the_group_he_drew_and_just_rename_it()
    {
        // 他 10-27 把组名写成了给我的话（"改成合适的名字"）：那一枚组要就地改名换成员，
        // 不能甩一枚新组、把他画的那个框留在原地。
        var board = Path.Combine(_root, "他画的框.canvas");
        var groupId = "uf947930abcdef";

        File.WriteAllText(board, """
            {"nodes":[
              {"id":"u1","type":"text","x":0,"y":0,"width":400,"height":300,"text":"一条已经看完的结论"},
              {"id":"u2","type":"text","x":0,"y":320,"width":300,"height":140,"text":"另一条已经看完的结论"},
              {"id":"uf947930abcdef","type":"group","x":-12,"y":-12,"width":424,"height":484,"label":"改成合适的名字","members":["u1","u2"]}
            ],"edges":[]}
            """);

        var receipt = BoardTools.BoardArchive("MCP 出口那轮（已结）", ["u1", "u2"], board, into_group: groupId);

        var after = CanvasStore.Load(board);
        var group = Assert.Single(after.Nodes.Where(node => node.Kind == CanvasNodeKind.Group));
        var kept = after.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).OrderBy(node => node.X).ToList();

        Assert.EndsWith("· MCP 出口那轮（已结）", group.Label);          // 组名必须带 MM-dd HH:mm 前缀
        Assert.Equal([groupId], after.Nodes.Where(node => node.Kind == CanvasNodeKind.Group).Select(node => node.Id));
        Assert.Equal(["u1".ToLowerInvariant(), "u2".ToLowerInvariant()], group.GroupMemberIds!.Select(id => id.ToLowerInvariant()));
        Assert.Contains("MCP 出口那轮（已结）", receipt);

        // 两格都收到组内最窄最矮：宽取 300、高取 140。
        Assert.All(kept, node => Assert.Equal(300, node.Width, 3));
        Assert.All(kept, node => Assert.Equal(140, node.Height, 3));

        // 框必须盖住每一格：10-27 实测漏过——板子把没挂手动尺寸的格子按正文量高后，
        // 摞里最下面那格探出组框 9px。框按拍完的尺寸算，尺寸又必须钉得住，这两条缺一个就会露角。
        Assert.All(kept, node => Assert.True(
            group.X <= node.X && group.Y <= node.Y
            && group.X + group.Width >= node.X + node.Width
            && group.Y + group.Height >= node.Y + node.Height,
            $"{node.Id} 探出了组框"));
    }

    [Fact]
    public void In_one_round_the_settled_ones_stack_and_the_open_one_stays_flat_in_the_same_group()
    {
        // 他 10-27 的摆法：一轮一个组；已勾的叠成一摞，没勾的平铺在下面——不散落、也不叠。
        var board = Path.Combine(_root, "一轮混着.canvas");

        var doc = new JsonObject
        {
            ["nodes"] = new JsonArray(
                Note("u1", 0, 0, 400, 200, "@t那轮 @n1 @b1\n已经结了的"),
                Note("u2", 420, 0, 360, 160, "@t那轮 @n2 @b1\n也已经结了"),
                Note("u3", 0, 400, 300, 140, "@t那轮 @n3 @b0\n还在等他拍")),
            ["edges"] = new JsonArray(),
        };

        File.WriteAllText(board, doc.ToJsonString());

        var receipt = BoardTools.BoardArchive("那轮（一半一半）", ["u1", "u2", "u3"], board);

        var document = CanvasStore.Load(board);
        var group = Assert.Single(document.Nodes.Where(node => node.Kind == CanvasNodeKind.Group));
        var first = document.Find("u1")!;
        var second = document.Find("u2")!;
        var open = document.Find("u3")!;

        // 已结的两格叠成一摞：尺寸收到组内最窄最矮（360×160），位置是错开的（叠起来本来就这样）。
        Assert.Equal(360, first.Width, 3);
        Assert.Equal(160, first.Height, 3);
        Assert.Equal(360, second.Width, 3);
        Assert.Equal(160, second.Height, 3);
        Assert.True(Math.Abs(first.X - second.X) < 60 && Math.Abs(first.Y - second.Y) < 60, "两格没有叠在一起");

        // 没结的那格不叠：落在那摞下面、不和它重叠，尺寸也不被改动，但仍在同一枚组里。
        Assert.True(open.Y >= Math.Max(first.Y, second.Y) + Math.Max(first.Height, second.Height) - 1, "未结那格没挪到摞下面");
        Assert.Equal(300, open.Width, 3);
        Assert.Equal(140, open.Height, 3);
        Assert.Contains("u3", group.GroupMemberIds!);
        Assert.Equal(3, group.GroupMemberIds!.Count);
        Assert.Contains("1 格留在同一个组里平铺", receipt);

        // 框盖住叠的那摞，也盖住平铺的那张。
        Assert.All([first, second, open], node => Assert.True(
            group.X <= node.X && group.Y <= node.Y
            && group.X + group.Width >= node.X + node.Width
            && group.Y + group.Height >= node.Y + node.Height,
            $"{node.Id} 探出了组框"));
    }

    [Fact]
    public void Tidying_pulls_the_blocks_together_without_touching_what_is_inside_them()
    {
        // 他 10-27 的截图：组与组之间空出半屏。收紧只挪块的位置，组内相对摆法一个字节不改。
        var board = Path.Combine(_root, "散得很开.canvas");
        var doc = new JsonObject
        {
            ["nodes"] = new JsonArray(
                Note("u1", 0, 0, 360, 120, "@t甲 甲组第一格"),
                Note("u2", 40, 40, 360, 120, "@t甲 甲组第二格"),
                new JsonObject { ["id"] = "g1", ["type"] = "group", ["label"] = "甲", ["x"] = -12, ["y"] = -12, ["width"] = 412, ["height"] = 172, ["members"] = new JsonArray("u1", "u2") },
                Note("u3", 900, 3000, 360, 120, "@t乙 乙组一格"),
                new JsonObject { ["id"] = "g2", ["type"] = "group", ["label"] = "乙", ["x"] = 888, ["y"] = 2988, ["width"] = 384, ["height"] = 144, ["members"] = new JsonArray("u3") }),
            ["edges"] = new JsonArray(),
        };

        File.WriteAllText(board, doc.ToJsonString());

        BoardTools.BoardTidy(gap: 48, board: board);

        var after = CanvasStore.Load(board);
        var g1 = after.Find("g1")!;
        var g2 = after.Find("g2")!;
        var u1 = after.Find("u1")!;
        var u2 = after.Find("u2")!;

        // 他 10-27 定的是横排：两块并排在同一行，缝 48，不再一路竖下去。
        Assert.Equal(g1.Y, g2.Y, 3);
        Assert.Equal(g1.X + g1.Width + 48, g2.X, 3);

        // 组内错位保留：两格的相对偏移还是 40,40。
        Assert.Equal(40, u2.X - u1.X, 3);
        Assert.Equal(40, u2.Y - u1.Y, 3);
    }

    [Fact]
    public void A_wide_title_card_is_pulled_into_the_pile_instead_of_being_left_outside()
    {
        // 10-27 实测的漏：标题卡 1128 宽、卡片 360，叠放要求 80% 重叠 ⇒ 宽的那张没进摞，
        // 还戳在组框外面，"已结"那组看起来就成了一格标题 + 一摞卡。统一尺寸要在叠之前发生。
        var board = Path.Combine(_root, "宽标题.canvas");
        var doc = new JsonObject
        {
            ["nodes"] = new JsonArray(
                Note("t1", 0, 0, 1128, 119, "### 这一轮的标题"),
                Note("u1", 0, 200, 360, 119, "@t那轮 @n1 @b1\n一格"),
                Note("u2", 0, 340, 360, 140, "@t那轮 @n2 @b1\n另一格")),
            ["edges"] = new JsonArray(),
        };

        File.WriteAllText(board, doc.ToJsonString());

        BoardTools.BoardArchive("宽标题那轮（已结）", ["t1", "u1", "u2"], board);

        var after = CanvasStore.Load(board);
        var group = Assert.Single(after.Nodes.Where(node => node.Kind == CanvasNodeKind.Group));
        var all = new[] { "t1", "u1", "u2" }.Select(id => after.Find(id)!).ToList();

        Assert.All(all, node => Assert.Equal(360, node.Width, 3));
        Assert.All(all, node => Assert.True(
            Math.Abs(node.X - all[0].X) < 60 && Math.Abs(node.Y - all[0].Y) < 60,
            $"{node.Id} 没进摞"));
        Assert.All(all, node => Assert.True(
            group.X <= node.X && group.Y <= node.Y
            && group.X + group.Width >= node.X + node.Width
            && group.Y + group.Height >= node.Y + node.Height,
            $"{node.Id} 探出了组框"));
    }

    [Fact]
    public void Agent_answers_are_blue_while_red_and_orange_keep_their_meaning()
    {
        // 他 10-27：红＝要他拍、橙＝风险保留原样，其余回答统一用蓝，一眼分得出谁写的。
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost(
            "配色",
            [Card("decision", "拍板"), Card("risk", "风险"), Card("task", "待办"), Card("note", "结论")],
            round: "配色",
            board: board);

        var body = CanvasStore.Load(board).Nodes
            .Where(node => node.Kind != CanvasNodeKind.Group && node.Id != "u1")
            .Where(node => !(node.Text ?? string.Empty).StartsWith("### ", StringComparison.Ordinal))
            .ToList();

        Assert.Contains(body, node => node.Color == "1");
        Assert.Contains(body, node => node.Color == "2");
        Assert.Equal(2, body.Count(node => node.Color == "5"));
        Assert.DoesNotContain(body, node => node.Color is null);
    }

    private static JsonObject Note(string id, int x, int y, int width, int height, string text)
        => new()
        {
            ["id"] = id,
            ["type"] = "text",
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["text"] = text,
        };

    /* 这三发钉的是 10-27 他打回的那件事：我落了两格"记我自己的待办 / 一条外推曲线"，
       他读完回「所以要我做什么？」「不用告诉我的就不用写在这里」。
       判据早就写在 AGENTS.md 与 skill 里了——证明只写文档挡不住，所以装进工具：
       凡是要挂 @b0（进他"还欠几格"那一栏）的卡，必须写清他下一步做什么。 */

    [Fact]
    public void A_card_that_sits_in_his_queue_but_names_no_action_is_refused_and_writes_nothing()
    {
        var board = SeededBoard(ordinal: 0);
        var before = CanvasStore.Load(board).Nodes.Count;

        var receipt = BoardTools.BoardPost(
            "没人要动作的一发",
            [
                new BoardCard { Kind = "task", Text = "数墨那 3.4ms 还挂在落定那一帧" },
                new BoardCard { Kind = "risk", Text = "拖一格随格数线性涨" },
            ],
            round: "闸",
            board: board);

        Assert.Contains("没落板", receipt, StringComparison.Ordinal);
        Assert.Contains("要你：", receipt, StringComparison.Ordinal);   // 回执要教下一步怎么写，不能只说不行
        Assert.Equal(before, CanvasStore.Load(board).Nodes.Count);      // 整发没动盘：不许"拒一半、落一半"
    }

    [Fact]
    public void The_same_card_lands_with_a_tick_once_it_names_his_next_action()
    {
        // 正对照：上一发被拒不是因为"task/risk 一律不让落"，缺的正是那一行。
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost(
            "有人要动作的一发",
            [new BoardCard { Kind = "task", Text = "数墨那 3.4ms 还挂在落定那一帧\n要你：拍要不要收（收＝我再花一发测量）" }],
            round: "闸",
            board: board);

        var landed = CanvasStore.Load(board).Nodes
            .Select(node => node.Text ?? string.Empty)
            .FirstOrDefault(text => text.Contains("数墨", StringComparison.Ordinal));

        Assert.NotNull(landed);
        Assert.Contains("@b0", landed!, StringComparison.Ordinal);
        Assert.Contains("要你：拍要不要收", landed!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_card_over_the_one_line_budget_is_refused_and_writes_nothing()
    {
        // 简洁也是闸（他 10-28 第二次说"板子上长篇大论"）。一格折完最多 4 行：
        // 24 字一行 × 4＝装得下一句事实＋一句「要你：…」；这一发故意写成一整段叙述。
        var board = SeededBoard(ordinal: 0);
        var before = CanvasStore.Load(board).Nodes.Count;

        var rambling = string.Join("", Enumerable.Repeat("拖动中每帧把整张节点表重读物化一遍所以越拖越贵，", 8));

        var receipt = BoardTools.BoardPost(
            "长篇那一发",
            [new BoardCard { Kind = "task", Text = $"{rambling}\n要你：拍要不要收" }],
            round: "简",
            board: board);

        Assert.Contains("没落板", receipt, StringComparison.Ordinal);
        Assert.Contains("折行", receipt, StringComparison.Ordinal);
        Assert.Equal(before, CanvasStore.Load(board).Nodes.Count);
    }

    [Fact]
    public void The_same_saying_split_into_two_cards_lands()
    {
        // 正对照：被拒的不是这件事，是"挤在一格里"。拆成一句事实＋一句要动作，就落得下去。
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost(
            "拆开那一发",
            [
                new BoardCard { Kind = "note", Text = "拖一格每帧重读物化整张节点表，越拖越贵" },
                new BoardCard { Kind = "task", Text = "这条改法是逐槽身份校验的惰性缓存\n要你：勾掉即认可" },
            ],
            round: "简",
            board: board);

        var texts = CanvasStore.Load(board).Nodes.Select(node => node.Text ?? string.Empty).ToList();

        Assert.Contains(texts, text => text.Contains("重读物化整张节点表", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.Contains("要你：勾掉即认可", StringComparison.Ordinal));
    }

    [Fact]
    public void A_note_needs_no_ask_line_because_it_never_sits_in_his_queue()
    {        // 结论卡不挂 @b0，不占他的"还欠几格"，所以这一闸不管它——否则每条读数都得编一句假动作。
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost(
            "只报读数的一发",
            [new BoardCard { Kind = "note", Text = "600 格拖一格 2.83→1.92ms/帧（提交 ae84f9a）" }],
            round: "闸",
            board: board);

        var landed = CanvasStore.Load(board).Nodes
            .Select(node => node.Text ?? string.Empty)
            .FirstOrDefault(text => text.Contains("2.83", StringComparison.Ordinal));

        Assert.NotNull(landed);
        Assert.DoesNotContain("@b0", landed!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_signed_post_stamps_the_title_and_every_card_with_the_by_tag()
    {
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost(
            "署名",
            [Card("note", "这一格带着署名")],
            round: "署名",
            agent: "night-zcode",
            board: board);

        // 署名是第二枚标签（@tby-名字），与轮次标签并排：他按轮次筛，别的 agent 按署名筛。
        // 标题卡也得带上——分清"这一组是谁的"从组名那一格开始。
        Assert.All(
            CanvasStore.Load(board).Nodes.Select(node => node.Text ?? string.Empty)
                .Where(text => text.Contains("### 署名") || text.Contains("@n1")),
            text => Assert.Contains("@tby-night-zcode", text));
    }

    [Fact]
    public void A_signed_patch_is_idempotent_about_the_by_tag()
    {
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPatch("u1", agent: "night-zcode", board: board);
        BoardTools.BoardPatch("u1", agent: "night-zcode", board: board);

        // 同一格反复更新不该叠出一串署名——AddTag 本身幂等，这条钉的是接线别把它绕开。
        var text = CanvasStore.Load(board).Nodes.Single(node => node.Id == "u1").Text!;

        Assert.Equal(1, text.Split("@tby-night-zcode").Length - 1);
    }

    [Fact]
    public void Read_by_signature_only_lists_that_agents_cards()
    {
        var board = SeededBoard(ordinal: 0);

        BoardTools.BoardPost("甲的", [Card("note", "甲写的一格")], round: "甲", agent: "甲agent", board: board);
        BoardTools.BoardPost("乙的", [Card("note", "乙写的一格")], round: "乙", board: board);

        var mine = BoardTools.BoardRead(board: board, by: "甲agent");
        var all = BoardTools.BoardRead(board: board);

        // 按署名只看一家的卡：几路 agent 同机各写各的，"夜间班写了啥"一发就有。
        Assert.Contains("甲写的一格", mine);
        Assert.DoesNotContain("乙写的一格", mine);
        Assert.Contains("乙写的一格", all);
        Assert.Contains("甲写的一格", all);
    }

    [Fact]
    public void Board_patch_accepts_the_short_ordinal_and_reports_unknown_ones()
    {
        var board = SeededBoard(ordinal: 7);

        var receipt = BoardTools.BoardPatch("@n7", text: "短号改的正文", board: board);

        Assert.Contains("已更新", receipt);
        Assert.Contains("短号改的正文", CanvasStore.Load(board).Nodes.Single(node => node.Id == "u1").Text);

        var miss = BoardTools.BoardPatch("@n999", text: "不该落盘", board: board);

        Assert.Contains("没有", miss);
        Assert.DoesNotContain("不该落盘", string.Join('\n', CanvasStore.Load(board).Nodes.Select(node => node.Text)));
    }

    [Fact]
    public void Deleting_a_card_prunes_the_roster_shrinks_the_frame_and_answers_the_short_ordinal()
    {
        var board = SeededBoard(ordinal: 7);

        BoardTools.BoardPost(
            "删除试验",
            [Card("note", "甲"), Card("note", "乙"), Card("note", "丙")],
            round: "删除",
            board: board);

        var before = CanvasStore.Load(board);
        var frame = before.Nodes.Single(node => node.Kind == CanvasNodeKind.Group && (node.Label ?? string.Empty).Contains("删除试验", StringComparison.Ordinal));
        var victim = before.Nodes.Single(node => (node.Text ?? string.Empty).Contains("\n乙", StringComparison.Ordinal));
        var ordinal = Tags.Read(victim.Text).Sequence ?? throw new InvalidOperationException("乙没有号");

        var frameBoxBefore = (frame.X, frame.Y, frame.Width, frame.Height);

        var receipt = BoardTools.BoardDelete([$"@n{ordinal}"], board);

        Assert.Contains(victim.Id, receipt);
        Assert.DoesNotContain(victim.Id, CanvasStore.Load(board).Nodes.Select(node => node.Id));

        var after = CanvasStore.Load(board).Nodes.Single(node => node.Id == frame.Id);

        Assert.DoesNotContain(victim.Id, after.GroupMemberIds ?? []);
        Assert.Equal(3, (after.GroupMemberIds ?? []).Count);

        // 框的契约＝幸存成员的 Enclose（标题卡横贯整行，宽度本就不会缩，别拿"变小"当尺）。
        var aliveIds = ((frame.GroupMemberIds ?? []).Where(id => id != victim.Id)).ToList();
        var expected = Grouping.Enclose(frame.Id, aliveIds
            .Select(id => before.Nodes.First(node => node.Id == id))
            .Select(node => new Grouping.Rect(node.Id, node.X, node.Y, node.Width, node.Height))
            .ToList());

        Assert.Equal(expected.X, after.X);
        Assert.Equal(expected.Y, after.Y);
        Assert.Equal(expected.Width, after.Width);
        Assert.Equal(expected.Height, after.Height);
        _ = frameBoxBefore;

        // 组里三格删光＝空框一并消失；成员卡原地留着。
        BoardTools.BoardDelete([after.Id], board);

        var cleared = CanvasStore.Load(board);

        Assert.DoesNotContain(after.Id, cleared.Nodes.Select(node => node.Id));
        Assert.Equal(2, cleared.Nodes.Count(node => (node.Text ?? string.Empty).Contains("\n甲")
            || (node.Text ?? string.Empty).Contains("\n丙")));
    }

    [Fact]
    public void Deleting_what_awaits_him_is_named_in_the_receipt_and_unknown_ids_are_reported()
    {
        var board = SeededBoard(ordinal: 7);

        BoardTools.BoardPost("等他拍的", [Card("decision", "要不要删")], round: "等他拍", board: board);

        var document = CanvasStore.Load(board);
        var pending = document.Nodes.Single(node => (node.Text ?? string.Empty).Contains("要不要删", StringComparison.Ordinal));

        var receipt = BoardTools.BoardDelete([pending.Id, "n999"], board);

        Assert.Contains("@b0", receipt);
        Assert.Contains("n999", receipt);
        Assert.DoesNotContain(pending.Id, CanvasStore.Load(board).Nodes.Select(node => node.Id));
    }

    /// <summary>
    /// 测试用的卡。三档要挂 @b0 的（decision/risk/task）必须带一行「要你：」，否则 <c>board_post</c>
    /// 那枚闸会把整发拒掉（10-27 加的：我落了两格"记我自己的待办/一条外推曲线"，他读完回的是
    /// 「所以要我做什么？」）。闸本身另有三发直接测，不走这个助手。
    /// </summary>
    private static BoardCard Card(string kind, string text) => new()
    {
        Kind = kind,
        Text = kind == "note" ? text : $"{text}\n要你：看一眼这一格",
    };

    /// <summary>
    /// 种一块只有一格（带 @n&lt;ordinal&gt;）的板，用来验"接号"接的是板上已有的最大号。
    /// 标记必须自成一行且只有标记：后面跟着正文的那一行按板子的规矩算正文，不算号。
    /// </summary>
    private string SeededBoard(int ordinal)
    {
        var path = Path.Combine(_root, $"出口{ordinal}.canvas");

        File.WriteAllText(path, $$"""
            {"nodes":[{"id":"u1","type":"text","x":0,"y":0,"width":300,"height":100,"text":"@t已有 @n{{ordinal}}\n他先写的那格"}],"edges":[]}
            """);

        return path;
    }
}
