using System;
using System.Diagnostics;
using System.IO;

namespace Kryox;

/// <summary>
/// Start-with-Windows helper. The app runs elevated (required by the
/// LibreHardwareMonitor kernel driver), so the plain HKCU "Run" registry key
/// is a poor fit (UAC prompt on every logon). A Task Scheduler entry with
/// "highest privileges" starts cleanly and silently.
/// </summary>
public static class Autostart
{
    private const string TaskName = "Kryox";

    /// <summary>True when the scheduled task exists.</summary>
    public static bool IsEnabled()
        => Run("/Query /TN \"" + TaskName + "\"") == 0;

    /// <summary>
    /// Registers/removes the ONLOGON boot task. The installer passes the
    /// installed exe path explicitly so the task never points at a stray copy.
    /// </summary>
    public static void Set(bool enable, string? exe = null)
    {
        if (enable)
        {
            exe ??= Environment.ProcessPath
                    ?? Path.Combine(AppContext.BaseDirectory, "Kryox.exe"); // single-file: BaseDirectory is the exe folder
            // NOTE: the extra \" quoting survives paths with spaces (schtasks needs them)
            Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F");
        }
        else
        {
            Run($"/Delete /TN \"{TaskName}\" /F");
        }
    }

    private static int Run(string args)
    {
        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        p.Start();
        p.WaitForExit(15000);
        return p.ExitCode;
    }
}
