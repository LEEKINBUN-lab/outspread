using System.Text.Json.Nodes;
using Outspread.Core;

namespace Outspread.Core.Tests;

/// <summary>
/// 复制下来那份片段的形状：拆得下来、贴得回去、给外面看的几行字说人话，以及"这份还算不算刚
/// 复制的那一发"那笔账。
/// </summary>
/// <remarks>
/// 这里不碰剪贴板（那是界面那一层，且需要一台有剪贴板的机器）：判的是纯数据的那一半——
/// 一次 Ctrl+C 到底带走了什么。
/// </remarks>
public class BoardFragmentTests
{
    private const string Board = @"D:\boards\源板.canvas";

    private static CanvasDocument Board1(params (string Id, string Text)[] notes)
    {
        var document = CanvasDocument.CreateNew();

        foreach (var (id, text) in notes)
        {
            document.Add(CanvasNodeKind.Text, 10, 20, 260, 90, id: id).Text = text;
        }

        return document;
    }

    private static string? Take(CanvasDocument document, out string plainText, params string[] ids)
        => BoardFragment.From(document, ids, Board, out plainText);

    [Fact]
    public void 选中的那几格拆下来贴到另一块板上还是那几格()
    {
        var source = Board1(("ua", "第一格"), ("ub", "第二格"));

        var fragment = Take(source, out _, "ua");

        Assert.NotNull(fragment);

        var target = CanvasDocument.CreateNew();

        target.AppendNode(BoardFragment.NodesOf(fragment)[0]);

        var pasted = Assert.Single(target.Nodes);

        Assert.Equal("第一格", pasted.Text);
        Assert.Equal(10, pasted.X);
        Assert.Equal(260, pasted.Width);
        // 新板上那个名字还空着就留着：agent 与历史快照指着的那个 id 跟得过来。
        Assert.Equal("ua", pasted.Id);
    }

    [Fact]
    public void 同一份片段贴两次不会撞同一个名字()
    {
        var fragment = Take(Board1(("ua", "一格")), out _, "ua");

        var target = CanvasDocument.CreateNew();

        var node = BoardFragment.NodesOf(fragment)[0];
        var first = target.AppendNode(node);
        var second = target.AppendNode(node);

        // 第一发保留原名字，第二发撞上自己 ⇒ 另铸一个。"再来一份"的语义不能靠调用方记得改名。
        Assert.Equal("ua", first.Id);
        Assert.NotEqual("ua", second.Id);
        Assert.Equal(2, target.Nodes.Count);
    }

    [Fact]
    public void 组里有一格被选中那圈框跟着走()
    {
        var document = Board1(("ua", "组里第一格"), ("ub", "组里第二格"), ("uc", "外面的"));

        var group = document.Add(CanvasNodeKind.Group, 0, 0, 600, 200, id: "ug");

        group.Label = "一组事";
        document.Find("ua")!.SetString("group", "ug");
        document.Find("ub")!.SetString("group", "ug");

        var fragment = Take(document, out _, "ua");

        var nodes = BoardFragment.NodesOf(fragment);

        // 半选也要有框：只搬成员会把那圈虚线丢在原地，贴过去的一堆格子就不再是一件东西。
        Assert.Equal(2, nodes.Count);
        Assert.Contains(nodes, node => node["type"]?.GetValue<string>() == "group");
    }

    [Fact]
    public void 只有一头在里的线不带过去()
    {
        var document = Board1(("ua", "一"), ("ub", "二"), ("uc", "三"));

        document.AddEdge("ua", "ub");
        document.AddEdge("ub", "uc");

        var fragment = Take(document, out _, "ua", "ub");
        var inside = BoardFragment.NodesOf(fragment).ToDictionary(
            node => node["id"]!.GetValue<string>(),
            node => node["id"]!.GetValue<string>());

        Assert.Single(BoardFragment.EdgesOf(fragment, inside));

        // 只带 ua：那根 ub→uc 的线有一头不在这一堆里，贴过去就指着不存在的格子。
        var partial = Take(document, out _, "ua");

        Assert.Empty(BoardFragment.EdgesOf(partial, new Dictionary<string, string> { ["ua"] = "ua" }));
    }

    [Fact]
    public void 给外面看的几行字一行一格并且说清了图和链接()
    {
        var document = Board1(("ua", "多行\n第二行"));

        var picture = document.Add(CanvasNodeKind.File, 0, 0, 200, 100, id: "uf");
        picture.FilePath = "assets/img_a.png";

        var link = document.Add(CanvasNodeKind.Link, 0, 0, 200, 60, id: "ul");
        link.Url = "https://jsoncanvas.org";

        Take(document, out var plain, "ua", "uf", "ul");

        Assert.Equal("多行 第二行", plain.Split('\n')[0] + " " + plain.Split('\n')[1]);

        // 图与链接不能贴成一串空：别的程序至少知道复制的是指着什么的东西。
        Assert.Contains("assets/img_a.png", plain);
        Assert.Contains("https://jsoncanvas.org", plain);
    }

    [Fact]
    public void 一段正常的文字不会被当成片段()
    {
        Assert.False(BoardFragment.LooksLikeFragment(null));
        Assert.False(BoardFragment.LooksLikeFragment(string.Empty));
        Assert.False(BoardFragment.LooksLikeFragment("从浏览器里复制的一段话"));
        Assert.False(BoardFragment.LooksLikeFragment("{\"nodes\":[]}"));
        Assert.False(BoardFragment.LooksLikeFragment("{\"nodes\":\"不是数组\"}"));
        Assert.False(BoardFragment.LooksLikeFragment("{坏掉的 JSON"));

        // 认得的是"有 nodes 且不为空"——人家正常贴进来的一段 JSON 不该被当成板子。
        Assert.False(BoardFragment.LooksLikeFragment("{\"other\":1}"));
        Assert.True(BoardFragment.LooksLikeFragment("{\"nodes\":[{\"id\":\"u1\",\"type\":\"text\"}]}"));
    }

    [Fact]
    public void 片段带着它对剪贴板的那笔账()
    {
        var fragment = Take(Board1(("ua", "一句话")), out var plain, "ua");

        Assert.Equal(plain, BoardFragment.ExpectedTextOf(fragment));

        // 一格字都没有的一堆格子也要有一句可对账的话——空的那一发永远贴不出来。
        var blank = Board1();

        blank.Add(CanvasNodeKind.Text, 0, 0, 200, 60, id: "ub");

        var emptyFragment = Take(blank, out var blankText, "ub");

        Assert.Equal("（空格子）", blankText);
        Assert.Equal(blankText, BoardFragment.ExpectedTextOf(emptyFragment));
    }

    [Fact]
    public void 一张没有图的板子拆不出片段也不报错()
    {
        var fragment = Take(Board1(("ua", "一")), out _, "不存在的id");

        Assert.Null(fragment);
    }

    [Fact]
    public void 片段本身是一份读得懂的画布文件()
    {
        var fragment = Take(Board1(("ua", "一"), ("ub", "二")), out _, "ua", "ub");

        // 认 JSON Canvas 的程序不用学新格式；我们自己的那两个键它不认识也不会崩。
        var parsed = CanvasDocument.Parse(fragment!);

        Assert.Equal(2, parsed.Nodes.Count);
        Assert.Equal("一", parsed.Nodes[0].Text);
        Assert.Equal(Board, BoardFragment.SourceBoardOf(fragment));
    }
}
