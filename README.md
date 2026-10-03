# MuseDashSteamRP

> A MelonLoader mod that pushes the song you're playing in Muse Dash into Steam Rich Presence, like DJMAX RESPECT V does. **Pipeline fully working & empirically verified; the display slot itself is gated on an official token upload by PeroPeroGames — full evidence below.** An "it's-ready-and-waiting" implementation plus a complete test kit for the community to push the official fix.

让 Muse Dash（喵斯快跑）像 DJMAX RESPECT V 一样，在 Steam 好友列表显示当前正在打的歌。

**当前状态（2026-10 实测）**：mod 的取数与 Steam 写键管线全部打通；但 Steam 对"没有官方 rich presence token 的应用"封死了所有自定义显示位。详见[实测结论](#实测结论2026-10-02-全通道实测闭环)。本仓库同时是**就绪实现 + 完整证据包**。

## 背景

- Steam 好友列表里游戏名下那行小字叫 Rich Presence：游戏调 `SetRichPresence("steam_display", "#Token")`，token 对应**开发者在 Steamworks 合作伙伴后台上传的本地化模板**（如 `#PlayingSong → "正在玩 %song%"`）。DJMAX 就是这么做的。
- Muse Dash 本体从不调用 `SetRichPresence`（游戏 `global-metadata.dat` 中 `steam_display` 零出现）——所以好友永远只能看到"正在玩 Muse Dash"。
- 第三方 mod 无法替 PeroPeroGames 上传模板，所以本 mod 把所有不需要官方配合的野路子全部实测了一遍（结果见下），并把可行管线做成了"官方 token 上线之日，改一行即点亮"的形态。

## 这个 mod 做什么

MelonLoader mod（net6，无 MuseDashMirror 依赖）：

- `StageBattleComponent.GameStart`（谱面开打）时读取 `GlobalDataBase.s_DbBattleStage.selectedMusicInfo` / `selectedDifficulty`，按模板格式化（中文正常）；
- 写入 Steam Rich Presence：优先借道游戏自带的 Steamworks.NET IL2CPP 代理（游戏已初始化 Steamworks，无需自行 Init/Shutdown），失败时回落到 native flat API（`SteamAPI_SteamFriends_v017` 实测有效；字符串 `LPUTF8Str` 编组防中文乱码）；
- `Channel` 配置三通道：`status` / `display` / `steam_text` / `all`。

## 实测结论（2026-10-02 全通道实测闭环）

用本仓库 `test/SteamRPTest` 的**读取探针**（枚举 Steam 存储侧实际键值）逐通道验证：

| 写入通道 | 存储侧 | 显示侧 |
|---|---|---|
| `steam_display` = 裸文本 | ❌ `SetRichPresence` 返回 True 但 Steam 存空串（静默丢弃） | 不可能 |
| `steam_display` = `#steam_text` + `steam_text` 键 | ❌ `steam_text` 能存上，`steam_display` 仍被清空 | 不可能 |
| `status` = 裸文本 | ✅ 唯一完整落库的键（`♪ 天使回响 [Master 10]`，中文完好） | ❌ 好友列表第二行不渲染（只认 `steam_display`）；网页迷你资料卡无状态行；"查看游戏信息"弹窗实机未见显示 |

显示侧旁证：Steam 网页版 miniprofile 接口对本人返回 `In-Game / Muse Dash`、无任何状态行。对照组：同一好友列表中《逃离塔科夫》（官方有 token）的第二行状态字正常渲染。

**结论：在 PeroPeroGames 上传 rich presence 本地化 token 之前，第三方 mod 无法在 Steam 任何界面显示自定义状态。**

## 安装

前置：[MelonLoader](https://melonloader.com/)（喵丝 mod 环境，社区通用）。

1. 下载 `MuseDashSteamRP.dll` 放入 `<游戏目录>\Mods\`；
2. 首次运行后生成 `UserData\MelonPreferences.cfg` 配置段。

> 诚实说明：在官方 token 上线前，本 mod **没有任何可见效果**（写入的 `status` 键 Steam 已存储但不渲染）。它的意义是管线就绪 + 本文证据。介意者可无视安装步骤，只把本仓库当调研报告读。

## 构建

```bash
# 默认使用本机 Muse Dash 安装路径；其他机器用环境变量 MD_DIRECTORY 指定游戏目录
dotnet build -c Release
# 游戏正在运行时 Mods 里的 dll 被锁,只编译不部署:
dotnet build -c Release -p:SkipModsCopy=true
```

构建成功后自动复制到 `<游戏目录>\Mods\MuseDashSteamRP.dll`。

## 配置

```ini
[MuseDashSteamRP]
Enabled = true
PlayingTemplate = ♪ {name} [{diff} {level}]   ; 可用变量 {name} {author} {diff} {level} {bpm}
MenuStatus = 在主菜单                         ; 留空则保持显示上一首歌
Channel = status                              ; status（唯一能落库）/ display / steam_text / all
```

日志：`MelonLoader\Latest.log` 中 `[RP]` 行记录写入内容，`[RP/native ...]` 表示走了 native 兜底。

## 推动官方（真正的解锁点）

 PeroPeroGames 只需在 Steamworks 后台 Rich Presence Setup 页上传一个万能模板，例如：

```
"lang"
{
  "Language"  "schinese"
  "Tokens"    { "#StatusPlaying" "♪ %text%" }
}
```

对官方是 5 分钟的事，收益是整个玩家群的好友列表显示（社区 mod、官方活动都能用）。欢迎拿本文实测数据到 [MDMC 社区](https://mdmc.moe) 或 PeroPeroGames 反馈渠道推动。官方 token 上线后，本 mod 把写键改为 `steam_display=#StatusPlaying` + 参数键（约一行改动）即可点亮。

## test/SteamRPTest —— 独立诊断工具

不依赖游戏进程的 Steamworks 控制台工具（.NET 10），用同一 appid（774171）初始化后：

```bash
# 首次使用:从 <游戏目录>\MuseDash_Data\Plugins\x86_64\ 复制 steam_api64.dll 到本目录
dotnet run --project test/SteamRPTest -- smoke   # 3 秒冒烟:验证 Init + 接口指针
dotnet run --project test/SteamRPTest -- read    # 诊断:枚举本账号当前存储的全部 presence 键 + readback 自检
dotnet run --project test/SteamRPTest            # 两阶段显示实测:裸文本/#steam_text 各 5 分钟,自动清理
```

`read` 探针是本次排查的关键工具——直接看 Steam 存储侧真相，绕开"API 返回 True 但实际丢弃"的静默行为。SteamID 运行时自动获取。

## 相关项目

- [MDMods/MuseDashMirror](https://github.com/MDMods/MuseDashMirror) —— 喵丝 mod 库（本 mod 未依赖，纯 Harmony + MelonMod）
- [Braasileiro/MDRPC](https://github.com/Braasileiro/MDRPC) —— Discord 侧的同类实现
- [BetterSort](https://github.com/Thedeergod666/BetterSort) —— 同作者的喵丝选曲增强 mod
