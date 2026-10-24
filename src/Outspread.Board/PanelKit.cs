using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;

namespace Outspread.Board;

/// <summary>
/// The one row type every floating surface is made of. Its shape is the right-click menu's, read off
/// his screenshots and off a live menu item: 32px tall, the words 16 in from the panel's edge, the
/// right column 12px and quiet, and a line that cannot be clicked rendered in the menu's own disabled
/// ink. The sheet around it is dressed by one method: <see cref="ChromeStyling.ApplyFloatingChrome"/>.
/// </summary>
/// <remarks>
/// <para>
/// His rule, third pass: "全部1:1完整复刻、复用右键菜单的样式，要让人感觉只是换了一下文字，一眼能看出来是
/// 一个组件". The first two passes got the numbers right and still read as two components, because what
/// differed was the *type system*: the menu has one kind of line and this app had three (14px ink rows,
/// 12px semi-bold letterspaced section titles, and keycaps where the menu has grey text). So the second
/// kind is gone and the third is gone with it.
/// </para>
/// <para>
/// Why this is not the platform's <c>MenuFlyoutItem</c>, which was tried first and is worth writing
/// down because it is the obvious next attempt: outside a real flyout, in WindowsAppSDK 2.5.1 —
/// a bare item makes <c>RenderTargetBitmap.RenderAsync</c> never return (the sheet stops being
/// photographable, which is the whole reason it is in the tree); hosted by a
/// <c>MenuFlyoutPresenter</c> it renders, but at a 39.62px row box against the menu's 32 (the live
/// flyout gets that box from per-item presenters a row here cannot get), its template collapses the
/// whole auto-width host to 240px, and the accelerator column — the thing that carries "Alt+A" and the
/// counts — is present but <c>Collapsed</c> (read out of a realized row: <c>『2』Collapsed/12px/0w</c>)
/// with no <c>IsKeyboardAcceleratorTextVisible</c> in this projection to switch it on. What that run
/// did buy is every number below, measured off the platform's own rendering instead of off a
/// screenshot: the row's ink is rgb 27,27,27, a disabled row's is rgb 161,161,162, and the right column
/// is 12px.
/// </para>
/// <para>
/// The right column has two renderers, not two kinds of line: grey accelerator text (the filter panel's
/// counts) or the keys themselves as keycaps (the key sheet's chords) — he asked for the caps back on
/// 2026-10-12, one round after they were dropped to make the sheets read as the menu. Same row, same
/// band, same label; only what paints the right column changes.
/// </para>
/// </remarks>
internal static class PanelKit
{
    /// <summary>Rows carry this so a surface can be asked "how tall is each of your rows, and what ink
    /// did you paint them with" — by type alone a column and a row are both panels, and a probe that
    /// walks by type measures a whole column and calls it a line.</summary>
    public const string RowTag = "sheet-row";

    /// <summary>One built row. The text is settable because two of the four surfaces (the filter
    /// banner and the say-chip) are the same row with a different sentence in it, said over and over —
    /// rebuilding them per message would be a second way to build a row.</summary>
    public sealed class SheetRow : ContentControl
    {
        private readonly TextBlock _name;
        private readonly TextBlock? _hint;

        internal SheetRow(Grid grid, TextBlock name, TextBlock? hint)
        {
            _name = name;
            _hint = hint;

            Tag = RowTag;
            Background = new SolidColorBrush(Colors.Transparent);
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Content = grid;
        }

        public string Text
        {
            get => _name.Text;
            set => _name.Text = value;
        }

        /// <summary>给自检读的那两把尺：这一行的字号与墨色，四块浮层只许出现一套。</summary>
        public double FontPx => _name.FontSize;

        public Brush Ink => _name.Foreground;
    }

    /// <summary>
    /// One row: the words on the left, the chord or the count on the right, one band tall whatever is
    /// in it. This is a line with something in it — a command, a count, a sentence — so it is painted
    /// in the menu's body ink. A line that only *names* a group is <see cref="Line"/>.
    /// <para>
    /// <paramref name="keys"/> says how the right column is drawn: as the menu's grey accelerator text,
    /// or as the keys themselves. It is a renderer for the same column, not a third kind of line.
    /// </para>
    /// </summary>
    public static SheetRow Row(string label, string? hint = null, Action? onClick = null, bool keys = false)
        => Build(label, hint, onClick, quiet: false, keys: keys, band: Sheet.RowPx);

    /// <summary>
    /// The one-sentence surfaces (the filter banner, the receipt line): the same row — one font, one
    /// body ink, the same inset — at <see cref="Sheet.OneLineRowPx"/>. A sentence is not a row a
    /// finger has to hit, so it does not get the list band.
    /// </summary>
    public static SheetRow OneLine(string label, string? hint = null, Action? onClick = null)
        => Build(label, hint, onClick, quiet: false, keys: false, band: Sheet.OneLineRowPx);

    /// <summary>A quiet line — a section title, an empty-board note. The menu has no such thing, so
    /// this borrows the look of the thing it is closest to: a disabled row, in the menu's own
    /// disabled ink. It is still the same row, at the same size, on the same band.</summary>
    public static SheetRow Line(string label) => Build(label, null, null, quiet: true, keys: false, band: Sheet.RowPx);

    /// <summary>
    /// The one格 you can type into. It is a row that holds a <see cref="TextBox"/> instead of two
    /// text columns: same band, same 12px in from the sheet's edge, same 14px body ink — that is why
    /// it has no border and no background of its own, since a boxed input inside a sheet is the
    /// platform's control walking in wearing its own clothes.
    /// </summary>
    /// <remarks>
    /// The placeholder is the sheet's only self-description, so it carries the keys the query answers
    /// to. It is painted in the disabled ink: a suggestion is not a sentence the user wrote.
    /// <para>
    /// What is still wrong with it, measured off a capture (<c>v5-search.png</c>, 2026-10-12): the
    /// default template adds an accent underline when the box is focused and a round clear button when
    /// it has text. Inside a square, hairline-edged sheet those two are exactly the
    /// "这不是同一个组件" this file exists to prevent.
    /// </para>
    /// <para>
    /// Replacing the template with a bare one was tried and made no difference to anything except the
    /// look, so it is not done here. What is left: override the template's own theme resources on the
    /// panel, or key a real style off <c>TextBox</c>. Neither is a one-line change, and the box is
    /// usable and correctly sized (40px, same band as every row) until then.
    /// </para>
    /// </remarks>
    public static TextBox Input(string placeholder)
    {
        var dark = NoteStyling.IsDark;

        return new TextBox
        {
            FontSize = Sheet.FontPx,
            MinHeight = Sheet.RowPx,
            Padding = new Thickness(Sheet.PadX, 0, Sheet.PadX, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            Foreground = new SolidColorBrush(NoteStyling.ToColor(Sheet.Ink(dark))),
            PlaceholderText = placeholder,
            PlaceholderForeground = new SolidColorBrush(NoteStyling.ToColor(Sheet.Quiet(dark))),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
    }

    private static SheetRow Build(string label, string? hint, Action? onClick, bool quiet, bool keys, double band)
    {
        var dark = NoteStyling.IsDark;

        var grid = new Grid
        {
            MinHeight = band,
            Padding = new Thickness(Sheet.PadX, 0, Sheet.PadX, 0),
            ColumnSpacing = Sheet.GapPx,
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var name = new TextBlock
        {
            Text = label,
            FontSize = Sheet.FontPx,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = new SolidColorBrush(NoteStyling.ToColor(quiet ? Sheet.Quiet(dark) : Sheet.Ink(dark))),
        };

        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        TextBlock? right = null;

        if (!string.IsNullOrEmpty(hint))
        {
            // 右列那一格只在真要有内容时才存在：Grid 的 ColumnSpacing 是按"列与列之间"算的，
            // 空列照样吃那 14px——量过回执那一枚（左 17px、右 30px），字就因此不居中、
            // 盒子还比需要宽一截。没有右列的行是一整行，不是一行加一段空气。
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            FrameworkElement column;

            if (keys)
            {
                column = Keys(hint!);
            }
            else
            {
                right = new TextBlock
                {
                    Text = hint,
                    FontSize = Sheet.HintPx,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(NoteStyling.ToColor(Sheet.HintInk(dark))),
                };

                column = right;
            }

            Grid.SetColumn(column, 1);
            grid.Children.Add(column);
        }

        var row = new SheetRow(grid, name, right);

        if (onClick is null)
        {
            return row;
        }

        // Hover is the row's only state, and it means one thing: this row does something.
        var hover = new SolidColorBrush(NoteStyling.WithAlpha(Sheet.Hover(dark), Sheet.HoverAlpha(dark)));

        row.PointerEntered += (_, _) => row.Background = hover;
        row.PointerExited += (_, _) => row.Background = new SolidColorBrush(Colors.Transparent);

        // 按下这一发由行自己吃掉：浮层的皮上"按到空白＝收起"，若让按下穿到面板，
        // 行会在抬起之前被 Collapsed——Tapped 就没地方落，点了结果什么都不会发生
        // （10-24 他报的"搜索不好用"就是这个）。注上这一句，点击落定后浮层仍开着。
        row.PointerPressed += (_, e) => e.Handled = true;

        row.Tapped += (_, e) =>
        {
            e.Handled = true;

            onClick();
        };

        return row;
    }

    /// <summary>A sheet's rows, stacked with no air of their own: the 32px band is the rhythm and the
    /// sheet's 4px is the only padding. Each column of the key sheet is one of these.</summary>
    public static StackPanel Host(params FrameworkElement[] rows)
    {
        var body = new StackPanel { Spacing = 0 };

        foreach (var row in rows)
        {
            body.Children.Add(row);
        }

        return body;
    }

    /// <summary>A chord as one key per stroke: <c>Alt+Shift+G</c> → [Alt][Shift][G]. A chord with no
    /// <c>+</c> ("Esc", "双击空白") is one key, which keeps the right column one shape.</summary>
    private static StackPanel Keys(string chord)
    {
        var keys = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = Sheet.KeyGap,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (var key in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            keys.Children.Add(Cap(key));
        }

        return keys;
    }

    /// <summary>一枚键帽：最小宽 22、高 23、内距 1/6、1px 边带 2px 下唇、圆角 4、字 12。
    /// 底色取板底——键帽要看起来像压在面板上的实物，用面板同色就只是一格文字。</summary>
    private static Border Cap(string text)
    {
        var dark = NoteStyling.IsDark;

        return new Border
        {
            MinWidth = Sheet.KeyMinWidth,
            Height = Sheet.KeyHeight,
            Padding = new Thickness(Sheet.KeyPadX, Sheet.KeyPadY, Sheet.KeyPadX, Sheet.KeyPadY),
            CornerRadius = new CornerRadius(Sheet.KeyRadius),
            BorderThickness = new Thickness(1, 1, 1, 2),
            BorderBrush = new SolidColorBrush(NoteStyling.WithAlpha(Badge.LineColour(dark), Badge.LineAlpha(dark))),
            Background = new SolidColorBrush(NoteStyling.ToColor(BoardGrid.BoardBackground(dark))),
            Child = new TextBlock
            {
                Text = text,
                FontSize = Sheet.KeyFont,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                // 键帽上的字是右列那一档（96），不是正文墨（27）：30 行里 60 枚键帽用正文墨会把
                // 整张页读成两张表叠在一起，也和他 10-23 点头的原型（纸键帽、灰字）不是一张。
                Foreground = new SolidColorBrush(NoteStyling.ToColor(Sheet.HintInk(dark))),
            },
        };
    }
}
