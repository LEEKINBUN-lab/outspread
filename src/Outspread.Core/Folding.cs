namespace Outspread.Core;

/// <summary>
/// 筛选生效时，一个组里那些<b>没命中</b>的便签折到<b>命中</b>的那格背后。
/// </summary>
/// <remarks>
/// <para>
/// 这一份只算画面，不碰文档。筛选是视图层的事（<c>NodeVisual</c> 里那条"压暗但仍在原处，绝不移动、
/// 也不改文档"），而"把三格并到一处"一旦写进文件，一次筛选就把便签的真位置同步给了手机——
/// 所以这里给的是<b>位移量</b>，不是新坐标。
/// </para>
/// <para>
/// 折进去的格子与锚点<b>左上角重合</b>：不发明"堆叠步长"这种数。藏起来的东西本来就不该露缝，
/// 而露出来的那一档由框上那枚"折起 N 格"的牌子负责说。
/// </para>
/// </remarks>
public static class Folding
{
    /// <summary>一枚便签在这一发里的下场。</summary>
    /// <param name="DX">要滑向锚点的水平位移（不折就是 0）。</param>
    /// <param name="DY">同上，垂直。</param>
    /// <param name="Folded">False = 原地原样；True = 滑过去并淡出。</param>
    public readonly record struct Fold(string Id, double DX, double DY, bool Folded);

    /// <param name="AnchorId">接住没命中的那些：命中者里的第一格（文档顺序）。一个都没命中时是 null。</param>
    /// <param name="Folds">每位成员的下场，顺序与进来时一致（z 序不许被折叠打乱）。</param>
    /// <param name="FoldedCount">折起来几格——框上那枚牌子写的就是它。</param>
    public readonly record struct Plan(string? AnchorId, IReadOnlyList<Fold> Folds, int FoldedCount)
    {
        public bool AnythingFolded => FoldedCount > 0;
    }

    /// <summary>
    /// 一组便签连同各自的命中判定 → 折叠计划。
    /// </summary>
    /// <remarks>
    /// 两种边界不折：一个都没命中（没有锚点可折进去，整组一起暗着）、全命中（没有东西要藏）。
    /// </remarks>
    public static Plan PlanOf(IReadOnlyList<(Grouping.Rect Member, bool Matches)> members)
    {
        var anchor = -1;
        var hits = 0;

        for (var i = 0; i < members.Count; i++)
        {
            if (!members[i].Matches)
            {
                continue;
            }

            if (anchor < 0)
            {
                anchor = i;
            }

            hits++;
        }

        if (anchor < 0 || hits == members.Count)
        {
            return new Plan(null, [.. members.Select(item => new Fold(item.Member.Id, 0, 0, false))], 0);
        }

        var target = members[anchor].Member;
        var folds = new List<Fold>(members.Count);
        var folded = 0;

        for (var i = 0; i < members.Count; i++)
        {
            var (member, matches) = members[i];

            if (matches)
            {
                folds.Add(new Fold(member.Id, 0, 0, false));

                continue;
            }

            folds.Add(new Fold(member.Id, target.X - member.X, target.Y - member.Y, true));
            folded++;
        }

        return new Plan(target.Id, folds, folded);
    }
}
