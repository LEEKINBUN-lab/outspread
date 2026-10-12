namespace Outspread.Core;

/// <summary>What a press on empty board space means, given which buttons and keys are in play.</summary>
public enum SurfaceAction
{
    /// <summary>Draw a selection box.</summary>
    Marquee,

    /// <summary>Move the camera.</summary>
    Pan,

    /// <summary>Neither — some other button (middle, or a second button held) is down.</summary>
    Ignore,
}

/// <summary>
/// The board's gesture vocabulary, as one decision function so both products can be pinned to it.
/// </summary>
/// <remarks>
/// Frozen with him on the prototype: left drag on empty space selects, right drag pans, space plus
/// drag pans, and a right click that never moved opens the board menu on release. The app used to
/// have it the other way round (left drag panned, and selecting needed Shift), which is one reason
/// he said the two products feel nothing alike.
/// </remarks>
public static class BoardGestures
{
    /// <summary>
    /// A press must travel this many screen pixels before it counts as a drag rather than a click
    /// (tldraw publishes 4 for mouse, 6 for coarse pointers).
    /// </summary>
    public const double DragThresholdPx = 4;

    public static SurfaceAction OnEmptyPress(bool rightButton, bool leftButton, bool spaceDown)
    {
        if (rightButton && leftButton)
        {
            // Both at once is not a gesture anyone means; guessing which one to honour would make
            // the board do something the user cannot predict.
            return SurfaceAction.Ignore;
        }

        if (rightButton)
        {
            // Right drag pans. A right press that never moves is turned into the menu on release,
            // so this function does not decide that part.
            return SurfaceAction.Pan;
        }

        if (!leftButton)
        {
            return SurfaceAction.Ignore;
        }

        return spaceDown ? SurfaceAction.Pan : SurfaceAction.Marquee;
    }

    /// <summary>Whether a release counts as a drag (as opposed to a click) at the given travel.</summary>
    public static bool IsDrag(double travelPx) => travelPx > DragThresholdPx;
}
