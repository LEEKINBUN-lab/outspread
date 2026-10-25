using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

/// <summary>Edges: which face an end attaches to, and what a change there costs in the patch.</summary>
public class EdgeTests
{
    private static CanvasNode Card(double x, double y, double w = 200, double h = 100)
        => CanvasDocument.CreateNew().Add(CanvasNodeKind.Text, x, y, w, h);

    [Theory]
    [InlineData(500, 50, CanvasEdge.Right)]      // 目标在右边 ⇒ 右边
    [InlineData(-100, 50, CanvasEdge.Left)]
    [InlineData(100, -200, CanvasEdge.Top)]
    [InlineData(100, 300, CanvasEdge.Bottom)]
    [InlineData(400, 260, CanvasEdge.Right)]    // 斜着够到两个面时，取中点更近的那一个（右边中点 200,50 比底边 100,100 近）
    public void A_dropped_end_picks_the_face_whose_midpoint_is_closest(double worldX, double worldY, string expected)
    {
        Assert.Equal(expected, CanvasEdge.NearestSide(Card(0, 0), worldX, worldY));
    }

    [Fact]
    public void A_fresh_edge_records_the_faces_it_was_drawn_with()
    {
        var document = CanvasDocument.CreateNew();
        var left = document.Add(CanvasNodeKind.Text, 0, 0, 200, 100);
        var right = document.Add(CanvasNodeKind.Text, 600, 0, 200, 100);

        var edge = document.AddEdge(left.Id, right.Id);

        // 写死面，而不是每次现算：否则拖过中点之后线会自己跳到另一面去。
        Assert.Equal(CanvasEdge.Right, edge.FromSide);
        Assert.Equal(CanvasEdge.Left, edge.ToSide);
    }

    [Fact]
    public void Moving_an_end_to_the_face_it_already_has_is_not_a_change()
    {
        var document = CanvasDocument.CreateNew();
        var left = document.Add(CanvasNodeKind.Text, 0, 0, 200, 100);
        var right = document.Add(CanvasNodeKind.Text, 600, 0, 200, 100);
        var edge = document.AddEdge(left.Id, right.Id);

        Assert.False(document.SetEdgeSide(edge.Id, fromEnd: true, CanvasEdge.Right));
        Assert.True(document.SetEdgeSide(edge.Id, fromEnd: true, CanvasEdge.Bottom));
        Assert.Equal(CanvasEdge.Bottom, document.Edges[0].FromSide);
        Assert.True(document.SetEdgeSide(edge.Id, fromEnd: false, CanvasEdge.Top));
        Assert.False(document.SetEdgeSide("没有这根", fromEnd: true, CanvasEdge.Top));
    }

    [Fact]
    public void Dragging_an_end_onto_another_card_moves_ownership_and_the_face()
    {
        var document = CanvasDocument.CreateNew();
        var left = document.Add(CanvasNodeKind.Text, 0, 0, 200, 100);
        var right = document.Add(CanvasNodeKind.Text, 600, 0, 200, 100);
        var above = document.Add(CanvasNodeKind.Text, 200, -300, 200, 100);
        var edge = document.AddEdge(left.Id, right.Id);

        // 松手时那一头停在另一格的上边：归属和面一起换。
        Assert.True(document.MoveEdgeEnd(edge.Id, fromEnd: false, above.Id, CanvasEdge.Bottom));

        Assert.Equal(left.Id, document.Edges[0].FromNode);
        Assert.Equal(above.Id, document.Edges[0].ToNode);
        Assert.Equal(CanvasEdge.Bottom, document.Edges[0].ToSide);

        // 已经在这一格上＝不是改动（哪怕换面，那是 SetEdgeSide 的事）；不存在的边、组节点都不动文档。
        Assert.False(document.MoveEdgeEnd(edge.Id, fromEnd: false, above.Id, CanvasEdge.Bottom));
        Assert.False(document.MoveEdgeEnd(edge.Id, fromEnd: false, above.Id, CanvasEdge.Top));
        Assert.False(document.MoveEdgeEnd("没有这根", fromEnd: true, right.Id, CanvasEdge.Left));

        var group = document.Add(CanvasNodeKind.Group, 0, 0, 900, 900);
        Assert.False(document.MoveEdgeEnd(edge.Id, fromEnd: true, group.Id, CanvasEdge.Top));
    }

    [Fact]
    public void A_face_change_travels_as_a_replacement_and_undoes_back()
    {
        var before = CanvasDocument.CreateNew();
        var left = before.Add(CanvasNodeKind.Text, 0, 0, 200, 100);
        var right = before.Add(CanvasNodeKind.Text, 600, 0, 200, 100);
        var edge = before.AddEdge(left.Id, right.Id);

        var after = CanvasDocument.Parse(before.ToJson());
        after.SetEdgeSide(edge.Id, fromEnd: true, CanvasEdge.Bottom);

        var patch = DocDiff.Between(before, after);

        // 换面记成"删掉那根、放回改过的这根"：落盘结果一样，撤销栈里是一步。
        Assert.Single(patch.EdgeRemovals);
        Assert.Single(patch.EdgeAdditions);
        Assert.Equal(2, patch.NodeCount);
        Assert.False(patch.IsEmpty);

        var target = CanvasDocument.Parse(before.ToJson());
        PatchApplier.Apply(target, patch);

        Assert.Equal(CanvasEdge.Bottom, target.Edges[0].FromSide);

        PatchApplier.Apply(target, DocDiff.Inverse(before, after));

        Assert.Equal(CanvasEdge.Right, target.Edges[0].FromSide);
    }

    [Fact]
    public void An_added_edge_survives_a_round_trip_and_a_removed_one_does_not_come_back()
    {
        var document = CanvasDocument.CreateNew();
        var a = document.Add(CanvasNodeKind.Text, 0, 0, 200, 100);
        var b = document.Add(CanvasNodeKind.Text, 600, 0, 200, 100);
        var edge = document.AddEdge(a.Id, b.Id);

        var reread = CanvasDocument.Parse(document.ToJson());

        Assert.Single(reread.Edges);
        Assert.Equal(edge.Id, reread.Edges[0].Id);
        Assert.Equal(b.Id, reread.Edges[0].ToNode);

        reread.RemoveEdge(edge.Id);

        Assert.Empty(CanvasDocument.Parse(reread.ToJson()).Edges);
    }

    [Fact]
    public void The_control_arm_is_clamped_and_the_head_follows_the_tangent()
    {
        Assert.Equal(28, EdgeGeometry.ControlArm(10));        // 近处不塌成一个点
        Assert.Equal(45, EdgeGeometry.ControlArm(100));
        Assert.Equal(180, EdgeGeometry.ControlArm(4000));      // 远处不甩出屏幕

        // 一根从右边水平进来的线：头朝右，两翼对称地退回去。
        var (tipX, tipY, leftX, leftY, rightX, rightY) = EdgeGeometry.ArrowHead(100, 50, 80, 50);

        Assert.Equal(100, tipX);
        Assert.Equal(91, leftX, 3);
        Assert.Equal(91, rightX, 3);
        Assert.Equal(54.5, leftY, 3);
        Assert.Equal(45.5, rightY, 3);

        // 退化情形（控制点与端点重合）不许产生 NaN：一根 NaN 的线会让整层画不出来。
        var degenerate = EdgeGeometry.ArrowHead(10, 10, 10, 10);

        Assert.False(double.IsNaN(degenerate.LeftX));
        Assert.Equal(10, degenerate.TipX);
    }

    [Fact]
    public void 拉线挑好的两面写得进文件也读得回来()
    {
        var document = CanvasDocument.CreateNew();

        var left = document.Add(CanvasNodeKind.Text, 0, 0, 200, 80);
        var right = document.Add(CanvasNodeKind.Text, 600, 400, 200, 80);

        // 自动那一条会选 e/w；这里强行要"从右边出、从上边进"，它必须原样落盘。
        document.AddEdge(left.Id, right.Id, "s", "n");

        var again = CanvasDocument.Parse(document.ToJson(), "board.canvas");

        var edge = Assert.Single(again.Edges);

        Assert.Equal("s", edge.FromSide);
        Assert.Equal("n", edge.ToSide);
    }

    [Fact]
    public void 留空的两面仍然交给自动朝向()
    {
        var document = CanvasDocument.CreateNew();

        var left = document.Add(CanvasNodeKind.Text, 0, 0, 200, 80);
        var right = document.Add(CanvasNodeKind.Text, 600, 0, 200, 80);

        var auto = document.AddEdge(left.Id, right.Id);
        var picked = document.AddEdge(right.Id, left.Id, CanvasEdge.Left, CanvasEdge.Right);

        // 自动那一支朝向对方：右边出去、左边进来。挑的那一支不许被改写。
        Assert.Equal(CanvasEdge.Right, auto.FromSide);
        Assert.Equal(CanvasEdge.Left, auto.ToSide);
        Assert.Equal(CanvasEdge.Left, picked.FromSide);
        Assert.Equal(CanvasEdge.Right, picked.ToSide);
    }

    [Fact]
    public void A_click_near_the_drawn_curve_is_close_and_a_click_off_it_is_far()
    {
        // 一条从 (0,0) 到 (200,0)、控制点把它拱到 y≈60 的曲线。
        const double x1 = 0, y1 = 0, c1x = 60, c1y = 80, c2x = 140, c2y = 80, x2 = 200, y2 = 0;

        // t=0.5 处曲线自己在 (100,60)；正中那一点到曲线的距离应当≈0。
        var onCurve = EdgeGeometry.DistanceToCurve(100, 60, x1, y1, c1x, c1y, c2x, c2y, x2, y2);

        Assert.True(onCurve < 0.2, $"曲线中点量到 {onCurve:0.###}，应≈0");

        // 往下 20px 的那一点离曲线也应该≈20（采样误差容许 0.5）。
        var beside = EdgeGeometry.DistanceToCurve(100, 80, x1, y1, c1x, c1y, c2x, c2y, x2, y2);

        Assert.InRange(beside, 19.5, 20.5);

        // 端点附近：曲线之外 10px 的落点也有距离读数，不会被判成"在线上"。
        Assert.True(EdgeGeometry.DistanceToCurve(-10, 0, x1, y1, c1x, c1y, c2x, c2y, x2, y2) > 9.5);
    }

    [Fact]
    public void The_delete_preview_red_is_distinct_from_wire_and_selection()
    {
        foreach (var dark in new[] { false, true })
        {
            var red = EdgeGeometry.DeleteColour(dark);
            var wire = EdgeGeometry.WireColour(dark);
            var selected = EdgeGeometry.SelectedColour(dark);

            Assert.True(red.R > red.G + 100 && red.R > red.B + 100, $"删除色不够红：{red.R},{red.G},{red.B}");
            Assert.True(
                Math.Abs(red.R - wire.R) + Math.Abs(red.G - wire.G) + Math.Abs(red.B - wire.B) > 150,
                "删除红和线灰太近");
            Assert.True(
                Math.Abs(red.R - selected.R) + Math.Abs(red.G - selected.G) + Math.Abs(red.B - selected.B) > 150,
                "删除红和选中蓝太近");
        }
    }
}
