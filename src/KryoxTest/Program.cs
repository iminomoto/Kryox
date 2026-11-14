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
//   `KryoxTest pngdash`   → saves a 3x preview PNG of the dashboard (no device needed)
//   `KryoxTest zenprobe`  → zen idle-screen value probe (leaves the panel in zen)
//   `KryoxTest zenlong`   → 12.5-min zen heartbeat experiment (same vs alternating value)
//   `KryoxTest zenframe`  → 15-min zen + frame-poke experiment (does a frame prevent the zen reboot?)

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

/// <summary>RGB24 320×240 buffer → 24bpp bitmap (row-copy; 1-bit white/black is order-agnostic).</summary>
static System.Drawing.Bitmap BufferToBitmap(byte[] rgb24)
{
    int w = SplashFrame.Width, h = SplashFrame.Height, stride = w * 3;
    var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
    var bd = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h),
        System.Drawing.Imaging.ImageLockMode.WriteOnly,
        System.Drawing.Imaging.PixelFormat.Format24bppRgb);
    try
    {
        for (int y = 0; y < h; y++)
            System.Runtime.InteropServices.Marshal.Copy(rgb24, y * stride, bd.Scan0 + y * bd.Stride, stride);
    }
    finally { bmp.UnlockBits(bd); }
    return bmp;
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

    var pngDash = args.Any(a => string.Equals(a, "pngdash", StringComparison.OrdinalIgnoreCase));
    if (pngDash)
    {
        Log("--- MODE: dashboard preview png (no device needed) ---");
        using var dash = new WebDashboard();

        // the dashboard's own mock loop is gated, so this feed is what shows
        dash.Push(52.5, 44.0, 38.2, 12.4, 32.0);

        var tEndPng = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < tEndPng && !(dash.Healthy && dash.LatestFrame is not null))
            Thread.Sleep(250);
        if (dash.LatestFrame is null)
        {
            Log($"dashboard never produced a frame (status: {dash.Status})");
            return;
        }

        Thread.Sleep(2500);   // dwell: let the animations reach a representative state
        dash.Push(52.5, 44.0, 38.2, 12.4, 32.0);
        Thread.Sleep(500);
        var frame = dash.LatestFrame!;

        // RGB24 320×240 → panel-exact preview, scale ×3 nearest-neighbor
        using var src = BufferToBitmap(frame);
        var big = new System.Drawing.Bitmap(SplashFrame.Width * 3, SplashFrame.Height * 3,
            System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g2 = System.Drawing.Graphics.FromImage(big))
        {
            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g2.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g2.DrawImage(src, new Rectangle(0, 0, big.Width, big.Height));
        }
        var dashPng = Path.Combine(artDir, "preview-dashboard.png");
        big.Save(dashPng, System.Drawing.Imaging.ImageFormat.Png);
        Log($"saved {dashPng} (status: {dash.Status}, healthy: {dash.Healthy})");
        big.Dispose();
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

    var zenLong = args.Any(a => string.Equals(a, "zenlong", StringComparison.OrdinalIgnoreCase));
    if (zenLong)
    {
        Log("--- MODE: zen long-run experiment (~12.5 min, panel shows ZEN 55 the whole time) ---");
        Log("--- WATCH THE PANEL: note WHEN (which phase) the boot animation plays, if at all ---");

        ushort c3 = Lm360Display.Crc16Xmodem(new byte[] { 0x03, 0x37, 0x00, 0x00 });
        Log($"crc self-test: zen55={c3:X4}");
        if (c3 != Lm360Display.Crc16Xmodem(new byte[] { 0x03, 0x37, 0x00, 0x00 }) || c3 == 0)
        {
            Log("unexpected zen packet — aborting");
            display.Disconnect();
            return;
        }

        for (int i = 0; i < 20; i++) display.SendRgbFrame(SplashFrame.Frame);   // panel awake
        display.SendZenIdle(55.5);
        Log($"[{DateTime.Now:HH:mm:ss}] PHASE A (6 min): SAME-value 55.5 heartbeat every 30 s — screen must show 55");

        var tEndA = DateTime.Now.AddMinutes(6);
        while (DateTime.Now < tEndA)
        {
            Thread.Sleep(30_000);
            display.SendZenIdle(55.5);
            Log($"[{DateTime.Now:HH:mm:ss}] A send 55.5");
        }

        Log($"[{DateTime.Now:HH:mm:ss}] PHASE B (6 min): ALTERNATING 55.5/55.0 every 30 s — screen still shows 55");

        var tEndB = DateTime.Now.AddMinutes(6);
        bool alt = false;
        while (DateTime.Now < tEndB)
        {
            Thread.Sleep(30_000);
            alt = !alt;
            display.SendZenIdle(alt ? 55.0 : 55.5);
            Log($"[{DateTime.Now:HH:mm:ss}] B send {(alt ? 55.0 : 55.5)}");
        }

        Log("experiment done — panel left in zen (55.5). Report which phases animated.");
        display.Disconnect();
        return;
    }

    var zenFrame = args.Any(a => string.Equals(a, "zenframe", StringComparison.OrdinalIgnoreCase));
    if (zenFrame)
    {
        Log("--- MODE: zen + frame-poke experiment (~15 min, ZEN 55 with a brief blink every 90 s) ---");
        Log("--- WATCH: the DeepCool BOOT ANIMATION is the failure; the 90 s blink is the expected poke ---");

        for (int i = 0; i < 20; i++) display.SendRgbFrame(SplashFrame.Frame);   // panel awake
        display.SendZenIdle(55.5);
        Log($"[{DateTime.Now:HH:mm:ss}] zen entered (55.5); poking one black frame + re-zen every 90 s");

        var black = new byte[320 * 240 * 3];   // all-black RGB24 frame
        var tEndPoke = DateTime.Now.AddMinutes(15);
        int poke = 0;
        while (DateTime.Now < tEndPoke)
        {
            Thread.Sleep(90_000);
            poke++;
            display.SendRgbFrame(black);
            display.SendZenIdle(55.5);
            Log($"[{DateTime.Now:HH:mm:ss}] poke #{poke}: black frame + re-zen");
        }

        Log("experiment done — panel left in zen (55.5). Report: any boot animation?");
        display.Disconnect();
        return;
    }

    var zenFast = args.Any(a => string.Equals(a, "zenfast", StringComparison.OrdinalIgnoreCase));
    if (zenFast)
    {
        Log("--- MODE: zen fast-feed experiment (~12 min, live sensor value pushed every 3 s) ---");
        Log("--- WATCH: the DeepCool BOOT ANIMATION is the failure; number updates are expected ---");

        for (int i = 0; i < 20; i++) display.SendRgbFrame(SplashFrame.Frame);   // panel awake
        sensors.Sample();
        int t0 = Math.Max(0, (int)Math.Round(sensors.CpuTempC));
        display.SendZenIdle(t0);
        Log($"[{DateTime.Now:HH:mm:ss}] zen entered with live temp {t0}; pushing sensor value every 3 s");

        var tEndFast = DateTime.Now.AddMinutes(12);
        int sent = 0;
        while (DateTime.Now < tEndFast)
        {
            Thread.Sleep(3000);
            sensors.Sample();
            int t = Math.Max(0, (int)Math.Round(sensors.CpuTempC));
            display.SendZenIdle(t);
            sent++;
            if (sent % 20 == 0) Log($"[{DateTime.Now:HH:mm:ss}] {sent} pushes sent (last value {t})");
        }

        Log($"experiment done — {sent} pushes over 12 min. Report: any boot animation?");
        display.Disconnect();
        return;
    }

    var zenQuick = args.Any(a => string.Equals(a, "zenquick", StringComparison.OrdinalIgnoreCase));
    if (zenQuick)
    {
        Log("--- MODE: zen quick test (1 min, live value pushed every 1 s) ---");
        Log("--- WATCH THE PANEL for the whole minute ---");

        for (int i = 0; i < 20; i++) display.SendRgbFrame(SplashFrame.Frame);   // panel awake
        sensors.Sample();
        int tq = Math.Max(0, (int)Math.Round(sensors.CpuTempC));
        display.SendZenIdle(tq);
        Log($"[{DateTime.Now:HH:mm:ss}] zen entered ({tq}°); pushing every 1 s for 60 s");

        for (int i = 1; i <= 60; i++)
        {
            Thread.Sleep(1000);
            sensors.Sample();
            int t = Math.Max(0, (int)Math.Round(sensors.CpuTempC));
            display.SendZenIdle(t);
            Log($"[{DateTime.Now:HH:mm:ss}] push {i:00}: {t}°");
        }

        Log("quick test done — panel left in zen. What did you see?");
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
