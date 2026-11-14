using System;
using System.Diagnostics;
using System.Threading;

namespace Kryox;

/// <summary>
/// High-level LM-Series (LM240 / LM360 / LM420...) LCD display driver.
///
/// Wire protocol (reverse-engineered from DeepCreative's L122/index.node and
/// live-verified on the LM360 — see docs/PROTOCOL.md):
///   * 320 x 240 framebuffer, RGB565 little-endian, 153,600 bytes per frame
///   * Frame        : 13-byte header + framebuffer, USB bulk OUT EP 0x01
///   * Frame header : AA 08 00 00 01 00 58 02 00 2C 01 BC 11
///                    (= DeepCreative's start_file_download(1) packet:
///                     AA + len(u16 LE)=8 + payload 00 01 00 58 02 00 2C 01
///                     + CRC-16/XMODEM over the payload, little-endian)
///   * The firmware expects frames continuously (DeepCreative streams every
///     ~300 ms); long gaps blank the panel. Kryox paces a 100 ms send-to-send
///     period (~10 fps — the dashboard page is captured only ~4x/s, so nothing
///     visible changes; pacing is plain Sleep and the system timer resolution
///     is left untouched).
/// </summary>
public sealed class Lm360Display : IDisposable
{
    public const int Width = 320;
    public const int Height = 240;

    /// <summary>
    /// Target send-to-send period (ms). 100 ~= 10 fps. The dashboard page is
    /// captured only ~4x per second (values and tank motion change no faster),
    /// so the panel plays every distinct frame and nothing visible is lost
    /// versus a faster push. Pacing is pure Sleep: with the stock ~15.6 ms
    /// timer tick the effective rate sits at ~9-10 fps with tolerated jitter,
    /// CPU is near-zero, and the system timer resolution is never raised.
    /// </summary>
    public const int FramePeriodMs = 100;

    private static readonly byte[] FrameHeader =
        [0xAA, 0x08, 0x00, 0x00, 0x01, 0x00, 0x58, 0x02, 0x00, 0x2C, 0x01, 0xBC, 0x11];

    private static readonly byte[] CmdBrightnessUp =
        [0xAA, 0x04, 0x00, 0x06, 0x03, 0x61, 0x00, 0xD2, 0x46];

    private static readonly byte[] CmdBrightnessDown =
        [0xAA, 0x04, 0x00, 0x06, 0x03, 0x1D, 0x00, 0xE6, 0x0B];

    private static readonly byte[] CmdZenToggle =
        [0xAA, 0x04, 0x00, 0x03, 0x00, 0x00, 0x00, 0xDC, 0x9B];

    private static readonly int FrameBufferLen = Width * Height * 2; // RGB565: 153600 bytes

    private readonly WinUsbDevice _usb;

    /// <summary>Reused frame buffer (header + RGB565 payload) to keep GC quiet.</summary>
    private readonly byte[] _framePacket = new byte[FrameHeader.Length + FrameBufferLen];

    /// <summary>Anchored start of the previous send — phase-locked pacing reference.</summary>
    private long _sendPhase;

    public Lm360Display(WinUsbDevice? preopenedDevice = null)
    {
        _usb = preopenedDevice ?? new WinUsbDevice();
    }

    public bool IsConnected => _usb.IsOpen;

    public string? DevicePath => _usb.DevicePath;

    /// <summary>Human-readable reason of the last failed TryConnect (null when none).</summary>
    public string? LastError => _usb.LastError;

    /// <summary>Exposes the underlying USB device — diagnostics/tests only.</summary>
    public WinUsbDevice GetDeviceForDiagnostics() => _usb;

    public bool TryConnect()
    {
        if (_usb.IsOpen) return true;

        // The live-verified sequence needs no init packet: the first
        // header + frame write itself wakes and drives the panel.
        return _usb.Open();
    }

    public void Disconnect() => _usb.Dispose();

    public void BrightnessUp() => _usb.Write(WinUsbDevice.BulkOutPipe, CmdBrightnessUp, CmdBrightnessUp.Length);

    public void BrightnessDown() => _usb.Write(WinUsbDevice.BulkOutPipe, CmdBrightnessDown, CmdBrightnessDown.Length);

    /// <summary>Toggles the screen off (zen) or back on.</summary>
    public void ZenToggle() => _usb.Write(WinUsbDevice.BulkOutPipe, CmdZenToggle, CmdZenToggle.Length);

    /// <summary>
    /// CRC-16/XMODEM over the payload bytes (poly 0x1021, init 0x0000,
    /// MSB-first) — the general-command checksum, appended little-endian.
    /// See docs/PROTOCOL.md for the hand-verified reference pairs.
    /// </summary>
    public static ushort Crc16Xmodem(ReadOnlySpan<byte> payload)
    {
        ushort crc = 0;
        foreach (byte b in payload)
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
        }
        return crc;
    }

    /// <summary>
    /// Sends a general command packet: AA + len(u16 LE) + payload + CRC16-LE.
    /// </summary>
    public bool SendCommand(byte[] payload)
    {
        var pkt = new byte[5 + payload.Length];
        pkt[0] = 0xAA;
        pkt[1] = (byte)payload.Length;
        pkt[2] = 0x00;
        payload.CopyTo(pkt, 3);
        ushort crc = Crc16Xmodem(payload);
        pkt[^2] = (byte)crc;
        pkt[^1] = (byte)(crc >> 8);
        return _usb.Write(WinUsbDevice.BulkOutPipe, pkt, pkt.Length);
    }

    /// <summary>
    /// Zen/idle screen with a numeric value — live-verified on the panel:
    /// command 03's argument displays on the firmware "ZEN" screen as degrees
    /// (55.5 sent → the screen showed "55"; `03 00 00 00` is the 0° default).
    /// </summary>
    public bool SendZenIdle(double degrees)
    {
        int whole = Math.Max(0, (int)degrees);
        int frac = Math.Clamp((int)Math.Round((degrees - whole) * 100), 0, 99);
        return SendCommand([0x03, (byte)whole, (byte)(whole >> 8), (byte)frac]);
    }

    /// <summary>
    /// Sends an RGB24 (24 bpp, W x H, row-major, top-left origin) buffer as one
    /// frame. <paramref name="rgb24"/> must be exactly W*H*3 bytes.
    ///
    /// Wire shape (live-verified with the real L122 module and libusb tracing):
    /// the 13-byte header packet and the pixel data go out as TWO separate
    /// bulk transfers — the firmware's per-transfer framing rejects the
    /// fused single write.
    /// </summary>
    public bool SendRgbFrame(ReadOnlySpan<byte> rgb24)
    {
        if (!_usb.IsOpen) return false;
        if (rgb24.Length != Width * Height * 3) return false;

        var packet = _framePacket;
        int dst = FrameHeader.Length;
        for (int i = 0; i < rgb24.Length; i += 3)
        {
            ushort rgb565 = (ushort)(((rgb24[i] & 0xF8) << 8)        // RRRRR---
                                     | ((rgb24[i + 1] & 0xFC) << 3) // GGGGGG--
                                     | (rgb24[i + 2] >> 3));        // BBBBB
            packet[dst++] = (byte)(rgb565 & 0xFF);
            packet[dst++] = (byte)(rgb565 >> 8);
        }

        long start = Stopwatch.GetTimestamp();
        bool sent = _usb.Write(WinUsbDevice.BulkOutPipe, FrameHeader, 0, FrameHeader.Length)          // transfer 1: command header
                 && _usb.Write(WinUsbDevice.BulkOutPipe, packet, FrameHeader.Length, FrameBufferLen); // transfer 2: 153,600 B RGB565

        Pace(start);   // holds the send-to-send cadence — call sites must NOT sleep
        return sent;
    }

    /// <summary>
    /// Phase-locked pacing: waits until (previous send start + period) with a
    /// plain Sleep — no busy-spinning, so the system timer resolution is left
    /// untouched. Wake jitter of up to one timer tick (~15.6 ms) is tolerated:
    /// panel content only changes ~4x per second, so it is never visible. If
    /// USB ever throttles past the target period, the phase re-syncs to "now".
    /// </summary>
    private void Pace(long startTicks)
    {
        long periodTicks = (long)(FramePeriodMs * Stopwatch.Frequency / 1000.0);
        long earliest = _sendPhase == 0 ? startTicks : _sendPhase + periodTicks;

        if (earliest > startTicks)
        {
            double remainMs = (earliest - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainMs > 1) Thread.Sleep((int)(remainMs - 1));
        }

        _sendPhase = Math.Max(earliest, startTicks);
    }

    public void Dispose() => Disconnect();
}
