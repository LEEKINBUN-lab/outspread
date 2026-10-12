using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Outspread.Core;

public enum CanvasIssueSeverity
{
    Warning,
    Error,
}

public readonly record struct CanvasIssue(
    CanvasIssueSeverity Severity,
    string Message,
    string? NodeId = null);

/// <summary>
/// Reports what a document gets wrong without being the only thing that can say
/// so: <see cref="CanvasDocument.Parse"/> already refuses shapes that cannot be
/// edited, and everything else is a message an agent can act on.
/// </summary>
public static partial class CanvasValidator
{
    private static readonly Regex HexColor = new(
        "^#[0-9a-fA-F]{6}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] PresetColors = ["1", "2", "3", "4", "5", "6"];

    public static IReadOnlyList<CanvasIssue> Validate(CanvasDocument document)
    {
        var issues = new List<CanvasIssue>();
        var nodes = document.Nodes;
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            var id = node.Id;

            if (!NodeIds.IsValidId(id))
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Error,
                    $"node \"{id}\" has an id with characters this board will not accept (letters, digits, _ - . :)"));
            }
            else if (!NodeIds.FollowsNamespaceConvention(id))
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Warning,
                    $"id \"{id}\" does not start with \"u\" (minted by the board) or \"a\" (minted by an agent); " +
                    "fine to read, but the prefix is what keeps two writers from colliding",
                    id));
            }

            ids.Add(id);

            if (!CanvasNode.TryParseKind(node.GetString("type"), out var kind))
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Error,
                    $"node \"{id}\" has type \"{node.GetString("type")}\" — must be text, file, link or group",
                    id));
                continue;
            }

            foreach (var field in new[] { "x", "y", "width", "height" })
            {
                if (!node.HasProperty(field) || node.GetNumber(field) is null)
                {
                    issues.Add(new CanvasIssue(
                        CanvasIssueSeverity.Error,
                        $"node \"{id}\" is missing numeric \"{field}\"",
                        id));
                }
            }

            if (node.Width is <= 0 || node.Height is <= 0)
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Warning,
                    $"node \"{id}\" has width/height {node.Width}×{node.Height}; a zero-sized node cannot be clicked or dragged",
                    id));
            }

            CheckColor(issues, node);

            switch (kind)
            {
                case CanvasNodeKind.Text when string.IsNullOrEmpty(node.Text):
                    issues.Add(new CanvasIssue(
                        CanvasIssueSeverity.Warning,
                        $"text node \"{id}\" has no text",
                        id));
                    break;

                case CanvasNodeKind.File when string.IsNullOrEmpty(node.FilePath):
                    issues.Add(new CanvasIssue(
                        CanvasIssueSeverity.Error,
                        $"file node \"{id}\" has no \"file\" path",
                        id));
                    break;

                case CanvasNodeKind.File when !AssetStore.IsSafeRelativePath(node.FilePath!):
                    issues.Add(new CanvasIssue(
                        CanvasIssueSeverity.Error,
                        $"file node \"{id}\" points outside the board folder (\"{node.FilePath}\"); " +
                        "paths must be relative to the .canvas file and contain no \"..\"",
                        id));
                    break;

                case CanvasNodeKind.Link when string.IsNullOrEmpty(node.Url):
                    issues.Add(new CanvasIssue(
                        CanvasIssueSeverity.Error,
                        $"link node \"{id}\" has no \"url\"",
                        id));
                    break;
            }

            if (node.HasProperty("rotation"))
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Warning,
                    $"node \"{id}\" carries a \"rotation\" property. JSON Canvas 1.0 has no rotation and this board " +
                    "does not either, so the value is kept in the file but ignored on screen.",
                    id));
            }
        }

        CheckEdges(issues, document, ids);

        return issues;
    }

    private static void CheckColor(List<CanvasIssue> issues, CanvasNode node)
    {
        var color = node.Color;

        if (color is null)
        {
            return;
        }

        if (!HexColor.IsMatch(color) && Array.IndexOf(PresetColors, color) < 0)
        {
            issues.Add(new CanvasIssue(
                CanvasIssueSeverity.Warning,
                $"node \"{node.Id}\" has color \"{color}\" — the spec allows #RRGGBB or the presets \"1\" to \"6\"",
                node.Id));

            return;
        }

        // A colour can be perfectly legal in the format and still make the note unreadable:
        // #7F7F7F measures 4.2:1 against the better of the two inks. The board keeps the
        // colour, because it is the user's content, but says out loud that the text will be
        // hard to read rather than letting "看不见字" be discovered by eye.
        var surface = NotePalette.SurfaceFor(color, dark: false);
        var ratio = NotePalette.Contrast(NotePalette.InkFor(surface), surface);

        if (ratio < 4.5)
        {
            issues.Add(new CanvasIssue(
                CanvasIssueSeverity.Warning,
                $"node \"{node.Id}\" has color \"{color}\", which reaches only {ratio:0.0}:1 against the better of the two inks; " +
                "the colour is kept but the text will be hard to read",
                node.Id));
        }
    }

    private static void CheckEdges(List<CanvasIssue> issues, CanvasDocument document, HashSet<string> ids)
    {
        if (document.Root["edges"] is not JsonArray edges)
        {
            return;
        }

        var edgeIds = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < edges.Count; index++)
        {
            if (edges[index] is not JsonObject json)
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Error,
                    $"edge #{index} is not an object"));
                continue;
            }

            var edge = new CanvasNode(json);
            var id = edge.Id;

            if (string.IsNullOrEmpty(id))
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Error,
                    $"edge #{index} has no \"id\""));
            }
            else if (!edgeIds.Add(id))
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Error,
                    $"edge id \"{id}\" is used more than once",
                    id));
            }

            foreach (var field in new[] { "fromNode", "fromSide", "fromEnd", "toNode", "toSide", "toEnd" })
            {
                if (string.IsNullOrEmpty(edge.GetString(field)))
                {
                    issues.Add(new CanvasIssue(
                        CanvasIssueSeverity.Error,
                        $"edge \"{id ?? $"#{index}"}\" is missing \"{field}\"",
                        id));
                }
            }

            foreach (var endpoint in new[] { edge.GetString("fromNode"), edge.GetString("toNode") })
            {
                if (!string.IsNullOrEmpty(endpoint) && !ids.Contains(endpoint))
                {
                    issues.Add(new CanvasIssue(
                        CanvasIssueSeverity.Error,
                        $"edge \"{id ?? $"#{index}"}\" refers to node \"{endpoint}\", which is not in this board",
                        id));
                }
            }

            if (edge.HasProperty("fromEnd") && !IsEndpoint(edge.GetString("fromEnd")))
            {
                issues.Add(new CanvasIssue(
                    CanvasIssueSeverity.Warning,
                    $"edge \"{id ?? $"#{index}"}\" has fromEnd \"{edge.GetString("fromEnd")}\" — expected top, right, bottom, left, none or arrow",
                    id));
            }

            if (edge.HasProperty("color"))
            {
                var color = edge.GetString("color");

                if (!string.IsNullOrEmpty(color) && !HexColor.IsMatch(color) && Array.IndexOf(PresetColors, color) < 0)
                {
                    issues.Add(new CanvasIssue(
                        CanvasIssueSeverity.Warning,
                        $"edge \"{id ?? $"#{index}"}\" has color \"{color}\" — the spec allows #RRGGBB or \"1\" to \"6\"",
                        id));
                }
            }
        }
    }

    private static bool IsEndpoint(string? value)
        => value is ("top" or "right" or "bottom" or "left" or "none" or "arrow");
}
