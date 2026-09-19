using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace LumiPad.App;

public sealed record PcMonitorSnapshot(
    double CpuLoad,
    double? CpuTemperature,
    double? CpuClockMHz,
    double GpuLoad,
    double? GpuTemperature,
    double? GpuClockMHz,
    double MemoryLoad,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double NetworkDownloadMbps,
    double NetworkUploadMbps,
    int? Fps,
    DateTimeOffset Timestamp);

public sealed class PcMonitorService : IDisposable
{
    private readonly object _gate = new();
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMemoryEnabled = true
    };

    private bool _opened;
    private long _lastRxBytes;
    private long _lastTxBytes;
    private DateTimeOffset _lastNetworkSample;

    public PcMonitorSnapshot ReadSnapshot()
    {
        lock (_gate)
        {
            EnsureOpen();

            foreach (IHardware hardware in _computer.Hardware)
                UpdateHardware(hardware);

            var cpuHardware = _computer.Hardware
                .FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

            var gpuHardware = _computer.Hardware
                .FirstOrDefault(h =>
                    h.HardwareType == HardwareType.GpuNvidia ||
                    h.HardwareType == HardwareType.GpuAmd ||
                    h.HardwareType == HardwareType.GpuIntel);

            double cpuLoad = ReadPreferred(
                cpuHardware,
                SensorType.Load,
                "CPU Total",
                fallbackMax: true) ?? 0;

            double? cpuTemp = ReadPreferred(
                cpuHardware,
                SensorType.Temperature,
                "CPU Package",
                fallbackMax: true);

            double? cpuClock = AverageCoreClock(cpuHardware);

            double gpuLoad = ReadPreferred(
                gpuHardware,
                SensorType.Load,
                "GPU Core",
                fallbackMax: true) ?? 0;

            double? gpuTemp = ReadPreferred(
                gpuHardware,
                SensorType.Temperature,
                "GPU Core",
                fallbackMax: true);

            double? gpuClock = ReadPreferred(
                gpuHardware,
                SensorType.Clock,
                "GPU Core",
                fallbackMax: true);

            GetMemory(out double usedGb, out double totalGb, out double memoryLoad);
            GetNetworkRates(out double downloadMbps, out double uploadMbps);

            return new PcMonitorSnapshot(
                Math.Clamp(cpuLoad, 0, 100),
                cpuTemp,
                cpuClock,
                Math.Clamp(gpuLoad, 0, 100),
                gpuTemp,
                gpuClock,
                Math.Clamp(memoryLoad, 0, 100),
                usedGb,
                totalGb,
                downloadMbps,
                uploadMbps,
                null,
                DateTimeOffset.UtcNow);
        }
    }

    private void EnsureOpen()
    {
        if (_opened)
            return;

        _computer.Open();
        _opened = true;
    }

    private static void UpdateHardware(IHardware hardware)
    {
        hardware.Update();

        foreach (IHardware child in hardware.SubHardware)
            UpdateHardware(child);
    }

    private static IEnumerable<ISensor> SensorsRecursive(IHardware? hardware)
    {
        if (hardware is null)
            yield break;

        foreach (ISensor sensor in hardware.Sensors)
            yield return sensor;

        foreach (IHardware child in hardware.SubHardware)
        {
            foreach (ISensor sensor in SensorsRecursive(child))
                yield return sensor;
        }
    }

    private static double? ReadPreferred(
        IHardware? hardware,
        SensorType type,
        string preferredName,
        bool fallbackMax)
    {
        var values = SensorsRecursive(hardware)
            .Where(s => s.SensorType == type && s.Value.HasValue)
            .ToArray();

        ISensor? preferred = values.FirstOrDefault(s =>
            s.Name.Contains(preferredName, StringComparison.OrdinalIgnoreCase));

        if (preferred?.Value is float preferredValue)
            return preferredValue;

        if (values.Length == 0)
            return null;

        return fallbackMax
            ? values.Max(s => (double)s.Value!.Value)
            : values.Average(s => (double)s.Value!.Value);
    }

    private static double? AverageCoreClock(IHardware? cpu)
    {
        var clocks = SensorsRecursive(cpu)
            .Where(s =>
                s.SensorType == SensorType.Clock &&
                s.Value.HasValue &&
                s.Value.Value > 0 &&
                s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
            .Select(s => (double)s.Value!.Value)
            .ToArray();

        if (clocks.Length == 0)
            return ReadPreferred(cpu, SensorType.Clock, "Core", fallbackMax: true);

        return clocks.Average();
    }

    private static void GetMemory(
        out double usedGb,
        out double totalGb,
        out double loadPercent)
    {
        var memory = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!GlobalMemoryStatusEx(ref memory) || memory.ullTotalPhys == 0)
        {
            usedGb = 0;
            totalGb = 0;
            loadPercent = 0;
            return;
        }

        ulong used = memory.ullTotalPhys - memory.ullAvailPhys;
        const double gib = 1024d * 1024d * 1024d;

        usedGb = used / gib;
        totalGb = memory.ullTotalPhys / gib;
        loadPercent = used * 100d / memory.ullTotalPhys;
    }

    private void GetNetworkRates(
        out double downloadMbps,
        out double uploadMbps)
    {
        long rx = 0;
        long tx = 0;

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                IPv4InterfaceStatistics stats = nic.GetIPv4Statistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            catch
            {
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        double seconds = (now - _lastNetworkSample).TotalSeconds;

        if (_lastNetworkSample == default || seconds <= 0)
        {
            downloadMbps = 0;
            uploadMbps = 0;
        }
        else
        {
            downloadMbps =
                Math.Max(0, rx - _lastRxBytes) * 8d / seconds / 1_000_000d;
            uploadMbps =
                Math.Max(0, tx - _lastTxBytes) * 8d / seconds / 1_000_000d;
        }

        _lastRxBytes = rx;
        _lastTxBytes = tx;
        _lastNetworkSample = now;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_opened)
                return;

            _computer.Close();
            _opened = false;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
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

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
