using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DiscordRPC;
using Kxnrl.Vanessa.Models;
using Kxnrl.Vanessa.Players;
using Kxnrl.Vanessa.Utils;
using Button = DiscordRPC.Button;

namespace Kxnrl.Vanessa;

internal class Program
{
    private const string NetEaseAppId = "481562643958595594";
    private const string TencentAppId = "903485504899665990";

    private static async Task Main()
    {
        // check run once
        _ = new Mutex(true, "MusicDiscordRpc", out var allow);

        if (!allow)
        {
            MessageBox.Show("MusicDiscordRpc is already running.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            Environment.Exit(-1);

            return;
        }

        if (Constants.GlobalConfig.IsFirstLoad)
        {
            // 启动就设置自动启动感觉不好
            Win32Api.AutoStart.Set(false);
        }

        var netEase = new DiscordRpcClient(NetEaseAppId);
        var tencent = new DiscordRpcClient(TencentAppId);
        netEase.Initialize();
        tencent.Initialize();

        if (!netEase.IsInitialized || !tencent.IsInitialized)
        {
            MessageBox.Show("Failed to init rpc client.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.Exit(-1);
        }

        // TODO Online Signatures
        await Task.CompletedTask;

        var notifyMenu = new ContextMenuStrip();

        var exitButton = new ToolStripMenuItem("Exit");
        var autoButton = new ToolStripMenuItem("AutoStart" + "    " + (Win32Api.AutoStart.Check() ? "√" : "✘"));
        notifyMenu.Items.Add(autoButton);
        notifyMenu.Items.Add(exitButton);

        var notifyIcon = new NotifyIcon
        {
            BalloonTipIcon = ToolTipIcon.Info,
            ContextMenuStrip = notifyMenu,
            Text = "NetEase Cloud Music DiscordRPC",
            Icon = AppResource.icon,
            Visible = true,
        };

        exitButton.Click += (_, _) =>
        {
            notifyIcon.Visible = false;
            Thread.Sleep(100);
            Environment.Exit(0);
        };

        autoButton.Click += (_, _) =>
        {
            var x = Win32Api.AutoStart.Check();

            Win32Api.AutoStart.Set(!x);

            autoButton.Text = "AutoStart" + "    " + (Win32Api.AutoStart.Check() ? "√" : "✘");
        };

        _ = Task.Run(async () => await UpdateThread(netEase, tencent));
        Application.Run();
    }

    private static async Task UpdateThread(DiscordRpcClient netEase, DiscordRpcClient tencent)
    {
        PlayerInfo? lastPolledInfo = null;
        var lastPollTime = DateTime.MinValue;
        // 定义跳转的容差
        // 如果实际进度变化与时间流逝的差异超过这个值则认为用户跳转了歌曲进度
        const double jumpToleranceSeconds = 0.4;

        PlayerInfo? pendingUpdateInfo = null;
        var lastChangeDetectedTime = DateTime.MinValue;
        // 防抖时间窗口
        // 只有在状态稳定超过1.5秒后，才发送更新
        const double debounceWindowSeconds = 1.5;

        while (true)
        {
            try
            {
                IMusicPlayer? player = null;
                DiscordRpcClient? rpcClient = null;
                var currentPlayerName = string.Empty;

                var netEaseHwnd = Win32Api.User32.FindWindow("OrpheusBrowserHost", null);
                if (netEaseHwnd != IntPtr.Zero &&
                    Win32Api.User32.GetWindowThreadProcessId(netEaseHwnd, out var netEaseProcessId) != 0 &&
                    netEaseProcessId != 0)
                {
                    player = new NetEase(netEaseProcessId);
                    rpcClient = netEase;
                    currentPlayerName = "NetEase CloudMusic"; 
                }
                else
                {
                    var tencentHwnd = Win32Api.User32.FindWindow("QQMusic_Daemon_Wnd", null);
                    if (tencentHwnd != IntPtr.Zero &&
                        Win32Api.User32.GetWindowThreadProcessId(tencentHwnd, out var tencentId) != 0 && tencentId != 0)
                    {
                        player = new Tencent(tencentId);
                        rpcClient = tencent;
                        currentPlayerName = "Tencent QQMusic";
                    }
                }

                var currentTime = DateTime.UtcNow;
                var currentPlayerInfo = player?.GetPlayerInfo();
                
                var isStateChanged = false;
                if ((currentPlayerInfo is null && lastPolledInfo is not null) ||
                    (currentPlayerInfo is not null && lastPolledInfo is null))
                {
                    isStateChanged = true;
                }
                else if (currentPlayerInfo is { } currentInfo && lastPolledInfo is { } lastInfo)
                {
                    if (currentInfo.Identity != lastInfo.Identity || currentInfo.Pause != lastInfo.Pause)
                    {
                        isStateChanged = true;
                    }
                    else if (!currentInfo.Pause)
                    {
                        var elapsedSeconds = (currentTime - lastPollTime).TotalSeconds;
                        var progressDelta = currentInfo.Schedule - lastInfo.Schedule;
                        if (Math.Abs(progressDelta - elapsedSeconds) > jumpToleranceSeconds)
                        {
                            isStateChanged = true;
                        }
                    }
                }
                
                if (isStateChanged)
                {
                    Debug.WriteLine(
                        $"State change detected. Resetting debounce timer. New song: {currentPlayerInfo?.Title ?? "None"}");
                    pendingUpdateInfo = currentPlayerInfo;
                    lastChangeDetectedTime = currentTime;
                }
                
                if (pendingUpdateInfo is not null &&
                    (currentTime - lastChangeDetectedTime).TotalSeconds > debounceWindowSeconds)
                {
                    Debug.WriteLine($"Debounce window passed. Sending RPC update for: {pendingUpdateInfo.Value.Title}");
                    
                    var info = pendingUpdateInfo.Value;
                    if (!info.Pause)
                    {
                        rpcClient?.Update(rpc =>
                        {
                            rpc.Details = StringUtils.GetTruncatedStringByMaxByteLength($"🎵 {info.Title}", 128);
                            rpc.State = StringUtils.GetTruncatedStringByMaxByteLength($"🎤 {info.Artists}", 128);
                            rpc.Type = ActivityType.Listening;
                            rpc.Timestamps = new Timestamps(
                                DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(info.Schedule)),
                                DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(info.Schedule))
                                    .Add(TimeSpan.FromSeconds(info.Duration))
                            );
                            rpc.Assets = new Assets
                            {
                                LargeImageKey = info.Cover,
                                LargeImageText = StringUtils.GetTruncatedStringByMaxByteLength($"💿 {info.Album}", 128),
                                SmallImageKey = "timg",
                                SmallImageText = currentPlayerName,
                            };
                            rpc.Buttons =
                            [
                                new Button { Label = "🎧 Listen", Url = info.Url },
                                new Button
                                {
                                    Label = "👏 View App on GitHub",
                                    Url = "https://github.com/Kxnrl/NetEase-Cloud-Music-DiscordRPC"
                                },
                            ];
                        });
                    }
                    else
                    {
                        rpcClient?.ClearPresence();
                    }
                    
                    pendingUpdateInfo = null;
                }
                
                if (currentPlayerInfo is null && pendingUpdateInfo is not null)
                {
                    Debug.WriteLine($"Player closed. Clearing pending update.");
                    rpcClient?.ClearPresence();
                    pendingUpdateInfo = null;
                }
                
                lastPolledInfo = currentPlayerInfo;
                lastPollTime = currentTime;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                lastPolledInfo = null;
                pendingUpdateInfo = null;
            }
            finally
            {
                // 用户就喜欢超低内存占用
                // 但是实际上来说并没有什么卵用
                // (所以建议直接注释掉，别强制手动gc了，直接做内存优化)
                // GC.Collect();
                // GC.WaitForFullGCComplete();

                await Task.Delay(TimeSpan.FromMilliseconds(233));
            }
        }
    }
}