using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Outspread.Core;

/// <summary>
/// The small protocol between the command line and a running board.
/// </summary>
/// <remarks>
/// Reading the file tells an agent what is on the board. It does not tell an agent what
/// the board looks like — whether the text overflowed its note, whether an image decoded,
/// whether the ink is readable. That only exists on screen, so the running window answers
/// with a PNG.
///
/// The pipe name is derived from the resolved board path so two open boards do not answer
/// a request meant for the other one, and so neither side has to be told which process to
/// talk to.
/// </remarks>
public static class ShotProtocol
{
    public const string RequestedOp = "shot";

    public static string PipeName(string boardPath)
    {
        var full = Path.GetFullPath(boardPath).Replace('\\', '/').ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant();

        return $"outspread-shot-{hash[..16]}";
    }

    public static string Serialize(ShotRequest request)
        => JsonSerializer.Serialize(request, RequestOptions);

    public static string Serialize(ShotReply reply)
        => JsonSerializer.Serialize(reply, ReplyOptions);

    public static ShotRequest? ReadRequest(string json) => Deserialize<ShotRequest>(json);

    public static ShotReply? ReadReply(string json) => Deserialize<ShotReply>(json);

    private static T? Deserialize<T>(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static readonly JsonSerializerOptions RequestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReplyOptions = RequestOptions;
}

/// <param name="Mode">
/// <c>view</c> captures what is on screen now; <c>fit</c> frames the whole board and
/// captures that; <c>node</c> frames one node. The first three render the board element.
/// <c>chrome</c> renders the whole window instead, because the tab strip that is the title bar
/// sits above the board and is not in that subtree — without this mode its look would be a claim
/// with no reading behind it, which is the failure the <c>Popup</c> panels used to have.
/// </param>
/// <param name="Select">
/// Comma-separated node ids to select for the shot, then put back. The selection chrome — ring,
/// inner contrast line, handles — is part of what has to match, and a capture that cannot show a
/// selection cannot measure it.
/// </param>
/// <param name="Panel">
/// <c>filter</c>, <c>cheat</c> or <c>banner</c>: open that floating surface for the shot, then put it
/// back the way it was. Same reason as <paramref name="Select"/> — and a stronger one: these panels
/// used to be <c>Popup</c>s, which are separate top-level windows and therefore invisible to a
/// capture of the board. Since they are in the board's own tree now, the corner radius and the
/// translucency are things that can be read off a PNG instead of claimed. <c>banner</c> is the
/// activation-only strip, so the request genuinely applies a tag filter (the first tag in use) and
/// hands the previous lens back afterwards.
/// <c>fold</c> / <c>unfold</c> are the two end states of group folding: the shot finds a group whose
/// members do not all match one tag, applies that lens, and sets the group folded / expanded without
/// waiting for the tween — the mid-frames of an animation are not something a PNG can settle. If the
/// board has no such shape the request refuses rather than photographing a dimmed group and calling
/// it folded. <c>boards</c> is the new-board / open-another-board surface.
/// </param>
public sealed record ShotRequest(
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("out")] string OutPath,
    [property: JsonPropertyName("mode")] string Mode = "view",
    [property: JsonPropertyName("id")] string? NodeId = null,
    [property: JsonPropertyName("select")] string? Select = null,
    [property: JsonPropertyName("panel")] string? Panel = null);

public sealed record ShotReply(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("width")] int Width = 0,
    [property: JsonPropertyName("height")] int Height = 0,
    [property: JsonPropertyName("zoom")] double Zoom = 0,
    [property: JsonPropertyName("nodes")] int Nodes = 0,
    [property: JsonPropertyName("error")] string? Error = null);
