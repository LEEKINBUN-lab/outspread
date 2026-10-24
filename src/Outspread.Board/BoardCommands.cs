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
    private readonly record struct Command(string Label, string Chord, string Section, Action Run, bool Dispatched = true, bool Listed = true)
    {
        public string MenuText => Chord is { Length: > 0 } chord ? $"{Label}    {chord}" : Label;
    }

    private Command[] Commands() =>
    [
        new("左对齐", "Alt+A", "排布", () => Arrange("左对齐", items => Aligning.Align(items, Aligning.Side.Left))),
        new("水平居中", "Alt+H", "排布", () => Arrange("水平居中", items => Aligning.Align(items, Aligning.Side.HCenter))),
        new("右对齐", "Alt+D", "排布", () => Arrange("右对齐", items => Aligning.Align(items, Aligning.Side.Right))),
        new("上对齐", "Alt+W", "排布", () => Arrange("上对齐", items => Aligning.Align(items, Aligning.Side.Top))),
        new("垂直居中", "Alt+V", "排布", () => Arrange("垂直居中", items => Aligning.Align(items, Aligning.Side.VCenter))),
        new("下对齐", "Alt+S", "排布", () => Arrange("下对齐", items => Aligning.Align(items, Aligning.Side.Bottom))),
        new("横向均分", "Alt+J", "排布", () => Arrange("横向均分", items => Aligning.Distribute(items, horizontal: true))),
        new("纵向均分", "Alt+K", "排布", () => Arrange("纵向均分", items => Aligning.Distribute(items, horizontal: false))),
        new("堆叠", "Alt+L", "排布", () => Arrange("堆叠", Aligning.Stack)),

        // 微调走的是窗口自己那枚 switch（e.Key 的名字是 Up/Down/Left/Right，写不成一张表里的键位串），
        // 但每一行仍在这里：菜单是单键快捷键缺席时唯一的替代面（WCAG 2.5.7），不在表里就等于够不着。
        new("上移一格", "↑", "排布", () => Nudge(0, -NudgePx), Dispatched: false),
        new("下移一格", "↓", "排布", () => Nudge(0, NudgePx), Dispatched: false),
        new("左移一格", "←", "排布", () => Nudge(-NudgePx, 0), Dispatched: false),
        new("右移一格", "→", "排布", () => Nudge(NudgePx, 0), Dispatched: false),
        new("按颜色聚拢", "Alt+Y", "聚拢", () => Cluster("按颜色聚拢", node => node.Color ?? "none")),
        new("按标签聚拢", "Alt+T", "聚拢", () => Cluster("按标签聚拢", node => Tags.Of(node.Text).FirstOrDefault() ?? "#无标签")),
        new("成组", "Ctrl+G", "组织", GroupSelection),
        new("拆组", "Ctrl+Shift+G", "组织", UngroupSelection),
        new("复制", "Ctrl+D", "组织", DuplicateSelection),

        // 连线按叠放次序把选中的串成一条链，全都在就是拆链（原型同一条命令两用）。
        new("连线", "Alt+C", "组织", ConnectSelection),
        new("从边缘拉一根线", "双击边缘", "组织", StartPullFromSelection, Dispatched: false),
        new("全部选中", "Ctrl+A", "板子", SelectAll, Dispatched: false, Listed: false),
        new("复制选中", "Ctrl+C", "板子", () => _ = CopySelectionAsync()),
        new("粘贴剪贴板", "Ctrl+V", "板子", () => _ = PasteFromClipboardAsync(), Dispatched: false),
        new("取消选中", "Esc", "板子", () => { ClearSelection(); ProjectSelection(); }, Dispatched: false),
        new("删掉选中", "Delete", "板子", RemoveSelection, Dispatched: false, Listed: false),
        new("撤回", "Ctrl+Z", "板子", Undo, Dispatched: false, Listed: false),
        new("重做", "Ctrl+Y", "板子", Redo, Dispatched: false, Listed: false),
        new("回到 100%", "Ctrl+0", "看", ResetView, Dispatched: false, Listed: false),
        new("新建一格", "双击空白", "板子", NewNoteAtCentre, Dispatched: false),

        // 查找与恢复没有第二个入口的要不得：查找是"贴进去的东西找得回来"，恢复是"改坏了还够得着"。
        new("查找", "Ctrl+F", "板子", OpenSearch, Listed: false),
        new("恢复…", string.Empty, "板子", OpenRestorePanel),

        // 一块板一件事：这一组管的是"这扇窗口现在在处理哪件事"，不动板子上的任何一个字。
        new("新板块", "Ctrl+T", "换板子", CreateNewBoard),
        new("关掉这块板", "Ctrl+W", "换板子", CloseCurrentBoard),
        new("下一块板", "Ctrl+PageDown", "换板子", () => StepBoard(1)),
        new("上一块板", "Ctrl+PageUp", "换板子", () => StepBoard(-1)),

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

    private static Grouping.Rect RectOf(CanvasNode node) => new(node.Id, node.X, node.Y, node.Width, node.Height);

    private static Aligning.Placed PlacedOf(CanvasNode node)
        => new(node.Id, node.X, node.Y, node.Width, node.Height);

    /// <summary>Clear the selection and the layer with it — never one without the other.</summary>
    private void ClearSelection()
    {
        _selection.Clear();
        _groupFocus = null;
    }

    private void GroupSelection()
    {
        var members = SelectedNotes();

        if (members.Count < 1)
        {
            Say("先选中至少一格再成组");
            return;
        }

        BeginGesture("成组");

        var frame = Grouping.Enclose(string.Empty, members.ConvertAll(RectOf));
        var made = _document.Add(CanvasNodeKind.Group, frame.X, frame.Y, frame.Width, frame.Height);

        // 新建的组要有一句能看见、能改的描述（10-22 他：新建的组没描述也没法改）。
        // 给个默认名，双击名牌即可改名。
        made.Label = "分组";

        EndGesture();

        _groupFocus = made.Id;
        Rebuild();
        SaveSoon();
        ProjectSelection();

        Say(members.Count == 1
            ? $"成组：框住这 1 格（想往里再加，选中它再『创建分组』，或用组钮拖）"
            : $"成组：框住 {members.Count} 格 · 点便签＝单枚，点名字或虚线边＝整组");
    }

    private void UngroupSelection()
    {
        var groups = GroupRects();
        var hit = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in SelectedNotes())
        {
            if (Grouping.GroupOf(RectOf(node), groups) is { } group)
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

        EndGesture();
        Rebuild();
        SaveSoon();
        ProjectSelection();

        Say($"拆掉了 {hit.Count} 个组，格子的位置没动（Ctrl＋Z 可回退）");
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
    private void Cluster(string label, Func<CanvasNode, string> keyOf)
    {
        var notes = BoardNotes();
        var units = Grouping.UnitsOf(notes.ConvertAll(RectOf), GroupRects());

        // 一个组跟着它第一格走：聚拢要的是"同类放一起"，而组是一件东西，不能为了分色把它劈成两半。
        // 所以那一簇由组里最先出现的那格决定，回执里也会说出搬了几件是整块。
        var keys = units
            .Select(unit => NodeOf(unit.MemberIds[0]) is { } first ? keyOf(first) : string.Empty)
            .ToList();

        ApplyUnitMoves(label, units, Aligning.Cluster([.. units.Select(PlacedOf)], keys));
    }
}
