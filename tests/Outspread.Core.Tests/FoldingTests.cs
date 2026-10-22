namespace Outspread.Core.Tests;

/// <summary>
/// 筛选时组里那几枚没命中的便签折到哪儿去——只算画面，不算文档。
/// </summary>
public class FoldingTests
{
    private static (Grouping.Rect, bool) Note(string id, double x, double y, bool matches)
        => (new Grouping.Rect(id, x, y, 100, 60), matches);

    [Fact]
    public void The_first_matching_note_catches_everything_that_did_not_match()
    {
        var plan = Folding.PlanOf([
            Note("a", 0, 0, false),
            Note("b", 200, 100, true),
            Note("c", 400, 300, true),
        ]);

        Assert.Equal("b", plan.AnchorId);
        Assert.Equal(1, plan.FoldedCount);
        Assert.Equal((200d, 100d), (plan.Folds[0].DX, plan.Folds[0].DY));
        Assert.True(plan.Folds[0].Folded);
    }

    [Fact]
    public void Matching_notes_are_left_exactly_where_they_are()
    {
        var plan = Folding.PlanOf([
            Note("a", 0, 0, false),
            Note("b", 200, 100, true),
        ]);

        Assert.False(plan.Folds[1].Folded);
        Assert.Equal((0d, 0d), (plan.Folds[1].DX, plan.Folds[1].DY));
    }

    [Fact]
    public void Nothing_folds_when_no_note_matches()
    {
        // 没有锚点可折进去：那一档整组一起暗着，而不是凭空叠成一叠。
        var plan = Folding.PlanOf([Note("a", 0, 0, false), Note("b", 200, 0, false)]);

        Assert.Null(plan.AnchorId);
        Assert.False(plan.AnythingFolded);
        Assert.All(plan.Folds, fold => Assert.False(fold.Folded));
    }

    [Fact]
    public void Nothing_folds_when_every_note_matches()
    {
        var plan = Folding.PlanOf([Note("a", 0, 0, true), Note("b", 200, 0, true)]);

        Assert.False(plan.AnythingFolded);
        Assert.Null(plan.AnchorId);
    }

    [Fact]
    public void A_folded_note_lands_corner_on_corner_with_its_anchor()
    {
        // 露缝就要多一枚"堆叠步长"的数，而藏起来的东西不该有缝。
        var plan = Folding.PlanOf([
            Note("anchor", 30, 40, true),
            Note("gone", 130, 240, false),
        ]);

        var gone = plan.Folds.First(fold => fold.Id == "gone");

        Assert.Equal(30, gone.DX + 130);
        Assert.Equal(40, gone.DY + 240);
    }

    [Fact]
    public void The_plan_keeps_the_order_it_was_handed()
    {
        var plan = Folding.PlanOf([
            Note("z", 0, 0, false),
            Note("y", 0, 0, true),
            Note("x", 0, 0, false),
        ]);

        Assert.Equal(new[] { "z", "y", "x" }, plan.Folds.Select(fold => fold.Id));
    }

    [Fact]
    public void An_empty_group_folds_nothing_and_names_no_anchor()
    {
        var plan = Folding.PlanOf([]);

        Assert.Empty(plan.Folds);
        Assert.Null(plan.AnchorId);
        Assert.False(plan.AnythingFolded);
    }
}
