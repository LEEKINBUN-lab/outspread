using System.Security.Cryptography;

namespace Outspread.Core;

/// <summary>
/// Node identifiers.
/// </summary>
/// <remarks>
/// The first character is a namespace: <c>u</c> is minted by the board while a
/// human is dragging, <c>a</c> is minted by an agent or the CLI. Two writers that
/// never coordinate therefore cannot collide by construction, which is the whole
/// point of a document both of them edit.
///
/// The remaining 12 hex digits are not decoration. An 8-character id carries 28
/// bits, and 28 bits collides roughly once per 20 000 ids — a rate this board would
/// have hit on a single busy afternoon, silently, through an agent's edit.
///
/// Anything matching <see cref="IsValidId"/> is accepted when reading, so an agent
/// that invents its own readable ids ("hypothesis-a") is not rejected.
/// </remarks>
public static class NodeIds
{
    public const char AppNamespace = 'u';

    public const char AgentNamespace = 'a';

    public const int Length = 13;

    private const int HexLength = Length - 1;

    public static string NewAppId() => New(AppNamespace);

    public static string NewAgentId() => New(AgentNamespace);

    public static string New(char namespacePrefix)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(namespacePrefix is 'u' or 'a' ? 0 : 1, 0);

        Span<byte> bytes = stackalloc byte[HexLength / 2];
        RandomNumberGenerator.Fill(bytes);

        return namespacePrefix + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
        {
            return false;
        }

        foreach (var character in id)
        {
            var ok = character is (>= 'a' and <= 'z')
                or (>= 'A' and <= 'Z')
                or (>= '0' and <= '9')
                or '_' or '-' or '.' or ':';

            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// An id that does not follow the namespace rule is not an error, only a warning:
    /// an agent is allowed to name things for itself.
    /// </summary>
    public static bool FollowsNamespaceConvention(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != Length)
        {
            return false;
        }

        if (id[0] != AppNamespace && id[0] != AgentNamespace)
        {
            return false;
        }

        foreach (var character in id.AsSpan(1))
        {
            if (!char.IsAsciiHexDigitLower(character))
            {
                return false;
            }
        }

        return true;
    }
}
