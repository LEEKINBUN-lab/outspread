namespace Outspread.Core.Tests;

/// <summary>
/// 排布命令眼里的"一件东西"：一张便签，或一整个组。
/// </summary>
public class GroupingUnitsTests
{
    private static Grouping.Rect Note(string id, double x, double y, double width = 100, double height = 60)
        => new(id, x, y, width, height);

    private static Grouping.Rect Frame(string id, double x, double y, double width, double height)
        => new(id, x, y, width, height);

    [Fact]
    public void A_group_is_one_unit_and_a_loose_note_is_another()
    {
        var frame = Frame("g1", 0, 0, 340, 90);
        var notes = new[] { Note("n1", 10, 10), Note("n2", 120, 10), Note("n3", 500, 500) };

        var units = Grouping.UnitsOf(notes, [frame]);

        Assert.Equal(2, units.Count);
        Assert.Equal("g1", units[0].Id);
        Assert.Equal(new[] { "n1", "n2" }, units[0].MemberIds);
        Assert.Equal(frame, units[0].Box);
        Assert.Equal("n3", units[1].Id);
        Assert.Equal(new[] { "n3" }, units[1].MemberIds);
        Assert.Equal(notes[2], units[1].Box);
    }

    [Fact]
    public void A_group_aligns_by_its_visible_edge_not_by_its_members_hull()
    {
        // 框比成员大一圈（Pad=10）。对齐看的是屏幕上那条边，所以单位矩形必须是框本身。
        var frame = Frame("g1", 90, 40, 220, 80);
        var notes = new[] { Note("n1", 100, 50), Note("n2", 160, 50) };

        var unit = Assert.Single(Grouping.UnitsOf(notes, [frame]));

        Assert.Equal(90, unit.Box.X);
        Assert.Equal(40, unit.Box.Y);
    }

    [Fact]
    public void A_note_in_two_nested_frames_belongs_to_the_inner_one()
    {
        var outer = Frame("g-outer", 0, 0, 900, 700);
        var inner = Frame("g-inner", 100, 100, 200, 120);

        var units = Grouping.UnitsOf([Note("n1", 120, 120)], [outer, inner]);

        Assert.Equal("g-inner", Assert.Single(units).Id);
    }

    [Fact]
    public void Units_come_back_in_the_order_their_first_member_appears()
    {
        // 堆叠与均分读的就是这个次序（z 序），所以它不能按 id 排、也不能按框排。
        var frame = Frame("g1", 0, 0, 340, 90);
        var notes = new[] { Note("n9", 500, 500), Note("n1", 10, 10), Note("n2", 120, 10) };

        var units = Grouping.UnitsOf(notes, [frame]);

        Assert.Equal(new[] { "n9", "g1" }, units.Select(unit => unit.Id));
    }

    [Fact]
    public void Aligning_units_carries_every_member_of_a_group_the_same_distance()
    {
        var frame = Frame("g1", 90, 40, 220, 80);
        var notes = new[] { Note("n1", 100, 50), Note("n2", 160, 60), Note("n3", 0, 0) };
        var before = notes.ToDictionary(note => note.Id);

        var units = Grouping.UnitsOf(notes, [frame]);

        var outcome = Aligning.Align([.. units.Select(unit =>
            new Aligning.Placed(unit.Id, unit.Box.X, unit.Box.Y, unit.Box.Width, unit.Box.Height))], Aligning.Side.Left);

        Assert.True(outcome.Ok);

        var moved = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);

        foreach (var move in outcome.Moves)
        {
            var unit = units.First(candidate => candidate.Id == move.Id);
            var delta = (move.X - unit.Box.X, move.Y - unit.Box.Y);

            foreach (var id in unit.MemberIds)
            {
                moved[id] = (before[id].X + delta.Item1, before[id].Y + delta.Item2);
            }
        }

        // 框从 x=90 挪到 x=0（整块左移 90），里面那两格跟着走：100→10、160→70，
        // 相对彼此一个像素都没动——"整块走"的全部含义。
        Assert.Equal((10d, 50d), moved["n1"]);
        Assert.Equal((70d, 60d), moved["n2"]);

        // 那张散格本来就在最左边，所以它不该被谁带着走。
        Assert.Equal((0d, 0d), moved["n3"]);
    }

    [Fact]
    public void A_group_with_no_members_is_not_invented_out_of_thin_air()
    {
        var units = Grouping.UnitsOf([Note("n1", 0, 0)], [Frame("g-empty", 400, 400, 100, 100)]);

        Assert.Equal(new[] { "n1" }, units.Select(unit => unit.Id));
    }
}
