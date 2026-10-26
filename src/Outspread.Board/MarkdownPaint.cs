using System;
using System.Collections.Generic;
using System.Security;
using System.Text;
using Outspread.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace Outspread.Board;

/// <summary>
/// Paints one card's markdown block. Kept as one place on purpose: the fit-measure and the card
/// both go through <see cref="Line"/>, so a heading that makes the first line taller cannot leave
/// the last line cut off — the defect class this surface keeps hitting.
/// </summary>
/// <remarks>
/// <para>
/// Every number here is a reading. The prototype's rules are em-relative
/// (<c>.md.h1 { font-size: 1.35em; line-height: 1.4em }</c>) because a note's own font size is
/// rewritten by the zoom — an absolute px rule would not scale, and did not: at 150% the heading
/// came out smaller than the body. The ratio 1.4 is <c>Leading / Size</c> (21/15). The line box is
/// then whatever the text engine hands out for that product, measured in the running card: whole
/// pixels, half-up (21→21, 25.2→25, 28.35→28, 21.5→22, 28.5→29, 30.5→31) — what <see cref="Box"/>
/// encodes. A browser keeps the fraction (28.34px for a heading); that is the one place the two
/// cannot agree, and it is under a pixel per heading, in the direction that keeps the last line
/// inside its frame.
/// </para>
/// <para>
/// Inline decoration is composed by parsing XAML rather than by adding to a TextBlock's inlines in
/// code, because the code path does not work in this projection: <c>Inlines.Add</c> throws
/// E_NOINTERFACE, and <c>Inlines.Append</c> — the method the collection actually exposes — returns
/// without taking the item. A card built that way has the right number of lines, the right heights
/// and the right child count, and paints <b>nothing</b>. The interface self-test reads the words
/// back out of the realized card, which is what found this.
/// </para>
/// <para>
/// Two of the prototype's inline details have no form here and are <b>not</b> drawn, so this is the
/// record of why rather than an omission: the code tint (<c>rgb(0 0 0 / .05)</c>) and the link's
/// underline. Both need a box inside a line, and <c>InlineUIContainer</c> is refused by the parser
/// that does take the rest of this markup — <c>Cannot add instance of type
/// 'Microsoft.UI.Xaml.Documents.InlineUIContainer' to a collection of type
/// 'Microsoft.UI.Xaml.Documents.InlineCollection'</c> — so a line carries Runs and nothing else.
/// What survives of a code span is its monospace face and its 0.92em size; a link's words stay
/// undecorated.
/// </para>
/// </remarks>
internal static class MarkdownPaint
{
    /// <summary><c>font-size: 0.92em</c> on the code run.</summary>
    public const double CodeScale = 0.92;

    /// <summary>The prototype's <c>padding: 0 3px</c> and <c>border-left: 2px … padding-left: 8px</c>.</summary>
    public const double CodePaddingX = 3;

    public const double QuoteBarPx = 2;

    public const double QuoteGapPx = 8;

    private const string Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    private const string Mono = "Cascadia Mono, Consolas, Monospace";

    /// <summary>
    /// Builds the lines in order. <paramref name="roomScreen"/> is the height the card has for text;
    /// when the block needs more, whole lines are dropped from the bottom and the last line kept is
    /// told how many rows it may still take, so the cut falls between lines and the ellipsis says
    /// there is more — never half a row of glyphs.
    /// </summary>
    public static List<UIElement> Lines(
        NoteType type,
        double zoom,
        IReadOnlyList<MarkdownLine> lines,
        SolidColorBrush ink,
        SolidColorBrush quoteBrush,
        SolidColorBrush quoteBar,
        double roomScreen)
    {
        var painted = new List<UIElement>();
        var count = Count(type, zoom, lines, roomScreen);
        var drawn = 0.0;

        for (var i = 0; i < count; i++)
        {
            var line = lines[i];
            var element = Line(type, zoom, line, ink, quoteBrush, quoteBar);

            if (i == count - 1 && count < lines.Count)
            {
                // The truncation point: this line may not grow past what is left of the card.
                var rows = Math.Max(1, (int)Math.Floor(Math.Max(0, roomScreen - drawn) / Box(type, zoom, line)));

                if (element is TextBlock label)
                {
                    label.MaxLines = rows;
                    label.TextTrimming = TextTrimming.WordEllipsis;
                }
            }

            painted.Add(element);
            drawn += Box(type, zoom, line);
        }

        return painted;
    }

    /// <summary>
    /// How many of these lines the card can draw in <paramref name="roomScreen"/>. Charged with the
    /// box the engine draws at this zoom (see <see cref="Box"/>): Core's world-unit rule is right for
    /// the frame, but a line rounded at 100% and then multiplied is not one multiplied and then
    /// rounded, and the difference is how much of the last row survives. The first line is always
    /// admitted — a card that shows nothing is worse than one that clips.
    /// </summary>
    public static int Count(NoteType type, double zoom, IReadOnlyList<MarkdownLine> lines, double roomScreen)
    {
        var used = 0.0;
        var count = 0;

        while (count < lines.Count && (count == 0 || used + Box(type, zoom, lines[count]) <= roomScreen))
        {
            used += Box(type, zoom, lines[count]);
            count++;
        }

        return count;
    }

    /// <summary>The whole-pixel line box the text engine gives this line at this zoom, half-up — the
    /// measured rule behind <see cref="Markdown.LineHeight"/>.</summary>
    public static double Box(NoteType type, double zoom, MarkdownLine line)
        => Math.Max(4, Math.Round(type.Leading * zoom * line.Scale, MidpointRounding.AwayFromZero));

    /// <summary>
    /// Measures the block at the width it will actually be painted at, returning both the total and
    /// each line's own height. The old source-line count treated a long paragraph as one row; at a
    /// different zoom that paragraph wraps to several rows and the card clipped without ever showing
    /// the fade. <see cref="Line"/> is the same element the paint uses, so the two cannot disagree.
    /// </summary>
    public static (double Total, List<double> Heights) Measured(
        NoteType type,
        double zoom,
        IReadOnlyList<MarkdownLine> lines,
        double widthScreen,
        SolidColorBrush ink,
        SolidColorBrush quoteBrush,
        SolidColorBrush quoteBar,
        XamlRoot? root)
    {
        var panel = new StackPanel
        {
            IsHitTestVisible = false,
            Width = Math.Max(20, widthScreen),
        };

        if (root is not null)
        {
            panel.XamlRoot = root;
        }

        foreach (var line in lines)
        {
            panel.Children.Add(Line(type, zoom, line, ink, quoteBrush, quoteBar));
        }

        panel.Measure(new Size(Math.Max(20, widthScreen), double.PositiveInfinity));

        var heights = new List<double>(lines.Count);

        foreach (var child in panel.Children)
        {
            heights.Add(((FrameworkElement)child).DesiredSize.Height);
        }

        return (panel.DesiredSize.Height, heights);
    }

    /// <summary>Every block line, with no trimming: the card clips at its own edge and the fade
    /// covers the cut. The old count-based trim is what made a markdown card end in a hard cut.</summary>
    public static List<UIElement> PaintAll(
        NoteType type,
        double zoom,
        IReadOnlyList<MarkdownLine> lines,
        SolidColorBrush ink,
        SolidColorBrush quoteBrush,
        SolidColorBrush quoteBar)
    {
        var painted = new List<UIElement>(lines.Count);

        foreach (var line in lines)
        {
            painted.Add(Line(type, zoom, line, ink, quoteBrush, quoteBar));
        }

        return painted;
    }

    /// <summary>One painted line: a TextBlock, or a quoted line wrapped in the bar it is drawn with.</summary>
    public static FrameworkElement Line(
        NoteType type,
        double zoom,
        MarkdownLine line,
        SolidColorBrush ink,
        SolidColorBrush quoteBrush,
        SolidColorBrush quoteBar)
    {
        var box = Box(type, zoom, line);
        var size = Math.Max(4, type.Size * zoom * line.Scale);

        var label = IsPlain(line)
            ? new TextBlock { Text = line.Spans[0].Text }
            : Parse(line, size);

        label.FontFamily = NoteStyling.NoteFont;
        label.FontSize = size;
        label.LineHeight = box;
        label.TextWrapping = TextWrapping.Wrap;
        label.IsHitTestVisible = false;
        label.Foreground = line.Kind == BlockKind.Quote ? quoteBrush : ink;
        // 600 在这台机器上拿不到真字面：微软雅黑只装了 Light/Regular/Bold（注册表里 msyhl/msyh/msyhbd），
        // 于是中文标题走合成粗体——笔画是算法加粗的，100% 缩放下看着就是一团糊。700 有真字面。
        label.FontWeight = line.Kind is BlockKind.Heading1 or BlockKind.Heading2 or BlockKind.Heading3
            ? FontWeights.Bold
            : FontWeights.Normal;

        if (line.Kind == BlockKind.Divider)
        {
            // One hairline across the card, sitting in the middle of the same line box every other
            // block costs — so a divider cannot change the height arithmetic anywhere.
            return new Border
            {
                Height = box,
                Padding = new Thickness(0, Math.Round(box / 2) - 1, 0, 0),
                Child = new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Height = 1,
                    VerticalAlignment = VerticalAlignment.Top,
                    Fill = new SolidColorBrush(NoteStyling.WithAlpha(Badge.LineColour(NoteStyling.IsDark), Badge.LineAlpha(NoteStyling.IsDark))),
                },
                IsHitTestVisible = false,
            };
        }

        if (line.Kind != BlockKind.Quote)
        {
            return label;
        }

        // The bar is a box, not a glyph: Core leaves the quote's Prefix empty so the two do not
        // draw the same mark twice.
        return new Border
        {
            BorderBrush = quoteBar,
            BorderThickness = new Thickness(QuoteBarPx * zoom, 0, 0, 0),
            Padding = new Thickness(QuoteGapPx * zoom, 0, 0, 0),
            IsHitTestVisible = false,
            Child = label,
        };
    }

    /// <summary>True when the line is one undecorated run — the common case, and the one that needs
    /// no parser.</summary>
    private static bool IsPlain(MarkdownLine line)
        => line.Prefix.Length == 0 && line.Spans.Count == 1 && line.Spans[0].Style == SpanStyle.None;

    private static TextBlock Parse(MarkdownLine line, double size)
    {
        var markup = new StringBuilder();

        markup.Append($"<TextBlock xmlns=\"{Presentation}\">");

        if (line.Prefix.Length > 0)
        {
            markup.Append($"<Run Text=\"{Escape(line.Prefix)}\"/>");
        }

        foreach (var span in line.Spans)
        {
            switch (span.Style)
            {
                case SpanStyle.Bold:
                    markup.Append($"<Run Text=\"{Escape(span.Text)}\" FontWeight=\"Bold\"/>");
                    break;

                case SpanStyle.Italic:
                    markup.Append($"<Run Text=\"{Escape(span.Text)}\" FontStyle=\"Italic\"/>");
                    break;

                case SpanStyle.Code:
                    // Mono and 0.92em, which is what a <code> is made of; the prototype's 5% tint
                    // has no per-run form here (see the class remarks).
                    markup.Append($"<Run Text=\"{Escape(span.Text)}\" FontFamily=\"{Mono}\" FontSize=\"{size * CodeScale:0.###}\"/>");
                    break;

                case SpanStyle.Link:
                    // The words, without the target: a card cannot be a hyperlink and a drag
                    // surface at once, and WinUI has no per-run underline to mark it with anyway.
                    markup.Append($"<Run Text=\"{Escape(span.Text)}\"/>");
                    break;

                default:
                    markup.Append($"<Run Text=\"{Escape(span.Text)}\"/>");
                    break;
            }
        }

        markup.Append("</TextBlock>");

        return (TextBlock)XamlReader.Load(markup.ToString());
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? string.Empty;

}
