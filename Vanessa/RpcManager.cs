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
    private class PlayerState
    {
        public IMusicPlayer? Player { get; set; }

        // 状态检测
        public PlayerInfo? LastPolledInfo { get; set; }
        public DateTime LastPollTime { get; set; } = DateTime.MinValue;

        // 防抖机制
        public PlayerInfo? PendingUpdateInfo { get; set; }
        public DateTime LastChangeDetectedTime { get; set; } = DateTime.MinValue;
    }

    private readonly PlayerState _netEaseState = new();
    private readonly PlayerState _tencentState = new();

    // 如果实际进度变化与时间流逝的差异超过0.4秒，则认为跳转了歌曲进度
    private const double JumpToleranceSeconds = 0.4;

    // 防抖处理，只有在状态稳定超过1.5秒后，才发送RPC更新
    private const double DebounceWindowSeconds = 1.5;

    /// <summary>
    /// 启动无限循环的更新线程
    /// </summary>
    public async Task Start()
    {
        while (true)
        {
            var currentTime = DateTime.UtcNow;
            try
            {
                PollPlayer(_netEaseState, "OrpheusBrowserHost", "NetEase CloudMusic", netEaseClient,
                    pid => new NetEase(pid), currentTime);
                PollPlayer(_tencentState, "QQMusic_Daemon_Wnd", "Tencent QQMusic", tencentClient,
                    pid => new Tencent(pid), currentTime);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FATAL ERROR] An exception occurred in the main poll loop: {ex.Message}");
                ClearAllPlayers();
            }
            finally
            {
                await Task.Delay(TimeSpan.FromMilliseconds(233));
            }
        }
    }

    /// <summary>
    /// 播放器轮询方法，包含进程检测、状态检测、防抖和RPC更新
    /// </summary>
    private static void PollPlayer(PlayerState state, string windowClass, string playerName, DiscordRpcClient rpcClient,
        Func<int, IMusicPlayer> playerFactory, DateTime currentTime)
    {
        var hwnd = Win32Api.User32.FindWindow(windowClass, null);
        if (hwnd != IntPtr.Zero && Win32Api.User32.GetWindowThreadProcessId(hwnd, out var pid) != 0)
        {
            if (state.Player is null)
            {
                Debug.WriteLine($"[{playerName}] Player process detected. Creating instance.");
                state.Player = playerFactory(pid);
            }

            var currentInfo = state.Player.GetPlayerInfo();

            // 状态变化检测
            var isStateChanged = DetectStateChange(currentInfo, state.LastPolledInfo, currentTime, state.LastPollTime,
                JumpToleranceSeconds);
            if (isStateChanged)
            {
                Debug.WriteLine(
                    $"[{playerName}] State change detected. Resetting debounce timer for: {currentInfo?.Title ?? "None (Clear)"}");
                state.PendingUpdateInfo = currentInfo;
                state.LastChangeDetectedTime = currentTime;
            }

            // 防抖
            if (state.PendingUpdateInfo is not null &&
                (currentTime - state.LastChangeDetectedTime).TotalSeconds > DebounceWindowSeconds)
            {
                Debug.WriteLine($"[{playerName}] Debounce window passed. Sending RPC update.");
                UpdateOrClearPresence(rpcClient, state.PendingUpdateInfo, playerName);
                state.PendingUpdateInfo = null;
            }

            state.LastPolledInfo = currentInfo;
            state.LastPollTime = currentTime;
        }
        else
        {
            if (state.Player is null) return;
            Debug.WriteLine($"[{playerName}] Player process lost. Clearing instance and RPC.");
            rpcClient.ClearPresence();
            state.Player = null;
            state.LastPolledInfo = null;
            state.PendingUpdateInfo = null;
        }
    }

    /// <summary>
    /// 发生严重错误时，清理所有播放器状态
    /// </summary>
    private void ClearAllPlayers()
    {
        netEaseClient.ClearPresence();
        _netEaseState.Player = null;
        _netEaseState.LastPolledInfo = null;
        _netEaseState.PendingUpdateInfo = null;

        tencentClient.ClearPresence();
        _tencentState.Player = null;
        _tencentState.LastPolledInfo = null;
        _tencentState.PendingUpdateInfo = null;
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
    /// 根据播放信息更新或清除Rich Presence
    /// </summary>
    private static void UpdateOrClearPresence(DiscordRpcClient rpcClient, PlayerInfo? info, string playerName)
    {
        if (info is not { } playerInfo)
        {
            rpcClient.ClearPresence();
            return;
        }

        var presence = new RichPresence
        {
            State = StringUtils.GetTruncatedStringByMaxByteLength($"🎤 {playerInfo.Artists}", 128),
            Type = ActivityType.Listening,
            Assets = new Assets
            {
                LargeImageKey = playerInfo.Cover,
                LargeImageText = StringUtils.GetTruncatedStringByMaxByteLength($"💿 {playerInfo.Album}", 128),
                SmallImageKey = "timg",
                SmallImageText = playerName,
            },
            Buttons =
            [
                new Button { Label = "🎧 Listen", Url = playerInfo.Url },
                new Button
                {
                    Label = "🆕 View App on GitHub (fork)",
                    Url = "https://github.com/kriYamiHikari/NetEase-Cloud-Music-DiscordRPC"
                },
            ]
        };

        // 根据播放状态决定是否设置时间戳和修改状态文本
        if (!playerInfo.Pause)
        {
            presence.Details = StringUtils.GetTruncatedStringByMaxByteLength($"▶️ {playerInfo.Title}", 128);
            presence.Timestamps = new Timestamps(
                DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(playerInfo.Schedule)),
                DateTime.UtcNow.Subtract(TimeSpan.FromSeconds(playerInfo.Schedule))
                    .Add(TimeSpan.FromSeconds(playerInfo.Duration))
            );
        }
        else
        {
            // 暂停时切换为暂停状态图标，但由于限制时间进度依旧会自动增长
            presence.Details = StringUtils.GetTruncatedStringByMaxByteLength($"⏸️ {playerInfo.Title}", 128);
        }

        rpcClient.SetPresence(presence);
    }
}