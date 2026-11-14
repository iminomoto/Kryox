using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Kryox;

/// <summary>
/// Static 1-bit boot splash — the only native-drawn frame. Every launch holds
/// the packaged 1-bit K mark centered on the card for 10 seconds, then hands
/// off to the WebView2 dashboard. If WebView2 ever fails to start, the splash
/// remains on the panel (the firmware blanks ~0.5 s after the stream stops),
/// so the panel never blanks.
///
/// Pure #000/#fff in the dashboard's design language: rounded 1px border card,
/// the user's pixel-K mark. Pre-rendered once at type load — zero allocations
/// per frame at runtime, and the preview renders the exact panel buffer.
/// </summary>
public static class SplashFrame
{
    public const int Width = Lm360Display.Width;   // 320
    public const int Height = Lm360Display.Height; // 240

    /// <summary>How long the K holds on every launch, before the dashboard takes over.</summary>
    public const int HoldDurationMs = 10_000;

    private static readonly byte[] _rgb24;

    /// <summary>Static ctor — explicit init order: assets render last.</summary>
    static SplashFrame() => _rgb24 = BuildRgb24();

    /// <summary>The RGB24 splash buffer. Treat as read-only; it is reused forever.</summary>
    public static byte[] Frame => _rgb24;

    /// <summary>Panel-exact preview (scale× nearest-neighbor) for human inspection.</summary>
    public static Bitmap PreviewBitmap(int scale = 3)
    {
        using var src = BufferToBitmap(_rgb24);
        var big = new Bitmap(Width * scale, Height * scale, PixelFormat.Format24bppRgb);
        using var g2 = Graphics.FromImage(big);
        g2.InterpolationMode = InterpolationMode.NearestNeighbor;
        g2.PixelOffsetMode = PixelOffsetMode.Half;
        g2.DrawImage(src, new Rectangle(0, 0, big.Width, big.Height));
        return big;
    }

    // ------------------------------------------------------------ drawing

    /// <summary>Draws the splash once: border card + the centered 1-bit K.</summary>
    private static Bitmap Draw()
    {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Black);

        // outer border card (the dashboard's 8px-margin grid look)
        using (var pen = new Pen(Color.White, 1f) { Alignment = PenAlignment.Inset })
        using (var path = Rounded(new RectangleF(8f, 8f, 304f, 224f), 16))
            g.DrawPath(pen, path);

        // the packaged pixel-K mark, alone, centered on the card
        using var mark = LoadMark();
        g.DrawImage(mark, (Width - mark.Width) / 2, (Height - mark.Height) / 2);
        return bmp;
    }

    private static Bitmap LoadMark()
    {
        var asm = typeof(SplashFrame).Assembly;
        var name = asm.GetManifestResourceNames()
                       .FirstOrDefault(n => n.EndsWith("kryox-mark.png", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("embedded kryox-mark.png not found");
        using var s = asm.GetManifestResourceStream(name)!;
        using var tmp = new Bitmap(s);
        return new Bitmap(tmp);   // clone detaches the bitmap from its stream
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ------------------------------------------------------------ pixel copy

    /// <summary>Thresholds the drawn splash to strict 1-bit (onebit-filter style).</summary>
    private static byte[] BuildRgb24()
    {
        using var bmp = Draw();
        var buf = new byte[Width * Height * 3];
        var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < Height; y++)
                {
                    byte* src = (byte*)data.Scan0 + y * data.Stride;
                    int dst = y * Width * 3;
                    for (int x = 0; x < Width; x++)
                    {
                        buf[dst + 0] = src[2] > 114 ? (byte)255 : (byte)0; // R
                        buf[dst + 1] = src[1] > 114 ? (byte)255 : (byte)0; // G
                        buf[dst + 2] = src[0] > 114 ? (byte)255 : (byte)0; // B
                        src += 3;
                        dst += 3;
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return buf;
    }

    /// <summary>Rebuilds a 1-bit panel buffer into a bitmap (for preview only).</summary>
    private static Bitmap BufferToBitmap(byte[] buf)
    {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        var data = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try { Marshal.Copy(buf, 0, data.Scan0, Math.Min(buf.Length, Height * data.Stride)); }
        finally { bmp.UnlockBits(data); }
        return bmp;
    }
}
