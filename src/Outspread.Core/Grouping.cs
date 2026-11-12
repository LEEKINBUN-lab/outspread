namespace Outspread.Core;

/// <summary>
/// Groups: a rectangle on the board, and the rule that a note click picks the one note while a
/// click on the frame's name plate or dashed edge picks every note the frame holds.
/// </summary>
/// <remarks>
/// <para>
/// 成员这件事有两条并存的读法，见 <see cref="GroupMap"/>：框上写着 <c>members</c> 名单就用名单，
/// 没写才退回"中心落在框里就算成员"的几何。下面这几个静态函数是**几何那一档**——它们仍然被
/// <see cref="GroupMap"/> 叫去服务没有名单的框，也服务提议成组、参考线这类"看上去堆在一起"的判断。
/// 直接拿它们回答"这格在哪个组里"是错的：那正是 10-24 他报的两格 bug 的来源（新建在框里当场入组、
/// 拖走一格把框拉长到吞掉别人）。
/// </para>
/// <para>
/// JSON Canvas 1.0 的 group 只定义 <c>label/background/backgroundStyle</c>，没有成员字段，规范对
/// 未知字段也没有前向兼容承诺（本仓早查过，记在 docs/canvas-format.md）。名单为什么仍然写成一份
/// 未知字段、以及丢了会退回到哪一档，写在 <see cref="CanvasNode.GroupMemberIds"/>。
/// </para>
/// <para>
/// 点击那条规矩是他自己的话，10-22 那一轮定的：单击组里的便签＝只选中那枚便签；点击组的文案或
/// 边缘的虚线＝选中组里的所有便签（不是整个组的范围）。组是一个关系，不是一块场地——所以选中的
/// 永远是格子的集合，框本身从不进选中。早期原型里"第一次点必中组、第二次点中格"的两层规矩被这一句
/// 取代，那枚 <c>groupFocus</c> 的状态机也随之作废。
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

/// <summary>
/// 谁在哪个组里：**显式成员名单优先**，那张表不在的框才退回几何包含。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要有这张表（10-24 他点的两格）：把几何当成员表，"新建一格正好落在某个框里"会当场入组，
/// "把一格里成员拖远"会让框按包围盒长到吞掉路上别人——两格都是同一句话的两个症状。成员是一张
/// 名单，不是一块场地；名单写在 group 节点的 <c>members</c> 上（见
/// <see cref="CanvasNode.GroupMemberIds"/>，规范没有这个字段，代价与退回规则写在那里）。
/// </para>
/// <para>
/// 一条硬规矩：**有名单的框不再靠几何收人**。否则新格落在显式框里仍会被算成成员，这一改等于没改。
/// 没有名单的框（别的工具写的、10-24 之前存的）照旧按几何——那才是它唯一能有的语义。
/// </para>
/// <para>
/// 11-12 起名单里也可以写**组的 id**：一个组能是另一个组的成员（嵌套分组）。读的时候按框的
/// 文档顺序定归属、一格只归一个上层组；写坏成环的名单在这里被拆开（环上后处理到的那个成员
/// 不再进名单），画面上不会出现互相包着、永远量不完的两只框。
/// </para>
/// </remarks>
public sealed class GroupMap
{
    private readonly Dictionary<string, string> _noteToGroup;
    private readonly Dictionary<string, List<string>> _members;
    private readonly Dictionary<string, Grouping.Rect> _frames;
    private readonly List<Grouping.Rect> _frameOrder;
    private readonly IReadOnlyList<Grouping.Rect> _notes;

    /// <summary>没有名单的那些框：只有它们还按几何收人。</summary>
    private readonly IReadOnlyList<Grouping.Rect> _openFrames;

    private GroupMap(
        Dictionary<string, string> noteToGroup,
        Dictionary<string, List<string>> members,
        Dictionary<string, Grouping.Rect> frames,
        List<Grouping.Rect> frameOrder,
        IReadOnlyList<Grouping.Rect> notes,
        IReadOnlyList<Grouping.Rect> openFrames)
    {
        _noteToGroup = noteToGroup;
        _members = members;
        _frames = frames;
        _frameOrder = frameOrder;
        _notes = notes;
        _openFrames = openFrames;
    }

    /// <param name="notes">板上所有便签（顺序＝文档顺序，也就是叠放次序）。</param>
    /// <param name="frames">每个框配它的名单；null＝这张表不在，退回几何。</param>
    public static GroupMap Build(
        IReadOnlyList<Grouping.Rect> notes,
        IReadOnlyList<(Grouping.Rect Frame, IReadOnlyList<string>? Members)> frames)
    {
        var live = new HashSet<string>(notes.Select(note => note.Id), StringComparer.Ordinal);
        var byId = new Dictionary<string, Grouping.Rect>(StringComparer.Ordinal);
        var frameOrder = new List<Grouping.Rect>();
        var raw = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (frame, ids) in frames)
        {
            live.Add(frame.Id);
            byId[frame.Id] = frame;
            frameOrder.Add(frame);
            raw[frame.Id] = ids ?? [];
        }

        // 外层是不是（直接或穿过几层）装着内层：只用来拆掉写坏的环，不改任何正常名单。
        bool Contains(string outerId, string innerId)
        {
            var pending = new Stack<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            pending.Push(outerId);

            while (pending.Count > 0)
            {
                var id = pending.Pop();

                if (!seen.Add(id) || !raw.TryGetValue(id, out var children))
                {
                    continue;
                }

                foreach (var child in children)
                {
                    if (string.Equals(child, innerId, StringComparison.Ordinal))
                    {
                        return true;
                    }

                    pending.Push(child);
                }
            }

            return false;
        }

        var noteToGroup = new Dictionary<string, string>(StringComparer.Ordinal);
        var members = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var open = new List<Grouping.Rect>();

        foreach (var (frame, ids) in frames)
        {
            if (ids is null)
            {
                open.Add(frame);

                continue;
            }

            // 名单里指向已经不在板上的那些格，读的时候丢掉：删一格不该在别的节点上留下鬼名。
            // 一格只归一个组：按框的文档顺序，先认到的那个组算——被前面的组认走的那些，
            // 不再列进后面那张表的成员里（否则"框 hug 谁"和"这格属于谁"会各说一套）。
            // 组也能是成员；会把自己套成环的那些成员在这里就被拆掉。
            var kept = new List<string>();

            foreach (var id in ids)
            {
                if (!live.Contains(id) || noteToGroup.ContainsKey(id))
                {
                    continue;
                }

                if (raw.ContainsKey(id) && (string.Equals(id, frame.Id, StringComparison.Ordinal) || Contains(id, frame.Id)))
                {
                    continue;
                }

                if (noteToGroup.TryAdd(id, frame.Id))
                {
                    kept.Add(id);
                }
            }

            members[frame.Id] = kept;
        }

        return new GroupMap(noteToGroup, members, byId, frameOrder, notes, open);
    }

    /// <summary>这个框有没有显式名单（没有＝它靠几何收人）。</summary>
    public bool HasRoster(string frameId) => _members.ContainsKey(frameId);

    /// <summary>
    /// 这一格属于哪个框。名单里有它就用名单；没有它、而它在某个组的名单里，也不再去按几何找别的框。
    /// </summary>
    public Grouping.Rect? GroupOf(Grouping.Rect note)
    {
        if (_noteToGroup.TryGetValue(note.Id, out var id))
        {
            return _frames.TryGetValue(id, out var frame) ? frame : (Grouping.Rect?)null;
        }

        return Grouping.GroupOf(note, _openFrames);
    }

    /// <summary>这个框的直接成员，按文档（叠放）顺序：便签在前、嵌进来的子组在后。</summary>
    public IReadOnlyList<Grouping.Rect> Members(string frameId)
    {
        if (!_members.TryGetValue(frameId, out var ids))
        {
            return Grouping.Members(_frames[frameId], _notes);
        }

        var wanted = new HashSet<string>(ids, StringComparer.Ordinal);
        var result = new List<Grouping.Rect>(wanted.Count);

        foreach (var note in _notes)
        {
            if (wanted.Contains(note.Id))
            {
                result.Add(note);
            }
        }

        foreach (var frame in _frameOrder)
        {
            if (wanted.Contains(frame.Id))
            {
                result.Add(frame);
            }
        }

        return result;
    }

    public IReadOnlyList<string> MemberIds(string frameId) => [.. Members(frameId).Select(member => member.Id)];

    /// <summary>
    /// 这个框里（含着嵌进来的子组一路走到底）所有便签的 id：点外面的名牌＝选里面全部便签。
    /// </summary>
    public IReadOnlyList<string> NoteIdsUnder(string frameId)
    {
        var collected = new List<string>();
        var seenNotes = new HashSet<string>(StringComparer.Ordinal);
        var seenFrames = new HashSet<string>(StringComparer.Ordinal);

        Walk(frameId);

        return collected;

        void Walk(string id)
        {
            if (!seenFrames.Add(id))
            {
                return;
            }

            if (!_members.TryGetValue(id, out var ids))
            {
                if (_frames.TryGetValue(id, out var box))
                {
                    foreach (var member in Grouping.Members(box, _notes))
                    {
                        if (seenNotes.Add(member.Id))
                        {
                            collected.Add(member.Id);
                        }
                    }
                }

                return;
            }

            var wanted = new HashSet<string>(ids, StringComparer.Ordinal);

            foreach (var note in _notes)
            {
                if (wanted.Contains(note.Id) && seenNotes.Add(note.Id))
                {
                    collected.Add(note.Id);
                }
            }

            foreach (var frame in _frameOrder)
            {
                if (wanted.Contains(frame.Id))
                {
                    Walk(frame.Id);
                }
            }
        }
    }

    /// <summary>这个框套了几层（最外层 0）：渲染把外面那圈画淡一档，嵌套才看得出层级。</summary>
    public int Depth(string frameId)
    {
        var depth = 0;
        var current = frameId;

        while (depth < 64 && _noteToGroup.TryGetValue(current, out var parent))
        {
            depth++;
            current = parent;
        }

        return depth;
    }

    /// <summary>这一格（便签或组）套在哪个外层组里；散着／最外层的返回 null。</summary>
    public string? ParentOf(string id) => _noteToGroup.TryGetValue(id, out var parent) ? parent : null;

    /// <summary>按在名牌或虚线上＝选中组里所有便签（框自己从不进选中）。</summary>
    public IReadOnlyList<string> OnFramePressed(Grouping.Rect group) => MemberIds(group.Id);

    /// <summary>一次排布眼里的"一件东西"：一张便签，或一整个组（见 <see cref="Grouping.Unit"/>）。</summary>
    public IReadOnlyList<Grouping.Unit> UnitsOf() => UnitsOf(_notes);

    /// <summary>
    /// 只把 <paramref name="notes"/> 归件：排布命令问的是"选中的那几枚算几件"，
    /// 不是整块板子——传子集时必须按子集分件，否则没选中的格子会被拖进同一件里一起动。
    /// </summary>
    public IReadOnlyList<Grouping.Unit> UnitsOf(IReadOnlyList<Grouping.Rect> notes)
    {
        var order = new List<string>();
        var buckets = new Dictionary<string, List<Grouping.Rect>>(StringComparer.Ordinal);

        foreach (var note in notes)
        {
            var key = GroupOf(note)?.Id ?? note.Id;

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

            return new Grouping.Unit(
                key,
                _frames.TryGetValue(key, out var frame) ? frame : members[0],
                members.ConvertAll(member => member.Id));
        });
    }
}
