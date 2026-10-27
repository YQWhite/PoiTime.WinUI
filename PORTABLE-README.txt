PoiTime 便携版（Windows x64）

使用方法：把整个 ZIP 完整解压到自己有写入权限的文件夹，双击 PoiTime.WinUI.exe。
请保留所有 DLL、PRI、Assets 和 voices 文件，不要单独移动 EXE。

本包自带 .NET 8.0.25、Windows App SDK 1.8 和 x64 Visual C++ 运行组件。
普通 Windows 10 / Windows 11 用户无需另行下载安装这些运行库，也无需安装 MSIX。

项目声明的系统下限：Windows 10 1809（内部版本 17763）。
建议使用 Windows 10 22H2（19045）或 Windows 11；旧版系统仍需实机验证。
仅适用于 x64 系统。Windows 10 的最终功能更新为 22H2；25H2 是 Windows 11 的版本名称。
请以 winver 的系统名称和内部版本号判断，不要仅看软件检测到的“Windows 10”文字。

Windows N 或删减了系统媒体功能的系统，需要启用/恢复 Windows 自带的媒体组件；
这些属于操作系统功能，无法由 .NET / Windows App SDK 运行库代替。

窗口关闭按钮会将程序隐藏到系统托盘，请从托盘菜单选择“退出”来完全结束程序。
普通启动无需管理员权限。放在 Program Files 等受保护目录下可能无法保存配置或下载语音。

如果启动失败，请先确认完整解压，再查看启动错误提示。
启动日志：%LOCALAPPDATA%\PoiTime\logs\startup.log
若没有日志，请在“事件查看器 → Windows 日志 → 应用程序”中查找 PoiTime 的错误。
托管代码运行之前发生的系统加载错误可能无法写入启动日志。

本包包含的 Microsoft .NET、Windows App SDK、Visual C++ 组件及其他第三方依赖，
版权和许可归各自权利人所有。原项目语音和图像版权说明仍然适用。
