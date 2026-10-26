using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Outspread.Core;

/// <summary>One cell of a pasted table. Spans are 1 when the source did not merge anything.</summary>
public sealed class TableCell
{
    public string Text { get; set; } = string.Empty;

    public int ColSpan { get; set; } = 1;

    public int RowSpan { get; set; } = 1;

    public string? Fill { get; set; }

    public bool Bold { get; set; }

    public bool Header { get; set; }

    public double? Width { get; set; }
}

/// <summary>A cell after row/column span placement.</summary>
public readonly record struct TablePlacement(int Row, int Column, TableCell Cell);

/// <summary>
/// The structure kept for a pasted spreadsheet. Rendering and geometry both read this, so a
/// merged cell cannot be drawn in one place and counted in another.
/// </summary>
public sealed class TableGrid
{
    public const int MaxRows = 200;

    public const int MaxColumns = 40;

    public const int MaxCells = 2000;

    public const int MaxCellChars = 2000;

    public List<TableCell[]> Rows { get; } = [];

    public List<double> ColumnWeights { get; } = [];

    public List<double> HintedColumnWeights { get; } = [];

    public string Title { get; set; } = "表格";

    public int ColumnCount { get; private set; }

    public int RowCount => Rows.Count;

    public int CellCount => Rows.Sum(row => row.Length);

    public bool IsUsable => RowCount > 0 && ColumnCount > 0 && CellCount > 1;

    public void AddRow(IEnumerable<TableCell> cells)
    {
        if (Rows.Count >= MaxRows)
        {
            return;
        }

        var row = cells.Take(MaxColumns).ToArray();

        if (row.Length > 0)
        {
            Rows.Add(row);
        }
    }

    /// <summary>Clamps spans to this table and derives the column count and widths.</summary>
    public void Finish()
    {
        if (Rows.Count > MaxRows)
        {
            Rows.RemoveRange(MaxRows, Rows.Count - MaxRows);
        }

        var cells = 0;

        for (var row = 0; row < Rows.Count; row++)
        {
            var kept = new List<TableCell>();

            foreach (var cell in Rows[row])
            {
                if (cells >= MaxCells)
                {
                    break;
                }

                cell.ColSpan = Math.Clamp(cell.ColSpan, 1, MaxColumns);
                cell.RowSpan = Math.Clamp(cell.RowSpan, 1, Rows.Count - row);
                cell.Text = Truncate(cell.Text, MaxCellChars);
                kept.Add(cell);
                cells++;
            }

            Rows[row] = kept.ToArray();
        }

        var placements = Placements();
        ColumnCount = placements.Count == 0 ? 0 : placements.Max(item => item.Column + item.Cell.ColSpan);

        NormalizeWeights(placements);
    }

    /// <summary>The cell covering (row, column). A merged cell answers at the anchor it was placed at,
    /// so double-clicking any part of it edits the one cell that is actually there.</summary>
    public TableCell? CellAt(int row, int column)
    {
        foreach (var item in Placements())
        {
            if (row >= item.Row
                && row < item.Row + item.Cell.RowSpan
                && column >= item.Column
                && column < item.Column + item.Cell.ColSpan)
            {
                return item.Cell;
            }
        }

        return null;
    }

    public bool TrySetCellText(int row, int column, string? text)
    {
        var cell = CellAt(row, column);

        if (cell is null)
        {
            return false;
        }

        cell.Text = Truncate(CollapseLines(text ?? string.Empty), MaxCellChars);

        return true;
    }

    /// <summary>
    /// One cell is one line on the board: line breaks read as spaces, and runs of spaces inside the
    /// line collapse — the same shape the paste path already gives a cell, so typing and pasting
    /// cannot leave two different-looking cells with the same words.
    /// </summary>
    private static string CollapseLines(string value)
        => string.Join(' ', value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Where(line => line.Length > 0));

    public IReadOnlyList<TablePlacement> Placements()
    {
        var occupied = new Dictionary<int, HashSet<int>>();
        var placed = new List<TablePlacement>();

        for (var row = 0; row < Rows.Count; row++)
        {
            if (!occupied.TryGetValue(row, out var taken))
            {
                taken = [];
                occupied[row] = taken;
            }

            var column = 0;

            foreach (var cell in Rows[row])
            {
                while (taken.Contains(column) && column < MaxColumns)
                {
                    column++;
                }

                if (column >= MaxColumns)
                {
                    break;
                }

                placed.Add(new TablePlacement(row, column, cell));

                for (var r = row; r < Math.Min(Rows.Count, row + cell.RowSpan); r++)
                {
                    if (!occupied.TryGetValue(r, out var line))
                    {
                        line = [];
                        occupied[r] = line;
                    }

                    for (var c = column; c < Math.Min(MaxColumns, column + cell.ColSpan); c++)
                    {
                        line.Add(c);
                    }
                }

                column += cell.ColSpan;
            }
        }

        return placed;
    }

    private void NormalizeWeights(IReadOnlyList<TablePlacement> placements)
    {
        ColumnWeights.Clear();

        if (ColumnCount <= 0)
        {
            return;
        }

        if (HintedColumnWeights.Count > 0)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                ColumnWeights.Add(column < HintedColumnWeights.Count && HintedColumnWeights[column] > 0
                    ? HintedColumnWeights[column]
                    : 1);
            }

            return;
        }

        var weights = new double[ColumnCount];

        foreach (var item in placements)
        {
            if (item.Cell.ColSpan == 1 && item.Cell.Width is > 0 and var width && item.Column < weights.Length)
            {
                weights[item.Column] = Math.Max(weights[item.Column], width);
            }
        }

        var positive = weights.Where(value => value > 0).ToArray();
        var fill = positive.Length > 0 ? positive.Average() : 1;

        for (var column = 0; column < weights.Length; column++)
        {
            ColumnWeights.Add(weights[column] > 0 ? weights[column] : fill);
        }
    }

    public string ToMarkdown()
    {
        if (!IsUsable)
        {
            return string.Empty;
        }

        var matrix = new string[RowCount, ColumnCount];

        for (var row = 0; row < RowCount; row++)
        {
            for (var column = 0; column < ColumnCount; column++)
            {
                matrix[row, column] = string.Empty;
            }
        }

        foreach (var item in Placements())
        {
            if (item.Row < RowCount && item.Column < ColumnCount)
            {
                matrix[item.Row, item.Column] = item.Cell.Text
                    .Replace("|", "\\|", StringComparison.Ordinal)
                    .Replace("\r", string.Empty, StringComparison.Ordinal)
                    .Replace("\n", "<br>", StringComparison.Ordinal);
            }
        }

        var builder = new StringBuilder();

        for (var row = 0; row < RowCount; row++)
        {
            builder.Append("| ");

            for (var column = 0; column < ColumnCount; column++)
            {
                builder.Append(matrix[row, column]);
                builder.Append(column == ColumnCount - 1 ? " |" : " | ");
            }

            builder.AppendLine();

            if (row == 0)
            {
                builder.Append('|');

                for (var column = 0; column < ColumnCount; column++)
                {
                    builder.Append(" --- |");
                }

                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    public JsonObject ToJson()
    {
        var rows = new JsonArray();

        foreach (var row in Rows)
        {
            var cells = new JsonArray();

            foreach (var cell in row)
            {
                var json = new JsonObject
                {
                    ["text"] = cell.Text,
                };

                if (cell.ColSpan != 1)
                {
                    json["colSpan"] = cell.ColSpan;
                }

                if (cell.RowSpan != 1)
                {
                    json["rowSpan"] = cell.RowSpan;
                }

                if (cell.Header)
                {
                    json["header"] = true;
                }

                if (cell.Bold)
                {
                    json["bold"] = true;
                }

                if (cell.Fill is { Length: > 0 })
                {
                    json["fill"] = cell.Fill;
                }

                if (cell.Width is > 0)
                {
                    json["width"] = Math.Round(cell.Width.Value, 1);
                }

                cells.Add(json);
            }

            rows.Add(cells);
        }

        var result = new JsonObject
        {
            ["version"] = 1,
            ["title"] = Title,
            ["rows"] = rows,
        };

        if (ColumnWeights.Count > 0)
        {
            var weights = new JsonArray();

            foreach (var weight in ColumnWeights)
            {
                weights.Add(Math.Round(weight, 2));
            }

            result["columns"] = weights;
        }

        return result;
    }

    public static bool LooksLikeTable(JsonNode? json)
        => json is JsonObject table && table["rows"] is JsonArray;

    public static TableGrid? FromJson(JsonNode? json)
    {
        if (json is not JsonObject table || table["rows"] is not JsonArray rows)
        {
            return null;
        }

        var grid = new TableGrid
        {
            Title = table["title"] is JsonValue title ? title.ToString() : "表格",
        };

        foreach (var rowNode in rows)
        {
            if (rowNode is not JsonArray row)
            {
                continue;
            }

            var cells = new List<TableCell>();

            foreach (var cellNode in row)
            {
                if (cellNode is not JsonObject cell)
                {
                    continue;
                }

                cells.Add(new TableCell
                {
                    Text = cell["text"]?.ToString() ?? string.Empty,
                    ColSpan = Number(cell["colSpan"], 1),
                    RowSpan = Number(cell["rowSpan"], 1),
                    Header = Bool(cell["header"]),
                    Bold = Bool(cell["bold"]),
                    Fill = TablePaste.NormalizeFill(cell["fill"]?.ToString()),
                    Width = Double(cell["width"]),
                });
            }

            grid.AddRow(cells);
        }

        grid.Finish();
        return grid.IsUsable ? grid : null;
    }

    private static int Number(JsonNode? node, int fallback)
        => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    private static double? Double(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static bool Bool(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static string Truncate(string value, int limit)
        => value.Length <= limit ? value : value[..limit];
}

/// <summary>Reads the table Excel puts on the clipboard, with a TSV fallback.</summary>
public static partial class TablePaste
{
    public const int MaxHtmlChars = 2_000_000;

    public static TableGrid? Read(string? html, string? text)
        => ReadHtml(html) ?? ReadTsv(text);

    public static TableGrid? ReadHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var start = html.IndexOf("<table", StringComparison.OrdinalIgnoreCase);

        if (start < 0)
        {
            return null;
        }

        var end = html.IndexOf("</table", start, StringComparison.OrdinalIgnoreCase);
        var slice = end < 0
            ? html[start..Math.Min(html.Length, start + MaxHtmlChars)]
            : html[start..Math.Min(html.Length, end + 8)];

        var grid = new TableGrid();
        var row = new List<TableCell>();
        TableCell? cell = null;
        var cellText = new StringBuilder();
        var caption = new StringBuilder();
        var hints = new List<double>();
        var inCaption = false;

        void FinishCell()
        {
            if (cell is null)
            {
                return;
            }

            cell.Text = NormalizeText(cellText.ToString());
            row.Add(cell);
            cell = null;
            cellText.Clear();
        }

        void FinishRow()
        {
            if (row.Count > 0)
            {
                grid.AddRow(row);
                row = [];
            }
        }

        foreach (Match match in TagOrTextPattern().Matches(slice))
        {
            var token = match.Value;

            if (!token.StartsWith('<'))
            {
                if (inCaption)
                {
                    caption.Append(token);
                }
                else if (cell is not null)
                {
                    cellText.Append(token);
                }

                continue;
            }

            var tag = TagNamePattern().Match(token);

            if (!tag.Success)
            {
                continue;
            }

            var closing = tag.Groups[1].Value.Length > 0;
            var name = tag.Groups[2].Value.ToLowerInvariant();
            var attributes = tag.Groups[3].Value;

            switch (name)
            {
                case "caption" when !closing:
                    inCaption = true;
                    break;

                case "caption" when closing:
                    inCaption = false;
                    grid.Title = NormalizeText(caption.ToString()) is { Length: > 0 } title ? title : "表格";
                    break;

                case "tr" when !closing:
                    FinishCell();
                    FinishRow();
                    break;

                case "tr" when closing:
                    FinishCell();
                    FinishRow();
                    break;

                case "td" or "th" when !closing:
                    FinishCell();
                    cell = NewCell(attributes, name == "th");
                    break;

                case "td" or "th" when closing:
                    FinishCell();
                    break;

                case "br" when !closing:
                    if (cell is not null)
                    {
                        cellText.Append('\n');
                    }

                    break;

                case "col" when !closing:
                    if (ParseWidth(FirstAttribute(attributes, "width")) is { } hint && hint > 0)
                    {
                        hints.Add(hint);
                    }

                    break;
            }
        }

        FinishCell();
        FinishRow();
        grid.HintedColumnWeights.AddRange(hints);
        grid.Finish();

        return grid.IsUsable ? grid : null;
    }

    public static TableGrid? ReadTsv(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('\t'))
        {
            return null;
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count < 2)
        {
            return null;
        }

        var grid = new TableGrid();
        var columns = 0;

        foreach (var line in lines)
        {
            var cells = line.Split('\t').Select(value => new TableCell { Text = value.Trim() }).ToList();

            while (cells.Count > 1 && cells[^1].Text.Length == 0)
            {
                cells.RemoveAt(cells.Count - 1);
            }

            columns = Math.Max(columns, cells.Count);
            grid.AddRow(cells);
        }

        if (columns < 2 || grid.RowCount < 2)
        {
            return null;
        }

        grid.Finish();
        return grid.IsUsable ? grid : null;
    }

    public static string? NormalizeFill(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();

        if (trimmed.Equals("transparent", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var hex = HexPattern().Match(trimmed);

        if (hex.Success)
        {
            var digits = hex.Groups[1].Value;

            if (digits.Length == 3)
            {
                digits = string.Concat(digits.Select(character => new string(character, 2)));
            }

            return "#" + digits.ToLowerInvariant();
        }

        var rgb = RgbPattern().Match(trimmed);

        if (rgb.Success
            && int.TryParse(rgb.Groups[1].Value, out var red)
            && int.TryParse(rgb.Groups[2].Value, out var green)
            && int.TryParse(rgb.Groups[3].Value, out var blue))
        {
            return $"#{Math.Clamp(red, 0, 255):x2}{Math.Clamp(green, 0, 255):x2}{Math.Clamp(blue, 0, 255):x2}";
        }

        return null;
    }

    private static string? FirstAttribute(string attributes, string name)
    {
        foreach (Match attribute in AttributePattern().Matches(attributes))
        {
            if (!string.Equals(attribute.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return attribute.Groups[2].Success ? attribute.Groups[2].Value
                : attribute.Groups[3].Success ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;
        }

        return null;
    }

    private static TableCell NewCell(string attributes, bool header)
    {
        var cell = new TableCell { Header = header, Bold = header };

        foreach (Match attribute in AttributePattern().Matches(attributes))
        {
            var name = attribute.Groups[1].Value.ToLowerInvariant();
            var value = attribute.Groups[2].Success ? attribute.Groups[2].Value
                : attribute.Groups[3].Success ? attribute.Groups[3].Value
                : attribute.Groups[4].Value;

            if (name == "colspan" && int.TryParse(value, out var colSpan))
            {
                cell.ColSpan = Math.Clamp(colSpan, 1, TableGrid.MaxColumns);
            }
            else if (name == "rowspan" && int.TryParse(value, out var rowSpan))
            {
                cell.RowSpan = Math.Clamp(rowSpan, 1, TableGrid.MaxRows);
            }
            else if (name == "width")
            {
                cell.Width = ParseWidth(value);
            }
            else if (name == "style")
            {
                ApplyStyle(cell, value);
            }
        }

        return cell;
    }

    private static void ApplyStyle(TableCell cell, string style)
    {
        foreach (var declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = declaration.IndexOf(':');

            if (separator <= 0)
            {
                continue;
            }

            var name = declaration[..separator].Trim().ToLowerInvariant();
            var value = declaration[(separator + 1)..].Trim();

            if (name is "background-color" or "background")
            {
                cell.Fill ??= NormalizeFill(value);
            }
            else if (name == "font-weight")
            {
                var bold = value.Equals("bold", StringComparison.OrdinalIgnoreCase)
                    || (int.TryParse(value, out var weight) && weight >= 600);

                if (bold)
                {
                    cell.Bold = true;
                }
            }
            else if (name == "width")
            {
                cell.Width ??= ParseWidth(value);
            }
        }
    }

    private static double? ParseWidth(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = WidthPattern().Match(value);

        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var width)
            ? width
            : null;
    }

    private static string NormalizeText(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var decoded = WebUtility.HtmlDecode(value)
            .Replace('\u00a0', ' ')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = decoded.Split('\n')
            .Select(line => SpacePattern().Replace(line, " ").Trim())
            .ToList();

        while (lines.Count > 0 && lines[0].Length == 0)
        {
            lines.RemoveAt(0);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var text = string.Join('\n', lines);

        return text.Length > TableGrid.MaxCellChars ? text[..TableGrid.MaxCellChars] : text;
    }

    [GeneratedRegex(@"(?is)<[^>]+>|[^<]+")]
    private static partial Regex TagOrTextPattern();

    [GeneratedRegex(@"(?is)<\s*(/?)\s*([a-z0-9]+)([^>]*)>")]
    private static partial Regex TagNamePattern();

    [GeneratedRegex(@"([a-zA-Z_:][-a-zA-Z0-9_:.]*)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))")]
    private static partial Regex AttributePattern();

    [GeneratedRegex(@"#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})\b")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex RgbPattern();

    [GeneratedRegex(@"^\s*([0-9.]+)")]
    private static partial Regex WidthPattern();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex SpacePattern();
}
