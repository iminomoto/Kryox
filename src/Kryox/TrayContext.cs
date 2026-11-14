using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Kryox;

/// <summary>
/// Tray application host: NotifyIcon + a background thread that pushes monitor
/// frames to the LM-Series display at ~10 fps (paced), with sensor sampling at
/// 1 Hz. The panel firmware drops rendering when frames stop arriving, so the
/// frame stream is continuous while running.
///
/// Rendering: one design only — the embedded 1-bit HTML dashboard in an
/// offscreen WebView2. The static 1-bit splash (white K centered) holds for
/// 10 seconds on every launch, keeps the panel alive if the dashboard lags,
/// and would remain on it if WebView2 ever failed, so the panel never blanks.
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Lm360Display _display = new();
    private readonly Sensors _sensors = new();
    private WebDashboard? _dash = new();

    private readonly ToolStripMenuItem _btnZen;
    private readonly ToolStripMenuItem _btnAutostart;

    /// <summary>App version (from the assembly), shown in the menu header and tooltip.</summary>
    private static readonly string Version =
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version is { } v
            ? $"v{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>Guaranteed splash hold on every launch (mirrors SplashFrame.HoldDurationMs).</summary>
    private static readonly TimeSpan SplashHold = TimeSpan.FromMilliseconds(SplashFrame.HoldDurationMs);
    private readonly DateTime _boot = DateTime.UtcNow;

    private volatile bool _zen;

    /// <summary>Last value pushed to the zen screen (-1 = force a fresh send).</summary>
    private int _zenTemp = -1;

    private Thread? _loop;
    private volatile bool _running = true;

    public TrayContext()
    {
        _tray = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "Kryox",
            Visible = true,
        };

        var menu = new ContextMenuStrip();

        var header = (ToolStripMenuItem)menu.Items.Add($"Kryox {Version}");
        header.Enabled = false;

        _btnZen = (ToolStripMenuItem)menu.Items.Add("Zen mode", image: null, (_, _) =>
        {
            if (!_display.IsConnected) return;
            // Zen mode = the firmware's own zen screen showing the live CPU temp
            // (command 03, live-verified). The panel renders it itself, so while
            // it is on the dashboard's whole WebView2 browser is unloaded and
            // the frame pipeline stops — the app idles at ~zero CPU and a
            // fraction of its RAM. Frames (and a fresh dashboard) come back the
            // moment zen is switched off; the splash covers the browser warmup.
            _zen = !_zen;
            _zenTemp = -1;
            if (_zen)
            {
                int t = Math.Max(0, (int)Math.Round(_sensors.CpuTempC));
                _zenTemp = t;
                _display.SendZenIdle(t);          // instant: ZEN <temp>°
                try { _dash?.Dispose(); } catch { }
                _dash = null;
            }
            else
            {
                _dash = new WebDashboard();
            }
            UpdateZenUi();
        });

        menu.Items.Add("Brightness +", image: null, (_, _) => { if (_display.IsConnected) _display.BrightnessUp(); });
        menu.Items.Add("Brightness −", image: null, (_, _) => { if (_display.IsConnected) _display.BrightnessDown(); });

        _btnAutostart = (ToolStripMenuItem)menu.Items.Add("Start with Windows", image: null, (_, _) =>
        {
            bool enable = !Autostart.IsEnabled();
            Autostart.Set(enable);
            UpdateAutostartUi();
        });

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;

        UpdateZenUi();
        UpdateAutostartUi();

        _loop = new Thread(StreamLoop)
        {
            IsBackground = true,
            Name = "Kryox stream",
        };
        _loop.Start();
    }

    // ------------------------------------------------------------ stream loop

    private void StreamLoop()
    {
        var lastSample = DateTime.MinValue;

        while (_running)
        {
            try
            {
                if (!_display.IsConnected && !_display.TryConnect())
                {
                    SetTrayText("LM360 not found");
                }
                else if (_zen)
                {
                    // zen mode: the firmware renders its own screen — just keep
                    // the displayed temp current, no frames, no dashboard
                    SetTrayText("zen mode");
                    if ((DateTime.Now - lastSample).TotalMilliseconds >= 5000)
                    {
                        _sensors.Sample();
                        lastSample = DateTime.Now;
                        int t = Math.Max(0, (int)Math.Round(_sensors.CpuTempC));
                        if (t != _zenTemp)
                        {
                            _zenTemp = t;
                            _display.SendZenIdle(t);
                        }
                    }
                    Thread.Sleep(500);   // no frame pacing here — keep the loop quiet
                }
                else
                {
                    bool inBeat = DateTime.UtcNow - _boot < SplashHold;

                    var dash = _dash;
                    SetTrayText(dash is { Healthy: true } ? "1-bit dashboard" : "starting");

                    // sensor refresh ~1 Hz
                    if ((DateTime.Now - lastSample).TotalMilliseconds >= 1000)
                    {
                        _sensors.Sample();
                        lastSample = DateTime.Now;
                        dash?.Push(_sensors.CpuTempC, _sensors.GpuTempC,
                                   _sensors.RamLoadPct, _sensors.RamUsedGB, _sensors.RamTotalGB);
                    }

                    // splash: hold the centered K for 10 s, then dashboard frames.
                    // The splash also covers warmup/failure, so the panel never blanks.
                    var frame = !inBeat ? dash?.LatestFrame : null;
                    _display.SendRgbFrame(frame ?? SplashFrame.Frame);
                }
            }
            catch
            {
                // survive transient USB hiccups; reconnect happens on the next pass
                try { _display.Disconnect(); } catch { }
            }

            // SendRgbFrame paces itself to the target frame period — no outer sleep
        }
    }

    private void SetTrayText(string text)
    {
        text = $"Kryox {Version} — {text}";
        if (_tray.Text != text)
        {
            try { _tray.Text = text; } catch { }
        }
    }

    private void UpdateZenUi() => _btnZen.Checked = _zen;

    private void UpdateAutostartUi() => _btnAutostart.Checked = Autostart.IsEnabled();

    private void ExitApp()
    {
        _running = false;
        try { _loop?.Join(500); } catch { }
        _tray.Visible = false;
        _tray.Dispose();
        try { _display.Disconnect(); } catch { }
        try { _dash?.Dispose(); } catch { }
        _sensors.Dispose();
        Application.Exit();
    }

    /// <summary>The app's own icon (embedded via ApplicationIcon); 1-bit teal square if unavailable.</summary>
    private static Icon LoadAppIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? MakeFallbackIcon();
        }
        catch { return MakeFallbackIcon(); }
    }

    private static Icon MakeFallbackIcon()
    {
        // generated once: 16x16 teal square = practically zero memory
        using var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(0, 150, 150));
        return Icon.FromHandle(bmp.GetHicon());
    }
}
