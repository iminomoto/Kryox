using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kryox;

/// <summary>
/// Minimal, dependency-free WinUSB client for the DeepCool LM-Series bulk-out
/// display device (VID 0x3633 / PID 0x0026). Windows binds this device to the
/// in-box WinUSB driver automatically (USB\MS_COMP_WINUSB), so no extra driver,
/// INF, Zadig or libusb is required.
/// </summary>
public sealed class WinUsbDevice : IDisposable
{
    public const ushort VendorId = 0x3633;
    public const ushort ProductId = 0x0026;

    /// <summary>Vendor device interface GUID claimed by the LM-Series MS-OS-descriptor.</summary>
    public static readonly Guid InterfaceGuid = new("{13eb360b-bc1e-46cb-ac8b-ef3da47b4062}");

    private readonly Guid _guid;

    public WinUsbDevice(Guid? interfaceGuid = null)
    {
        _guid = interfaceGuid ?? InterfaceGuid;
    }

    /// <summary>Bulk OUT endpoint address of the display data pipe.</summary>
    public const byte BulkOutPipe = 0x01;

    /// <summary>WinUSB writes are chunked at 64 KB — well under any host controller limit,
    /// and bulk streams are reassembled transparently by the bus, so the receiving
    /// firmware always sees one continuous data stream.</summary>
    private const int MaxChunk = 0x10000;

    private IntPtr _winUsbHandle;
    private SafeFileHandle? _deviceHandle;
    private string? _devicePath;

    public bool IsOpen { get; private set; }
    public bool IsOverlapped { get; private set; }
    public string? DevicePath => _devicePath;
    public string? LastError { get; private set; }

    #region P/Invoke

    private const int DIGCF_PRESENT = 0x00000002;
    private const int DIGCF_DEVICEINTERFACE = 0x00000010;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint OPEN_EXISTING = 3;
    private const int INFINITE = unchecked((int)0xFFFFFFFF);

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA interfaceData, IntPtr detailBuffer, uint detailBufferSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Initialize(SafeHandle deviceHandle, out IntPtr interfaceHandle);

    // WINUSB_POWER_POLICY: 1 = DEVICE_IDLE_ENABLE ("DeviceIdleEnabled"), ULONG 0/1.
    private const uint DeviceIdleEnable = 0x00000001;

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_SetPowerPolicy(IntPtr interfaceHandle, uint policyType, uint dataLength, ref uint value);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_Free(IntPtr interfaceHandle);

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern unsafe bool WinUsb_WritePipe(IntPtr interfaceHandle, byte pipeId, byte* buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint OffsetLow;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(SafeHandle hFile, int milliseconds);

    // This layout matches winusb.h; we only need PipeType and PipeId which sit
    // at fixed early offsets. Oversized (Size=48) so the native writer can't
    // overflow regardless of ABI differences.
    [StructLayout(LayoutKind.Sequential, Size = 48)]
    private struct USB_PIPE_INFORMATION
    {
        public uint PipeType;   // UsbdPipeType: Control=0, Isochronous=1, Bulk=2, Interrupt=3
        public byte PipeId;     // endpoint address
    }

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryInterfaceSettings(IntPtr interfaceHandle, byte alternateInterfaceIndex, out USB_INTERFACE_DESCRIPTOR descriptor);

    [StructLayout(LayoutKind.Sequential)]
    private struct USB_INTERFACE_DESCRIPTOR
    {
        public byte bLength;
        public byte bDescriptorType;
        public byte bInterfaceNumber;
        public byte bAlternateSetting;
        public byte bNumEndpoints;
        public byte bInterfaceClass;
        public byte bInterfaceSubClass;
        public byte bInterfaceProtocol;
        public byte iInterface;
    }

    [DllImport("winusb.dll", SetLastError = true)]
    private static extern bool WinUsb_QueryPipe(IntPtr interfaceHandle, byte alternateInterfaceIndex, byte pipeIndex, out USB_PIPE_INFORMATION pipeInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeHandle hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    #endregion

    /// <summary>
    /// Enumerates present WinUSB devices on the LM-Series interface GUID and
    /// returns the device path (e.g. "\\?\USB#VID_3633&PID_0026#...#{...}").
    /// </summary>
    public static string? FindDevicePath(Guid interfaceGuid)
    {
        var ifaceData = new SP_DEVICE_INTERFACE_DATA();
        ifaceData.cbSize = Marshal.SizeOf(ifaceData);
        var guid = interfaceGuid; // cannot pass a static readonly as ref/out

        var devInfoSet = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (devInfoSet == IntPtr.Zero || devInfoSet == new IntPtr(-1))
            return null;

        try
        {
            // Note: SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize is 8 on x64, 6 on x86
            // (the canonical SetupAPI "struct size" gotcha). The string itself,
            // however, always begins right after the 4-byte cbSize field.
            int cbSize = IntPtr.Size == 8 ? 8 : 6;
            const int StringOffset = 4;

            for (int idx = 0; ; idx++)
            {
                if (!SetupDiEnumDeviceInterfaces(devInfoSet, IntPtr.Zero, ref guid, idx, ref ifaceData))
                    break; // no more interfaces

                // Query the required / advised detail buffer size (fails on purpose).
                SetupDiGetDeviceInterfaceDetail(devInfoSet, ref ifaceData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) required = 512;

                var buffer = Marshal.AllocHGlobal((int)required + cbSize);
                try
                {
                    Marshal.WriteInt32(buffer, 0, cbSize);             // cbSize
                    if (IntPtr.Size == 8) Marshal.WriteInt32(buffer, 4, 0);

                    if (!SetupDiGetDeviceInterfaceDetail(devInfoSet, ref ifaceData, buffer, required + (uint)cbSize, out _, IntPtr.Zero))
                        continue;

                    string? path = Marshal.PtrToStringUni(buffer + StringOffset);
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(devInfoSet);
        }

        return null;
    }

    public bool Open()
    {
        try
        {
            _devicePath = FindDevicePath(_guid);
            if (_devicePath is null)
            {
                LastError = "enumerate: no LM-Series WinUSB interface present";
                return false;
            }

            // WinUsb_Initialize is picky about how the handle was opened:
            // try the documented variants in order until one is accepted.
            (uint share, uint flags)[] attempts =
            [
                (FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_FLAG_OVERLAPPED), // the variant known to satisfy WinUsb_Initialize
                (FILE_SHARE_READ | FILE_SHARE_WRITE, 0),
                (0, FILE_FLAG_OVERLAPPED),
                (0, 0),
            ];

            foreach (var (share, flags) in attempts)
            {
                _deviceHandle = CreateFileW(_devicePath, GENERIC_READ | GENERIC_WRITE, share, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
                if (_deviceHandle.IsInvalid)
                {
                    LastError = $"CreateFile(share={share:X}, flags={flags:X}): {new Win32Exception(Marshal.GetLastWin32Error()).Message}";
                    continue;
                }

                if (!WinUsb_Initialize(_deviceHandle, out _winUsbHandle))
                {
                    var err = new Win32Exception(Marshal.GetLastWin32Error());
                    LastError = $"WinUsb_Initialize(share={share:X}, flags={flags:X}): {err.Message} (0x{err.NativeErrorCode:X8})";
                    _deviceHandle.Dispose();
                    _deviceHandle = null;
                    continue;
                }

                IsOverlapped = (flags & FILE_FLAG_OVERLAPPED) != 0;

                // WinUSB arms device-idle detection by default, so Windows
                // selectively suspends the panel during zen mode's silent gaps
                // (temp constant → no writes → suspend). The MCU reinitializes
                // on resume — the DeepCool boot animation plays — so idle must
                // stay disabled for the lifetime of this handle. Best-effort:
                // if the call fails we only fall back to the old behavior,
                // never lose the open.
                uint idleDisabled = 0;
                WinUsb_SetPowerPolicy(_winUsbHandle, DeviceIdleEnable, sizeof(uint), ref idleDisabled);

                LastError = null;
                IsOpen = true;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Close();
            return false;
        }
    }

    /// <summary>Writes count bytes starting at buffer[offset] into the bulk OUT pipe.</summary>
    public bool Write(byte pipeId, byte[] buffer, int offset, int count)
    {
        if (!IsOpen) return false;

        int end = offset + count;
        int pos = offset;
        while (pos < end)
        {
            int n = Math.Min(MaxChunk, end - pos);
            if (!WriteChunk(pipeId, buffer, pos, n)) return false;
            pos += n;
        }
        return true;
    }

    /// <summary>Dump the interface + endpoint table to a log string (diagnostics).</summary>
    public string DescribePipes()
    {
        if (!IsOpen) return "<not open>";

        var sb = new System.Text.StringBuilder();
        if (WinUsb_QueryInterfaceSettings(_winUsbHandle, 0, out var desc))
        {
            sb.AppendLine($"interface {desc.bInterfaceNumber} alt={desc.bAlternateSetting} class=0x{desc.bInterfaceClass:X2} endpoints={desc.bNumEndpoints}");
            for (byte i = 0; i < desc.bNumEndpoints; i++)
            {
                if (!WinUsb_QueryPipe(_winUsbHandle, 0, i, out var pipe))
                {
                    sb.AppendLine($"  pipe[{i}]: <query failed 0x{Marshal.GetLastWin32Error():X}>");
                    continue;
                }
                string type = pipe.PipeType switch { 0 => "control", 1 => "iso", 2 => "bulk", 3 => "interrupt", _ => pipe.PipeType.ToString() };
                string dir = (pipe.PipeId & 0x80) != 0 ? "IN" : "OUT";
                sb.AppendLine($"  pipe[{i}]: ep=0x{pipe.PipeId:X2} dir={dir} type={type}");
            }
        }
        else
        {
            sb.Append($"<QueryInterfaceSettings failed 0x{Marshal.GetLastWin32Error():X}>");
        }
        return sb.ToString();
    }

    /// <summary>Writes an arbitrary buffer to the bulk OUT pipe.</summary>
    public bool Write(byte pipeId, byte[] buffer, int length)
        => Write(pipeId, buffer, 0, length);

    private unsafe bool WriteChunk(byte pipeId, byte[] buffer, int offset, int count)
    {
        fixed (byte* p = buffer)
        {
            if (!IsOverlapped)
                return WinUsb_WritePipe(_winUsbHandle, pipeId, p + offset, (uint)count, out _, IntPtr.Zero);

            // Handle opened with FILE_FLAG_OVERLAPPED: every write must carry an
            // OVERLAPPED structure. We serialize all writes (single 1 Hz sender),
            // so one reusable stack struct is safe.
            var ov = new OVERLAPPED();
            bool ok = WinUsb_WritePipe(_winUsbHandle, pipeId, p + offset, (uint)count, out _, (IntPtr)(void*)&ov);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                if (err != 997 /* ERROR_IO_PENDING */)
                    return false;

                if (WaitForSingleObject(_deviceHandle!, INFINITE) != 0 /* WAIT_OBJECT_0 */)
                    return false;

                if (!GetOverlappedResult(_deviceHandle!, ref ov, out uint transferred, true))
                    return false;
            }
            return true;
        }
    }

    private void Close()
    {
        if (_winUsbHandle != IntPtr.Zero)
        {
            WinUsb_Free(_winUsbHandle);
            _winUsbHandle = IntPtr.Zero;
        }
        _deviceHandle?.Dispose();
        _deviceHandle = null;
        IsOpen = false;
    }

    public void Dispose() => Close();
}
