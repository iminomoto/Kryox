# LM-Serie LCD Protocol (LM240 / LM360 / LM420)

Fully reverse-engineered from DeepCreative's native module (`resources/app.asar.unpacked/resources/L122/index.node`,
decompiled with Ghidra via REA) and **live-verified against the physical LM360**.
The C# implementation in this repo reproduces it exactly; every table entry below
was confirmed on the wire and/or on the panel.

## Device

| Property | Value |
|---|---|
| USB VID / PID | `0x3633` / `0x0026` (`USB\VID_3633&PID_0026`) |
| Serial (this unit) | `13EA7A540907` |
| Bus binding | In-box **WinUSB** driver (auto-installed, `USB\MS_COMP_WINUSB`) |
| Vendor interface GUID | `{13EB360B-BC1E-46CB-AC8B-EF3DA47B4062}` (MS OS descriptor) |
| Interface | 0, endpoints `0x01` OUT bulk / `0x81` IN bulk |
| Display | **320 × 240** (confirmed by renderer bundle constants + 0x25800 = 320·240·2) |
| Framebuffer | RGB565 little-endian, 153,600 bytes = one frame |
| Panel behavior | Blanks when frames stop; shows its boot logo while in "image download" mode awaiting pixel data |

`index.node` is a Rust (`napi-rs`) USB driver. JS-facing exports (camelCase):
`refreshDevice, sendImageData, sendGeneralCommand, closeDevice, getResponse,
changeMode, startFileDownload, updateArguments, startUpAnimation, changeSettings, reboot`.

## Packet envelope (general commands)

Built by `FUN_18000b1b0`:

```
AA | len:u16 LE | payload... | crc16:u16 LE
```

**CRC16 = CRC-16/XMODEM** (`FUN_180010250`): poly `0x1021`, init `0x0000`,
MSB-first, computed over the payload bytes only, appended little-endian.
Hand-verified against two independent captures:

| payload | CRC | full packet |
|---|---|---|
| `06 03 61 00` | `0x46D2` | `AA 04 00 06 03 61 00 D2 46` (brightness up) |
| `00 01 00 58 02 00 2C 01` | `0x11BC` | `AA 08 00 00 01 00 58 02 00 2C 01 BC 11` (frame header) |

## Known payloads

| Purpose | Payload | Notes |
|---|---|---|
| **Frame header** (`startFileDownload(1)`) | `00 01 00 58 02 00 2C 01` | byte1 = command `0x01`; `00 58 02 00` = LE `0x00025800` = 153,600 frame bytes; `2C 01` = LE `300` (firmware refresh period, ms) |
| Brightness up | `06 03 61 00` | |
| Brightness down | `06 03 1D 00` | |
| Zen idle screen | `03 <int lo> <int hi> <frac u8>` | panel leaves frame mode and shows its "ZEN" screen displaying the value as °; `03 00 00 00` = the 0° default. **Live-verified:** sending 55.5 → screen showed "55". **Also live-observed:** a zen screen left silent for minutes reboots the panel (boot animation plays, zen re-enters with the last value) — keep it fed by re-sending command 03 with the current value every ~30 s; same-value rewrites are in-place and invisible |
| changeMode | `07 <mode>` | `FUN_18000bcf0`; mode `0` = "Digital mode" per UI bundle; write + bulk-read 512 B response (10 s timeout), replies "change_mode_success"/"change_mode_fail" |
| updateArguments (numeric) | `03 <int lo> <int hi> <frac u8>` | `FUN_18000c5b0`; double with 2 decimals, write-only |

**There is no init packet.** The earlier `AA 01 00 09 09 91` assumption is wrong —
the frame header itself wakes and drives the panel.

## Frame flow (verified end-to-end, 29 JS frames + final C# stream)

1. Prepare a 320×240 image as **RGB888**, row-major, top-left origin:
   230,400 bytes (`R,G,B` per pixel).
2. Write the 13-byte frame header (payload table above) to **EP 0x01** — one bulk transfer.
3. Convert RGB888 → **RGB565 LE**: `u16 = ((R & 0xF8) << 8) | ((G & 0xFC) << 3) | (B >> 3)`
   (decompiled `FUN_180010050`: byte0 = R, byte1 = G, byte2 = B) → 153,600 bytes.
4. Write the framebuffer as a **separate** bulk transfer to EP 0x01 (1000 ms timeout;
   no header prefix, no trailing CRC, no status read).
5. Repeat continuously. **Cadence must be ≲300 ms** (DeepCreative streams every
   ~300 ms; the firmware watchdog blanks the panel when frames stop).
   This app paces send-to-send at 100 ms (~10 fps effective with the stock 15.6 ms
   timer tick). The dashboard page is captured only ~4x per second, so nothing
   visible is lost, the system timer resolution is left untouched (pure Sleep
   pacing, no busy-spin). Sensor sampling stays at 1 Hz inside the loop.

The "two separate transfers" shape matters: L122.node and daedlock both issue
the header and the pixel data as two distinct host writes, and a fused single
write of header+pixels is ignored by the firmware. Host-side chunking (our WinUSB
client splits at 64 KB) is invisible on the wire because 153,600 is an exact
multiple of the 512-byte bulk packet size — no short packets mid-frame.

## Command/response wire notes

* `startFileDownload`, `changeMode`, `updateArguments` each write their AA- packet
  then read 512 bytes from EP 0x81 (timeouts 2 s / 10 s / none respectively).
* `getResponse` = plain bulk read of 512 bytes (5 s timeout).
* In the idle/monitor state the panel answers `00…00` (zeros) — no error payload.

## Differences from the old notes

* ❌ `AA 01 00 09 29 91` "init/query" — not a real step; dropped.
* ❌ "1 fps is enough" — falsified; the panel blanks without the ~300 ms stream.
* ✅ CRC algorithm identified: CRC-16/XMODEM over payload, LE trailer.
* ✅ Panel resolution 320×240 confirmed; pixel order RGB (not BGR) into RGB565.
* ✅ Frame header decoded as `start_file_download(1)` with size `0x25800` + period `300`.
* ✅ Wire shape: header packet and framebuffer are two separate bulk transfers.
