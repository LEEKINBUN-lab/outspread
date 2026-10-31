using System.Text.Json;

namespace Outspread.Core;

/// <summary>Execution status of a helm-gate node, as the board displays it.</summary>
public enum GateStatus
{
    None,
    Planned,
    Doing,
    Blocked,
    Done,
    Dropped,
}

/// <summary>One gate node the board can draw a status chip for.</summary>
public sealed record GateChip(string NodeId, string CardId, GateStatus Status, int Progress, string Title)
{
    /// <summary>
    /// The chip this becomes on a card. Source is stable per gate node (so the
    /// chip repaint key flips when the label flips), and "done" rides the
    /// existing completed-chip shape (green with a check); the other states
    /// carry their own colour via <see cref="NoteMarks.Mark.Gate"/>.
    /// </summary>
    public NoteMarks.Mark ToMark()
        => new(
            Label: Status switch
            {
                GateStatus.Planned => "待开工",
                GateStatus.Doing => "进行中",
                GateStatus.Blocked => "堵住",
                GateStatus.Done => "已完成",
                GateStatus.Dropped => "已放弃",
                _ => "闸门",
            },
            Source: "gate:" + NodeId,
            IsDate: false,
            Done: Status == GateStatus.Done ? true : null,
            Gate: Status);
}

/// <summary>
/// Read-only projection of a board's helm-gate ledger (<c>&lt;board&gt;.gate.json</c>).
/// The plugin owns that file; the board only parses the few keys it draws and
/// never writes it. A missing file means the plugin is not installed for this
/// board — that is the normal case and must look exactly like "no chips".
/// </summary>
public static class GateProjection
{
    /// <summary>
    /// Loads the chips for a board. Returns null when the ledger is absent or
    /// unreadable — never throws: a broken optional plugin must not take the
    /// board down with it.
    /// </summary>
    public static IReadOnlyList<GateChip>? TryLoad(string boardPath)
    {
        var path = CanvasStore.GatePath(boardPath);

        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(CanvasStore.ReadAllText(path));

            if (!document.RootElement.TryGetProperty("nodes", out var nodes)
                || nodes.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var chips = new List<GateChip>();

            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object
                    || !node.TryGetProperty("card", out var cardElement)
                    || cardElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var card = cardElement.GetString();

                if (string.IsNullOrEmpty(card))
                {
                    continue;
                }

                var id = node.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() ?? string.Empty
                    : string.Empty;
                var title = node.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString() ?? string.Empty
                    : string.Empty;
                var status = ReadStatus(node);
                var progress = node.TryGetProperty("progress", out var progressElement)
                               && progressElement.TryGetInt32(out var value)
                    ? Math.Clamp(value, 0, 100)
                    : 0;

                chips.Add(new GateChip(id, card, status, progress, title));
            }

            return chips;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static GateStatus ReadStatus(JsonElement node)
    {
        if (!node.TryGetProperty("status", out var element) || element.ValueKind != JsonValueKind.String)
        {
            return GateStatus.None;
        }

        return element.GetString() switch
        {
            "planned" => GateStatus.Planned,
            "doing" => GateStatus.Doing,
            "blocked" => GateStatus.Blocked,
            "done" => GateStatus.Done,
            "dropped" => GateStatus.Dropped,
            _ => GateStatus.None,
        };
    }
}
