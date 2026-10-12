namespace Outspread.Core;

/// <summary>
/// Groups: a rectangle on the board, and the two-level click that decides whether the user means
/// the container or the one note inside it.
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
/// The click rule is his own words from the prototype round: 第一次点必定选中组，第二次点则选中组内单个元素.
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

    /// <summary>Which layer a click on a note lands on.</summary>
    public enum Layer
    {
        /// <summary>The whole group: every member becomes selected.</summary>
        Group,

        /// <summary>Just the one note clicked.</summary>
        Unit,
    }

    /// <param name="Layer">What to select.</param>
    /// <param name="Ids">The ids to put in the selection.</param>
    /// <param name="FocusedGroup">The group that owns the layer afterwards — null clears it.</param>
    public readonly record struct Selection(Layer Layer, IReadOnlyList<string> Ids, string? FocusedGroup);

    /// <summary>
    /// The two-level click.
    /// </summary>
    /// <remarks>
    /// Ctrl/Shift is the escape hatch out of the layering: it always addresses the single note,
    /// because additive picking is already a precise operation and forcing a user to click twice
    /// to add one note is the annoyance the layering was meant to trade away.
    /// </remarks>
    /// <param name="focusedGroup">The group whose layer is currently active, if any.</param>
    public static Selection OnNotePressed(
        Rect pressed,
        IReadOnlyList<Rect> notes,
        IReadOnlyList<Rect> groups,
        string? focusedGroup,
        bool additive)
    {
        var group = GroupOf(pressed, groups);

        if (group is null)
        {
            return new Selection(Layer.Unit, [pressed.Id], FocusedGroup: null);
        }

        if (additive)
        {
            return new Selection(Layer.Unit, [pressed.Id], group.Value.Id);
        }

        // First click on a member of a group nobody has focused yet: take the whole group.
        if (focusedGroup != group.Value.Id)
        {
            var ids = notes.Where(member => group.Value.Holds(member)).Select(member => member.Id).ToList();

            if (!ids.Contains(pressed.Id))
            {
                ids.Add(pressed.Id);
            }

            return new Selection(Layer.Group, ids, group.Value.Id);
        }

        return new Selection(Layer.Unit, [pressed.Id], group.Value.Id);
    }

    /// <summary>
    /// Everything that must move when the layer is the group's: the clicked note plus its members.
    /// </summary>
    public static IReadOnlyList<string> DragSet(
        Rect pressed,
        IReadOnlyList<Rect> notes,
        IReadOnlyList<Rect> groups,
        Layer layer)
    {
        if (layer == Layer.Unit)
        {
            return [pressed.Id];
        }

        var group = GroupOf(pressed, groups);

        return group is null
            ? [pressed.Id]
            : notes.Where(member => group.Value.Holds(member)).Select(member => member.Id).ToList();
    }
}
