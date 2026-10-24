namespace Outspread.Core.Tests;

/// <summary>
/// 成员是一张名单，不是一块场地（10-24 他点的那两格 bug 的根）。
/// </summary>
/// <remarks>
/// 名单写在 group 节点的 <c>members</c> 上（规范没有这个字段，读法与退回规则见
/// <see cref="CanvasNode.GroupMemberIds"/> 与 <see cref="GroupMap"/>）。这里钉的是判据本身：
/// 有名单的框**不再靠几何收人**——否则"新建一格正好落在框里"照样当场入组，那一格等于没修。
/// </remarks>
public class GroupMapTests
{
    private static Grouping.Rect Note(string id, double x, double y, double w = 100, double h = 60)
        => new(id, x, y, w, h);

    private static (Grouping.Rect Frame, IReadOnlyList<string>? Members) Frame(string id, IReadOnlyList<string>? ids)
        => (Note(id, 0, 0, 300, 200), ids);

    [Fact]
    public void A_roster_frame_keeps_the_member_it_named_even_when_she_is_far_outside()
    {
        var a = Note("a", 10, 10);
        var b = Note("b", 2_000, 10);
        var map = GroupMap.Build([a, b], [Frame("g", ["a", "b"])]);

        Assert.Equal(["a", "b"], map.MemberIds("g"));
        Assert.Equal("g", map.GroupOf(b)?.Id);
    }

    /// <summary>
    /// 他那格截图：新建的一格落在旧框里就当场长出分组。名单那一档必须回答"它不是成员"，
    /// 哪怕它的中心正正落在框里。
    /// </summary>
    [Fact]
    public void A_note_dropped_inside_a_roster_frame_is_not_a_member()
    {
        var member = Note("a", 10, 10);
        var fresh = Note("new", 120, 90);
        var map = GroupMap.Build([member, fresh], [Frame("g", ["a"])]);

        Assert.Null(map.GroupOf(fresh));
        Assert.Equal(["a"], map.MemberIds("g"));
    }

    [Fact]
    public void A_frame_without_a_roster_still_reads_geometry()
    {
        var inside = Note("a", 120, 90);
        var outside = Note("b", 900, 90);
        var map = GroupMap.Build([inside, outside], [Frame("g", null)]);

        Assert.Equal("g", map.GroupOf(inside)?.Id);
        Assert.Null(map.GroupOf(outside));
        Assert.Equal(["a"], map.MemberIds("g"));
    }

    [Fact]
    public void Names_of_notes_that_are_gone_are_dropped_when_reading()
    {
        var kept = Note("a", 10, 10);
        var map = GroupMap.Build([kept], [Frame("g", ["a", "deleted"])]);

        Assert.Equal(["a"], map.MemberIds("g"));
    }

    [Fact]
    public void A_note_belongs_to_one_group_and_the_first_roster_that_claims_it_wins()
    {
        var a = Note("a", 10, 10);

        // 两个框的名单都写了它：按框的文档顺序，先认到的那个算，而且它不会被两份名单同时搬走。
        var map = GroupMap.Build(
            [a],
            [(Note("g1", 0, 0, 300, 200), ["a"]), (Note("g2", 0, 0, 200, 200), ["a"])]);

        Assert.Equal("g1", map.GroupOf(a)?.Id);
        Assert.Equal(["a"], map.MemberIds("g1"));
        Assert.Empty(map.MemberIds("g2"));
    }

    [Fact]
    public void Members_come_back_in_document_order_not_roster_order()
    {
        var a = Note("a", 10, 10);
        var b = Note("b", 40, 10);

        // 名单写的是 b、a；文档顺序是 a、b ⇒ 叠放次序读出来还是 a、b。
        var map = GroupMap.Build([a, b], [Frame("g", ["b", "a"])]);

        Assert.Equal(["a", "b"], map.MemberIds("g"));
    }

    [Fact]
    public void A_roster_frame_is_one_unit_for_layout_and_a_stray_note_is_its_own()
    {
        var a = Note("a", 10, 10);
        var b = Note("b", 20, 40);
        var stray = Note("c", 5_000, 10);
        var map = GroupMap.Build([a, b, stray], [Frame("g", ["a", "b"])]);

        var units = map.UnitsOf();

        Assert.Equal(2, units.Count);
        Assert.Equal("g", units[0].Id);
        Assert.Equal(["a", "b"], units[0].MemberIds);
        Assert.Equal("c", units[1].Id);
    }

    /// <summary>
    /// 删掉一个框以后，它名单里那些格子不该还挂在一张不存在的表上（几何那一档也不该顺手把它们
    /// 收进别的框——它们本来就散着）。
    /// </summary>
    [Fact]
    public void Removing_the_frame_without_editing_the_roster_leaves_the_notes_free()
    {
        var a = Note("a", 10, 10);
        var map = GroupMap.Build([a], []);

        Assert.Null(map.GroupOf(a));
    }

    [Fact]
    public void HasRoster_tells_the_two_regimes_apart()
    {
        var a = Note("a", 10, 10);

        Assert.True(GroupMap.Build([a], [Frame("g", [])]).HasRoster("g"));
        Assert.False(GroupMap.Build([a], [Frame("g", null)]).HasRoster("g"));
    }
}
