namespace Outspread.Core;

/// <summary>
/// Byte-level rules for the text this program writes.
/// </summary>
public static class JsonText
{
    /// <summary>
    /// Forces LF endings. A board is meant to sit in git and be read by an agent, and
    /// CRLF makes every untouched line look touched.
    /// </summary>
    public static string NormalizeNewLines(string text)
    {
        if (text.IndexOf('\r') < 0)
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
            {
                builder.Append('\n');
                index++;
            }
            else if (text[index] == '\r')
            {
                builder.Append('\n');
            }
            else
            {
                builder.Append(text[index]);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Turns newlines in a pasted body into the single form the document stores.
    /// Clipboard text arrives with CRLF, and a text node keeps its content as one JSON
    /// string, so the difference is visible in every diff of every paste.
    /// </summary>
    public static string NormalizeTextNewlines(string? value)
        => value is null ? string.Empty : NormalizeNewLines(value.Replace("\u2028", "\n").Replace("\u2029", "\n"));
}
