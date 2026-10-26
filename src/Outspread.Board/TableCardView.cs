using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Outspread.Core;
using Windows.UI;

namespace Outspread.Board;

/// <summary>
/// Draws the grid a spreadsheet paste left behind. Every cell answers to a double-click: this card is
/// a table to be edited, not a picture of a table that has to be re-pasted.
/// </summary>
internal sealed class TableCardView : Grid
{
    private readonly Border _frame = new()
    {
        BorderThickness = new Thickness(1),
        Background = new SolidColorBrush(Colors.Transparent),
        CornerRadius = new CornerRadius(0),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private TableGrid? _grid;
    private string _key = string.Empty;

    /// <summary>The drawn cells, so the window can ask which one a double-click landed on.</summary>
    private readonly List<(int Row, int Column, Border Frame)> _cells = [];

    public TableCardView()
    {
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Top;
        Children.Add(_frame);
    }

    /// <summary>命中在表格哪一格上，连同它自己画出来的方框。卡片自己的 DoubleTapped 收不到——
    /// 按下时板子把指针捕获走了，所以这一问由 <c>OnSurfaceDoubleTapped</c> 发起，判据和画出来的
    /// 那一格是同一份，"看见的那一格"与"改掉的那一格"才不会是两格。</summary>
    public (int Row, int Column, Windows.Foundation.Rect Bounds)? CellAt(Windows.Foundation.Point at, UIElement relativeTo)
    {
        foreach (var cell in _cells)
        {
            var bounds = cell.Frame.TransformToVisual(relativeTo)
                .TransformBounds(new Windows.Foundation.Rect(0, 0, cell.Frame.ActualWidth, cell.Frame.ActualHeight));

            if (bounds.Contains(at))
            {
                return (cell.Row, cell.Column, bounds);
            }
        }

        return null;
    }

    public void Render(
        TableGrid grid,
        double zoom,
        double bodyWidth,
        double maxHeight,
        Color ink,
        bool dark)
    {
        var scale = Math.Max(0.35, zoom);
        var key = string.Join(
            '|',
            Math.Round(bodyWidth, 1),
            Math.Round(maxHeight, 1),
            Math.Round(scale, 2),
            dark,
            ink);

        if (ReferenceEquals(_grid, grid) && string.Equals(key, _key, StringComparison.Ordinal))
        {
            return;
        }

        _grid = grid;
        _key = key;

        var line = new SolidColorBrush(NoteStyling.FallbackLine());
        var rowHeight = Math.Clamp(maxHeight / Math.Max(1, grid.RowCount), 18 * scale, EntityCardLayout.TableLineWorld * scale);
        var tableHeight = grid.RowCount * rowHeight;

        _frame.BorderBrush = line;
        _frame.Height = tableHeight;
        Height = tableHeight;
        _cells.Clear();

        var table = new Grid();
        var weights = grid.ColumnWeights.Count == grid.ColumnCount
            ? grid.ColumnWeights
            : Enumerable.Repeat(1d, grid.ColumnCount).ToList();

        foreach (var weight in weights)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(Math.Max(0.01, weight), GridUnitType.Star),
            });
        }

        for (var row = 0; row < grid.RowCount; row++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(rowHeight) });
        }

        var typeColor = EntityCardPalette.TypeColor(EntityCardKind.Table, dark);

        foreach (var placement in grid.Placements())
        {
            var cell = placement.Cell;
            var explicitFill = ParseFill(cell.Fill);
            var fill = explicitFill;

            if (fill is null && cell.Header)
            {
                fill = Color.FromArgb(0x18, typeColor.R, typeColor.G, typeColor.B);
            }

            var content = new TextBlock
            {
                Text = cell.Text,
                FontFamily = NoteStyling.NoteFont,
                FontSize = Math.Max(4, 12 * scale),
                LineHeight = Math.Max(5, 17 * scale),
                FontWeight = cell.Bold || cell.Header ? FontWeights.SemiBold : FontWeights.Normal,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(explicitFill is null ? ink : NoteStyling.InkFor(explicitFill.Value)),
            };

            var frame = new Border
            {
                Background = fill is null ? new SolidColorBrush(Colors.Transparent) : new SolidColorBrush(fill.Value),
                BorderBrush = line,
                BorderThickness = new Thickness(
                    0,
                    0,
                    placement.Column + cell.ColSpan >= grid.ColumnCount ? 0 : 1,
                    placement.Row + cell.RowSpan >= grid.RowCount ? 0 : 1),
                Padding = new Thickness(4 * scale, 0, 4 * scale, 0),
                Child = content,
            };

            var row = placement.Row;
            var column = placement.Column;

            Grid.SetRow(frame, row);
            Grid.SetColumn(frame, column);
            Grid.SetRowSpan(frame, cell.RowSpan);
            Grid.SetColumnSpan(frame, cell.ColSpan);
            table.Children.Add(frame);
            _cells.Add((row, column, frame));
        }

        _frame.Child = table;
    }

    private static Color? ParseFill(string? value)
    {
        if (value is null || value.Length != 7 || value[0] != '#')
        {
            return null;
        }

        if (!byte.TryParse(value[1..3], System.Globalization.NumberStyles.HexNumber, null, out var red)
            || !byte.TryParse(value[3..5], System.Globalization.NumberStyles.HexNumber, null, out var green)
            || !byte.TryParse(value[5..7], System.Globalization.NumberStyles.HexNumber, null, out var blue))
        {
            return null;
        }

        return Color.FromArgb(255, red, green, blue);
    }
}
