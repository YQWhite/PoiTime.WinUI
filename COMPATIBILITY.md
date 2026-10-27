# PoiTime ZIP 兼容性修复

## 排查依据

以下结论来自原开发项目的发布设置、MSBuild 求值和发布目录检查。

- 原项目目标为 `net8.0-windows10.0.19041.0`，系统下限声明为 `10.0.17763.0`。
- 原项目引用 Windows App SDK `1.8.260209005`、H.NotifyIcon.WinUI `2.1.2`。
- 原 `.csproj` 将 `WindowsAppSDKSelfContained` 同时设为 true 和 false，后面的 false 生效。
- 原 `win-x64.pubxml` 将 `SelfContained` 设为 false。
- MSBuild 求值确认这两个值均为 false。已有 x64 发布包的 runtimeconfig 要求
  `Microsoft.NETCore.App 8.0.0`，目录没有 coreclr、hostfxr、Microsoft.UI.Xaml 原生 DLL。

因此，原 x64 包依赖外部 .NET 8 x64 和 Windows App Runtime 1.8 x64。
.NET Desktop Runtime 8 也包含所需的 .NET Runtime；仅装 .NET Framework 4.8 或 .NET 10
不会默认满足这个 .NET 8 应用。不同 Windows App Runtime 主/次版本也不能任意替代。
无受影响机器的错误日志，不能把每一例启动失败都归为同一原因。

## 本次修复

- 统一 .NET / Windows App SDK 自包含设置，随包提供 .NET 8.0.25。
- x64 发布目录改为固定结构，禁用裁剪、单文件和 ReadyToRun。
- 发布脚本从 Visual Studio 的可再发行目录复制 x64 CRT DLL 到 EXE 旁，检查必要文件后打 ZIP。
- 给 XAML 初始化期间的事件加保护，避免控件尚未生成就访问它们。
- 窗口外观按能力启用，设置图标失败时保留主窗口启动。
- 取消每次普通启动都要求管理员重启的逻辑，下载时的权限处理保留。
- 添加启动日志及启动异常提示。原生加载器在 App 构造前的错误仍需事件查看器诊断。

## 再次发布

在 PowerShell 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\publish-portable.ps1
```

需要 .NET 8 SDK、Visual Studio Build Tools、Windows SDK、C++ 可再发行组件目录。
首次构建需要恢复 NuGet 包。如果当前缓存齐全，可加 `-Offline`。
脚本默认输出到仓库内的 `artifacts`，拒绝覆盖已有成品；再次运行请传入新的 `-OutputRoot`。
版本由 `Directory.Build.props` 统一提供，脚本检查两个清单和 EXE 版本，生成带版本号的 ZIP 及 SHA256。
GitHub Actions 使用相同脚本构建，推送匹配项目版本的 `v主版本.次版本.修订号` 标签后自动发布 Release。

请对外分发整个 ZIP。自包含不代表单 EXE；删除 DLL/PRI/资源文件会破坏发布包。

项目声明的 Windows 10 1809 下限不等于已经验证的兼容性承诺。
仍应在干净的 Windows 10 22H2、Windows 11 标准用户环境中验证启动、托盘、播放和下载。
N 版/精简系统缺少的 Windows 媒体功能需要在操作系统中恢复。

## 原版用户临时补装

- .NET 8 Windows x64 Runtime：https://dotnet.microsoft.com/download/dotnet/8.0
- Windows App Runtime 1.8 x64：从 https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads
  选择 1.8 稳定分支的最新运行时安装程序。
- 缺失 MSVCP140/VCRUNTIME140 等 DLL 时安装微软 x64 VC++ v14 可再发行组件。

本次重新生成的便携包已经包含上述应用运行组件，普通用户无需补装。

## 本次验证结果

- 自包含 Release x64 发布通过。
- 使用 UI Automation 确认主窗口及主页成功创建，四个内置语音包正常显示。
- 实际触发语音试听及停止，播放器状态和按钮文字正常切换。
- 检查运行进程，确认 hostfxr、hostpolicy、coreclr、Microsoft.UI.Xaml 和
  Microsoft.WindowsAppRuntime 均从发布文件夹加载。
- 最终成品程序集与已测试程序集 SHA256 一致。
- 检查嵌入清单中引用的 24 个原生 DLL 均已随包提供。
- 对照发布目录逐一验证 ZIP 中的文件及长度，并验证 ZIP SHA256。
- 本机系统内部版本为 28000；没有 Windows 10 实机验证结果。

上述为本次修复的本机验证记录；GitHub 每次构建的输出和 ZIP 可在 Actions 中查看。
