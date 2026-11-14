using System;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace Kryox;

/// <summary>
/// Lightweight background system sensor: CPU temperature, GPU temperature and
/// RAM load. CPU/GPU temperatures come from LibreHardwareMonitor (needs admin
/// rights for the WinRing0 kernel driver), RAM load is a simple Win32 call.
/// </summary>
public sealed class Sensors : IDisposable
{
    private readonly Computer _computer;

    public double CpuTempC { get; private set; }
    public double GpuTempC { get; private set; }
    public double RamLoadPct { get; private set; }

    /// <summary>Memory usage breakdown (GiB), for dashboards that show it.</summary>
    public double RamUsedGB { get; private set; }
    public double RamTotalGB { get; private set; }

    public bool CpuSensorFound { get; private set; }
    public bool GpuSensorFound { get; private set; }

    public Sensors()
    {
        // CPU + GPU sensors only; system RAM load comes from GlobalMemoryStatusEx
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,          // NVIDIA/AMD/Intel; NVIDIA temps via NVAPI, admin-free
            IsMemoryEnabled = false,      // avoid duplicate RAM telemetry
            IsMotherboardEnabled = false,
            IsControllerEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,     // not needed; avoids extra drivers
        };
        _computer.Open();
    }

    /// <summary>Refreshes all three metrics. Call about once per second.</summary>
    public void Sample()
    {
        double cpu = double.NaN, gpu = double.NaN;

        foreach (var hw in _computer.Hardware)
        {
            try { hw.Update(); } catch { continue; }

            switch (hw.HardwareType)
            {
                case HardwareType.Cpu:
                    cpu = PickTemp(hw, cpu);
                    break;
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    gpu = PickTemp(hw, gpu);
                    break;
            }
        }

        CpuTempC = Round(cpu);
        GpuTempC = Round(gpu);

        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms))
        {
            RamLoadPct = ms.dwMemoryLoad;
            RamTotalGB = Math.Round(ms.ullTotalPhys / 1073741824.0, 1);
            RamUsedGB = Math.Round((ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0, 1);
        }
        else
        {
            RamLoadPct = 0;
            RamTotalGB = 0;
            RamUsedGB = 0;
        }

        CpuSensorFound = CpuTempC > 0;
        GpuSensorFound = GpuTempC > 0;

        double Round(double v) => double.IsNaN(v) ? 0 : Math.Round(v, 1);
    }

    /// <summary>
    /// Chooses the best temperature from a hardware item. Prefers:
    /// CPU  : "Package" / "Tctl/Tdie"  →  hottest "Core"  →  any other temp
    /// GPU  : "GPU Core"  →  hottest remaining sensor
    /// </summary>
    private static double PickTemp(IHardware hw, double currentValue)
    {
        static bool IsFinite(double v) => !double.IsNaN(v) && v > 0.0;

        double package = double.NaN;   // CPU "Package" or AMD "Tctl/Tdie"
        double gpuCore = double.NaN;   // NVIDIA/other "GPU Core"
        double coreMax = double.NaN;   // hottest "Core #N"
        double otherMax = double.NaN;  // any remaining temp sensor

        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Temperature) continue;
            double v = s.Value ?? double.NaN;
            if (double.IsNaN(v) || v < 0) continue;

            string name = s.Name ?? string.Empty;
            if (name.StartsWith("Package", StringComparison.OrdinalIgnoreCase))
                package = v;
            else if (name.Equals("Tctl/Tdie", StringComparison.OrdinalIgnoreCase))
                package = v;
            else if (name.StartsWith("GPU Core", StringComparison.OrdinalIgnoreCase))
                gpuCore = v;
            else if (name.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsFinite(coreMax) || v > coreMax) coreMax = v;
            }
            else
            {
                if (!IsFinite(otherMax) || v > otherMax) otherMax = v;
            }
        }

        // Preference chain — first finite value wins:
        double pick = package;
        if (!IsFinite(pick)) pick = gpuCore;
        if (!IsFinite(pick)) pick = coreMax;
        if (!IsFinite(pick)) pick = otherMax;

        return IsFinite(pick) ? pick : currentValue;
    }
    #region Win32: GlobalMemoryStatusEx

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    #endregion

    public void Dispose()
    {
        try { _computer.Close(); } catch { }
    }
}
