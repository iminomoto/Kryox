using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using WinFormsWebView2 = Microsoft.Web.WebView2.WinForms.WebView2;

namespace Kryox;

/// <summary>
/// Hosts the 1-bit HTML dashboard (embedded in the assembly, <see cref="LoadEmbeddedHtml"/>)
/// in an offscreen WebView2 at exactly 320×240 and turns browser frames into
/// RGB24 buffers for the LM360 stream loop.
///
/// Architecture: this class runs its own STA thread with a WinForms message
/// pump (WebView2 needs one). External callers only touch three members:
///   * Healthy      — the pipeline produces frames
///   * LatestFrame  — the newest captured 320×240 RGB24 buffer (null until then)
///   * Push(...)    — feeds real sensor values via window.updateMetrics()
///                    (the page's tween animations smooth them in)
///
/// The dashboard's own mock-data loop is neutralised by an injected gate
/// script (document-created, runs before page scripts): anyone calling
/// updateMetrics() without the __deep flag is silently dropped, so only the
/// app's real feed drives the rings/tank.
/// </summary>
public sealed class WebDashboard : IDisposable
{
    public const int Width = Lm360Display.Width;   // 320
    public const int Height = Lm360Display.Height; // 240

    /// <summary>Runs before any page script: wraps window.updateMetrics.</summary>
    private const string GateScript = """
        (() => {
          let real = null;
          Object.defineProperty(window, 'updateMetrics', {
            configurable: false,
            get: () => m => { if (m && m.__deep && real) real(m); },
            set: fn => { real = fn; }
          });
        })();
        """;

    private readonly Thread _thread;
    private volatile Form? _form;
    private volatile WinFormsWebView2? _wv;
    private volatile CoreWebView2? _core;
    private volatile byte[]? _frame;
    private volatile bool _healthy;
    private volatile string _status = "starting";
    private System.Windows.Forms.Timer? _captureTimer;   // on the STA thread
    private int _escalations;

    public bool Healthy => _healthy;
    public byte[]? LatestFrame => _frame;
    public string Status => _status;

    public WebDashboard()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Kryox dashboard" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Feeds a sensor sample; harmless at any time (no-ops until ready).</summary>
    public void Push(double cpu, double gpu, double ramPct, double ramUsedGB, double ramTotalGB)
    {
        var f = _form;
        var core = _core;
        if (f is null || core is null || !f.IsHandleCreated) return;

        void DoPush()
        {
            try
            {
                var json = string.Create(CultureInfo.InvariantCulture,
                    $"{{cpu:{cpu:0},gpu:{gpu:0},ram:{ramPct:0},ramUsedGB:{ramUsedGB:0.0},ramTotalGB:{ramTotalGB:0.0},__deep:true}}");
                _ = core.ExecuteScriptAsync($"window.updateMetrics({json});");
            }
            catch { /* dashboard dying — next Push retries */ }
        }

        try { f.BeginInvoke(new Action(DoPush)); } catch { }
    }

    public void Dispose()
    {
        try { _captureTimer?.Stop(); } catch { }
        try { _wv?.Dispose(); } catch { }
        try
        {
            var f = _form;
            if (f?.IsHandleCreated ?? false) f.BeginInvoke(new Action(f.Close));
        }
        catch { }
        _healthy = false;
        _thread.Join(1500);
    }

    // ------------------------------------------------------------ STA thread

    private void Run()
    {
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ClientSize = new Size(Width, Height),
            Text = "Kryox dashboard",
            Opacity = 1d,
        };
        var wv = new WinFormsWebView2 { AllowExternalDrop = false, Dock = DockStyle.Fill };
        form.Controls.Add(wv);
        _form = form;
        _wv = wv;

        var _ = form.Handle;   // force the FORM's native handle so BeginInvoke works
        form.BeginInvoke(new Action(async () =>
        {
            try
            {
                var udf = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Kryox", "WebView2");
                var env = await CoreWebView2Environment.CreateAsync(null, udf);
                await wv.EnsureCoreWebView2Async(env);
                await ConfigureCoreAsync(wv);
                _status = "page loading";
                _captureTimer = new System.Windows.Forms.Timer { Interval = 250 };
                _captureTimer.Tick += async (_, _) => await CaptureTickAsync();
                _captureTimer.Start();
            }
            catch (Exception ex)
            {
                _status = $"init failed: {ex.Message}";
            }
        }));

        Application.Run(form);
    }

    private async Task ConfigureCoreAsync(WinFormsWebView2 wv)
    {
        var core = wv.CoreWebView2;
        _core = core;

        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        await core.AddScriptToExecuteOnDocumentCreatedAsync(GateScript);
        await core.CallDevToolsProtocolMethodAsync("Page.enable", "{}");
        // Exact panel viewport at scale 1, no matter the host window / DPI.
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
            "{\"width\":320,\"height\":240,\"deviceScaleFactor\":1,\"mobile\":false}");

        core.NavigateToString(LoadEmbeddedHtml());
    }

    private static string LoadEmbeddedHtml()
    {
        var asm = typeof(WebDashboard).Assembly;
        var name = asm.GetManifestResourceNames()
                       .FirstOrDefault(n => n.EndsWith("dashboard-1bit.html", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("embedded dashboard-1bit.html not found");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ------------------------------------------------------------ capture

    private async Task CaptureTickAsync()
    {
        var png = await CaptureOnceAsync();
        if (png is not null)
        {
            if (!_healthy)
            {
                _healthy = true;
                _status = _escalations == 0 ? "running (hidden window)"
                        : _form!.Visible && _form.Location.X < -1000 ? "running (offscreen window)"
                        : "running (mirror window)";
            }
            _frame = png;
            return;
        }

        _escalations++;
        try
        {
            if (_escalations == 4)
            {
                _status = "escalating: offscreen visible";
                var f = _form!;
                f.Show();
                f.Location = new Point(-32000, -32000);
            }
            else if (_escalations == 8)
            {
                _status = "escalating: mirror window";
                var f = _form!;
                var wa = Screen.PrimaryScreen!.WorkingArea;
                f.Location = new Point(wa.Right - Width - 8, wa.Bottom - Height - 8);
                f.Show();
            }
        }
        catch (Exception ex)
        {
            _status = $"window staging failed: {ex.Message}";
        }
    }

    private async Task<byte[]?> CaptureOnceAsync()
    {
        var core = _core;
        if (core is null) return null;
        try
        {
            var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",
                "{\"format\":\"png\",\"fromSurface\":true}");
            using var doc = JsonDocument.Parse(json);
            var b64 = doc.RootElement.GetProperty("data").GetString();
            if (string.IsNullOrEmpty(b64) || b64.Length < 400) return null;

            using var ms = new MemoryStream(Convert.FromBase64String(b64));
            using var bmp = new Bitmap(ms);
            if (bmp.Width != Width || bmp.Height != Height)
            {
                // defensive: surface came at a different scale — normalize
                using var scaled = new Bitmap(bmp, Width, Height);
                return ToRgb24(scaled);
            }
            return ToRgb24(bmp);
        }
        catch { return null; }
    }

    private static byte[] ToRgb24(Bitmap bmp)
    {
        var buf = new byte[Width * Height * 3];
        var d = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < Height; y++)
                {
                    byte* p = (byte*)d.Scan0 + y * d.Stride;   // BGRA
                    int dst = y * Width * 3;
                    for (int x = 0; x < Width; x++)
                    {
                        // the dashboard's CSS filter is already strict 1-bit; this
                        // threshold only guards against slight AA leakage
                        buf[dst + 0] = p[2] > 127 ? (byte)255 : (byte)0; // R
                        buf[dst + 1] = p[1] > 127 ? (byte)255 : (byte)0; // G
                        buf[dst + 2] = p[0] > 127 ? (byte)255 : (byte)0; // B
                        p += 4;
                        dst += 3;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(d); }
        return buf;
    }
}
