using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace LumiPad.App;

public sealed record PcGpuInfo(
    string Id,
    string Name,
    double Load,
    HardwareType Type);

public sealed record PcMonitorSnapshot(
    double CpuLoad,
    double? CpuTemperature,
    double? CpuClockMHz,
    double GpuLoad,
    double? GpuTemperature,
    double? GpuClockMHz,
    string GpuId,
    string GpuName,
    IReadOnlyList<PcGpuInfo> AvailableGpus,
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

    public PcMonitorSnapshot ReadSnapshot(string? selectedGpuId)
    {
        lock (_gate)
        {
            EnsureOpen();

            foreach (IHardware hardware in _computer.Hardware)
                UpdateHardware(hardware);

            IHardware? cpuHardware = _computer.Hardware
                .FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

            IHardware[] gpuHardware = _computer.Hardware
                .Where(IsGpu)
                .ToArray();

            PcGpuInfo[] gpuInfos = gpuHardware
                .Select(h => new PcGpuInfo(
                    h.Identifier.ToString(),
                    string.IsNullOrWhiteSpace(h.Name) ? h.HardwareType.ToString() : h.Name.Trim(),
                    Math.Clamp(
                        ReadPreferred(h, SensorType.Load, "GPU Core", true) ?? 0,
                        0,
                        100),
                    h.HardwareType))
                .ToArray();

            IHardware? selectedGpu = SelectGpu(
                gpuHardware,
                gpuInfos,
                selectedGpuId);

            PcGpuInfo selectedGpuInfo = selectedGpu is null
                ? new PcGpuInfo("none", "No GPU detected", 0, 0)
                : gpuInfos.First(g =>
                    string.Equals(
                        g.Id,
                        selectedGpu.Identifier.ToString(),
                        StringComparison.OrdinalIgnoreCase));

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

            double gpuLoad = selectedGpu is null
                ? 0
                : ReadPreferred(
                    selectedGpu,
                    SensorType.Load,
                    "GPU Core",
                    fallbackMax: true) ?? 0;

            double? gpuTemp = selectedGpu is null
                ? null
                : ReadPreferred(
                    selectedGpu,
                    SensorType.Temperature,
                    "GPU Core",
                    fallbackMax: true);

            double? gpuClock = selectedGpu is null
                ? null
                : ReadPreferred(
                    selectedGpu,
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
                selectedGpuInfo.Id,
                selectedGpuInfo.Name,
                gpuInfos,
                Math.Clamp(memoryLoad, 0, 100),
                usedGb,
                totalGb,
                downloadMbps,
                uploadMbps,
                null,
                DateTimeOffset.UtcNow);
        }
    }

    private static bool IsGpu(IHardware hardware) =>
        hardware.HardwareType == HardwareType.GpuNvidia ||
        hardware.HardwareType == HardwareType.GpuAmd ||
        hardware.HardwareType == HardwareType.GpuIntel;

    private static IHardware? SelectGpu(
        IHardware[] hardware,
        PcGpuInfo[] infos,
        string? selectedGpuId)
    {
        if (hardware.Length == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(selectedGpuId) &&
            !string.Equals(selectedGpuId, "auto", StringComparison.OrdinalIgnoreCase))
        {
            IHardware? manual = hardware.FirstOrDefault(h =>
                string.Equals(
                    h.Identifier.ToString(),
                    selectedGpuId,
                    StringComparison.OrdinalIgnoreCase));

            if (manual is not null)
                return manual;
        }

        // MSI Afterburner-style Auto mode: follow the GPU doing real work.
        // On a tie, prefer a discrete NVIDIA/AMD GPU over an idle integrated GPU.
        PcGpuInfo winner = infos
            .OrderByDescending(g => g.Load)
            .ThenByDescending(g =>
                g.Type == HardwareType.GpuNvidia ||
                g.Type == HardwareType.GpuAmd
                    ? 1
                    : 0)
            .First();

        return hardware.First(h =>
            string.Equals(
                h.Identifier.ToString(),
                winner.Id,
                StringComparison.OrdinalIgnoreCase));
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
