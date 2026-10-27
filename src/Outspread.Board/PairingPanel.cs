using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Outspread.Core;
using QRCoder;

namespace Outspread.Board;

/// <summary>
/// 配对手机那一层：把"这台电脑上的这块板，跟云端哪个仓"讲给手机听。
/// </summary>
/// <remarks>
/// <para>
/// 它在窗口里而不是只在终端里，理由只有两条：**少一步**（用这块板的人坐在电脑前打开的就是这个
/// 窗口，不该让他记一条带 <c>--board</c> 路径的命令）；**画得对**（界面直接把模块网格画成图形，
/// 不必靠方块字加上 81 列宽度才画得开）。
/// </para>
/// <para>
/// 不存在"终端那一发配错板子"这种洞，别为它写代码：载荷只有那六个键，<c>--board</c> 进不去
/// （它只喂给人看的那一行说明）。测试 <c>the_pairing_code_names_the_repository_not_the_board</c>
/// 把这件事钉住。当前打开的是哪块板，仍然决定界面上那行字说什么——只是不改变码的字节。
/// </para>
/// <para>
/// 码里是一枚能写他仓库的令牌。所以这一层不落盘、不进日志、不把载荷文字显示在界面上
/// （<c>--json</c> 那一档留在 CLI，给脚本和取证用）。要让手机再扫一次就重新生成：
/// 载荷里没有过期字段，所以这里不许写"几分钟内有效"那种不存在的机制——能说的只有"重新生成"
/// 与"去 GitHub 撤这把"。
/// </para>
/// </remarks>
public sealed partial class MainWindow
{
    private bool _pairingOpen;

    private void SetPairingPanel(bool open)
    {
        if (open)
        {
            // 没有板子路径就没有"这块板"可配对；没有远端仓库或令牌，码里会是空的六分之一。
            // 这一层不说谎：够不着就讲够不着，不给一张画了半张的卡。
            var block = BlockedPairing();

            if (block is not null)
            {
                Say(block);

                return;
            }

            ChromeStyling.Ensure(NoteStyling.IsDark);
            ChromeStyling.ApplyFloatingChrome(PairingPanel);

            RebuildPairingPanel();
        }

        PairingPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        _pairingOpen = open;
    }

    private void ClosePairingPanel()
    {
        if (!_pairingOpen)
        {
            return;
        }

        SetPairingPanel(false);

        BoardArea.Focus(FocusState.Programmatic);
    }

    private void OpenPairingPanel()
    {
        SetSearchPanel(false);
        SetRestorePanel(false);
        SetFilterPanel(false);
        CloseChordPage();
        SetPairingPanel(true);
    }

    private void OnPairingPanelPressed(object sender, PointerRoutedEventArgs e)
    {
        // 与恢复那层不同：这层里有两个按钮，按到空白处不该把它们收掉。
        e.Handled = true;
    }

    /// <summary>配对现在能不能做；不能就做一句人话，能做就 null。</summary>
    private string? BlockedPairing()
    {
        if (_boardPath is null)
        {
            return "还没有打开的板面，没有可配对的板";
        }

        if (SyncConfig.Load(SyncLayout.Root) is not { IsSet: true })
        {
            return "还没定远端仓库：跑一次 outspread sync link --remote owner/仓库名（或把令牌交给命令行那发）";
        }

        if (SyncCredentials.Read() is not { Length: > 0 })
        {
            return "读不到远端令牌：outspread sync link --token-stdin，或用 cmdkey 存一次";
        }

        return null;
    }

    private void RebuildPairingPanel()
    {
        var config = SyncConfig.Load(SyncLayout.Root)!;
        var token = SyncCredentials.Read();

        FrameworkElement picture;
        string payload;

        try
        {
            payload = PairingPayload.Build(config, token);

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);

            picture = QrImage.Build(data.ModuleMatrix);
        }
        catch (SyncSetupException ex)
        {
            // 构造失败就是构造失败，别把一张空卡留在屏幕上。
            PairingBody.Children.Clear();
            PairingBody.Children.Add(PanelKit.Line($"配不出来：{ex.Message}"));

            return;
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(_boardPath);

        PairingBody.Children.Clear();
        // 只有码和两枚动作：码里那六个键是给手机读的，不是给人在这层逐行念的。
        PairingBody.Children.Add(PanelKit.Line($"配对手机 · {name}"));
        PairingBody.Children.Add(picture);
        PairingBody.Children.Add(PanelKit.Row("重新生成", null, () => SetPairingPanel(true)));
        PairingBody.Children.Add(PanelKit.Row("关掉", null, ClosePairingPanel));
    }
}
