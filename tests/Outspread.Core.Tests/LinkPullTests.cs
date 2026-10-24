using Outspread.Core;

namespace Outspread.Core.Tests;

/// <summary>
/// 拉线态的吸附几何。每条都用能手算的坐标写死期望值——"吸得准不准"在这一层量，不开窗口。
/// </summary>
public sealed class LinkPullTests
{
    private static Grouping.Rect Note(string id, double x, double y, double w, double h)
        => new(id, x, y, w, h);

    [Theory]
    [InlineData(50, -30, CanvasEdge.Top)]
    [InlineData(50, 90, CanvasEdge.Bottom)]
    [InlineData(-20, 30, CanvasEdge.Left)]
    [InlineData(120, 30, CanvasEdge.Right)]
    public void 四边中心各吸自己那条(double px, double py, string expected)
        => Assert.Equal(expected, LinkPull.SideNearest(Note("a", 0, 0, 100, 60), px, py));

    [Fact]
    public void 落在格子里就一定吸它()
    {
        var notes = new[] { Note("a", 0, 0, 100, 60), Note("b", 300, 0, 100, 60) };

        // 指针在 a 的正中心：离四条边都是 30–50，但它在 a 里面 ⇒ 该是 a，不能被半径规则放走。
        var hit = LinkPull.Nearest(notes, 50, 30, null, LinkPull.Reach(1));

        Assert.NotNull(hit);
        Assert.Equal("a", hit!.Value.NodeId);
    }

    [Fact]
    public void 格子外只在半径内才吸()
    {
        var notes = new[] { Note("b", 300, 0, 100, 60) };

        // b 的右边中心是 (400,30)：距 20 ⇒ 吸住且报 right；距 200 ⇒ 谁都不吸。
        var near = LinkPull.Nearest(notes, 420, 30, null, LinkPull.Reach(1));
        var far = LinkPull.Nearest(notes, 600, 30, null, LinkPull.Reach(1));

        Assert.NotNull(near);
        Assert.Equal("b", near!.Value.NodeId);
        Assert.Equal(CanvasEdge.Right, near.Value.Side);
        Assert.Null(far);
    }

    [Fact]
    public void 起点自己不参与()
    {
        var notes = new[] { Note("a", 0, 0, 100, 60), Note("b", 140, 0, 100, 60) };

        // 两枚并排，指针压在 a 上：排除 a 之后只能报 b 的左那条边（或者超半径就 null），绝不报 a。
        var hit = LinkPull.Nearest(notes, 50, 30, "a", LinkPull.Reach(1));

        Assert.True(hit is null || hit.Value.NodeId != "a");
    }

    [Fact]
    public void 两枚都够近时谁近算谁()
    {
        var notes = new[] { Note("a", 0, 0, 100, 60), Note("b", 130, 0, 100, 60) };

        // 指针 (125,30)：a 的右边中心 (100,30) 距 25，b 的左边中心 (130,30) 距 5 ⇒ b。
        var hit = LinkPull.Nearest(notes, 125, 30, null, LinkPull.Reach(1));

        Assert.NotNull(hit);
        Assert.Equal("b", hit!.Value.NodeId);
        Assert.Equal(CanvasEdge.Left, hit.Value.Side);
    }

    [Theory]
    [InlineData(1.0, 44)]
    [InlineData(1.25, 35.2)]   // 放大后世界像素要跟着缩，否则"粘不住"
    [InlineData(0.5, 88)]      // 缩小则放宽
    [InlineData(0.0, 44)]      // 拿不到缩放不许除零
    public void 吸附半径按缩放归一(double zoom, double expected)
        => Assert.Equal(expected, LinkPull.Reach(zoom), 9);

    [Fact]
    public void 零尺寸的格子不参与()
        => Assert.Null(LinkPull.Nearest([Note("ghost", 0, 0, 0, 0)], 50, 30, null, LinkPull.Reach(1)));
}
