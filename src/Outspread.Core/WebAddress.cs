using System.Net;
using System.Text.RegularExpressions;

namespace Outspread.Core;

/// <summary>
/// The small part of a URL a card shows: a title, a host, and nothing that looks like a path.
/// </summary>
public static partial class WebAddress
{
    public const int MaxTitleLength = 120;

    public static bool TrySingleUrl(string? text, out Uri uri)
    {
        uri = null!;

        var value = (text ?? string.Empty).Trim();

        if (value.Length is 0 or > 2048 || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || parsed.Host.Length == 0)
        {
            return false;
        }

        uri = parsed;

        return true;
    }

    public static string Host(Uri uri)
        => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

    /// <summary>
    /// 在网页上选中链接文字→右键「复制」：剪贴板的**文本**档给的是链接显示文字，真正的地址躺在
    /// HTML 的 <c>href</c> 里。这一发把那条路接上——但只在这段文字本身就是一行短标题时才认。
    /// </summary>
    /// <remarks>
    /// 两道闸缺一不可：<b>文本必须是单行且不超过 <see cref="MaxTitleLength"/></b>（一整段摘录里
    /// 常夹着一条链接，那种时候人要的是那段字，不是一张卡）；<b>HTML 里只能有一条不同的 http(s) 地址</b>
    /// （多条就是在猜，宁可不建）。
    /// </remarks>
    public static bool TryAnchoredUrl(string? html, string? plainText, out Uri uri)
    {
        uri = null!;

        var words = (plainText ?? string.Empty).Trim();

        if (words.Length is 0 or > MaxTitleLength || words.Contains('\n') || words.Contains('\r'))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            return false;
        }

        var found = AnchorPattern().Matches(html)
            .Select(match => WebUtility.HtmlDecode(FirstSet(match)))
            .Select(value => value.Trim())
            .Where(value => value.Length > 0 && !value.StartsWith("#", StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (found.Count != 1 || !TrySingleUrl(found[0], out var parsed))
        {
            return false;
        }

        uri = parsed;

        return true;
    }

    /// <summary>href 的三种写法（双引号／单引号／不带引号）分别落在第 1、2、3 组，取先出现的那一组。</summary>
    private static string FirstSet(Match match)
        => match.Groups[1].Success ? match.Groups[1].Value
            : match.Groups[2].Success ? match.Groups[2].Value
            : match.Groups[3].Value;

    [GeneratedRegex(@"<a\b[^>]*?href\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorPattern();

    /// <summary>What to show before a page title is known. Never a full path.</summary>
    public static string FallbackTitle(Uri uri)
    {
        var leaf = uri.Segments
            .Select(segment => segment.Trim('/'))
            .LastOrDefault(segment => segment.Length > 0);

        if (leaf is { Length: > 0 })
        {
            try
            {
                leaf = Uri.UnescapeDataString(leaf);
            }
            catch (UriFormatException)
            {
                // A malformed escape is not a reason to lose the host line.
            }

            leaf = Path.GetFileNameWithoutExtension(leaf).Replace('-', ' ').Replace('_', ' ').Trim();

            if (leaf.Length > 0)
            {
                return leaf.Length > MaxTitleLength ? leaf[..MaxTitleLength] : leaf;
            }
        }

        return Host(uri);
    }

    public static string? TitleFromHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var match = TitlePattern().Match(html);

        if (!match.Success)
        {
            return null;
        }

        var title = WebUtility.HtmlDecode(TagPattern().Replace(match.Groups[1].Value, " "));
        title = WhitespacePattern().Replace(title, " ").Trim();

        if (title.Length == 0)
        {
            return null;
        }

        return title.Length > MaxTitleLength ? title[..MaxTitleLength] : title;
    }

    [GeneratedRegex(@"<title\b[^>]*>(.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
