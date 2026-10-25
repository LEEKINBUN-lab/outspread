using Outspread.Core;
using Xunit;

namespace Outspread.Core.Tests;

public class SyncHousekeepingTests
{
    private static string Scratch()
    {
        var folder = Path.Combine(Path.GetTempPath(), "outspread-tests", "housekeeping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);

        return folder;
    }

    [Fact]
    public void A_default_name_whose_remote_board_is_gone_is_released_but_a_live_or_open_one_is_not()
    {
        var root = Scratch();

        try
        {
            foreach (var stem in new[] { "未命名板1", "未命名板2", "未命名板3", "待办事项", "selftest-rename" })
            {
                Directory.CreateDirectory(Path.Combine(root, stem));
            }

            var tree = new RemoteTree(
                "tree",
                [new RemoteEntry("boards/未命名板3.canvas", "token", 10)]);

            var released = SyncHousekeeping.ReleaseUntitledLedgers(tree, root, keepStems: ["未命名板2"]);

            Assert.Equal(["未命名板1", "selftest-rename"], released.OrderBy(name => name));
            Assert.False(Directory.Exists(Path.Combine(root, "未命名板1")));
            Assert.False(Directory.Exists(Path.Combine(root, "selftest-rename")));
            Assert.True(Directory.Exists(Path.Combine(root, "未命名板2")));   // 还开着：名字不能动
            Assert.True(Directory.Exists(Path.Combine(root, "未命名板3")));   // 远端还有：账本是防复活凭据
            Assert.True(Directory.Exists(Path.Combine(root, "待办事项")));    // 不是默认名/自检残留
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
