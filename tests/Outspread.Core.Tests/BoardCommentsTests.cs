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
