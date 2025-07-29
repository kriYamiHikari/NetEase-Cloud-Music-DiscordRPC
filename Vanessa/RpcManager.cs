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
    private IMusicPlayer? _activePlayer;
    private string _activePlayerName = string.Empty;
    private DiscordRpcClient? _activeRpcClient;

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
                UpdateActivePlayer();

                var currentTime = DateTime.UtcNow;
                var currentPlayerInfo = _activePlayer?.GetPlayerInfo();

                var isStateChanged = DetectStateChange(currentPlayerInfo, lastPolledInfo, currentTime, lastPollTime,
                    jumpToleranceSeconds);
                if (isStateChanged)
                {
                    Debug.WriteLine(
                        $"State change detected. Resetting debounce timer for: {currentPlayerInfo?.Title ?? "None (Clear)"}");
                    pendingUpdateInfo = currentPlayerInfo;
                    lastChangeDetectedTime = currentTime;
                }

                if (pendingUpdateInfo is not null &&
                    (currentTime - lastChangeDetectedTime).TotalSeconds > debounceWindowSeconds)
                {
                    Debug.WriteLine($"Debounce window passed. Sending RPC update for: {pendingUpdateInfo.Value.Title}");
                    UpdateRichPresence(_activeRpcClient, pendingUpdateInfo.Value, _activePlayerName);
                    pendingUpdateInfo = null;
                }

                lastPolledInfo = currentPlayerInfo;
                lastPollTime = currentTime;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] An exception occurred in UpdateThread: {ex.Message}");
                _activePlayer = null;
                lastPolledInfo = null;
                pendingUpdateInfo = null;
                netEaseClient.ClearPresence();
                tencentClient.ClearPresence();
            }
            finally
            {
                await Task.Delay(TimeSpan.FromMilliseconds(233));
            }
        }
    }

    /// <summary>
    /// 检测并更新当前活动的播放器实例
    /// </summary>
    private void UpdateActivePlayer()
    {
        // 优先检测网易云音乐
        var netEaseHwnd = Win32Api.User32.FindWindow("OrpheusBrowserHost", null);
        if (netEaseHwnd != IntPtr.Zero &&
            Win32Api.User32.GetWindowThreadProcessId(netEaseHwnd, out var netEaseProcessId) != 0)
        {
            if (_activePlayer is NetEase) return;

            Debug.WriteLine("Switching to NetEase CloudMusic player.");
            _activeRpcClient?.ClearPresence();

            _activePlayer = new NetEase(netEaseProcessId);
            _activePlayerName = "NetEase CloudMusic";
            _activeRpcClient = netEaseClient;
            return;
        }

        // 如果没有网易云，再检测QQ音乐
        var tencentHwnd = Win32Api.User32.FindWindow("QQMusic_Daemon_Wnd", null);
        if (tencentHwnd != IntPtr.Zero && Win32Api.User32.GetWindowThreadProcessId(tencentHwnd, out var tencentId) != 0)
        {
            if (_activePlayer is Tencent) return;

            Debug.WriteLine("Switching to Tencent QQMusic player.");
            _activeRpcClient?.ClearPresence();

            _activePlayer = new Tencent(tencentId);
            _activePlayerName = "Tencent QQMusic";
            _activeRpcClient = tencentClient;
            return;
        }

        if (_activePlayer is null) return;

        Debug.WriteLine("No active player detected. Clearing player instance and presence.");
        _activeRpcClient?.ClearPresence();
        _activePlayer = null;
        _activePlayerName = string.Empty;
        _activeRpcClient = null;
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

        var presence = new RichPresence
        {
            State = StringUtils.GetTruncatedStringByMaxByteLength($"🎤 {info.Artists}", 128),
            Type = ActivityType.Listening,
            Assets = new Assets
            {
                LargeImageKey = info.Cover,
                LargeImageText = StringUtils.GetTruncatedStringByMaxByteLength($"💿 {info.Album}", 128),
                SmallImageKey = "timg",
                SmallImageText = playerName,
            },
            Buttons =
            [
                new Button { Label = "🎧 Listen", Url = info.Url },
                new Button
                {
                    Label = "🆕 View App on GitHub (fork)",
                    Url = "https://github.com/kriYamiHikari/NetEase-Cloud-Music-DiscordRPC"
                },
            ]
        };

        // 根据播放状态决定是否设置时间戳和修改状态文本
        if (!info.Pause)
        {
            presence.Details = StringUtils.GetTruncatedStringByMaxByteLength($"▶️ {info.Title}", 128);
            presence.Type = ActivityType.Listening;
            presence.Timestamps = new Timestamps(
                DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(info.Schedule)),
                DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(info.Schedule))
                    .Add(TimeSpan.FromSeconds(info.Duration))
            );
        }
        else
        {
            // 暂停时切换为暂停状态图标，但由于限制时间进度依旧会自动增长
            presence.Details = StringUtils.GetTruncatedStringByMaxByteLength($"⏸️ {info.Title}", 128);
        }

        rpcClient.SetPresence(presence);
    }
}