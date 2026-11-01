using Microsoft.Win32;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace PoiTime.WinUI.Services;

public enum AutoStartMode
{
    Disabled,
    Standard,
    HighPriority
}

public enum ElevationResult
{
    Success,
    Cancelled,
    Failed,
    MissingHelper
}

public static class AutoStartManager
{
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PoiTime.WinUI";
    private const string ServiceName = "PoiTimeStartup";

    public static AutoStartMode GetCurrentMode()
    {
        if (IsServiceInstalled())
            return AutoStartMode.HighPriority;

        return IsStandardAutoStartEnabled() ? AutoStartMode.Standard : AutoStartMode.Disabled;
    }

    public static bool HasLegacyAutoStartTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/query /tn \"PoiTimeAutoStart\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void RemoveLegacyAutoStartTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/delete /tn \"PoiTimeAutoStart\" /f",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            process?.WaitForExit();
        }
        catch
        {
        }
    }

    public static void SetStandardAutoStart(bool enabled)
    {
        RemoveLegacyAutoStartTask();
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, true);

        key.DeleteValue("PoiTime", false);
        key.DeleteValue("PoiTimeAutoStart", false);
        if (enabled)
        {
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("无法确定程序路径。");
            key.SetValue(RunValueName, $"\"{exePath}\" /autostart", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(RunValueName, false);
        }
    }

    public static async Task<ElevationResult> ConfigureHighPriorityAsync(bool enabled)
    {
        string helperPath = Path.Combine(AppContext.BaseDirectory, "startup-service", "PoiTime.StartupService.exe");
        if (!File.Exists(helperPath))
            return ElevationResult.MissingHelper;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helperPath,
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(enabled ? "--install" : "--uninstall");
            if (enabled)
            {
                startInfo.ArgumentList.Add("--app");
                startInfo.ArgumentList.Add(Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法确定程序路径。"));
            }

            using var process = Process.Start(startInfo);
            if (process is null)
                return ElevationResult.Failed;

            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? ElevationResult.Success : ElevationResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return ElevationResult.Cancelled;
        }
        catch
        {
            return ElevationResult.Failed;
        }
    }

    private static bool IsStandardAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return !string.IsNullOrWhiteSpace(key?.GetValue(RunValueName) as string);
    }

    private static bool IsServiceInstalled()
    {
        try
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
        catch
        {
            return false;
        }
    }
}
