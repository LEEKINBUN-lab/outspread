using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// The command table, and the commands that change the document's shape rather than its geometry.
/// </summary>
/// <remarks>
/// <para>
/// One row per command, read by the menu, by the keyboard dispatcher and by the self test — the
/// prototype's <c>CMDS</c> shape. That is not tidiness: his config defers bare-key shortcuts, which
/// leaves the menu as the only non-keyboard way to reach a command (WCAG 2.5.7), so a command that
/// exists in the key handler but not in the menu is a rule violation, and a menu entry with no
/// handler is a lie. Generating both from one table makes both impossible.
/// </para>
/// <para>
/// Chords are modifier combinations only, and every command states its precondition in the same
/// breath as its action — refusing says how many notes were needed and how many were offered.
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    /// <param name="Dispatched">
    /// False for the handful of keys the window still handles in its own switch (Esc, Ctrl+A,
    /// Ctrl+V, Ctrl+Z/Y, Delete, Ctrl+0). They live in this table so the cheat sheet cannot lie
    /// about them, but the table must not also run them — that would fire twice. M1b's registry
    /// pass is meant to fold those six into the switch-free path and drop this flag.
    /// </param>
    /// <param name="Listed">
    /// False keeps a working command off the two surfaces that *list* things (the key sheet and the
    /// right-click menu) while the keyboard still dispatches it. His call on 2026-10-12: the
    /// everyday five (全选 / 删除 / 撤销 / 重做 / 查找) do not need a menu route — a list that
    /// carries them reads like a manual, and the sheet is for the chords nobody guesses.
    /// </param>
    /// <param name="Sheet">
    /// False keeps the row off the Tab sheet only. The sheet is the "chords nobody guesses" page:
    /// copy/paste, Esc, arrow nudges, double-click-new and closing a board were named by him on
    /// 10-24 as noise there. The menu and the keyboard still use the same row.
    /// </param>
    /// <param name="Menu">
    /// False keeps the row off the right-click menu only. “从边缘拉一根线” stays on the Tab sheet —
    /// it is the only place the double-click gesture is written down — but the menu already has
    /// plenty of entries and he asked for that one to go (10-25).
    /// </param>
    private readonly record struct Command(string Label, string Chord, string Section, Action Run, bool Dispatched = true, bool Listed = true, bool Sheet = true, bool Menu = true)
    {
        public string MenuText => Chord is { Length: > 0 } chord ? $"{Label}    {chord}" : Label;

        /// <summary>
        /// 键帽上那一串：方向键写成箭头（Alt+Left → Alt+←）。表里的字符串仍用 e.Key 的名字，
        /// 因为派发是拿它和 <c>e.Key.ToString()</c> 比的；这里只换显示。
        /// </summary>
        public string ChordDisplay => Chord is { Length: > 0 } chord
            ? string.Join('+', chord.Split('+').Select(part => part switch
            {
                "Left" => "←",
                "Right" => "→",
                "Up" => "↑",
                "Down" => "↓",
                _ => part,
            }))
            : string.Empty;
    }

    private Command[] Commands() =>
    [
        new("左对齐", "Alt+Left", "排布", () => Arrange("左对齐", items => Aligning.Align(items, Aligning.Side.Left))),
        new("水平居中", "Alt+H", "排布", () => Arrange("水平居中", items => Aligning.Align(items, Aligning.Side.HCenter))),
        new("右对齐", "Alt+Right", "排布", () => Arrange("右对齐", items => Aligning.Align(items, Aligning.Side.Right))),
        new("上对齐", "Alt+Up", "排布", () => Arrange("上对齐", items => Aligning.Align(items, Aligning.Side.Top))),
        new("垂直居中", "Alt+V", "排布", () => Arrange("垂直居中", items => Aligning.Align(items, Aligning.Side.VCenter))),
        new("下对齐", "Alt+Down", "排布", () => Arrange("下对齐", items => Aligning.Align(items, Aligning.Side.Bottom))),
        new("横向均分", "Alt+J", "排布", () => Arrange("横向均分", items => Aligning.Distribute(items, horizontal: true))),
        new("纵向均分", "Alt+K", "排布", () => Arrange("纵向均分", items => Aligning.Distribute(items, horizontal: false))),
        new("堆叠", "Alt+L", "排布", () => Arrange("堆叠", Aligning.Stack)),
        new("按颜色排列", "Alt+Y", "排列", () => ArrangeByKey("按颜色排列", node => node.Color ?? "无颜色")),
        new("按标签排列", "Alt+T", "排列", () => ArrangeByKey("按标签排列", node => Tags.Of(node.Text).FirstOrDefault() ?? "#无标签")),
        new("成组", "Ctrl+G", "组织", GroupOrDissolve),
        new("拆组", string.Empty, "组织", UngroupSelection, Sheet: false),
        new("复制", "Ctrl+D", "组织", DuplicateSelection),

        // 连线按叠放次序把选中的串成一条链，全都在就是拆链（原型同一条命令两用）。
        new("连线", "Alt+C", "组织", ConnectSelection),
        new("从边缘拉一根线", "双击边缘", "组织", StartPullFromSelection, Dispatched: false, Menu: false),
        new("全部选中", "Ctrl+A", "板子", SelectAll, Dispatched: false, Listed: false),
        new("复制选中", "Ctrl+C", "板子", () => _ = CopySelectionAsync(), Sheet: false),
        new("粘贴剪贴板", "Ctrl+V", "板子", () => _ = PasteFromClipboardAsync(), Dispatched: false, Sheet: false),
        new("取消选中", "Esc", "板子", () => { ClearSelection(); ProjectSelection(); }, Dispatched: false, Sheet: false),
        new("删掉选中", "Delete", "板子", RemoveSelection, Dispatched: false, Listed: false),
        new("撤回", "Ctrl+Z", "板子", Undo, Dispatched: false, Listed: false),
        new("重做", "Ctrl+Y", "板子", Redo, Dispatched: false, Listed: false),
        new("新建一格", "双击空白", "板子", NewNoteAtCentre, Dispatched: false, Sheet: false),

        // 查找与恢复没有第二个入口的要不得：查找是"贴进去的东西找得回来"，恢复是"改坏了还够得着"。
        new("查找", "Ctrl+F", "板子", OpenSearch, Listed: false),
        new("恢复…", string.Empty, "板子", OpenRestorePanel),

        // 一块板一件事：这一组管的是"这扇窗口现在在处理哪件事"，不动板子上的任何一个字。
        new("新板块", "Ctrl+N", "换板子", CreateNewBoard),
        new("关掉这块板", "Ctrl+W", "换板子", CloseCurrentBoard, Sheet: false),
        new("下一块板", "Ctrl+Right", "换板子", () => StepBoard(1)),
        new("上一块板", "Ctrl+Left", "换板子", () => StepBoard(-1)),

        // Shift+1 / Shift+2 同样走那枚 switch（数字键的 e.Key 是 Number1），这里只负责菜单与清单上有它。
        new("看到全部", "Shift+1", "看", ZoomToFit, Dispatched: false),
        new("看到选中", "Shift+2", "看", ZoomToSelection, Dispatched: false),
    ];

    /// <summary>Every node on the board — the same call the keyboard's Ctrl+A makes.</summary>
    private void SelectAll()
    {
        var emptyGroups = EmptyGroupIds();

        foreach (var node in _document.Nodes)
        {
            // 空组已经被藏起来；把它选进来只会在屏幕上留下一个没有内容的实线框。
            if (node.Kind == CanvasNodeKind.Group && emptyGroups.Contains(node.Id))
            {
                continue;
            }

            _selection.Add(node.Id);
        }

        ProjectSelection();

        Say($"选中 {_selection.Count} 个节点 · 缩放 {Percent()}");
    }

    /// <summary>
    /// Sections in first-appearance order, so the menu reads like this table — and only the rows that
    /// are <b>listed</b>: the two surfaces that enumerate commands read this, the keyboard dispatcher
    /// reads <see cref="Commands"/> whole, so hiding a row from the list cannot switch it off.
    /// </summary>
    private IEnumerable<IGrouping<string, Command>> Sections() => Commands().Where(command => command.Listed).GroupBy(command => command.Section);

    // ---------------------------------------------------------------------
    // The group layer
    // ---------------------------------------------------------------------

    /// <summary>
    /// 最近一次按下的是哪一组的名牌／环带——只是菜单的<b>语境</b>（右键它时"拆组"排前面），
    /// 不再是一层选中状态：10-22 起单击便签就是那一枚，点边才选整组，层这个概念没了。
    /// </summary>
    private string? _groupFocus;

    private List<CanvasNode> SelectedNotes()
        => _document.Nodes.Where(node => _selection.Contains(node.Id) && node.Kind != CanvasNodeKind.Group).ToList();

    private List<CanvasNode> BoardNotes()
        => _document.Nodes.Where(node => node.Kind != CanvasNodeKind.Group).ToList();

    private List<Grouping.Rect> GroupRects()
        => _document.Nodes.Where(node => node.Kind == CanvasNodeKind.Group).Select(RectOf).ToList();

    /// <summary>
    /// 谁在哪个组里：一次算一份，问的人全走这一份。
    /// </summary>
    /// <remarks>
    /// 规矩（10-24 他点的）：组框上写着 <c>members</c> 名单就用名单，<b>有名单的框不再靠几何收人</b>——
    /// 否则"新建一格正好落在框里"照样当场入组，那一格等于没修。没有名单的框（别的工具写的、
    /// 这份改动之前存的）照旧按几何。写名单的只有三处：成组、组钮拖入／拖出、拆组（删框）。
    /// 普通拖动一格什么都不写：那是挪位置，不是改关系。
    /// </remarks>
    private GroupMap Groups()
        => GroupMap.Build(
            BoardNotes().ConvertAll(RectOf),
            [.. _document.Nodes
                .Where(node => node.Kind == CanvasNodeKind.Group)
                .Select(node => (RectOf(node), (IReadOnlyList<string>?)node.GroupMemberIds))]);

    private static Grouping.Rect RectOf(CanvasNode node) => new(node.Id, node.X, node.Y, node.Width, node.Height);

    private static Aligning.Placed PlacedOf(CanvasNode node)
        => new(node.Id, node.X, node.Y, node.Width, node.Height);

    /// <summary>Clear the selection and the layer with it — never one without the other.</summary>
    private void ClearSelection()
    {
        _selection.Clear();
        _groupFocus = null;

        // 线是另一种选中物，但同一时刻只选一样：清格子也清线。
        ClearEdgeSelection();
    }

    private void GroupSelection()
    {
        var members = SelectedNotes();

        if (members.Count == 0)
        {
            Say("先选中至少一格再成组");
            return;
        }

        var map = Groups();
        var loose = members.Where(note => map.GroupOf(RectOf(note)) is null).ToList();

        if (loose.Count == 0)
        {
            Say("选中的格子都已经在组里了 · 按 Ctrl＋G 是解散");
            return;
        }

        // 选中里已经有组里的格子 ⇒ 把没成组的并进那个组，不再罩第二个框
        // （他 10-24：成组和没成组的混选，没成组的入组）。
        if (loose.Count < members.Count && TargetGroup(map, members) is { } target)
        {
            AppendToGroup(target, loose, map);

            return;
        }

        BeginGesture("成组");

        var frame = Grouping.Enclose(string.Empty, members.ConvertAll(RectOf));
        var made = _document.Add(CanvasNodeKind.Group, frame.X, frame.Y, frame.Width, frame.Height);

        // 新建的组要有一句能看见、能改的描述（10-22 他：新建的组没描述也没法改）。
        // 给个默认名，双击名牌即可改名。
        made.Label = "分组";

        // 成员是一张名单：成组那一拍把它写死，之后只有组钮那一路能改它。
        made.SetGroupMembers([.. members.Select(member => member.Id)]);

        EndGesture();

        _groupFocus = made.Id;
        Rebuild();
        Reselect(members.Select(member => member.Id));
        SaveSoon();
        ProjectSelection();

        Say(members.Count == 1
            ? "成组：框住这 1 格 · 再按 Ctrl＋G 是解散"
            : $"成组：框住 {members.Count} 格 · 再按 Ctrl＋G 是解散");
    }

    /// <summary>
    /// Ctrl＋G 这一发是"成组"还是"解散"：选中的每一格都已经在组里 ⇒ 解散；否则按
    /// <see cref="GroupSelection"/> 办（全散＝新建，混选＝把散格并进已有的组）。
    /// </summary>
    private void GroupOrDissolve()
    {
        var members = SelectedNotes();
        var map = Groups();

        if (members.Count > 0 && members.All(note => map.GroupOf(RectOf(note)) is not null))
        {
            UngroupSelection();

            return;
        }

        GroupSelection();
    }

    /// <summary>混选里该并进哪个组：先认当前聚焦的那个组，再认"选中成员最多的那个"。</summary>
    private Grouping.Rect? TargetGroup(GroupMap map, IReadOnlyList<CanvasNode> members)
    {
        var groups = members
            .Select(note => map.GroupOf(RectOf(note)))
            .Where(group => group is not null)
            .Select(group => group!.Value)
            .ToList();

        if (groups.Count == 0)
        {
            return null;
        }

        if (_groupFocus is { } focus && groups.Any(group => group.Id == focus))
        {
            return groups.First(group => group.Id == focus);
        }

        return groups
            .GroupBy(group => group.Id)
            .OrderByDescending(bucket => bucket.Count())
            .ThenBy(bucket => bucket.Key, StringComparer.Ordinal)
            .First()
            .First();
    }

    /// <summary>把没成组的并进已有那个组：只改名单，框在 EndGesture 里按名单自己长。</summary>
    private void AppendToGroup(Grouping.Rect target, IReadOnlyList<CanvasNode> loose, GroupMap map)
    {
        if (NodeOf(target.Id) is not { Kind: CanvasNodeKind.Group } frame)
        {
            return;
        }

        var ids = (frame.GroupMemberIds ?? map.MemberIds(target.Id)).ToList();

        foreach (var note in loose)
        {
            if (!ids.Contains(note.Id, StringComparer.Ordinal))
            {
                ids.Add(note.Id);
            }
        }

        var keep = _selection.ToList();

        BeginGesture("并进组");
        frame.SetGroupMembers(ids);
        EndGesture();

        _groupFocus = frame.Id;
        Rebuild();
        Reselect(keep);
        SaveSoon();
        ProjectSelection();

        Say($"把 {loose.Count} 格并进了这个组 · 框跟着长，格子没动 · 再按 Ctrl＋G 是解散");
    }

    /// <summary>重铸会清选中；成组/解散之后要把人还选着——不然"再按一次"就没有对象了。</summary>
    private void Reselect(IEnumerable<string> ids)
    {
        foreach (var id in ids)
        {
            if (NodeOf(id) is not null)
            {
                _selection.Add(id);
            }
        }
    }

    /// <summary>解散选中这些格子所在的组（菜单那一项；Ctrl＋G 在"全都在组里"时走这里）。</summary>
    private void UngroupSelection()
    {
        var groups = GroupRects();
        var hit = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in SelectedNotes())
        {
            if (Groups().GroupOf(RectOf(node)) is { } group)
            {
                hit.Add(group.Id);
            }
        }

        foreach (var id in _selection.Where(selected => groups.Any(group => group.Id == selected)))
        {
            hit.Add(id);
        }

        if (hit.Count == 0)
        {
            Say("选中的格子里没有哪格在组里");
            return;
        }

        BeginGesture("拆组");

        foreach (var id in hit)
        {
            _document.Remove(id);
        }

        _groupFocus = null;

        var keep = _selection.ToList();

        EndGesture();
        Rebuild();
        Reselect(keep);
        SaveSoon();
        ProjectSelection();

        Say($"解散了 {hit.Count} 个组，格子的位置没动（Ctrl＋Z 可回退）");
    }

    private void DuplicateSelection()
    {
        var members = SelectedNotes();

        if (members.Count == 0)
        {
            Say("没有选中任何格子");
            return;
        }

        BeginGesture($"复制 {members.Count} 格");

        foreach (var node in members)
        {
            var copy = _document.Add(
                node.Kind,
                node.X + Aligning.MinGapPx,
                node.Y + Aligning.MinGapPx,
                node.Width,
                node.Height,
                node.Color);

            copy.Text = node.Text;
            copy.FilePath = node.FilePath;
            copy.Url = node.Url;
            copy.Label = node.Label;
        }

        EndGesture();
        Rebuild();
        SaveSoon();

        Say($"复制了 {members.Count} 格，副本在右下方各 {Aligning.MinGapPx:0} 世界像素");
    }

    /// <summary>
    /// Re-flow the whole board by a key, not just the selection — the command's promise is "put
    /// like with like", which means nothing over a subset.
    /// </summary>
    /// <summary>
    /// 排列：同一颜色 / 同一标签的合成一块、块内排成矩阵、块间距更大；块内按到期优先级，
    /// 并尽量把有连线的排在一起（块也按连线多少相邻），这样线不会横穿整块板。
    /// </summary>
    private void ArrangeByKey(string label, Func<CanvasNode, string> keyOf)
    {
        var units = Groups().UnitsOf();

        // 一个组跟着它第一格走：排列要的是"同类放一起"，而组是一件东西，不能为了分色把它劈成两半。
        // 所以那一块由组里最先出现的那格决定，回执里也会说出搬了几件是整块。
        var keys = units
            .Select(unit => NodeOf(unit.MemberIds[0]) is { } first ? keyOf(first) : string.Empty)
            .ToList();

        // 组里有一格逾期，这个组就按逾期那一档排：时间是"这一件东西有多急"，不是某一格的事。
        var priorities = units
            .Select(unit => unit.MemberIds
                .Select(NodeOf)
                .Where(note => note is not null)
                .Select(note => DuePriority(note!))
                .DefaultIfEmpty(DuePriorityOfNone)
                .Min())
            .ToList();

        // 连线按"件"算：组里任何一格连到别件，都算这个组连到那一件。
        var unitOf = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var unit in units)
        {
            foreach (var member in unit.MemberIds)
            {
                unitOf[member] = unit.Id;
            }
        }

        var links = _document.Edges
            .Select(edge => (
                From: unitOf.GetValueOrDefault(edge.FromNode),
                To: unitOf.GetValueOrDefault(edge.ToNode)))
            .Where(pair => pair.From is not null && pair.To is not null && pair.From != pair.To)
            .Select(pair => (pair.From!, pair.To!))
            .ToList();

        ApplyUnitMoves(label, units, Aligning.Arrange([.. units.Select(PlacedOf)], keys, priorities, links));
    }

    /// <summary>"无日期"那一档的下标；排列的优先级越小越靠前。</summary>
    private const int DuePriorityOfNone = 4;

    /// <summary>到期优先级：逾期 → 今天 → 将来 → 日期读不出 → 无日期。</summary>
    private int DuePriority(CanvasNode note) => NoteMarks.StateOf(Tags.Read(note.Text), _view.Today) switch
    {
        NoteMarks.DueState.Late => 0,
        NoteMarks.DueState.Today => 1,
        NoteMarks.DueState.Upcoming => 2,
        NoteMarks.DueState.Malformed => 3,
        _ => DuePriorityOfNone,
    };

    /// <summary>
    /// Ctrl＋G 是一枚开关：全散＝新建；混选＝把散格并进已有的那个组；全在组里＝解散。
    /// 组完/解散以后选中必须还在——否则"再按一次"没有对象。三发撤销要回到原样。
    /// </summary>
    private void SelfTestGroupToggle(List<string> problems)
    {
        var before = _document.ToJson();
        var loose = BoardNotes().Where(note => Groups().GroupOf(RectOf(note)) is null).Take(2).ToList();

        if (loose.Count < 2)
        {
            problems.Add("自检成组开关: 板上不足两格散格，这一发不适用");

            return;
        }

        var groupsBefore = GroupRects().Count;

        SelectOnly(loose[0].Id);
        GroupOrDissolve();

        var group = Groups().GroupOf(RectOf(loose[0]))?.Id;

        if (group is null || GroupRects().Count != groupsBefore + 1)
        {
            problems.Add($"自检成组开关: 单格 Ctrl＋G 没建出新组（组数 {GroupRects().Count}，原 {groupsBefore}）");

            Undo();
            Rebuild();
            ClearSelection();
            ProjectSelection();

            return;
        }

        if (!_selection.Contains(loose[0].Id))
        {
            problems.Add("自检成组开关: 成组以后选中被清掉了——再按一次没有对象");
        }

        // 混选：这一格已在组里 ＋ 另一格散着 ⇒ 只把散的那格并进去，不罩第二个框。
        SelectOnly(loose[0].Id);
        _selection.Add(loose[1].Id);
        ProjectSelection();
        GroupOrDissolve();

        var roster = NodeOf(group)?.GroupMemberIds ?? [];

        if (GroupRects().Count != groupsBefore + 1)
        {
            problems.Add($"自检成组开关: 混选以后组数是 {GroupRects().Count}，应仍是 {groupsBefore + 1}（不该再造一个框）");
        }

        if (!roster.Contains(loose[1].Id, StringComparer.Ordinal))
        {
            problems.Add("自检成组开关: 混选里没成组的那格没被并进组");
        }

        // 全在组里 ⇒ 再按一次是解散。
        GroupOrDissolve();

        if (NodeOf(group) is not null)
        {
            problems.Add("自检成组开关: 选中的全在组里时按 Ctrl＋G 没解散");
        }

        if (!_selection.Contains(loose[0].Id) || !_selection.Contains(loose[1].Id))
        {
            problems.Add("自检成组开关: 解散以后选中丢了");
        }

        for (var step = 0; step < 3; step++)
        {
            Undo();
        }

        Rebuild();

        if (_document.ToJson() != before)
        {
            problems.Add("自检成组开关: 三发撤销以后文档没回到原样");
        }
        else
        {
            Diagnostics.Write("selftest 成组开关: 全散新建、混选并入、全在组里解散、选中一路保住；三发撤销回原样");
        }

        ClearSelection();
        ProjectSelection();
    }

    /// <summary>
    /// 排列这一发的界面接线：排完不许有格子重叠；整发是一步撤销，撤销后文档回到原样。
    /// 矩阵、优先级、连线顺序是 Core 那几条单测的事，这里只钉"界面上真的按新算法排了"。
    /// </summary>
    private void SelfTestArrange(List<string> problems)
    {
        var before = _document.ToJson();
        var notes = BoardNotes();

        if (notes.Count < 4)
        {
            problems.Add("自检排列: 板上不足四格，这一发不适用");

            return;
        }

        ArrangeByKey("按颜色排列", note => note.Color ?? "无颜色");

        var placed = BoardNotes().ConvertAll(RectOf);
        var overlaps = 0;

        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                var a = placed[i];
                var b = placed[j];

                if (a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height)
                {
                    overlaps++;
                }
            }
        }

        if (overlaps > 0)
        {
            problems.Add($"自检排列: 排完有 {overlaps} 对格子重叠");
        }

        Undo();
        Rebuild();

        if (_document.ToJson() != before)
        {
            problems.Add("自检排列: 撤销一发以后文档没回到原样");
        }
        else
        {
            Diagnostics.Write($"selftest 排列: {placed.Count} 格无重叠、按颜色排成矩阵（块内 {Aligning.MinGapPx}／块间 {Aligning.BucketGapPx}）、撤销回原样");
        }

        ClearSelection();
        ProjectSelection();
    }
}
