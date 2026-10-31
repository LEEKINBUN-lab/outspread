using System.Text.Json;
using System.Text.Json.Nodes;
using Outspread.Core;

namespace Outspread.Cli;

/// <summary>
/// <c>outspread plugin list</c>: 查看本机登记了哪些可选插件。登记表在
/// <c>%LOCALAPPDATA%\Outspread\plugins.json</c>，写它的是插件自己的
/// <c>helm-gate install</c>——画板与 CLI 都不往里写，这里只读。
/// 显示本身不依赖这份登记（认的是板旁的 .gate.json 在不在）。
/// </summary>
internal static class PluginCommand
{
    private static string RegistryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Outspread",
        "plugins.json");

    public static int Run(Options options)
    {
        var sub = options.Positional.Count > 0 ? options.Positional[0] : "list";

        if (sub is not ("list" or "ls"))
        {
            throw new ArgumentException("outspread plugin 目前只有 list 一个子命令。");
        }

        if (!File.Exists(RegistryPath))
        {
            Console.WriteLine("（没有登记任何插件。装过 helm-gate 的话，在插件目录跑 helm-gate install。）");
            return 0;
        }

        JsonNode? root;

        try
        {
            root = JsonNode.Parse(File.ReadAllText(RegistryPath));
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"plugins.json 读不懂（{ex.Message}）；删掉它即可重置登记。");
        }

        if (root?["plugins"] is not JsonArray plugins || plugins.Count == 0)
        {
            Console.WriteLine("（登记表是空的。）");
            return 0;
        }

        foreach (var plugin in plugins.OfType<JsonObject>())
        {
            var id = plugin["id"]?.GetValue<string>() ?? "<无 id>";
            var exe = plugin["exe"]?.GetValue<string>() ?? "-";
            var installed = plugin["installedAt"]?.GetValue<string>() ?? "-";
            var alive = File.Exists(exe) ? "" : "（exe 不在了）";

            Console.WriteLine($"{id}  {exe}  装于 {installed}{alive}");
        }

        return 0;
    }
}
