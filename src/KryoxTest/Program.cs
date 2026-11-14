using System;
using System.IO;
using System.Linq;
using System.Text;
using Kryox;
using System.Threading;

// Smoke-test console v8:
//   `KryoxTest`           → USB stream regression: pushes the static 1-bit splash for 40 s
//   `KryoxTest web`       → 1-bit HTML dashboard pipeline test (45 s)
//   `KryoxTest png`       → saves a 3x preview PNG of the splash (no device needed)
//   `KryoxTest zenprobe`  → zen idle-screen value probe (leaves the panel in zen)

var pngMode = args.Any(a => string.Equals(a, "png", StringComparison.OrdinalIgnoreCase));
var webMode = args.Any(a => string.Equals(a, "web", StringComparison.OrdinalIgnoreCase));

// harness artifacts never land in the repo — everything goes to %TEMP%\Kryox
var artDir = Path.Combine(Path.GetTempPath(), "Kryox");
Directory.CreateDirectory(artDir);
var logPath = Path.Combine(artDir, "kryox-test.log");
var sb = new StringBuilder();

void Log(string s)
{
    sb.AppendLine(s);
    Console.WriteLine(s);
}

try
{
    Log($"[{DateTime.Now:HH:mm:ss}] Kryox smoke test v8{(pngMode ? " (png preview)" : webMode ? " (web mode)" : "")}");

    if (pngMode)
    {
        var path = Path.Combine(artDir, "preview-splash.png");
        using var pb = SplashFrame.PreviewBitmap();
        pb.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Log($"saved {path}");
        return;
    }

    var display = new Lm360Display();
    if (!display.TryConnect())
    {
        Log($"FAILED: cannot open WinUSB device. Reason: {display.LastError}");
        throw new Exception("usb open failed");
    }
    Log($"Connected via WinUSB: {display.DevicePath}");

    using var sensors = new Sensors();

    // sanity check the splash buffer before streaming (white-on-black 1-bit,
    // so far fewer nonzero bytes than the old colored pages)
    var probe = SplashFrame.Frame;
    int nz = 0;
    for (int i = 0; i < probe.Length; i++) if (probe[i] != 0) nz++;
    Log($"splash rgb24 sanity: len={probe.Length} nonZeroBytes={nz} (expect ≥ a few thousand: logo + text on black)");
    if (nz < 2000) throw new Exception("splash (near-)black buffer");

    var zenProbe = args.Any(a => string.Equals(a, "zenprobe", StringComparison.OrdinalIgnoreCase));
    if (zenProbe)
    {
        Log("--- MODE: zen idle-screen value probe ---");
        Log("--- WATCH THE PANEL: it ends left in zen mode showing a probe value ---");

        // CRC self-test against three known-good packets before touching the panel
        ushort c1 = Lm360Display.Crc16Xmodem(new byte[] { 0x06, 0x03, 0x61, 0x00 });
        ushort c2 = Lm360Display.Crc16Xmodem(new byte[] { 0x00, 0x01, 0x00, 0x58, 0x02, 0x00, 0x2C, 0x01 });
        ushort c3 = Lm360Display.Crc16Xmodem(new byte[] { 0x03, 0x00, 0x00, 0x00 });
        Log($"crc self-test: bright={c1:X4} (want 46D2) frame={c2:X4} (want 11BC) zen={c3:X4} (want 9BDC)");
        if (c1 != 0x46D2 || c2 != 0x11BC || c3 != 0x9BDC)
        {
            Log("CRC SELF-TEST FAILED — aborting probe");
            display.Disconnect();
            return;
        }

        for (int i = 0; i < 20; i++) display.SendRgbFrame(SplashFrame.Frame);   // panel awake, splash visible
        Log("zen: entering idle with 0 (expect the usual ZEN 0° screen)");
        display.SendZenIdle(0);
        Thread.Sleep(1500);

        Log("zen: sending 55.5 — LEAVING PANEL IN ZEN NOW; look at the number it shows");
        display.SendZenIdle(55.5);
        Thread.Sleep(1000);

        Log("probe done — panel left in zen for inspection (the app is NOT running)");
        display.Disconnect();
        return;
    }

    if (webMode)
    {
        Log("--- MODE: 1-bit HTML dashboard via WebView2 ---");
        Log("--- EXPECT: static 1-bit splash until the dashboard is healthy, then the dashboard ---");
        using var dash = new WebDashboard();

        var tEndWeb = DateTime.Now.AddSeconds(45);
        var lastSampleWeb = DateTime.MinValue;
        int nWeb = 0, okWeb = 0, failpWeb = 0;
        var lastStatusWeb = "";
        while (DateTime.Now < tEndWeb)
        {
            if ((DateTime.Now - lastSampleWeb).TotalMilliseconds >= 1000)
            {
                sensors.Sample();
                lastSampleWeb = DateTime.Now;
                dash.Push(sensors.CpuTempC, sensors.GpuTempC,
                          sensors.RamLoadPct, sensors.RamUsedGB, sensors.RamTotalGB);
                if (dash.Status != lastStatusWeb)
                {
                    lastStatusWeb = dash.Status;
                    Log($"dashboard: {lastStatusWeb}");
                }
            }

            var f = dash.LatestFrame ?? SplashFrame.Frame;
            if (f == dash.LatestFrame) nWeb++;
            if (display.SendRgbFrame(f)) okWeb++; else failpWeb++;   // SendRgbFrame paces itself
        }

        Log($"web pipeline: pushed ok={okWeb} fail={failpWeb} dashboardFrames={nWeb} healthy={dash.Healthy} (status: {dash.Status})");
        Log($"sensors: cpu={sensors.CpuTempC:0.#}C gpu={sensors.GpuTempC:0.#}C ram={sensors.RamLoadPct:0.#}% used={sensors.RamUsedGB:0.0}/{sensors.RamTotalGB:0.0} GB");
        display.Disconnect();
        return;
    }

    Log("--- MODE: USB stream regression (static splash) ---");
    Log("--- EXPECT: static 1-bit splash (Kryox logo + wordmark) on the panel for 40 s ---");
    var lastSample = DateTime.MinValue;
    int ok = 0, fail = 0;
    var tEnd = DateTime.Now.AddSeconds(40);
    while (DateTime.Now < tEnd)
    {
        if ((DateTime.Now - lastSample).TotalMilliseconds >= 1000)
        {
            sensors.Sample();
            lastSample = DateTime.Now;
        }

        if (display.SendRgbFrame(SplashFrame.Frame)) ok++; else fail++;   // SendRgbFrame paces itself
    }

    Log($"streamed for 40 s: ok={ok} fail={fail} cpu={sensors.CpuTempC:0.#}C gpu={sensors.GpuTempC:0.#}C ram={sensors.RamLoadPct:0.#}%");
    Log($"OK: streamed ok={ok}/40s (~{ok / 40.0:0.0} fps effective).");
    display.Disconnect();
}
catch (Exception ex)
{
    for (var cur = ex; cur is not null; cur = cur.InnerException)
        Log($"EXCEPTION: {cur.GetType().Name}: {cur.Message}");
}
finally
{
    File.WriteAllText(logPath, sb.ToString());
    Thread.Sleep(1200);
}
