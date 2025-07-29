using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Kxnrl.Vanessa.Models;
using Kxnrl.Vanessa.Utils;
using Kxnrl.Vanessa.Win32Api;

namespace Kxnrl.Vanessa.Players;

internal sealed class NetEase : IMusicPlayer
{
    private readonly string _path;
    private readonly ProcessMemory _process;
    private readonly nint _audioPlayerPointer;
    private readonly nint _schedulePointer;

    private NetEasePlaylist? _cachedPlaylist;
    private DateTime _lastFileWriteTime;
    private string? _cachedNormalizedHash;

    private const string AudioPlayerPattern
        = "48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 0D ? ? ? ? E8 ? ? ? ? 90 48 8D 0D ? ? ? ? E8 ? ? ? ? 48 8D 05 ? ? ? ? 48 8D A5 ? ? ? ? 5F 5D C3 CC CC CC CC CC 48 89 4C 24 ? 55 57 48 81 EC ? ? ? ? 48 8D 6C 24 ? 48 8D 7C 24";

    private const string AudioSchedulePattern = "66 0F 2E 0D ? ? ? ? 7A ? 75 ? 66 0F 2E 15";

    public NetEase(int pid)
    {
        _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NetEase",
            "CloudMusic",
            "WebData",
            "file",
            "playingList");
        _lastFileWriteTime = DateTime.MinValue;

        var moduleBaseAddress = ProcessUtils.GetModuleBaseAddress(pid, "cloudmusic.dll");
        if (moduleBaseAddress == IntPtr.Zero)
        {
            throw new DllNotFoundException("Could not find cloudmusic.dll in the target process.");
        }

        _process = new ProcessMemory(pid);

        if (Memory.FindPattern(AudioPlayerPattern, pid, moduleBaseAddress, out var app))
        {
            var textAddress = nint.Add(app, 3);
            var displacement = _process.ReadInt32(textAddress);
            _audioPlayerPointer = textAddress + displacement + sizeof(int);
        }

        if (Memory.FindPattern(AudioSchedulePattern, pid, moduleBaseAddress, out var asp))
        {
            var textAddress = nint.Add(asp, 4);
            var displacement = _process.ReadInt32(textAddress);
            _schedulePointer = textAddress + displacement + sizeof(int);
        }

        if (_audioPlayerPointer == nint.Zero)
        {
            throw new EntryPointNotFoundException("Failed to find AudioPlayer pointer.");
        }

        if (_schedulePointer == nint.Zero)
        {
            throw new EntryPointNotFoundException("Failed to find Scheduler pointer.");
        }
    }

    public PlayerInfo? GetPlayerInfo()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _cachedPlaylist = null;
                return null;
            }

            var currentWriteTime = File.GetLastWriteTimeUtc(_path);
            if (currentWriteTime != _lastFileWriteTime || _cachedPlaylist is null)
            {
                var fileBytes = File.ReadAllBytes(_path);
                if (TryGetNormalizedContent(fileBytes, out var normalizedJson, out var newNormalizedHash))
                {
                    if (newNormalizedHash != _cachedNormalizedHash || _cachedPlaylist is null)
                    {
                        Debug.WriteLine("[NetEase] Playlist content changed. Deserializing new playlist.");
                        _cachedPlaylist = JsonSerializer.Deserialize<NetEasePlaylist>(normalizedJson);
                        _cachedNormalizedHash = newNormalizedHash;
                    }
                }
                else
                {
                    _cachedPlaylist = null;
                }

                _lastFileWriteTime = currentWriteTime;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] Failed to process NetEase playlist: {ex.Message}");
            _cachedPlaylist = null;
            return null;
        }

        if (_cachedPlaylist is null || _cachedPlaylist.List.Count == 0)
        {
            return null;
        }

        var status = GetPlayerStatus();
        if (status == PlayStatus.Waiting)
        {
            return null;
        }

        var identity = GetCurrentSongId();
        var currentTrackItem = _cachedPlaylist.List.Find(x => x.Identity == identity);

        if (currentTrackItem is not { Track: { } track })
        {
            return null;
        }

        return new PlayerInfo
        {
            Identity = identity,
            Title = track.Name,
            Artists = string.Join(',', track.Artists.Select(x => x.Singer)),
            Album = track.Album.Name,
            Cover = track.Album.Cover,
            Duration = GetSongDuration(),
            Schedule = GetSchedule(),
            Pause = status == PlayStatus.Paused,
            Url = $"https://music.163.com/#/song?id={identity}",
        };
    }

    private static bool TryGetNormalizedContent(byte[] fileBytes, out string normalizedJson, out string newHash)
    {
        normalizedJson = string.Empty;
        newHash = string.Empty;
        try
        {
            var rootNode = JsonNode.Parse(fileBytes);
            if (rootNode is not JsonObject rootObj || !rootObj.ContainsKey("list") || rootObj["list"] is not JsonArray)
            {
                return false;
            }

            var listArray = rootNode["list"]!.AsArray();
            var clonedArray = JsonNode.Parse(listArray.ToJsonString())!.AsArray();

            foreach (var item in clonedArray)
            {
                NormalizeSongObject(item);
            }

            var newRoot = new JsonObject { ["list"] = clonedArray };
            normalizedJson = newRoot.ToJsonString();
            var hashBytes = MD5.HashData(Encoding.UTF8.GetBytes(normalizedJson));
            newHash = Convert.ToBase64String(hashBytes);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void NormalizeSongObject(JsonNode? item)
    {
        if (item is not JsonObject songObject) return;

        songObject.Remove("randomOrder");
        songObject.Remove("privilege");
        songObject.Remove("referInfo");
        songObject.Remove("fromInfo");

        if (songObject.TryGetPropertyValue("track", out var trackNode) && trackNode is JsonObject trackObj)
        {
            trackObj.Remove("privilege");
        }
    }

    #region Unsafe

    private enum PlayStatus
    {
        Waiting,
        Playing,
        Paused,
        Unknown3,
        Unknown4,
    }

    private double GetSchedule()
        => _process.ReadDouble(_schedulePointer);

    private PlayStatus GetPlayerStatus()
        => (PlayStatus)_process.ReadInt32(_audioPlayerPointer, 0x60);

    private float GetPlayerVolume()
        => _process.ReadFloat(_audioPlayerPointer, 0x64);

    private float GetCurrentVolume()
        => _process.ReadFloat(_audioPlayerPointer, 0x68);

    private double GetSongDuration()
        => _process.ReadDouble(_audioPlayerPointer, 0xa8);

    private string GetCurrentSongId()
    {
        var audioPlayInfo = _process.ReadInt64(_audioPlayerPointer, 0x50);
        if (audioPlayInfo == 0)
        {
            return string.Empty;
        }

        var strPtr = audioPlayInfo + 0x10;
        var strLength = _process.ReadInt64((nint)strPtr, 0x10);

        // small string optimization
        byte[] strBuffer;
        if (strLength <= 15)
        {
            strBuffer = _process.ReadBytes((nint)strPtr, (int)strLength);
        }
        else
        {
            var strAddress = _process.ReadInt64((nint)strPtr);
            strBuffer = _process.ReadBytes((nint)strAddress, (int)strLength);
        }

        var str = Encoding.UTF8.GetString(strBuffer);
        return string.IsNullOrEmpty(str) ? string.Empty : str[..str.IndexOf('_')];
    }

    #endregion
}

internal record NetEasePlaylistTrackArtist([property: JsonPropertyName("name")] string Singer);

internal record NetEasePlaylistTrackAlbum(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("cover")] string Cover);

internal record NetEasePlaylistTrack(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("artists")]
    NetEasePlaylistTrackArtist[] Artists,
    [property: JsonPropertyName("album")] NetEasePlaylistTrackAlbum Album);

internal record NetEasePlaylistItem(
    [property: JsonPropertyName("id")] string Identity,
    [property: JsonPropertyName("track")] NetEasePlaylistTrack Track);

internal record NetEasePlaylist([property: JsonPropertyName("list")] List<NetEasePlaylistItem> List);