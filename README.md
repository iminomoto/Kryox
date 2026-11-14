# Kryox — dead-simple LM360 display monitor

A small native tray app that replaces DeepCreative (420 MB installer, heavy GUI)
with just what a DeepCool **LM360** cooler display actually needs:

* CPU temperature  (LibreHardwareMonitor, ring-0 kernel driver included)
* GPU temperature  (NVIDIA/AMD/Intel — NVIDIA via NVAPI, no admin needed)
* RAM load         (Win32 `GlobalMemoryStatusEx`)
* 320×240 monitor page streamed live to the cooler screen at **~10 fps** over
  **WinUSB** bulk writes (no libusb — the in-box driver is enough)

No web renderer, no wallpaper engine, no overlay 3D engine — just the sensor
loop and the USB stream. The goal: **low CPU, small memory footprint**.

The look is a single **1-bit dashboard** (pure black/white, dithered rings,
comet marker, animated wave tank for RAM) — an HTML design authored in this
project, rendered pixel-perfect in an offscreen WebView2 at exactly 320×240 and
pushed to the panel. A static 1-bit splash (bordered card, white K centered)
covers the first ten seconds until the dashboard is healthy — and would stay
on the panel if WebView2 were ever unavailable — so it never goes blank.

## Install

**One file is all you need** — a single 28 MB `Kryox.exe` (it is on the Desktop).

Double-click it anywhere:

1. one UAC prompt (the sensor kernel driver needs admin),
2. it copies itself to `%LOCALAPPDATA%\Kryox\app\Kryox.exe`,
3. registers the `Kryox` scheduled task (ONLOGON, highest privileges), so it
   starts silently in the tray on every boot,
4. launches the installed copy and exits — from then on the app runs from
   `%LOCALAPPDATA%\Kryox\app`, never touching your desktop or the repo.

Running the desktop file again later is a no-op while the app is already
running, and an instant self-update when it is not.

**Quit DeepCreative first** if you use it: only one app can hold the display's
WinUSB interface, and DeepCreative streams your panels every ~300 ms on its own.

Tray icon options: zen mode (the firmware zen screen shows the live CPU temp;
while it is on the dashboard's browser is unloaded — near-zero CPU/RAM),
brightness ±, "Start with Windows" (silent scheduled task), and Exit. While
running, the panel is continuously refreshed; leaving one final
frame is not possible — the panel blanks ~½ s after the stream stops (firmware
watchdog).

To uninstall: tray → Exit, then `schtasks /Delete /TN Kryox /F` and delete the
`%LOCALAPPDATA%\Kryox` folder.

### Building from source

```powershell
dotnet build src\Kryox\Kryox.csproj -c Release
.\src\Kryox\bin\Release\net8.0-windows10.0.19041.0\Kryox.exe    # dev run — set KRYOX_NODEPLOY=1 to skip self-install

dotnet publish src\Kryox\Kryox.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false
```

## How it talks to the display

The wire protocol was fully reverse-engineered from DeepCreative's native
`L122/index.node` module (Ghidra decompile via REA) and **live-verified on the
physical panel**:

* 13-byte command header (`AA 08 00 00 01 00 58 02 00 2C 01 BC 11`) as one bulk
  transfer, then the 320×240 frame as **RGB565 little-endian** (153,600 B) as a
  second bulk transfer, endpoint `0x01`, paced to a 100 ms period (~10 fps).
* General commands (`AA + len + payload + CRC16/XMODEM-LE`) for brightness/zen.

All details, CRC algorithm, decompiled function references and live-test
results: [`docs/PROTOCOL.md`](docs/PROTOCOL.md).

## Building blocks

| File | Purpose |
|---|---|
| `WinUsbDevice.cs` | Zero-dependency WinUSB client (VID `0x3633` / PID `0x0026`) |
| `Lm360Display.cs` | Wire protocol: header + RGB565 frames, brightness/zen |
| `Sensors.cs` | CPU/GPU temp via LibreHardwareMonitor + RAM load/used/total via Win32 |
| `SplashFrame.cs` | 1-bit splash: white K centered on the card, 10 s hold, last resort if WebView2 fails |
| `Web\dashboard-1bit.html` | The 1-bit dashboard design (embedded, captured via WebDashboard) |
| `WebDashboard.cs` | Offscreen WebView2 host: captures the HTML dashboard at 320×240 |
| `TrayContext.cs` | Tray host: ~10 fps paced stream thread, 1 Hz sampling, zen/brightness panel controls |
| `Autostart.cs` | schtasks ONLOGON boot task, highest privileges (elevation-friendly) |
| `Program.cs` | Entry point + self-installer: copies to `%LOCALAPPDATA%\Kryox\app`, registers the boot task, starts the installed copy |

## Test harness

```powershell
dotnet build src\KryoxTest\KryoxTest.csproj -c Release
.\src\KryoxTest\bin\Release\net8.0-windows10.0.19041.0\KryoxTest.exe
```

Streams the same monitor page from the console for 40 s (WYSIWYG proof of the
middleware stack without the tray UI). Log and preview images land in
`%TEMP%\Kryox` — nothing is written into the repo.

## Credits

* Display protocol reverse-engineering references the open-source
  [daedlock/deepcool-lm](https://github.com/daedlock/deepcool-lm) driver (MIT)
  and was completed via REA/Ghidra decompilation of DeepCreative's `index.node`.
* The 1-bit dashboard design (`src\Kryox\Web\dashboard-1bit.html`) was
  hand-authored in this project.
* Sensor readings via [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (MPL-2.0).

## License

[MIT](LICENSE).
