using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Il2CppAssets.Scripts.Database;
using Il2CppAssets.Scripts.UI.Panels;
using Il2CppFormulaBase;
using MelonLoader;

namespace MuseDashSteamRP;

// Muse Dash 本体从不调 SetRichPresence(global-metadata 里 steam_display 零出现),
// 所以 rich presence 各键完全归本 mod 所有。2026-10-02 实测结论:
//   status     —— 唯一能完整落库的键(Steam 存储侧确认);官方文档称其渲染于
//                 好友右键"查看游戏信息"弹窗,好友列表那一行不渲染它
//   display    —— steam_display 裸文本:SetRichPresence 返回 True 但 Steam 存空串,
//                 通道死亡(app 无官方 token 时无法解析的值一律静默丢弃)
//   steam_text —— steam_display="#steam_text" 同样被清空,通道死亡
// 好友列表第二行(DJMAX 效果)的唯一通路是 PeroPeroGames 在 Steamworks 后台
// 上传本地化 token(如 #StatusPlaying → "%text%"),那天到来后 mod 改一行即可。
public class Main : MelonMod
{
    private static readonly string[] DifficultyNames = { "?", "Easy", "Hard", "Master", "Hidden", "Special" };

    private static MelonPreferences_Entry<bool> _enabled;
    private static MelonPreferences_Entry<string> _playingTemplate;
    private static MelonPreferences_Entry<string> _menuStatus;
    private static MelonPreferences_Entry<string> _channel;

    public static string Channel => _channel?.Value ?? "status";

    public override void OnInitializeMelon()
    {
        var category = MelonPreferences.CreateCategory("MuseDashSteamRP", "Muse Dash Steam Rich Presence");
        _enabled = category.CreateEntry("Enabled", true, "Enable rich presence push");
        _playingTemplate = category.CreateEntry("PlayingTemplate", "♪ {name} [{diff} {level}]", "Status while playing. Variables: {name} {author} {diff} {level} {bpm}");
        _menuStatus = category.CreateEntry("MenuStatus", "在主菜单", "Status in main menu. Leave empty to keep the last song shown.");
        _channel = category.CreateEntry("Channel", "status", "Which presence keys to write: status (only one that persists) / display / steam_text / all");

        SteamRpc.Log = m => LoggerInstance.Msg(m);
        HarmonyInstance.PatchAll();

        LoggerInstance.Msg("MuseDashSteamRP loaded. Song status will be pushed on battle start.");
    }

    [HarmonyPatch(typeof(StageBattleComponent), nameof(StageBattleComponent.GameStart))]
    private static class GameStartPatch
    {
        private static void Postfix() => PushPlayingStatus();
    }

    [HarmonyPatch(typeof(PnlMenu), nameof(PnlMenu.Awake))]
    private static class PnlMenuPatch
    {
        private static void Postfix()
        {
            if (!_enabled.Value || string.IsNullOrEmpty(_menuStatus.Value))
                return;
            SteamRpc.SetStatus(_menuStatus.Value);
        }
    }

    private static void PushPlayingStatus()
    {
        try
        {
            if (!_enabled.Value)
                return;

            var stage = GlobalDataBase.s_DbBattleStage;
            var info = stage?.selectedMusicInfo;
            if (info == null)
                return;

            var diff = stage.selectedDifficulty;
            var text = _playingTemplate.Value
                .Replace("{name}", info.name ?? string.Empty)
                .Replace("{author}", info.author ?? string.Empty)
                .Replace("{diff}", DiffName(diff))
                .Replace("{level}", SafeLevel(info, diff))
                .Replace("{bpm}", info.bpm ?? string.Empty);

            SteamRpc.SetStatus(text);
        }
        catch (Exception e)
        {
            Melon<Main>.Logger.Error($"Failed to push playing status: {e}");
        }
    }

    private static string DiffName(int diff) =>
        diff >= 0 && diff < DifficultyNames.Length ? DifficultyNames[diff] : "?";

    private static string SafeLevel(MusicInfo info, int diff)
    {
        try
        {
            return info.GetMusicLevelStringByDiff(diff) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}

internal static class SteamRpc
{
    internal static Action<string> Log = _ => { };
    private static bool _proxyFailed;

    /// <summary>按 Channel 配置把文本写进 rich presence 键。text 传 null 表示清除。</summary>
    internal static void SetStatus(string text)
    {
        var writes = BuildWrites(Main.Channel, text);
        if (!_proxyFailed)
        {
            try
            {
                // 游戏本体已初始化 Steamworks(DLC/平台层),直接借道它自带的
                // Steamworks.NET IL2CPP 代理程序集,无需自己管理 Init/Shutdown。
                foreach (var (key, value) in writes)
                    Il2CppSteamworks.SteamFriends.SetRichPresence(key, value);
                Log(text == null ? "[RP] cleared" : $"[RP] {text}");
                return;
            }
            catch (Exception e)
            {
                _proxyFailed = true;
                Log($"Steamworks.NET proxy unavailable ({e.Message}); trying native flat API");
            }
        }

        NativeSet(writes);
    }

    private static List<KeyValuePair<string, string>> BuildWrites(string channel, string text)
    {
        if (text == null)
            return new List<KeyValuePair<string, string>>
            {
                new("steam_display", ""), new("steam_text", ""), new("status", ""),
            };

        switch (channel)
        {
            case "status":
                return new List<KeyValuePair<string, string>> { new("status", text) };
            case "display":
                return new List<KeyValuePair<string, string>> { new("steam_display", text) };
            case "steam_text":
                return new List<KeyValuePair<string, string>>
                {
                    new("steam_display", "#steam_text"), new("steam_text", text),
                };
            default: // all:status 键 + steam_display 裸文本一起上,实机验证期默认
                return new List<KeyValuePair<string, string>>
                {
                    new("status", text), new("steam_display", text),
                };
        }
    }

    #region Native flat Steamworks API fallback

    // 模块名与游戏 Plugins/x86_64 已加载的一致,同进程 DllImport 按名命中。
    // 字符串必须 LPUTF8Str:steam_api 收 UTF-8,默认 CharSet.Ansi 会按系统
    // 代码页(GBK)转码,中文歌名直接乱码——SteamSongPresence 旧版的教训。
    private const string SteamApi = "steam_api64.dll";

    private static void NativeSet(List<KeyValuePair<string, string>> writes)
    {
        try
        {
            var friends = GetFriendsPtr();
            if (friends == IntPtr.Zero)
            {
                Log("[RP/native] no ISteamFriends interface found");
                return;
            }

            foreach (var (key, value) in writes)
            {
                var ok = SteamAPI_ISteamFriends_SetRichPresence(friends, key, value ?? "");
                Log($"[RP/native {key}] {(value == "" ? "<clear>" : value)} -> {ok}");
            }
        }
        catch (Exception e)
        {
            Log($"[RP/native] failed: {e.Message}");
        }
    }

    private static IntPtr _friendsPtr;

    private static IntPtr GetFriendsPtr()
    {
        if (_friendsPtr != IntPtr.Zero)
            return _friendsPtr;

        // v017 访问器已在本机游戏的 steam_api64.dll(Steamworks SDK 1.57)上
        // 实测有效;游戏日后升级 SDK 就按新版本号往下探。
        foreach (var probe in new Func<IntPtr>[]
                 {
                     SteamAPI_SteamFriends_v017, SteamAPI_SteamFriends_v018,
                     SteamAPI_SteamFriends_v019, SteamAPI_SteamFriends_v016,
                 })
        {
            try
            {
                var ptr = probe();
                if (ptr != IntPtr.Zero)
                {
                    _friendsPtr = ptr;
                    return ptr;
                }
            }
            catch (EntryPointNotFoundException)
            {
                // 该 SDK 版本没有这个访问器,试下一个
            }
        }

        // 访问器全缺时的兜底:按版本串手动取接口。注意版本串格式是
        // "SteamFriends017",不是 "STEAMFRIENDS_INTERFACE_VERSION017"。
        var user = SteamAPI_GetHSteamUser();
        foreach (var version in new[] { "SteamFriends017", "SteamFriends018", "SteamFriends016" })
        {
            var ptr = SteamInternal_FindOrCreateUserInterface(user, version);
            if (ptr != IntPtr.Zero)
            {
                _friendsPtr = ptr;
                return ptr;
            }
        }

        return IntPtr.Zero;
    }

    [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamFriends_v016();

    [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamFriends_v017();

    [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamFriends_v018();

    [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamFriends_v019();

    [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
    private static extern int SteamAPI_GetHSteamUser();

    [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamInternal_FindOrCreateUserInterface(int hSteamUser,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pchVersion);

    [DllImport(SteamApi, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pchKey,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pchValue);

    #endregion
}
