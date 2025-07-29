using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DiscordRPC;

namespace Kxnrl.Vanessa;

internal static class Program
{
    private const string NetEaseAppId = "481562643958595594";
    private const string TencentAppId = "903485504899665990";

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 单实例运行
        using var mutex = new Mutex(true, "MusicDiscordRpc", out var isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("MusicDiscordRpc is already running.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // 初始化RPC客户端
        var netEaseClient = new DiscordRpcClient(NetEaseAppId);
        var tencentClient = new DiscordRpcClient(TencentAppId);
        netEaseClient.Initialize();
        tencentClient.Initialize();

        if (!netEaseClient.IsInitialized || !tencentClient.IsInitialized)
        {
            MessageBox.Show("Failed to initialize Discord RPC client.", "Error", MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        // 核心服务
        var rpcManager = new RpcManager(netEaseClient, tencentClient);
        Task.Run(rpcManager.Start);

        // 托盘图标
        using var trayIcon = CreateTrayIcon();
        trayIcon.Visible = true;
        Application.Run();

        // 应用退出时清理RPC客户端
        netEaseClient.Dispose();
        tencentClient.Dispose();
    }

    /// <summary>
    /// 创建并配置系统托盘图标及其菜单
    /// </summary>
    private static NotifyIcon CreateTrayIcon()
    {
        var autoStartMenuItem = new ToolStripMenuItem("AutoStart") { CheckOnClick = true };
        var exitMenuItem = new ToolStripMenuItem("Exit");

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add(autoStartMenuItem);
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add(exitMenuItem);

        autoStartMenuItem.Checked = Win32Api.AutoStart.Check();
        autoStartMenuItem.Click += (_, _) => Win32Api.AutoStart.Set(autoStartMenuItem.Checked);
        exitMenuItem.Click += (_, _) => Application.Exit();

        return new NotifyIcon
        {
            Icon = AppResource.icon,
            // 既然不是单一支持网易云的，这个部分名字感觉可以修改，先保持原样
            Text = "NetEase Cloud Music DiscordRPC",
            ContextMenuStrip = contextMenu
        };
    }
}