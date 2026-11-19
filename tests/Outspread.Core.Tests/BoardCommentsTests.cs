namespace Outspread.Core.Tests;

/// <summary>
/// 评论是旁挂文件：线程挂在哪一格、数几条、结没结，都要能从盘上原样回来。
/// </summary>
/// <remarks>
/// 这里钉三件最容易悄悄坏掉的事：① 文件里缺了／坏了不许拦住板子打开；② 删掉一格之后线程仍留在
/// 文件里（撤销回来还得看见）；③ 写盘时中文不转义——这份文件是给人看的，转成 \uXXXX 等于没法读。
/// </remarks>
public class BoardCommentsTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"outspread-comments-{Guid.NewGuid():n}.json");

    [Fact]
    public void Threads_survive_a_round_trip_and_keep_chinese_literal()
    {
        var path = TempPath();

        try
        {
            var comments = new BoardComments();
            var thread = comments.AddThread("a000000000001", "他", "这条还没完：能一键回到板面吗？", agent: false, threadId: "c000000000001", time: "19:30");

            comments.Reply(thread.Id, "Claude", "可以，已解决的收进抽屉，随时能拉回来。", agent: true, time: "19:31");
            comments.Save(path);

            var text = File.ReadAllText(path);

            Assert.Contains("这条还没完", text);
            Assert.DoesNotContain("\\u", text);

            // 算得出来的东西不落盘：文件里只有线程本身。多写一个 unresolvedThreads，
            // 就等于给"到底结了几条"留了第二个真值来源，读到旧数时没人说得清信哪个。
            Assert.DoesNotContain("unresolvedThreads", text);
            Assert.DoesNotContain("messageTotal", text);

            var loaded = BoardComments.Load(path);

            Assert.Null(loaded.Problem);
            Assert.Single(loaded.Threads);
            Assert.Equal(2, loaded.Threads[0].Messages.Count);
            Assert.Equal("a000000000001", loaded.Threads[0].Node);
            Assert.True(loaded.Threads[0].Messages[1].Agent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Counts_are_per_cell_and_can_exclude_resolved_threads()
    {
        var comments = new BoardComments();

        var open = comments.AddThread("n1", "他", "一", agent: false, threadId: "c1", time: "10:00");
        var done = comments.AddThread("n1", "他", "二", agent: false, threadId: "c2", time: "10:01");
        comments.AddThread("n2", "他", "三", agent: false, threadId: "c3", time: "10:02");
        comments.SetResolved(done.Id, true);

        // n1 上两条线程、每条一条开篇消息＝2 条；徽标上的数就是它，不是线程数。
        Assert.Equal(2, comments.MessageCount("n1"));
        Assert.Equal(1, comments.MessageCount("n1", unresolvedOnly: true));
        Assert.Equal(1, comments.MessageCount("n2"));
        Assert.Equal(0, comments.MessageCount("n9"));
        Assert.Equal(2, comments.UnresolvedThreads);
        Assert.Equal(3, comments.MessageTotal);

        // 回复一条结掉的线程＝它重新打开（原型里那句「已解决，回复会重新打开」）。
        comments.Reply(done.Id, "他", "还有一句", agent: false);

        Assert.False(done.Resolved);
        Assert.Equal(3, comments.MessageCount("n1", unresolvedOnly: true));
    }

    [Fact]
    public void A_broken_or_missing_file_never_blocks_the_board()
    {
        var missing = TempPath();
        var broken = TempPath();

        try
        {
            var none = BoardComments.Load(missing);

            Assert.Empty(none.Threads);
            Assert.Null(none.Problem);

            File.WriteAllText(broken, "{ 这不是 JSON");

            var bad = BoardComments.Load(broken);

            Assert.Empty(bad.Threads);
            Assert.NotNull(bad.Problem);
        }
        finally
        {
            File.Delete(broken);
        }
    }

    [Fact]
    public void Threads_without_an_anchor_are_dropped_so_no_badge_is_unclickable()
    {
        var path = TempPath();

        try
        {
            File.WriteAllText(path, """
            {
              "threads": [
                { "id": "c1", "node": "n1", "messages": [] },
                { "id": "c2", "node": "", "messages": [] },
                { "id": "", "node": "n3", "messages": [] }
              ]
            }
            """);

            var loaded = BoardComments.Load(path);

            Assert.Single(loaded.Threads);
            Assert.Equal("c1", loaded.Threads[0].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TempBoard() => Path.Combine(Path.GetTempPath(), $"outspread-comments-{Guid.NewGuid():n}.canvas");

    private static void CleanUp(string board)
    {
        foreach (var path in new[] { board, CanvasStore.CommentsPath(board) })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Twelve_writers_at_once_all_survive()
    {
        var board = TempBoard();
        var path = CanvasStore.CommentsPath(board);

        try
        {
            // 2026-11-19：12 发并发只活下 3 条。写的一侧现在是「按板互斥 + 盘上那份再并一次」，
            // 这一条就是那次丢数据的回归锁。
            var writers = Enumerable.Range(0, 12).Select(i => Task.Run(() =>
            {
                var comments = new BoardComments();
                comments.AddThread("n1", "agent", $"并发 {i}", agent: true);
                comments.Save(path);
            }));

            await Task.WhenAll(writers);

            var loaded = BoardComments.Load(path);

            Assert.Null(loaded.Problem);
            Assert.Equal(12, loaded.Threads.Count);
            Assert.Equal(12, loaded.MessageTotal);
            Assert.Equal(12, loaded.Threads.SelectMany(thread => thread.Messages).Select(message => message.Id).Distinct().Count());
        }
        finally
        {
            CleanUp(board);
        }
    }

    [Fact]
    public void Merge_is_a_union_and_the_newer_state_wins_per_thread()
    {
        var local = new BoardComments();
        var lhs = local.AddThread("n1", "他", "本机这句", agent: false, threadId: "c1", time: "10:00");
        local.AddThread("n2", "他", "只有本机有", agent: false, threadId: "c3", time: "10:02");

        var far = new BoardComments();
        var rhs = far.AddThread("n1", "agent", "远端那句", agent: true, threadId: "c1", time: "10:01");
        rhs.Messages.Insert(0, new CommentMessage
        {
            Id = lhs.Messages[0].Id,
            Author = lhs.Messages[0].Author,
            Text = lhs.Messages[0].Text,
            Time = lhs.Messages[0].Time,
            At = lhs.Messages[0].At,
        });
        rhs.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O");

        var merged = BoardComments.Merge(local, far);

        Assert.Equal(2, merged.Threads.Count);
        Assert.Equal(2, merged.Find("c1")!.Messages.Count);
        Assert.False(merged.Find("c1")!.Resolved);

        // 两端都有的线程不重复；只有本机有的照留；只有远端有的算新消息（亮橙点）。
        Assert.NotNull(merged.Find("c3"));
        Assert.False(merged.Find("c3")!.Unread);
    }

    [Fact]
    public void A_deleted_thread_stays_deleted_after_merging()
    {
        var local = new BoardComments();
        local.AddThread("n1", "他", "删我", agent: false, threadId: "c1", time: "10:00");
        local.RemoveThread("c1");

        // 远端还留着那条（另一台设备没删过），并集不能把它带回来。
        var far = new BoardComments();
        far.AddThread("n1", "他", "删我", agent: false, threadId: "c1", time: "10:00");

        var merged = BoardComments.Merge(local, far);

        Assert.Empty(merged.Threads);
        Assert.Single(merged.Tombstones);
    }

    [Fact]
    public void Unread_is_a_local_view_and_never_reaches_the_wire()
    {
        var comments = new BoardComments();
        var thread = comments.AddThread("n1", "agent", "新消息", agent: true, threadId: "c1", time: "10:00");

        // 同一份内容、未读没打开的那一版：消息号也一样，只有 unread 不同。
        var quiet = BoardComments.Parse(comments.ToJson(includeUnread: false));
        thread.Unread = true;

        Assert.Contains("unread", comments.ToJson());
        Assert.DoesNotContain("unread", comments.ToJson(includeUnread: false));

        // 未读翻一下不算"内容变了"：同步不该因为它花一次往返。
        Assert.Equal(
            BoardComments.ContentDigest(quiet),
            BoardComments.ContentDigest(comments));
    }

    [Fact]
    public void Merge_is_order_independent_so_two_devices_cannot_ping_pong()
    {
        var left = new BoardComments();
        left.AddThread("n1", "他", "第一句", agent: false, threadId: "c1", time: "10:00");
        left.AddThread("n2", "他", "第二句", agent: false, threadId: "c2", time: "10:01");

        // 右端拿到同一份内容、顺序倒过来，再补一条：两边合并必须落到同一份字节上。
        var right = BoardComments.Parse(left.ToJson(includeUnread: false));
        right.Threads.Reverse();
        right.AddThread("n3", "他", "右端新写的", agent: false, threadId: "c3", time: "10:02");

        var oneWay = BoardComments.Merge(left, right);
        var otherWay = BoardComments.Merge(right, left);

        Assert.Equal(oneWay.ToJson(includeUnread: false), otherWay.ToJson(includeUnread: false));
        Assert.Equal(BoardComments.ContentDigest(oneWay), BoardComments.ContentDigest(otherWay));
    }

    [Fact]
    public void Legacy_messages_get_the_same_id_at_both_ends()
    {
        const string legacy = """
        {
          "threads": [
            {
              "id": "c1",
              "node": "n1",
              "messages": [
                { "author": "他", "text": "旧账", "time": "10:00", "agent": false }
              ]
            }
          ]
        }
        """;

        var left = BoardComments.Parse(legacy);
        var right = BoardComments.Parse(legacy);

        Assert.NotNull(left.Threads[0].Messages[0].Id);
        Assert.Equal(left.Threads[0].Messages[0].Id, right.Threads[0].Messages[0].Id);

        // 并一次不会把同一条旧消息算成两条。
        Assert.Single(BoardComments.Merge(left, right).Threads[0].Messages);
    }

    [Fact]
    public void A_broken_file_is_refused_not_overwritten()
    {
        var board = TempBoard();
        var path = CanvasStore.CommentsPath(board);

        try
        {
            File.WriteAllText(path, "{ 这不是 JSON");

            var comments = new BoardComments();
            comments.AddThread("n1", "他", "这句不能被写进一份读不懂的旧账", agent: false);

            Assert.Throws<IOException>(() => comments.Save(path));
            Assert.Equal("{ 这不是 JSON", File.ReadAllText(path));
        }
        finally
        {
            CleanUp(board);
        }
    }

    [Fact]
    public void Resolving_marks_it_read_and_revision_advances_so_the_layer_repaints()
    {
        var comments = new BoardComments();
        var thread = comments.AddThread("n1", "他", "一", agent: false, threadId: "c1", time: "10:00");

        thread.Unread = true;

        var before = comments.Revision;

        Assert.True(comments.SetResolved(thread.Id, true));
        Assert.False(thread.Unread);
        Assert.True(comments.Revision > before);

        // 同一状态再设一次不算改动：徽标层不该因为一次空点击整层重铺。
        var settled = comments.Revision;

        Assert.False(comments.SetResolved(thread.Id, true));
        Assert.Equal(settled, comments.Revision);
    }
}
