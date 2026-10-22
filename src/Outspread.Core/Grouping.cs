namespace Outspread.Core;

/// <summary>
/// Groups: a rectangle on the board, and the rule that a note click picks the one note while a
/// click on the frame's name plate or dashed edge picks every note the frame holds.
/// </summary>
/// <remarks>
/// <para>
/// JSON Canvas 1.0 has no member list on a group — the spec only says a group is a visual
/// container — so membership is <b>geometric</b>: a note belongs to the group whose rectangle
/// holds its centre. That is the prototype's rule (<c>membersOf</c>), and it has one consequence
/// the data model cannot hide: dragging a note out of the frame leaves the group, immediately and
/// silently. The port has to make that visible on the surface rather than pretend membership.
/// </para>
/// <para>
/// The click rule is his own words from the 10-22 round: 单击组里的便签＝只选中那枚便签；
/// 点击组的文案或边缘的虚线＝选中组里的所有便签（不是整个组的范围）.组是一个关系，不是
/// 一块场地——所以选中的永远是格子的集合，框本身从不进选中。早期原型里"第一次点必中组、
/// 第二次点中格"的两层规矩被这一句取代，那枚 <c>groupFocus</c> 的状态机也随之作废。
/// </para>
/// </remarks>
public static class Grouping
{
    /// <summary>World pixels the frame grows past its members' bounding box (Obsidian pads too).</summary>
    public const double Pad = 10;

    /// <summary>Anything with a rectangle: a note, a link, an image, or a group itself.</summary>
    public readonly record struct Rect(string Id, double X, double Y, double Width, double Height)
    {
        public double Right => X + Width;

        public double Bottom => Y + Height;

        public double CenterX => X + (Width / 2);

        public double CenterY => Y + (Height / 2);
    }

    /// <summary>The frame to draw around a set of notes, padded outward on all four sides.</summary>
    public static Rect Enclose(string id, IReadOnlyList<Rect> members)
    {
        if (members.Count == 0)
        {
            return new Rect(id, 0, 0, 0, 0);
        }

        var left = members.Min(member => member.X) - Pad;
        var top = members.Min(member => member.Y) - Pad;
        var right = members.Max(member => member.Right) + Pad;
        var bottom = members.Max(member => member.Bottom) + Pad;

        return new Rect(id, left, top, right - left, bottom - top);
    }

    /// <summary>A note is in a group when the group's rectangle holds the note's centre.</summary>
    public static bool Holds(this Rect group, Rect member)
        => member.CenterX >= group.X && member.CenterX <= group.Right
        && member.CenterY >= group.Y && member.CenterY <= group.Bottom;

    /// <summary>
    /// The group a note sits in — the <b>innermost</b> one when frames nest, so a group inside a
    /// group behaves like a member of the smaller frame rather than both at once.
    /// </summary>
    public static Rect? GroupOf(Rect member, IReadOnlyList<Rect> groups)
        => groups.Where(group => group.Holds(member))
            .OrderBy(group => group.Width * group.Height)
            .Cast<Rect?>()
            .FirstOrDefault();

    public static IReadOnlyList<Rect> Members(Rect group, IReadOnlyList<Rect> candidates)
        => candidates.Where(member => group.Holds(member)).ToList();

    /// <summary>
    /// 一次排布眼里的"一件东西"：一张便签，或一整个组。
    /// </summary>
    /// <param name="Id">组的话是那个框的 id，散格的话是那张格子的 id。</param>
    /// <param name="Box">这一件占的矩形：组用框本身（它带着 Pad，也是屏幕上那条边），散格用它自己。</param>
    /// <param name="MemberIds">搬这一件时要一起搬走的格子（散格就是它一枚）。顺序＝文档顺序。</param>
    public readonly record struct Unit(string Id, Rect Box, IReadOnlyList<string> MemberIds);

    /// <summary>
    /// 把一批便签归成单位：每张便签归到<b>最内层</b>那个框底下，没有框就是它自己一件。
    /// </summary>
    /// <remarks>
    /// 组框自己不是一种"东西"——它是成员包围盒的派生物（见 <see cref="Enclose"/>）。把它和它的成员
    /// 一起交给对齐，等于让一个容器去和它装的东西排队：实测那一发是 Ctrl+A＋左对齐，
    /// 25 个节点（含框）全压到同一列，框留在原地，而邻居便签的中心落进了框里被当场算成成员。
    /// 单位的顺序是成员第一次出现的顺序，也就是文档（z）顺序——堆叠与均分读的就是这个次序。
    /// </remarks>
    public static IReadOnlyList<Unit> UnitsOf(IReadOnlyList<Rect> notes, IReadOnlyList<Rect> groups)
    {
        var frames = groups.ToDictionary(group => group.Id, StringComparer.Ordinal);
        var order = new List<string>();
        var buckets = new Dictionary<string, List<Rect>>(StringComparer.Ordinal);

        foreach (var note in notes)
        {
            var key = GroupOf(note, groups)?.Id ?? note.Id;

            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = [];
                buckets[key] = bucket;
                order.Add(key);
            }

            bucket.Add(note);
        }

        return order.ConvertAll(key =>
        {
            var members = buckets[key];

            return new Unit(
                key,
                frames.TryGetValue(key, out var frame) ? frame : members[0],
                members.ConvertAll(member => member.Id));
        });
    }

    /// <summary>
    /// 按在组的<b>名牌或边缘虚线</b>上：组里所有便签进选中。选中的永远是格子的集合，框本身从不
    /// 进选中——框进选中就等于"整个组的范围变蓝"，而那正是这一版要消灭的语义。
    /// </summary>
    /// <remarks>
    /// 按在便签上没有这条判定可问：单击就是一枚，Core 不再管选中层次。曾经的两层规矩
    /// （<c>OnNotePressed</c> ＋ <c>DragSet</c> ＋ <c>focusedGroup</c> 状态机）随 10-22 的新语义
    /// 一并删除——被证伪的契约留在原地，下一次实现就会照着它长回来。
    /// </remarks>
    public static IReadOnlyList<string> OnFramePressed(Rect group, IReadOnlyList<Rect> notes)
        => [.. notes.Where(member => group.Holds(member)).Select(member => member.Id)];

    /// <summary>两个矩形有没有真的碰上（一角进入就算；边贴边零面积不算）。</summary>
    public static bool Intersects(this Rect left, Rect right)
        => left.X < right.Right && right.X < left.Right
            && left.Y < right.Bottom && right.Y < left.Bottom;

    /// <summary>叠多少才算"明显"：按两格相交的那块面积占<b>小格</b>的比例算，8% 以下只算擦肩。</summary>
    public const double SuggestMinOverlap = 0.08;

    /// <summary>
    /// 挪开手的那一拍：这一格把自己送进了哪几枚的身边。"叠在一起"用眼界的矩形相交来量——
    /// 提议成组要的就是"看上去堆成一堆"那一形，不是中心距离（距离量不出"堆"，只量得出"靠近"）。
    /// </summary>
    public static IReadOnlyList<Rect> TouchedBy(
        Rect moved,
        IReadOnlyList<Rect> notes,
        double minOverlap = SuggestMinOverlap)
        => [.. notes.Where(other => other.Id != moved.Id
            && OverlapOf(moved, other) >= minOverlap)];

    /// <summary>相交面积 ÷ 较小格的面积（0＝没碰上）。</summary>
    public static double OverlapOf(Rect left, Rect right)
    {
        var width = Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X);
        var height = Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y);

        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var smaller = Math.Min(left.Width * left.Height, right.Width * right.Height);

        return smaller <= 0 ? 0 : (width * height) / smaller;
    }
}
