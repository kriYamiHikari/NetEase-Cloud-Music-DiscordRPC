using System;
using System.Diagnostics;
using System.Threading.Tasks;
using DiscordRPC;
using Kxnrl.Vanessa.Models;
using Kxnrl.Vanessa.Players;
using Kxnrl.Vanessa.Utils;

namespace Kxnrl.Vanessa;

/// <summary>
/// 封装所有Discord RPC更新的核心逻辑
/// </summary>
internal class RpcManager(DiscordRpcClient netEaseClient, DiscordRpcClient tencentClient)
{
    /// <summary>
    /// 启动无限循环的更新线程
    /// </summary>
    public async Task Start()
    {
        PlayerInfo? lastPolledInfo = null;
        var lastPollTime = DateTime.MinValue;
        // 如果实际进度变化与时间流逝的差异超过0.4秒，则认为跳转了歌曲进度
        const double jumpToleranceSeconds = 0.4;

        PlayerInfo? pendingUpdateInfo = null;
        var lastChangeDetectedTime = DateTime.MinValue;
        // 防抖处理，只有在状态稳定超过1.5秒后，才发送RPC更新
        const double debounceWindowSeconds = 1.5;

        while (true)
        {
            try
            {
                var playerFound = TryGetActivePlayer(out var player, out var rpcClient, out var currentPlayerName);

                var currentTime = DateTime.UtcNow;
                var currentPlayerInfo = playerFound ? player?.GetPlayerInfo() : null;

                // 检测状态是否有任何有意义的变化
                var isStateChanged = DetectStateChange(currentPlayerInfo, lastPolledInfo, currentTime, lastPollTime,
                    jumpToleranceSeconds);
                if (isStateChanged)
                {
                    Debug.WriteLine(
                        $"State change detected. Resetting debounce timer for: {currentPlayerInfo?.Title ?? "None"}");
                    pendingUpdateInfo = currentPlayerInfo;
                    lastChangeDetectedTime = currentTime;
                }

                if (pendingUpdateInfo is not null &&
                    (currentTime - lastChangeDetectedTime).TotalSeconds > debounceWindowSeconds)
                {
                    Debug.WriteLine($"Debounce window passed. Sending RPC update for: {pendingUpdateInfo.Value.Title}");
                    UpdateRichPresence(rpcClient, pendingUpdateInfo.Value, currentPlayerName);
                    pendingUpdateInfo = null;
                }

                if (!playerFound && pendingUpdateInfo is not null)
                {
                    Debug.WriteLine("Player closed. Clearing pending update.");
                    netEaseClient.ClearPresence();
                    tencentClient.ClearPresence();
                    pendingUpdateInfo = null;
                }

                lastPolledInfo = currentPlayerInfo;
                lastPollTime = currentTime;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] An exception occurred in UpdateThread: {ex.Message}");
                // 发生异常时重置状态
                lastPolledInfo = null;
                pendingUpdateInfo = null;
            }
            finally
            {
                await Task.Delay(TimeSpan.FromMilliseconds(233));
            }
        }
    }

    /// <summary>
    /// 查找当前是否有受支持的音乐播放器正在运行
    /// </summary>
    private bool TryGetActivePlayer(out IMusicPlayer? player, out DiscordRpcClient? rpcClient, out string playerName)
    {
        // 优先检测网易云音乐
        var netEaseHwnd = Win32Api.User32.FindWindow("OrpheusBrowserHost", null);
        if (netEaseHwnd != IntPtr.Zero &&
            Win32Api.User32.GetWindowThreadProcessId(netEaseHwnd, out var netEaseProcessId) != 0 &&
            netEaseProcessId != 0)
        {
            player = new NetEase(netEaseProcessId);
            rpcClient = netEaseClient;
            playerName = "NetEase CloudMusic";
            return true;
        }

        // 如果没有网易云，再检测QQ音乐
        var tencentHwnd = Win32Api.User32.FindWindow("QQMusic_Daemon_Wnd", null);
        if (tencentHwnd != IntPtr.Zero &&
            Win32Api.User32.GetWindowThreadProcessId(tencentHwnd, out var tencentId) != 0 && tencentId != 0)
        {
            player = new Tencent(tencentId);
            rpcClient = tencentClient;
            playerName = "Tencent QQMusic";
            return true;
        }

        player = null;
        rpcClient = null;
        playerName = string.Empty;
        return false;
    }

    /// <summary>
    /// 比较当前和上一次的播放信息，以确定是否有“有意义的”状态变化
    /// </summary>
    private static bool DetectStateChange(PlayerInfo? current, PlayerInfo? last, DateTime currentTime,
        DateTime lastTime, double tolerance)
    {
        if ((current is null && last is not null) || (current is not null && last is null)) return true;
        if (current is not { } c || last is not { } l) return false;
        if (c.Identity != l.Identity || c.Pause != l.Pause) return true;
        if (c.Pause) return false;

        var elapsed = (currentTime - lastTime).TotalSeconds;
        var progressDelta = c.Schedule - l.Schedule;

        return Math.Abs(progressDelta - elapsed) > tolerance;
    }

    /// <summary>
    /// 构建并发送Rich Presence更新到Discord
    /// </summary>
    private static void UpdateRichPresence(DiscordRpcClient? rpcClient, PlayerInfo info, string playerName)
    {
        if (rpcClient is null) return;

        if (!info.Pause)
        {
            rpcClient.Update(rpc =>
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
                    SmallImageText = playerName,
                };
                rpc.Buttons =
                [
                    new Button { Label = "🎧 Listen", Url = info.Url },
                    new Button
                    {
                        Label = "👏 View App on GitHub", Url = "https://github.com/Kxnrl/NetEase-Cloud-Music-DiscordRPC"
                    },
                ];
            });
        }
        else
        {
            rpcClient.ClearPresence();
        }
    }
}