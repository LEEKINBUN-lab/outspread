using System.Text.RegularExpressions;

namespace Outspread.Core;

/// <summary>
/// 把**一行文本**读成一个本地绝对路径。认这几种形状：
/// <c>D:\资料\方案.pdf</c>、<c>D:/资料/方案.pdf</c>、<c>\\nas\共享\x</c>、
/// <c>file:///D:/资料/方案.pdf</c>（含 <c>%20</c> 那类转义）、以及被双引号包住的那几种
/// ——资源管理器右键的「复制文件地址」给的就是 <c>"D:\资料\方案.pdf"</c>。
/// </summary>
/// <remarks>
/// 只认**独占一行**的内容：从浏览器或聊天窗口复制来的正文里常躺着一个 <c>D:\</c> 或 <c>\\nas\</c>，
/// 误认的代价（粘一段摘录却落出一张卡）比漏认大得多。含空格是允许的——路径里有空格太常见，
/// 那条"不能有空格"的规矩属于网址，不属于路径。
/// </remarks>
public static partial class LocalPath
{
    public static bool TrySingle(string? text, out string path)
    {
        path = string.Empty;

        var value = (text ?? string.Empty).Trim();

        if (value.Length == 0 || value.Contains('\n') || value.Contains('\r'))
        {
            return false;
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1].Trim();
        }

        if (value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return TryFromFileUri(value, ref path);
        }

        if (!DrivePattern().IsMatch(value) && !UncPattern().IsMatch(value))
        {
            return false;
        }

        path = Normalize(value);

        return path.Length > 0;
    }

    /// <summary>统一分隔符并去掉尾部斜杠——但盘根 <c>D:\</c> 要留着：削成 <c>D:</c> 就成了"该盘当前目录"，是另一个东西。</summary>
    private static string Normalize(string value)
    {
        var unified = value.Replace('/', '\\').TrimEnd();

        while (unified.Length > 3 && unified.EndsWith("\\", StringComparison.Ordinal))
        {
            unified = unified[..^1];
        }

        return HasIllegalChar(unified) ? string.Empty : unified;
    }

    /// <summary>盘符后面那枚冒号是路径的一部分，别处的冒号不是（NTFS 里 <c>:</c> 是数据流分隔符，不是我们能放进卡里的东西）。</summary>
    private static bool HasIllegalChar(string value)
        => value.Skip(value.Length >= 2 && value[1] == ':' ? 2 : 0)
            .Any(character => character < ' ' || character is '<' or '>' or '"' or '|' or '?' or '*' or ':');

    private static bool TryFromFileUri(string value, ref string path)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile)
        {
            return false;
        }

        // LocalPath 已经解过转义，也把 file://nas/共享 直接给成 \\nas\共享。
        var local = uri.LocalPath;

        if (local.Length < 3)
        {
            return false;
        }

        path = Normalize(local);

        return path.Length > 0;
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/].*$")]
    private static partial Regex DrivePattern();

    [GeneratedRegex(@"^\\\\[^\\/:*?""<>|\r\n]+\\[^\\/:*?""<>|\r\n]+.*$")]
    private static partial Regex UncPattern();
}
