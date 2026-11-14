using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Kryox;

internal static class Program
{
    private static Mutex? _singleInstance;

    /// <summary>
    /// Installed location: %LOCALAPPDATA%\Kryox\app\Kryox.exe. The single
    /// desktop exe copies itself here on double-click, registers the boot
    /// task and starts the installed copy — only the installed copy ever
    /// runs, so the app survives pruning of the repo and desktop files.
    /// </summary>
    private static readonly string InstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kryox", "app");

    private static readonly string InstallExe = Path.Combine(InstallDir, "Kryox.exe");

    [STAThread]
    private static void Main()
    {
        // Any run that is not from the installed location and not an explicit
        // development run (KRYOX_NODEPLOY=1) acts as install/upgrade: deploy
        // and exit — the installed copy is what keeps running from here on.
        if (!SamePath(Environment.ProcessPath, InstallExe) &&
            Environment.GetEnvironmentVariable("KRYOX_NODEPLOY") != "1")
        {
            Deploy();
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // A UI-thread exception must never show the fatal error dialog (one
        // accidental "Quit" click ends the whole app) — log it and keep going;
        // the stream loop is self-healing. Anything that ever escapes another
        // thread is logged too, so no death can be silent again.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => CrashLog("ui", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog("domain", e.ExceptionObject as Exception);

        // one instance only
        _singleInstance = new Mutex(true, "Kryox-LM360", out bool first);
        if (!first) return;

        Application.Run(new TrayContext());
    }

    // ------------------------------------------------------------ install

    /// <summary>
    /// Copies self to the installed location, (re)points the ONLOGON boot task
    /// at it, and starts it. The UAC prompt (the manifest requires admin for
    /// the sensor driver) covers all of this — one prompt on install, silent
    /// starts on every boot afterwards.
    /// </summary>
    private static void Deploy()
    {
        string here = Environment.ProcessPath ?? "Kryox.exe";
        int me = Process.GetCurrentProcess().Id;
        bool installedRunning = false;

        foreach (var p in Process.GetProcessesByName("Kryox"))
        {
            if (p.Id == me) continue;
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { /* unreadable — treat as stale */ }
            if (SamePath(path, InstallExe)) { installedRunning = true; continue; }
            try
            {
                p.Kill();
                p.WaitForExit(2000);
                Log($"deploy: killed stale instance ({path ?? "#" + p.Id})");
            }
            catch { /* already gone */ }
        }

        if (installedRunning)
        {
            Log("deploy: installed copy already running — nothing to do");
            return;
        }

        try
        {
            Directory.CreateDirectory(InstallDir);
            if (!SamePath(here, InstallExe))
            {
                if (File.Exists(InstallExe)) File.Delete(InstallExe);
                File.Copy(here, InstallExe);
                Log($"deploy: copied {here}");
                Log($"deploy:    to {InstallExe}");
            }

            Autostart.Set(true, InstallExe);   // boot task always points at the installed exe
            Log("deploy: boot task (Kryox, ONLOGON, highest) registered");

            Process.Start(new ProcessStartInfo(InstallExe) { UseShellExecute = true });
            Log("deploy: started installed copy");
        }
        catch (Exception e)
        {
            Log("deploy FAILED: " + e.Message);
        }
    }

    /// <summary>Append-only install log — the installer has no UI, so failures must land somewhere.</summary>
    private static void Log(string s)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "Kryox");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "install.log"), $"[{DateTime.Now:HH:mm:ss}] {s}\n");
        }
        catch { /* never let logging break the install */ }
    }

    /// <summary>
    /// Last-gasp exception trace: appends to %TEMP%\Kryox\crash.log. The app's
    /// one silent death taught us this file is the only voice it has when
    /// something goes wrong.
    /// </summary>
    private static void CrashLog(string kind, Exception? ex)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "Kryox");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:HH:mm:ss}] {kind}: {ex}\n");
        }
        catch { /* never let logging kill the app */ }
    }

    private static bool SamePath(string? a, string b)
        => !string.IsNullOrEmpty(a) &&
           string.Equals(Path.GetFullPath(a), b, StringComparison.OrdinalIgnoreCase);
}
