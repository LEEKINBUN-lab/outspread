using System.Text;
using System.Text.Json;

namespace Outspread.Core;

/// <summary>
/// The one place the pairing payload is built, because a phone matches on field names and
/// nothing about it is allowed to drift between the terminal and the window.
/// </summary>
/// <remarks>
/// Six fields, in this order, values as strings: <c>v</c>, <c>host</c>, <c>owner</c>,
/// <c>repo</c>, <c>branch</c>, <c>token</c>. The phone answers a missing key with
/// "配对码缺字段：&lt;名字&gt;", so renaming or re-typing one of these is a break with both
/// installed apps, not a refactor.
///
/// It carries no board list on purpose. Which boards exist is a fact about the repository that
/// the phone reads from the folder it is about to sync; a list copied into a code goes stale,
/// and a stale inventory is worse than none.
///
/// Nothing in here is drawn or displayed — only built. Where the text goes (a terminal's block
/// characters, a window's image, a pipe for <c>--json</c>) is each caller's own business, and
/// none of them is allowed to keep a second copy of these six keys.
/// </remarks>
public static class PairingPayload
{
    /// <summary>Bumped only when the six keys stop describing what a phone should do.</summary>
    public const string Version = "1";

    /// <summary>The exact key set, in the order written. A contract, not an implementation detail.</summary>
    public static readonly IReadOnlyList<string> FieldNames = ["v", "host", "owner", "repo", "branch", "token"];

    /// <summary>
    /// Builds the payload, or throws with the missing piece named. An empty token is refused
    /// here rather than becoming a code that scans and does nothing.
    /// </summary>
    public static string Build(SyncConfig config, string? token)
    {
        if (config is null || !config.IsSet)
        {
            throw new SyncSetupException("还没定远端仓库（owner/仓库名是空的），没有可配对的东西");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new SyncSetupException($"读不到 {config.ShortName} 的令牌，配对码里不能没有它");
        }

        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["v"] = Version,
            ["host"] = config.Host,
            ["owner"] = config.Owner,
            ["repo"] = config.Repo,
            ["branch"] = config.Branch,
            ["token"] = token,
        });
    }

    public static int ByteLength(string payload) => Encoding.UTF8.GetByteCount(payload);

    /// <summary>
    /// Reads the six keys back out. Used by the CLI's <c>--json</c> round trip and by any test
    /// that wants to prove the shape without going through a phone.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? Parse(string payload)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(payload);

            if (parsed is null || FieldNames.Any(name => !parsed.ContainsKey(name)))
            {
                return null;
            }

            return parsed;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
