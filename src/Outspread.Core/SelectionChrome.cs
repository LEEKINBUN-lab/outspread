namespace Outspread.Core;

/// <summary>
/// The chrome drawn over the board: the selection ring, its handles, the marquee and the snap lines.
/// </summary>
/// <remarks>
/// <para>
/// Every value is the prototype's own token, read from <c>:root</c> and
/// <c>[data-theme="dark"]</c> and converted out of <c>hsl()</c> here so the arithmetic is visible:
/// <c>hsl(214 84% 56%)</c> is <c>rgb(49,130,237)</c> and <c>hsl(210 100% 56%)</c> is
/// <c>rgb(31,143,255)</c> — two blues that look alike and are not interchangeable, which is why the
/// fill is its own constant rather than "the accent at 24%".
/// </para>
/// <para>
/// The ring's inner contrast line is the reason there are two strokes: on a dark note a plain blue
/// outline disappears into the card, so a light 1px line sits inside it (Figma does the same).
/// </para>
/// </remarks>
public static class SelectionChrome
{
    /// <summary>Both strokes of the ring are one device pixel, at every zoom.</summary>
    public const double RingThicknessPx = 1;
    public const double InnerContrastThicknessPx = 1;

    /// <summary>The square a handle is drawn at.</summary>
    public const double HandleSeenPx = 8;

    /// <summary>What a handle grows to under the pointer — 83ms linear, the shortest Microsoft tier.</summary>
    public const double HandleHoverPx = 10;

    /// <summary>
    /// The pointer target. 24px is WCAG 2.5.8's floor, and it is deliberately wider than the drawn
    /// 8px: without it the handles are a precision task wearing a decoration.
    /// </summary>
    public const double HandleHitPx = 24;

    /// <summary>How close to a node's edge the pointer has to be before a drag resizes instead of moves.</summary>
    public const double EdgeBandPx = 6;

    /// <summary>The prototype's zoom clamp: 5% to 800%.</summary>
    public const double MinZoom = 0.05;
    public const double MaxZoom = 8.0;

    /// <summary>Ring and handle border: <c>--accent</c>.</summary>
    public static Rgb Ring(bool dark) => dark ? new Rgb(100, 164, 247) : new Rgb(49, 130, 237);

    /// <summary>Base of both translucent fills: <c>--sel-fill</c> and <c>--marquee-fill</c> share it.</summary>
    public static Rgb Fill(bool dark) => dark ? new Rgb(51, 153, 255) : new Rgb(31, 143, 255);

    public static double SelectionFillAlpha(bool dark) => dark ? 0.20 : 0.24;

    public static double MarqueeFillAlpha(bool dark) => dark ? 0.10 : 0.12;

    /// <summary>The line inside the ring: white on the light board, black on the dark one.</summary>
    public static Rgb InnerContrast(bool dark) => dark ? new Rgb(0, 0, 0) : new Rgb(255, 255, 255);

    public static double InnerContrastAlpha(bool dark) => dark ? 0.55 : 0.85;

    /// <summary>Handle face: <c>--panel</c>, the chrome's own surface, not the board's.</summary>
    public static Rgb HandleFace(bool dark) => dark ? new Rgb(27, 30, 34) : new Rgb(255, 255, 255);

    /// <summary>The 1px ring outside the handle that keeps a white handle readable on a white card.</summary>
    public static double HandleEdgeAlpha(bool dark) => dark ? 0.50 : 0.22;

    /// <summary>Snap guide colour: <c>--snap</c>.</summary>
    public static Rgb Snap(bool dark) => dark ? new Rgb(255, 144, 144) : new Rgb(255, 107, 107);
}
