using System.Text;

namespace Outspread.Cli;

/// <summary>
/// <c>outspread verb --option value positional</c>, parsed without a framework.
/// </summary>
/// <remarks>
/// Long text is accepted from a file (<c>--text-file</c>) as well as inline, because a
/// multi-line note pasted into a command line is exactly where quoting rules start eating
/// content, and an agent that gets it half wrong writes half a note to the board.
/// </remarks>
internal sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);

    private Options(string? command, List<string> positional)
    {
        Command = command;
        Positional = positional;
    }

    public string? Command { get; }

    public List<string> Positional { get; }

    public static Options Parse(string[] args)
    {
        var parsed = new Options(args.Length > 0 ? args[0] : null, []);

        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                parsed.Positional.Add(argument);
                continue;
            }

            // Hyphens are dropped so --text-file and --textFile are the same option.
            var name = argument[2..].Replace("-", string.Empty);
            string value;

            var equals = name.IndexOf('=');

            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                value = args[++index];
            }
            else
            {
                value = string.Empty;
            }

            if (!parsed._values.TryGetValue(name, out var list))
            {
                list = [];
                parsed._values[name] = list;
            }

            list.Add(value);
        }

        return parsed;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>A switch given with no value, such as --json.</summary>
    public bool Flag(string name) => _values.TryGetValue(name, out var list) && list.Count > 0;

    public string? Raw(string name) => Value(name);

    public string? Value(string name) => _values.TryGetValue(name, out var list) ? list[^1] : null;

    public IReadOnlyList<string> Values(string name)
        => _values.TryGetValue(name, out var list) ? list : Array.Empty<string>();

    public double Number(string name, double fallback)
        => Value(name) is { } raw && raw.Length > 0 ? Parse(raw, name) : fallback;

    public string? Text()
    {
        // A body that spans lines or carries quotes comes from a file, because an agent
        // getting shell quoting half wrong would write half a note onto the board.
        if (Value("textfile") is { Length: > 0 } path)
        {
            return File.ReadAllText(path);
        }

        return Value("text");
    }

    public bool IsTextRequested() => Has("text") || Has("textfile");

    public string SinglePositional(string command)
        => Positional.Count switch
        {
            0 => throw new ArgumentException($"outspread {command} 需要一个参数"),
            1 => Positional[0],
            _ => throw new ArgumentException($"outspread {command} 只接受一个参数，给了 {Positional.Count} 个：{string.Join(' ', Positional)}"),
        };

    private static double Parse(string raw, string name)
        => double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new ArgumentException($"--{name} 要的是数字，给的是 \"{raw}\"");
}
