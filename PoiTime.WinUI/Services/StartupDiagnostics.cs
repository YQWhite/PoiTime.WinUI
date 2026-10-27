using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PoiTime.WinUI.Services
{
    internal static class StartupDiagnostics
    {
        private static readonly object LogLock = new();
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PoiTime", "logs", "startup.log");

        public static void Initialize()
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Write("AppDomain.UnhandledException", args.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, args) =>
                Write("TaskScheduler.UnobservedTaskException", args.Exception);
            Write($"Starting; OS={Environment.OSVersion}; Runtime={Environment.Version}; " +
                $"Architecture={RuntimeInformation.ProcessArchitecture}");
        }

        public static void Write(string stage, Exception? exception = null)
        {
            try
            {
                lock (LogLock)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllText(LogPath,
                        $"[{DateTimeOffset.Now:O}] {stage}{Environment.NewLine}" +
                        (exception == null ? "" : exception + Environment.NewLine));
                }
            }
            catch
            {
                // A log directory failure must not stop the application from opening.
            }
        }

        public static void ReportFatal(Exception exception)
        {
            Write("Startup failed", exception);
            MessageBox(IntPtr.Zero,
                $"PoiTime 启动失败：{exception.Message}\n\n" +
                "请完整解压程序文件夹后再运行，不要只复制 EXE。\n" +
                "Windows N 或精简系统还需要恢复系统自带的媒体组件。\n\n" +
                $"错误代码：0x{exception.HResult:X8}\n日志：{LogPath}",
                "PoiTime 启动失败", 0x10);
        }

        [DllImport("user32.dll", EntryPoint = "MessageBoxW", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr window, string text, string caption, uint type);
    }
}
