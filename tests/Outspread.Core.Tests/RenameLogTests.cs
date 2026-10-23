using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Outspread.Core.Tests;

/// <summary>
/// 改名标记的解析。它有一批**跨端共享的向量**（<c>golden/renames/vectors.json</c>，桌面与安卓各存
/// 一份逐字节相同的副本，两端各自回放并核对自己那份的 sha256）——"两份实现逐字一致"这句话，
/// 只有靠同一批用例在两端各跑一遍才算数，写在注释里不算。
/// </summary>
public class RenameLogTests
{
    /// <summary>两端各自钉住这一份向量的 sha256。改了向量＝协议改了，必须两端同时改、两个 sha 同时换。</summary>
    private const string VectorsSha256 = "9fae49446246cb01b14eb798b06580c81a4b1fe279515ce56c0360e449814b2e";

    [Fact]
    public void The_shared_vectors_replay_exactly_as_the_contract_says()
    {
        var path = FindVectors();
        var bytes = File.ReadAllBytes(path);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        Assert.True(
            digest == VectorsSha256,
            $"这份向量不再是两端约定的那一份：sha256 {digest}，契约钉的是 {VectorsSha256}。协议改了要两端一起改。");

        var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        var vectors = doc.RootElement.GetProperty("vectors").EnumerateArray().ToList();

        Assert.True(vectors.Count >= 10, $"向量只剩 {vectors.Count} 条，这一节判不了决胜");

        var problems = new List<string>();

        foreach (var vector in vectors)
        {
            var log = new RenameLog
            {
                Entries = vector.GetProperty("entries").EnumerateArray().Select(entry => new RenameEntry(
                    entry.GetProperty("from").GetString()!,
                    entry.GetProperty("to").GetString()!,
                    entry.GetProperty("by").GetString()!,
                    entry.GetProperty("at").GetString()!)).ToList(),
            };

            var exists = vector.GetProperty("exists").EnumerateArray().Select(name => name.GetString()).ToHashSet(StringComparer.Ordinal);
            var got = log.Follow(vector.GetProperty("myStem").GetString()!, exists.Contains);
            var want = vector.GetProperty("expect").ValueKind == JsonValueKind.Null ? null : vector.GetProperty("expect").GetString();

            if (!string.Equals(got, want, StringComparison.Ordinal))
            {
                problems.Add($"{vector.GetProperty("name").GetString()}：答『{got ?? "不跟"}』，契约说『{want ?? "不跟"}』");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void A_retry_of_the_same_rename_does_not_stack_two_entries()
    {
        var log = new RenameLog();
        var entry = new RenameEntry("周会", "项目周报", "PC-A", "2026-10-23T21:40:00Z");

        log.Append(entry);
        log.Append(entry);

        Assert.Single(log.Entries);
    }

    [Fact]
    public void The_log_is_capped_by_dropping_the_oldest()
    {
        var log = new RenameLog();

        for (var i = 0; i < RenameLog.MaxEntries + 5; i++)
        {
            log.Append(new RenameEntry($"b{i}", $"n{i}", "PC-A", "2026-10-23T21:40:00Z"));
        }

        Assert.Equal(RenameLog.MaxEntries, log.Entries.Count);
        Assert.Equal("b5", log.Entries[0].From);

        // 被挤出去的那条不再是"这块板被改过名"——它已经老到不该再影响任何一端。
        Assert.Null(log.Follow("b4", _ => true));
        Assert.Equal("n5", log.Follow("b5", _ => true));
    }

    [Fact]
    public void Unreadable_marker_reads_as_no_marker_and_says_why()
    {
        var log = RenameLog.Parse("{ this is not json", out var problem);

        Assert.NotNull(log);
        Assert.Empty(log.Entries);
        Assert.NotNull(problem);
    }

    [Fact]
    public void An_absent_marker_is_not_an_error()
    {
        Assert.NotNull(RenameLog.Parse(null, out var problem));
        Assert.Null(problem);
    }

    /// <summary>契约第 10 条：进仓、两端都读的文件一律 LF（同一份内容算出两个摘要就是错的）。</summary>
    [Fact]
    public void The_bytes_going_into_the_repository_are_line_ending_free()
    {
        var log = new RenameLog();
        log.Append(new RenameEntry("周会", "项目周报", "PC-A", "2026-10-23T21:40:00Z"));

        Assert.DoesNotContain((byte)'\r', RenameLog.ToBytes(log));
    }

    [Fact]
    public void A_board_renamed_twice_follows_the_whole_chain()
    {
        var log = new RenameLog();
        log.Append(new RenameEntry("周会", "项目周报", "PC-A", "2026-10-23T21:40:00Z"));
        log.Append(new RenameEntry("项目周报", "本季复盘", "PC-A", "2026-10-23T22:00:00Z"));

        Assert.Equal("本季复盘", log.Follow("周会", _ => true));

        // 尽头不在仓里（半截发布）⇒ 一步都不跟，也不停在看起来能用的半路上。
        Assert.Null(log.Follow("周会", name => name == "项目周报"));
    }

    private static string FindVectors()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "golden", "renames", "vectors.json");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到 golden/renames/vectors.json —— 它和这份测试必须在同一个仓里。");
    }
}
