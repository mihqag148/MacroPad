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
        IsMemoryEnabled = true,
        IsMotherboardEnabled = true
    };

    private bool _opened;
    private long _lastRxBytes;
    private long _lastTxBytes;
    private DateTimeOffset _lastNetworkSample;

    private ulong _lastCpuIdle;
    private ulong _lastCpuKernel;
    private ulong _lastCpuUser;
    private bool _haveCpuTimes;

    public PcMonitorSnapshot ReadSnapshot(string? selectedGpuId)
    {
        lock (_gate)
        {
            EnsureOpen();
            UpdateAllHardware();

            IHardware? cpuHardware = _computer.Hardware
                .FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

            IHardware[] gpuHardware = _computer.Hardware
                .Where(IsGpu)
                .ToArray();

            PcGpuInfo[] gpuInfos = gpuHardware
                .Select(h => new PcGpuInfo(
                    h.Identifier.ToString(),
                    string.IsNullOrWhiteSpace(h.Name)
                        ? h.HardwareType.ToString()
                        : h.Name.Trim(),
                    ReadGpuLoad(h),
                    h.HardwareType))
                .ToArray();

            IHardware? selectedGpu = SelectGpu(
                gpuHardware,
                gpuInfos,
                selectedGpuId);

            PcGpuInfo selectedGpuInfo = selectedGpu is null
                ? new PcGpuInfo(
                    "none",
                    "No GPU detected",
                    0,
                    0)
                : gpuInfos.First(g =>
                    string.Equals(
                        g.Id,
                        selectedGpu.Identifier.ToString(),
                        StringComparison.OrdinalIgnoreCase));

            // Windows GetSystemTimes is used as the dependable baseline for CPU
            // usage. LibreHardwareMonitor is still used for clocks/temperature.
            double? windowsCpuLoad = ReadWindowsCpuLoad();
            double lhmCpuLoad = ReadCpuLoad(cpuHardware);
            double cpuLoad = windowsCpuLoad.HasValue
                ? windowsCpuLoad.Value
                : lhmCpuLoad;

            // If the Windows sample is the very first one, LHM can fill the gap.
            if (cpuLoad <= 0.01 && lhmCpuLoad > 0.01)
                cpuLoad = lhmCpuLoad;

            double? cpuTemp =
                ReadTemperature(
                    cpuHardware,
                    "CPU Package",
                    "Package",
                    "Core");

            double? cpuClock =
                AverageCoreClock(cpuHardware);

            double gpuLoad =
                selectedGpu is null
                    ? 0
                    : ReadGpuLoad(selectedGpu);

            double? gpuTemp =
                selectedGpu is null
                    ? null
                    : ReadTemperature(
                        selectedGpu,
                        "GPU Core",
                        "Hot Spot",
                        "Core");

            double? gpuClock =
                selectedGpu is null
                    ? null
                    : ReadGpuClock(selectedGpu);

            GetMemory(
                out double usedGb,
                out double totalGb,
                out double memoryLoad);

            GetNetworkRates(
                out double downloadMbps,
                out double uploadMbps);

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

    private void EnsureOpen()
    {
        if (_opened)
            return;

        _computer.Open();
        _opened = true;

        // Several load sensors need two samples before they contain a useful
        // value. Prime the hardware tree once instead of showing permanent 0%.
        UpdateAllHardware();
        Thread.Sleep(160);
        UpdateAllHardware();

        // Prime GetSystemTimes for the same reason.
        _ = ReadWindowsCpuLoad();
    }

    private void UpdateAllHardware()
    {
        foreach (IHardware hardware in _computer.Hardware)
            UpdateHardware(hardware);
    }

    private static void UpdateHardware(IHardware hardware)
    {
        hardware.Update();

        foreach (IHardware child in hardware.SubHardware)
            UpdateHardware(child);
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
            !string.Equals(
                selectedGpuId,
                "auto",
                StringComparison.OrdinalIgnoreCase))
        {
            IHardware? manual = hardware.FirstOrDefault(h =>
                string.Equals(
                    h.Identifier.ToString(),
                    selectedGpuId,
                    StringComparison.OrdinalIgnoreCase));

            if (manual is not null)
                return manual;
        }

        // Auto mode follows the adapter that is actually doing the most work.
        // When both are equally idle, prefer a discrete NVIDIA/AMD card.
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

    private static IEnumerable<ISensor> SensorsRecursive(
        IHardware? hardware)
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

    private static ISensor[] SensorsOfType(
        IHardware? hardware,
        SensorType type) =>
        SensorsRecursive(hardware)
            .Where(s =>
                s.SensorType == type &&
                s.Value.HasValue &&
                !float.IsNaN(s.Value.Value) &&
                !float.IsInfinity(s.Value.Value))
            .ToArray();

    private static double ReadCpuLoad(IHardware? cpu)
    {
        ISensor[] sensors = SensorsOfType(cpu, SensorType.Load);

        if (sensors.Length == 0)
            return 0;

        double? total = sensors
            .Where(s =>
                s.Name.Contains(
                    "CPU Total",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    s.Name,
                    "Total CPU",
                    StringComparison.OrdinalIgnoreCase))
            .Select(s => (double?)s.Value!.Value)
            .FirstOrDefault();

        double[] coreLoads = sensors
            .Where(s =>
                s.Name.Contains(
                    "Core",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "Thread",
                    StringComparison.OrdinalIgnoreCase))
            .Select(s => Math.Clamp((double)s.Value!.Value, 0, 100))
            .ToArray();

        double fallback = coreLoads.Length > 0
            ? coreLoads.Average()
            : sensors
                .Select(s => Math.Clamp((double)s.Value!.Value, 0, 100))
                .DefaultIfEmpty(0)
                .Max();

        // Some firmware/CPU combinations expose CPU Total but leave it at 0
        // while the per-core counters are live.
        if (!total.HasValue ||
            (total.Value <= 0.01 && fallback > 0.01))
        {
            return fallback;
        }

        return Math.Clamp(total.Value, 0, 100);
    }

    private static double ReadGpuLoad(IHardware gpu)
    {
        ISensor[] loads = SensorsOfType(gpu, SensorType.Load);

        if (loads.Length == 0)
            return 0;

        // "GPU Core" is ideal when present and alive. On Intel/hybrid systems
        // the useful sensor can instead be D3D 3D / Graphics / Render.
        double preferred = loads
            .Where(s =>
                s.Name.Contains(
                    "GPU Core",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "D3D 3D",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "Graphics",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "Render",
                    StringComparison.OrdinalIgnoreCase))
            .Select(s => Math.Clamp((double)s.Value!.Value, 0, 100))
            .DefaultIfEmpty(0)
            .Max();

        double anyEngine = loads
            .Where(s =>
                !s.Name.Contains(
                    "Memory",
                    StringComparison.OrdinalIgnoreCase))
            .Select(s => Math.Clamp((double)s.Value!.Value, 0, 100))
            .DefaultIfEmpty(0)
            .Max();

        return Math.Max(preferred, anyEngine);
    }

    private static double? ReadTemperature(
        IHardware? hardware,
        params string[] preferredNames)
    {
        ISensor[] sensors =
            SensorsOfType(hardware, SensorType.Temperature);

        // 0 C / 1 C is not a valid live PC sensor reading here; treating it as
        // missing is much more useful than displaying a fake 0 degrees.
        sensors = sensors
            .Where(s => s.Value!.Value > 1f)
            .ToArray();

        if (sensors.Length == 0)
            return null;

        foreach (string preferredName in preferredNames)
        {
            ISensor? preferred = sensors.FirstOrDefault(s =>
                s.Name.Contains(
                    preferredName,
                    StringComparison.OrdinalIgnoreCase));

            if (preferred?.Value is float value && value > 1f)
                return value;
        }

        return sensors.Max(s => (double)s.Value!.Value);
    }

    private static double? AverageCoreClock(IHardware? cpu)
    {
        double[] clocks =
            SensorsOfType(cpu, SensorType.Clock)
                .Where(s =>
                    s.Value!.Value > 1 &&
                    (s.Name.Contains(
                         "Core",
                         StringComparison.OrdinalIgnoreCase) ||
                     s.Name.Contains(
                         "CPU",
                         StringComparison.OrdinalIgnoreCase)))
                .Select(s => (double)s.Value!.Value)
                .ToArray();

        if (clocks.Length == 0)
            return null;

        return clocks.Average();
    }

    private static double? ReadGpuClock(IHardware gpu)
    {
        ISensor[] clocks =
            SensorsOfType(gpu, SensorType.Clock)
                .Where(s => s.Value!.Value > 1)
                .ToArray();

        if (clocks.Length == 0)
            return null;

        ISensor? core = clocks.FirstOrDefault(s =>
            s.Name.Contains(
                "Core",
                StringComparison.OrdinalIgnoreCase) ||
            s.Name.Contains(
                "Graphics",
                StringComparison.OrdinalIgnoreCase));

        if (core?.Value is float value && value > 1)
            return value;

        return clocks
            .Select(s => (double)s.Value!.Value)
            .DefaultIfEmpty()
            .Max();
    }

    private double? ReadWindowsCpuLoad()
    {
        if (!GetSystemTimes(
                out FILETIME idle,
                out FILETIME kernel,
                out FILETIME user))
        {
            return null;
        }

        ulong idleNow = ToUInt64(idle);
        ulong kernelNow = ToUInt64(kernel);
        ulong userNow = ToUInt64(user);

        if (!_haveCpuTimes)
        {
            _lastCpuIdle = idleNow;
            _lastCpuKernel = kernelNow;
            _lastCpuUser = userNow;
            _haveCpuTimes = true;
            return null;
        }

        ulong idleDelta = idleNow - _lastCpuIdle;
        ulong kernelDelta = kernelNow - _lastCpuKernel;
        ulong userDelta = userNow - _lastCpuUser;

        _lastCpuIdle = idleNow;
        _lastCpuKernel = kernelNow;
        _lastCpuUser = userNow;

        ulong total = kernelDelta + userDelta;
        if (total == 0)
            return null;

        // Kernel time includes idle time.
        double busy =
            100d * (total - Math.Min(idleDelta, total)) / total;

        return Math.Clamp(busy, 0, 100);
    }

    private static ulong ToUInt64(FILETIME time) =>
        ((ulong)time.dwHighDateTime << 32) |
        time.dwLowDateTime;

    private static void GetMemory(
        out double usedGb,
        out double totalGb,
        out double loadPercent)
    {
        var memory = new MEMORYSTATUSEX
        {
            dwLength =
                (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!GlobalMemoryStatusEx(ref memory) ||
            memory.ullTotalPhys == 0)
        {
            usedGb = 0;
            totalGb = 0;
            loadPercent = 0;
            return;
        }

        ulong used =
            memory.ullTotalPhys - memory.ullAvailPhys;

        const double gib =
            1024d * 1024d * 1024d;

        usedGb = used / gib;
        totalGb = memory.ullTotalPhys / gib;
        loadPercent =
            used * 100d / memory.ullTotalPhys;
    }

    private void GetNetworkRates(
        out double downloadMbps,
        out double uploadMbps)
    {
        long rx = 0;
        long tx = 0;

        foreach (
            NetworkInterface nic in
            NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus !=
                    OperationalStatus.Up ||
                nic.NetworkInterfaceType ==
                    NetworkInterfaceType.Loopback ||
                nic.NetworkInterfaceType ==
                    NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            try
            {
                IPv4InterfaceStatistics stats =
                    nic.GetIPv4Statistics();

                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            catch
            {
            }
        }

        DateTimeOffset now =
            DateTimeOffset.UtcNow;

        double seconds =
            (now - _lastNetworkSample)
            .TotalSeconds;

        if (_lastNetworkSample == default ||
            seconds <= 0)
        {
            downloadMbps = 0;
            uploadMbps = 0;
        }
        else
        {
            downloadMbps =
                Math.Max(
                    0,
                    rx - _lastRxBytes) *
                8d /
                seconds /
                1_000_000d;

            uploadMbps =
                Math.Max(
                    0,
                    tx - _lastTxBytes) *
                8d /
                seconds /
                1_000_000d;
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

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Auto)]
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Auto,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(
        ref MEMORYSTATUSEX buffer);

    [DllImport(
        "kernel32.dll",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);
}
