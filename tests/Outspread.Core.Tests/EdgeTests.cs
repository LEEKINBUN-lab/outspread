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
}
