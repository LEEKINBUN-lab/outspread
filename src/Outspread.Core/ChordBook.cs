using System.Text;

namespace Outspread.Core;

/// <summary>
/// 他改过的键位：命令名 → 键位串，一行一条，存在 <c>%LOCALAPPDATA%\Outspread\chords.txt</c>。
/// </summary>
/// <remarks>
/// <para>
/// 命令表（<c>BoardCommands.Commands()</c>）仍是唯一来源，这张表只放"哪一条被改成了什么"。
/// 没写在这里的命令一律用表里的默认值，所以删掉一行就是恢复那一条的默认键位——文件本身也是
/// 这份承诺的凭据，坏掉了不会让任何一条命令变成按不下去。
/// </para>
/// <para>
/// 读失败按空表处理并把原因交回调用方（他上一次的改键没了，但板子照开）；写失败必须把原因报回
/// 界面——静默失败会让他以为改成了，下次开窗却是旧键位。这和 <see cref="BoardSession"/> 同一条规矩。
/// </para>
/// <para>
/// 规范化：修饰键只认 Ctrl / Shift / Alt，且落盘顺序固定为 Ctrl、Shift、Alt、主键。派发那一侧
/// 拼串就是按这个顺序拼的（见界面的 <c>ChordFrom</c>），所以文件里手写的 <c>Alt+Ctrl+K</c> 也要先
/// 折成 <c>Ctrl+Alt+K</c> 再比——两边各拼各的顺序，就是"明明改了却不生效"那种错。
/// </para>
/// </remarks>
public sealed class ChordBook
{
    public const string FileName = "chords.txt";

    /// <summary>修饰键的规范顺序（也是派发拼串的顺序）。</summary>
    public static readonly string[] ModifierNames = ["Ctrl", "Shift", "Alt"];

    /// <summary><c>e.Key.ToString()</c> 会吐出来的那几个修饰键名字：它们不能当主键。</summary>
    private static readonly string[] ModifierKeys = ["Control", "Shift", "Menu", "LeftWindows", "RightWindows"];

    private readonly Dictionary<string, string> _overrides;

    private ChordBook(Dictionary<string, string> overrides) => _overrides = overrides;

    /// <summary>改过的那些：命令名 → 键位串（已规范化）。顺序即落盘顺序，便于人读。</summary>
    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    public bool Any => _overrides.Count > 0;

    public static string PathIn(string directory) => Path.Combine(directory, FileName);

    public static ChordBook Empty() => new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// 这一条命令现在该按哪一串：改过的值优先，否则回默认。默认值由命令表给，这里不猜。
    /// </summary>
    public string Resolve(string label, string fallback)
        => _overrides.TryGetValue(label, out var chord) ? chord : fallback;

    /// <summary>写一条改键。键位串不合语法时返回 false 并且什么都不改。</summary>
    public bool Set(string label, string chord)
    {
        if (label.Length == 0 || !TryCanonical(chord, out var canonical))
        {
            return false;
        }

        _overrides[label] = canonical;

        return true;
    }

    /// <summary>恢复一条的默认键位。返回是否真的删掉了什么。</summary>
    public bool Remove(string label) => _overrides.Remove(label);

    /// <summary>全部恢复默认。</summary>
    public void Reset() => _overrides.Clear();

    /// <summary>
    /// 一串键位合不合法：至少一枚修饰键（Ctrl / Alt / Shift）加一个主键，修饰键不许重复，
    /// 主键不许又是修饰键。合法时 <paramref name="canonical"/> 是折好顺序的那一串。
    /// </summary>
    public static bool TryCanonical(string? chord, out string canonical)
    {
        canonical = string.Empty;

        if (chord is not { Length: > 0 })
        {
            return false;
        }

        var parts = chord.Split('+', StringSplitOptions.TrimEntries);

        // 至少要"一枚修饰键 + 一个主键"：单键（Esc、Delete）不在这套语法里，他 10-27 定档——
        // 单键改键等于把常用键从平台手里抢走，而这里改的是组合键。
        if (parts.Length < 2 || parts.Any(part => part.Length == 0))
        {
            return false;
        }

        var key = parts[^1];

        // 主键不许又是修饰键：`e.Key` 那边叫 Control/Shift/Menu，文件里人写的是 Ctrl/Alt/Shift，
        // 两种写法都得挡住——挡住一种，"Ctrl+Alt" 就会被当成"Ctrl 修饰 + 主键 Alt"收下来。
        if (!key.All(char.IsAsciiLetterOrDigit)
            || ModifierKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
            || ModifierNames.Contains(key, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        // 单字母主键折成大写（Alt+k → Alt+K，`e.Key.ToString()` 给的就是大写），
        // 多字母的名字（Number1、OemPlus）原样留着——那是平台的拼法，改一个字母就派发不到了。
        if (key.Length == 1)
        {
            key = key.ToUpperInvariant();
        }

        var modifiers = new List<string>();

        foreach (var part in parts[..^1])
        {
            var name = ModifierNames.FirstOrDefault(candidate => string.Equals(candidate, part, StringComparison.OrdinalIgnoreCase));

            if (name is null || modifiers.Contains(name))
            {
                return false;
            }

            modifiers.Add(name);
        }

        canonical = string.Join('+', ModifierNames.Where(modifiers.Contains).Append(key));

        return true;
    }

    /// <summary>一串键位能不能被绑上（＝语法成立）。界面拿它挡"只按了修饰键"与"不带修饰键"。</summary>
    public static bool IsBindable(string? chord) => TryCanonical(chord, out _);

    /// <summary>
    /// 从盘上读。文件不在＝没有改键（第一次开窗就是这样），不是错；读不懂的行整行丢掉并把行数
    /// 说回来，读不到文件则整表按空处理——两条都只是"这次用默认键位"，不该挡住开窗。
    /// </summary>
    public static (ChordBook Book, string? Problem) Load(string path)
    {
        if (!File.Exists(path))
        {
            return (Empty(), null);
        }

        try
        {
            var overrides = new Dictionary<string, string>(StringComparer.Ordinal);
            var ignored = new List<string>();

            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                var line = raw.Trim();

                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                var cut = line.IndexOf('=');

                if (cut <= 0)
                {
                    ignored.Add(line);

                    continue;
                }

                var label = line[..cut].Trim();

                if (label.Length == 0 || !TryCanonical(line[(cut + 1)..], out var canonical))
                {
                    ignored.Add(line);

                    continue;
                }

                // 同一个命令写了两遍：后写的赢，和配置文件一般的读法一致。
                overrides[label] = canonical;
            }

            var problem = ignored.Count == 0
                ? null
                : $"{ignored.Count} 行读不懂，已按默认键位处理（{string.Join("／", ignored.Take(3))}）";

            return (new ChordBook(overrides), problem);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Empty(), $"读不到 {path}（{ex.Message}），这次按默认键位");
        }
    }

    /// <summary>
    /// 落盘（原子改名，和 <see cref="BoardSession"/> 一个写法）。成功返回 null，否则返回原因——
    /// 调用方必须把它说到界面上：改键没落盘而界面说"改好了"，下次开窗就变回旧键位。
    /// </summary>
    public string? Save(string path)
    {
        var folder = Path.GetDirectoryName(path);

        if (folder is not { Length: > 0 })
        {
            return $"这不是一个文件路径：{path}";
        }

        var staged = path + ".tmp";

        try
        {
            Directory.CreateDirectory(folder);

            var lines = new List<string>
            {
                "# 键位：一行一条「命令名=键位」，键位是 Ctrl/Alt/Shift 加一个主键。",
                "# 删掉一行＝那一条回到命令表里的默认键位。",
            };

            lines.AddRange(_overrides
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}={pair.Value}"));

            File.WriteAllLines(staged, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(staged, path, overwrite: true);

            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }
}
