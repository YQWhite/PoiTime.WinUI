# PoiTime! - 舰娘Collection整点报时工具 / Kantai Collection Hourly Voice Announcement Tool

![GitHub](https://img.shields.io/badge/Language-C%23-blue)
![GitHub](https://img.shields.io/badge/Platform-Windows-lightgrey)
![GitHub](https://img.shields.io/badge/Version-1.2.1-green)

**请从 [Releases](https://github.com/YQWhite/PoiTime.WinUI/releases/latest) 下载便携版 ZIP，完整解压后运行。已包含 .NET、Windows App SDK 和 Visual C++ 运行组件，无需自行安装运行库或 MSIX。**

---

## 🌸 项目简介 / Introduction

**PoiTime!** 是一个基于《舰队Collection》（艦隊これくしょん）角色语音的整点报时工具。
通过调用舰娘语音包，在系统托盘静默运行，每到预设时间便会播放对应的语音，还原游戏中「提督、〇時です」的经典报时体验！

**PoiTime!** is a hourly voice announcement tool inspired by *Kantai Collection (KanColle)*.
It silently runs in the system tray and plays character voices at scheduled times, recreating the iconic "Teitoku, X-ji desu!" experience from the game.

---

## ⚓ 功能特性 / Features

- **多舰娘支持**：自由切换夕立、时雨等舰娘语音包（需配置对应资源）
- **精确报时**：每分钟检测系统时间，误差小于2秒
- **无窗口化**：通过托盘图标管理，支持右键菜单快速操作
- **开机自启**：自动注册到系统启动项
- **跨ID兼容**：自适应 `voices/<id>/` 目录结构，轻松扩展新角色
- **Multiple Shipgirls**: Switch between Yukikaze, Shigure, and other characters (requires voice packs)
- **Precision Timing**: Checks system time every second with <2s delay
- **Tray-only UI**: Manage via system tray with context menu
- **Auto-start**: Registers itself to Windows startup
- **ID-based Structure**: Extendable via `voices/<id>/` directory

---

## 🚢 舰C元素 / KanColle Integration

- **语音还原**：支持舰娘官方台词格式（例：`15時です。おやつにしましょう！`）
- **角色命名**：语音包配置中可定义舰娘名称，托盘图标实时显示当前角色
- **扩展性**：遵循「舰娘ID」文件夹规范，兼容POI、74EO等常用插件的资源结构
- **Authentic Voices**: Supports official voice line formats (e.g., `15-ji desu. Oyatsu ni shimashou!`)
- **Dynamic Naming**: Tray icon displays current shipgirl name from config
- **Compatibility**: Follows "Shipgirl ID" folder standards, compatible with POI/74EO resource structures

---

## 🛠️ 快速入门 / Getting Started

### 安装步骤 / Installation

1. 从 [Release 页面](https://github.com/YQWhite/PoiTime.WinUI/releases/latest) 下载 `PoiTime-Portable-1.2.1-win-x64.zip`。
2. 完整解压到有写入权限的目录，双击 `PoiTime.WinUI.exe`。请保留所有 DLL、PRI 和资源文件。
3. 包内含四个语音包，也可以在程序内下载，或自行添加到 `voices/<id>/`（[示例](PoiTime.WinUI/voices/144)）。

Windows x64 only. Extract the complete ZIP and run `PoiTime.WinUI.exe`; application runtimes are included.
建议 Windows 10 22H2 或 Windows 11。声明的系统下限是 Windows 10 1809，旧版系统仍需实机验证。
Windows N / 精简系统需要保留系统媒体功能。故障处理详见 [兼容性说明](COMPATIBILITY.md)。

### 编译与自动发布

开发环境需要 .NET 8 SDK、Visual Studio / Build Tools 的 MSBuild、C++ 工具和 Windows SDK。
在仓库根目录运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-portable.ps1
```

成品输出到 `artifacts/`。脚本检查自包含设置、必要文件和版本号，并生成 ZIP 及 SHA256。
重复构建请用 `-OutputRoot` 指定新的输出目录。

[GitHub Actions](https://github.com/YQWhite/PoiTime.WinUI/actions) 会在推送 master/main、提交 PR 或手动运行时编译并上传构建产物。
推送 `v` 开头的版本标签会在编译成功后自动创建 Release 并上传 ZIP 和校验文件。

版本采用 `主版本.次版本.修订号`：当前程序版本为 `1.2.1`，标签为 `v1.2.1`，文件/程序集版本为 `1.2.1.0`。
下次发布时，先更新 `Directory.Build.props` 中的 `VersionPrefix`、README 的版本号，以及
`PoiTime.WinUI/app.manifest`、`PoiTime.WinUI/Package.appxmanifest` 中对应的四段版本号，然后提交并推送：

```powershell
git tag -a v1.2.2 -m "Release v1.2.2"
git push origin master
git push origin v1.2.2
```

标签与项目版本不一致会阻止发布。普通分支构建不会创建 Release，已有历史标签保留。

### 基本操作 / Usage

- **右键托盘图标** → 修改ID / 重载配置 / 退出
- **首次运行**：自动生成 `config.json`
- **Right-click tray icon** → Change ID / Reload / Exit
- **First run**: Auto-generates `config.json` and registers auto-start

## 🛠️ downloader.py 使用教程

### ❗请注意，V1.0起，程序更新自动下载语音包逻辑，无需此脚本❗

[点击此处观看视频演示](https://YQWhite.github.io/PoiTime/)

1. ~~从 [Release页面](https://github.com/YQWhite/PoiTime/releases) 下载脚本~~
2. ~~安装 [Python](https://www.python.org/downloads/)~~
3. ~~安装依赖项 ``pip install requests tqdm``~~
4. ~~打开cmd运行脚本 ``py downloader.py``~~
5. ~~前往你想要语音包的角色的kcwiki页面（以下以时雨为例，https://zh.kcwiki.cn/wiki/%E6%97%B6%E9%9B%A8）~~
6. ~~（可选，若你不知道舰娘报时对应的改装型号，请进行步骤6-8，否则直接进行步骤9）F12打开开发者控制台，切换到"网络/Network"标签页。~~
7. ~~（可选）点击控制台左上角"开始记录"。~~
8. ~~（可选）点击任意时报语音的播放按钮，在控制台中会出现xxx.mp3，比如时雨0点报时就是080a-0000.mp3，080a即为时雨改在kcwiki上的编号。~~
9. ~~在downloader.py运行窗口中输入kcwiki编号（若你不知道舰娘报时对应的改装型号，请进行步骤6-8，否则直接进行此步骤），即可开始下载语音包。~~
10. ~~下载完成后，会在脚本同目录下新建downloads文件夹，其中编号文件夹即为语音包文件夹，放置在voices文件夹下即可。~~

---

## 📂 语音包规范 / Voice Pack Specification

```plaintext
voices/
└── 144/                  # 舰娘ID（夕立=144）
    ├── config.json       # 定义语音时间点与舰娘名称
    ├── 12-00.mp3         # 文件名格式: [Custom].mp3
    └── 15-30.mp3
```

## 📂 语音包配置文件规范 / Voice Pack configuration Specification

```plaintext
{
  "voiceCount": 24,
  "name": "夕立",
  "voices": [
    { "hour": 1, "minute": 0, "fileName": "0100.mp3" },
    { "hour": 2, "minute": 0, "fileName": "0200.mp3" },
    { "hour": 3, "minute": 0, "fileName": "0300.mp3" },
    { "hour": 4, "minute": 0, "fileName": "0400.mp3" },
    { "hour": 5, "minute": 0, "fileName": "0500.mp3" },
    { "hour": 6, "minute": 0, "fileName": "0600.mp3" },
    { "hour": 7, "minute": 0, "fileName": "0700.mp3" },
    { "hour": 8, "minute": 0, "fileName": "0800.mp3" },
    { "hour": 9, "minute": 0, "fileName": "0900.mp3" },
    { "hour": 10, "minute": 0, "fileName": "1000.mp3" },
    { "hour": 11, "minute": 0, "fileName": "1100.mp3" },
    { "hour": 12, "minute": 0, "fileName": "1200.mp3" },
    { "hour": 13, "minute": 0, "fileName": "1300.mp3" },
    { "hour": 14, "minute": 0, "fileName": "1400.mp3" },
    { "hour": 15, "minute": 0, "fileName": "1500.mp3" },
    { "hour": 16, "minute": 0, "fileName": "1600.mp3" },
    { "hour": 17, "minute": 0, "fileName": "1700.mp3" },
    { "hour": 18, "minute": 0, "fileName": "1800.mp3" },
    { "hour": 19, "minute": 0, "fileName": "1900.mp3" },
    { "hour": 20, "minute": 0, "fileName": "2000.mp3" },
    { "hour": 21, "minute": 0, "fileName": "2100.mp3" },
    { "hour": 22, "minute": 0, "fileName": "2200.mp3" },
    { "hour": 23, "minute": 0, "fileName": "2300.mp3" },
    { "hour": 0, "minute": 0, "fileName": "0000.mp3" }
  ],
  "special": {
    "date": "2025-02-11",
    "fileName": "special.mp3"
  }
}


```

---

## 🎉 特别致谢 / Special Thanks

- 本项目灵感来源于《舰队Collection》的「秘书舰报时」机制
- 使用 NAudio 实现音频播放
- Inspired by KanColle's "Secretary Ship Hourly Announcement" feature
- Powered by NAudio for audio playback

---

📜 许可证 / License

- MIT License | 本项目仅供学习交流，与《舰队Collection》官方无关联

---

**Let your shipgirl accompany your digital life!**
**让舰娘的语音陪伴您的每一刻！** 🌸
