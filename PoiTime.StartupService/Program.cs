using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.ServiceProcess;

namespace PoiTime.StartupService;

internal static class Program
{
    private const string ServiceName = "PoiTimeStartup";

    public static int Main(string[] args)
    {
        if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            return Install(args);

        if (args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase))
            return Uninstall();

        string? appPath = GetOptionValue(args, "--app");
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath))
            return 2;

        ServiceBase.Run(new PoiTimeStartupService(appPath));
        return 0;
    }

    private static int Install(string[] args)
    {
        if (!IsAdministrator())
            return 3;

        string? appPath = GetOptionValue(args, "--app");
        string? servicePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(appPath) || !File.Exists(appPath) || string.IsNullOrWhiteSpace(servicePath))
            return 2;

        try
        {
            StopAndDeleteService();
            string binPath = $"\"{servicePath}\" --app \"{appPath}\"";
            int result = RunSc(
                ("create", ServiceName),
                ("binPath=", binPath),
                ("start=", "auto"),
                ("DisplayName=", "PoiTime! High Priority Startup"),
                ("obj=", "LocalSystem"));
            if (result != 0)
                return 10 + result;

            RunSc(("description", ServiceName), (null, "在用户登录时更早启动 PoiTime!"));
            result = RunSc(("start", ServiceName));
            return result == 0 ? 0 : 100 + result;
        }
        catch
        {
            return 1;
        }
    }

    private static int Uninstall()
    {
        if (!IsAdministrator())
            return 3;

        try
        {
            StopAndDeleteService();
            return IsServiceInstalled() ? 1 : 0;
        }
        catch
        {
            return 1;
        }
    }

    private static void StopAndDeleteService()
    {
        if (!IsServiceInstalled())
            return;

        RunSc(("stop", ServiceName));
        for (int attempt = 0; attempt < 20; attempt++)
        {
            using var service = new ServiceController(ServiceName);
            try
            {
                service.Refresh();
                if (service.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
                    break;
            }
            catch
            {
                break;
            }
            Thread.Sleep(100);
        }

        RunSc(("delete", ServiceName));
        for (int attempt = 0; attempt < 30 && IsServiceInstalled(); attempt++)
            Thread.Sleep(100);
    }

    private static bool IsServiceInstalled()
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"query {ServiceName}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        process?.WaitForExit();
        return process?.ExitCode == 0;
    }

    private static int RunSc(params (string? Name, string Value)[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var (name, value) in arguments)
        {
            if (name is not null)
                startInfo.ArgumentList.Add(name);
            startInfo.ArgumentList.Add(value);
        }

        using var process = Process.Start(startInfo);
        process?.WaitForExit();
        return process?.ExitCode ?? 1;
    }

    private static string? GetOptionValue(IReadOnlyList<string> args, string option)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i].Equals(option, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
