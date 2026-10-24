namespace Outspread.Core;

/// <summary>
/// Who is talking right now, and what the next input is allowed to do.
/// </summary>
/// <remarks>
/// <para>
/// Before this existed the answer was spread over about ten fields on the window
/// (<c>_marqueeing</c>, <c>_panning</c>, <c>_rightDrag</c>, <c>_nodeGesture</c>,
/// <c>_resizeGesture</c>, <c>_editingId</c>, …), and each input handler walked that set of
/// booleans asking its own question. Three separate bugs came from that shape — the static
/// right-click that had to decide between a pan and a menu, the edge handle fighting a resize
/// for the same press, the selection ring showing while a note was being typed in. They are one
/// bug: there was no single value answering "whose turn is it".
/// </para>
/// <para>
/// This is that value, and the transitions are pure so the table in
/// <c>docs/interaction-machine.md</c> can be executed rather than described.
/// </para>
/// </remarks>
public static class BoardMachine
{
    /// <summary>
    /// <param name="Idle">Nothing is selected and no gesture is running.</param>
    /// <param name="Selected">Something is selected; the board owns the keyboard.</param>
    /// <param name="Pressed">A button is down but has not travelled the drag threshold yet — it is
    /// still possibly a click, possibly a static right-click that means "menu".</param>
    /// <param name="Dragging">Past the threshold, so this is a gesture; <see cref="Op"/> says which.</param>
    /// <param name="Editing">A note's text box owns the keyboard.</param>
    /// <param name="Finding">The find box owns the keyboard.</param>
    /// <param name="Menu">The context menu is open and is modal.</param>
    /// </summary>
    public enum Phase
    {
        Idle,
        Selected,
        Pressed,
        Dragging,
        Editing,
        Finding,
        Menu,
    }

    /// <summary>Which gesture a drag is running. Only meaningful in <see cref="Phase.Dragging"/>.</summary>
    public enum PointerOp
    {
        None,
        Marquee,
        Pan,
        Move,
        Resize,
        Connect,
    }

    /// <param name="At">Where we are.</param>
    /// <param name="Op">The gesture in flight, if any.</param>
    /// <param name="Additive">Whether the press carries Shift/Ctrl — it decides if releasing a
    /// zero-travel marquee clears the selection or leaves it alone.</param>
    /// <param name="NodeId">The note being edited, in <see cref="Phase.Editing"/>.</param>
    /// <param name="Direction">For a resize, which edge the press landed on ("n"/"e"/"s"/"w" or a
    /// corner). Kept apart from <paramref name="NodeId"/> so nothing can read a direction as an id.</param>
    public readonly record struct Interaction(
        Phase At,
        PointerOp Op = PointerOp.None,
        bool Additive = false,
        string? NodeId = null,
        string? Direction = null)
    {
        public static readonly Interaction Idle = new(Phase.Idle);

        public static readonly Interaction Selected = new(Phase.Selected);

        public bool OwnsBoardKeys => At is Phase.Idle or Phase.Selected;

        public bool IsDraggingGesture => At == Phase.Dragging;
    }

    /// <summary>What a press is, as far as the machine cares.</summary>
    /// <param name="OnNode">The press landed on a note rather than on empty board.</param>
    /// <param name="Edge">Which edge of that note it landed on (within the band), or null.</param>
    /// <remarks>
    /// 这里没有"空格按没按"这一格：10-24 他把"空格＋拖＝平移"撤了，空格从此不进判定（按一下＝复位，
    /// 归 <c>Cheatsheet</c> 那一层）。多一个入参就等于把那条撤掉的规矩留在词汇表里。
    /// </remarks>
    public readonly record struct PressFacts(
        bool Right,
        bool Left,
        bool Additive,
        bool OnNode,
        string? Edge = null);

    /// <summary>The action the caller has to carry out for this press.</summary>
    public enum BeginEffect
    {
        Nothing,
        StartMarquee,
        StartPan,
        StartMove,
        StartResize,
        CloseMenu,
        KeepEditing,
    }

    /// <summary>What a release means once the machine has resolved travel against the threshold.</summary>
    public enum ReleaseEffect
    {
        Nothing,
        /// <summary>Commit the gesture as one undoable transaction.</summary>
        Commit,
        /// <summary>A zero-travel right-click: the press was a menu request, not a pan.</summary>
        OpenMenu,
        /// <summary>A zero-travel marquee on empty board replaces the selection — with nothing.</summary>
        ClearSelection,
        /// <summary>Undo the in-flight mutation without touching history.</summary>
        Discard,
    }

    /// <summary>
    /// A press. Empty board keeps its rule in <see cref="BoardGestures.OnEmptyPress"/> — this does
    /// not re-decide it, it just stops every caller from re-deciding it too.
    /// </summary>
    public static (Interaction Next, BeginEffect Effect) OnPress(Interaction now, PressFacts press)
    {
        if (now.At == Phase.Menu)
        {
            return (now with { At = press.OnNode ? Phase.Selected : Phase.Idle }, BeginEffect.CloseMenu);
        }

        if (now.At == Phase.Editing)
        {
            // Clicking elsewhere commits the text; the editor's own LostFocus does that, so the
            // machine's job here is only to not treat the click as a second gesture as well.
            return (now, BeginEffect.KeepEditing);
        }

        if (now.At is Phase.Pressed or Phase.Dragging)
        {
            return (now, BeginEffect.Nothing);
        }

        if (!press.OnNode)
        {
            return BoardGestures.OnEmptyPress(press.Right, press.Left) switch
            {
                SurfaceAction.Pan => (new Interaction(Phase.Pressed, PointerOp.Pan, press.Additive), BeginEffect.StartPan),
                SurfaceAction.Marquee => (new Interaction(Phase.Pressed, PointerOp.Marquee, press.Additive), BeginEffect.StartMarquee),
                _ => (now, BeginEffect.Nothing),
            };
        }

        // Only a plain left press on a note grabs it; a right press on a note still pans, because
        // panning has to be reachable from anywhere on the board, not only from empty space.
        if (press.Right && !press.Left)
        {
            return (new Interaction(Phase.Pressed, PointerOp.Pan, press.Additive), BeginEffect.StartPan);
        }

        if (!press.Left)
        {
            return (now, BeginEffect.Nothing);
        }

        return press.Edge is { Length: > 0 } edge
            ? (new Interaction(Phase.Pressed, PointerOp.Resize, press.Additive, Direction: edge), BeginEffect.StartResize)
            : (new Interaction(Phase.Pressed, PointerOp.Move, press.Additive), BeginEffect.StartMove);
    }

    /// <summary>
    /// Pointer movement. The only thing that promotes a press to a gesture is travel past the
    /// threshold — that single rule is what keeps a static right-click from being eaten by a pan.
    /// </summary>
    public static Interaction OnMove(Interaction now, double travelPx)
    {
        if (now.At != Phase.Pressed || !BoardGestures.IsDrag(travelPx))
        {
            return now;
        }

        return now with { At = Phase.Dragging };
    }

    public static (Interaction Next, ReleaseEffect Effect) OnRelease(Interaction now, double travelPx)
    {
        var (next, effect) = Resolve(now, travelPx);

        return next.At switch
        {
            Phase.Dragging => (IdleOrSelected(effect), effect),
            Phase.Pressed when now.Op == PointerOp.Pan => (now with { At = Phase.Menu }, ReleaseEffect.OpenMenu),
            Phase.Pressed when now.Op == PointerOp.Marquee => (Interaction.Idle, now.Additive ? ReleaseEffect.Nothing : ReleaseEffect.ClearSelection),
            Phase.Pressed => (Interaction.Selected, ReleaseEffect.Nothing),
            _ => (now, ReleaseEffect.Nothing),
        };

        static Interaction IdleOrSelected(ReleaseEffect effect)
            => effect == ReleaseEffect.ClearSelection ? Interaction.Idle : Interaction.Selected;
    }

    private static (Interaction, ReleaseEffect) Resolve(Interaction now, double travelPx)
    {
        if (now.At != Phase.Dragging)
        {
            return (now, ReleaseEffect.Nothing);
        }

        // A marquee that never moved is a click on empty board, not a transaction.
        if (now.Op == PointerOp.Marquee && !BoardGestures.IsDrag(travelPx))
        {
            return (now with { At = Phase.Pressed }, ReleaseEffect.Nothing);
        }

        return (now, ReleaseEffect.Commit);
    }

    /// <summary>A cancelled pointer (lost capture, window switched away): never leaves a transaction behind.</summary>
    public static (Interaction Next, ReleaseEffect Effect) OnCancel(Interaction now)
        => now.At == Phase.Dragging
            ? (Interaction.Selected, ReleaseEffect.Discard)
            : (now with { At = now.Op == PointerOp.None ? Phase.Idle : Phase.Selected }, ReleaseEffect.Nothing);

    public static Interaction OnEditBegan(string nodeId) => new(Phase.Editing, NodeId: nodeId);

    public static Interaction OnEditEnded(bool hadSelection) => hadSelection ? Interaction.Selected : Interaction.Idle;

    public static Interaction OnMenuOpened(Interaction now) => now with { At = Phase.Menu };

    public static Interaction OnMenuClosed(Interaction now)
        => now with { At = now.Op == PointerOp.None ? Phase.Selected : Phase.Idle };
}
