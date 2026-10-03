// Steam Rich Presence 实测工具(独立进程,不依赖游戏):
//   smoke      —— 3 秒冒烟:验证 Init + 接口指针
//   read       —— 诊断:枚举本账号在 774171 下当前存储的全部 rich presence 键,
//                 并做 readback 自检(写一个探针键再读回,验证读写链路)
//   (无参数)   —— 两阶段显示实测:阶段1 steam_display 裸文本 5 分钟,
//                 阶段2 #steam_text 通道 5 分钟,自动清理
// 用游戏自带 steam_api64.dll(Steamworks SDK 1.57),steam_appid.txt = 774171 (Muse Dash)。
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class Steam
{
    private const string Dll = "steam_api64.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SteamAPI_Init();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SteamAPI_Shutdown();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SteamAPI_SteamFriends_v017();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    internal static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string key,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SteamAPI_ISteamFriends_RequestFriendRichPresence(IntPtr self, ulong steamID);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int SteamAPI_ISteamFriends_GetFriendRichPresenceKeyCount(IntPtr self, ulong steamID);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresenceKeyByIndex(IntPtr self, ulong steamID, int iKey);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong steamID,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pchKey);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr SteamAPI_SteamUser_v023();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    internal static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
}

internal static class Program
{
    private static IntPtr _friends;
    private static ulong _myId;

    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "full";
        Log("SteamAPI_Init ...");
        if (!Steam.SteamAPI_Init())
        {
            Log("SteamAPI_Init FAILED");
            return 2;
        }
        _friends = Steam.SteamAPI_SteamFriends_v017();
        if (_friends == IntPtr.Zero)
        {
            Log("ISteamFriends NULL");
            return 3;
        }
        Log($"Init OK, ISteamFriends=0x{_friends.ToInt64():X}");

        var user = Steam.SteamAPI_SteamUser_v023();
        _myId = user == IntPtr.Zero ? 0 : Steam.SteamAPI_ISteamUser_GetSteamID(user);
        if (_myId == 0)
        {
            Log("GetSteamID FAILED");
            return 4;
        }
        Log($"SteamID = {_myId}");

        try
        {
            switch (mode)
            {
                case "smoke": return Smoke();
                case "read": return Read();
                default: return Full();
            }
        }
        finally
        {
            Steam.SteamAPI_Shutdown();
            Log("shutdown");
        }
    }

    private static int Smoke()
    {
        Set("steam_display", "smoke");
        Set("steam_display", null);
        Log("smoke done");
        return 0;
    }

    private static int Read()
    {
        Log($"RequestFriendRichPresence(self {_myId}) ...");
        Steam.SteamAPI_ISteamFriends_RequestFriendRichPresence(_friends, _myId);
        Thread.Sleep(2000);

        DumpKeys("游戏进程写入后的现状");

        // readback 自检:验证"写→存→读"链路本身是通的
        Set("steam_display", "PROBE_READBACK_TEST");
        Thread.Sleep(1000);
        DumpKeys("readback 自检写入后");

        Set("steam_display", null);
        Thread.Sleep(500);
        DumpKeys("清理后");
        return 0;
    }

    private static void DumpKeys(string title)
    {
        var n = Steam.SteamAPI_ISteamFriends_GetFriendRichPresenceKeyCount(_friends, _myId);
        Log($"--- {title}: KeyCount={n} ---");
        for (var i = 0; i < n; i++)
        {
            var keyPtr = Steam.SteamAPI_ISteamFriends_GetFriendRichPresenceKeyByIndex(_friends, _myId, i);
            var key = Marshal.PtrToStringUTF8(keyPtr) ?? "<null>";
            var valPtr = Steam.SteamAPI_ISteamFriends_GetFriendRichPresence(_friends, _myId, key);
            var val = Marshal.PtrToStringUTF8(valPtr) ?? "<null>";
            Log($"    {key} = {val}");
        }
    }

    private static int Full()
    {
        Log("=== PHASE 1: raw text into steam_display (5 min) ===");
        Set("steam_display", $"BetterSort·SteamRP实测[阶段1·裸文本] {DateTime.Now:HH:mm:ss}");
        Thread.Sleep(TimeSpan.FromMinutes(5));

        Log("=== PHASE 2: #steam_text channel (5 min) ===");
        Set("steam_display", "#steam_text");
        Set("steam_text", $"BetterSort·SteamRP实测[阶段2·steam_text通道] {DateTime.Now:HH:mm:ss}");
        Thread.Sleep(TimeSpan.FromMinutes(5));

        Set("steam_display", null);
        Set("steam_text", null);
        Log("cleanup done");
        return 0;
    }

    private static void Set(string k, string v) =>
        Log($"Set({k}, {v ?? "<null>"}) -> {Steam.SteamAPI_ISteamFriends_SetRichPresence(_friends, k, v ?? "")}");

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
}
