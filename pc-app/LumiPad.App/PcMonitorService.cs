using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;

namespace LumiPad.App;

public sealed record PcGpuInfo(
    string Id,
    string Name,
    double? Load,
    HardwareType Type);

public sealed record PcMonitorSnapshot(
    double CpuLoad,
    double? CpuTemperature,
    double? CpuClockMHz,
    double? GpuLoad,
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
        IsMotherboardEnabled = true,
        IsControllerEnabled = true
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

            IHardware? cpuHardware = AllHardware()
                .FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);

            IHardware[] gpuHardware = AllHardware()
                .Where(IsGpu)
                .GroupBy(h => h.Identifier.ToString())
                .Select(g => g.First())
                .ToArray();

            Dictionary<int, double?> wmiGpuLoads =
                ReadWindowsGpuEngineLoads();

            PcGpuInfo[] gpuInfos = gpuHardware
                .Select((hardware, index) =>
                {
                    double? load =
                        ReadGpuLoad(hardware);

                    if ((!load.HasValue || load.Value <= 0.01) &&
                        wmiGpuLoads.TryGetValue(index, out double? wmiLoad) &&
                        wmiLoad.HasValue)
                    {
                        load = wmiLoad;
                    }

                    return new PcGpuInfo(
                        hardware.Identifier.ToString(),
                        string.IsNullOrWhiteSpace(hardware.Name)
                            ? hardware.HardwareType.ToString()
                            : hardware.Name.Trim(),
                        load,
                        hardware.HardwareType);
                })
                .ToArray();

            IHardware? selectedGpu = SelectGpu(
                gpuHardware,
                gpuInfos,
                selectedGpuId);

            int selectedGpuIndex =
                selectedGpu is null
                    ? -1
                    : Array.FindIndex(
                        gpuHardware,
                        h => string.Equals(
                            h.Identifier.ToString(),
                            selectedGpu.Identifier.ToString(),
                            StringComparison.OrdinalIgnoreCase));

            PcGpuInfo selectedGpuInfo = selectedGpu is null
                ? new PcGpuInfo(
                    "none",
                    "No GPU detected",
                    null,
                    0)
                : gpuInfos.First(g =>
                    string.Equals(
                        g.Id,
                        selectedGpu.Identifier.ToString(),
                        StringComparison.OrdinalIgnoreCase));

            double? windowsCpuLoad = ReadWindowsCpuLoad();
            double? lhmCpuLoad = ReadCpuLoad(cpuHardware);

            double cpuLoad =
                windowsCpuLoad ??
                lhmCpuLoad ??
                0;

            if (cpuLoad <= 0.01 &&
                lhmCpuLoad.HasValue &&
                lhmCpuLoad.Value > 0.01)
            {
                cpuLoad = lhmCpuLoad.Value;
            }

            double? cpuTemp =
                ReadTemperature(
                    cpuHardware,
                    "CPU Package",
                    "Package",
                    "Tctl",
                    "Tdie",
                    "Core");

            cpuTemp ??=
                ReadGlobalCpuTemperature();

            cpuTemp ??=
                ReadExternalMonitorCpuTemperature();

            cpuTemp ??=
                ReadAcpiThermalZoneTemperature();

            double? cpuClock =
                AverageCoreClock(cpuHardware);

            cpuClock ??=
                ReadWindowsCpuClock();

            double? gpuLoad =
                selectedGpu is null
                    ? null
                    : selectedGpuInfo.Load;

            if ((!gpuLoad.HasValue || gpuLoad.Value <= 0.01) &&
                selectedGpuIndex >= 0 &&
                wmiGpuLoads.TryGetValue(
                    selectedGpuIndex,
                    out double? selectedWmiLoad) &&
                selectedWmiLoad.HasValue)
            {
                gpuLoad = selectedWmiLoad;
            }

            double? gpuTemp =
                selectedGpu is null
                    ? null
                    : ReadTemperature(
                        selectedGpu,
                        "GPU Hot Spot",
                        "GPU Core",
                        "Hot Spot",
                        "Core",
                        "Temperature");

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
                gpuLoad.HasValue
                    ? Math.Clamp(gpuLoad.Value, 0, 100)
                    : null,
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

        // Prime sensors that need more than one sample.
        UpdateAllHardware();
        Thread.Sleep(220);
        UpdateAllHardware();

        _ = ReadWindowsCpuLoad();
        _ = ReadWindowsGpuEngineLoads();
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

    private IEnumerable<IHardware> AllHardware()
    {
        foreach (IHardware hardware in _computer.Hardware)
        {
            yield return hardware;

            foreach (IHardware child in HardwareRecursive(hardware))
                yield return child;
        }
    }

    private static IEnumerable<IHardware> HardwareRecursive(
        IHardware hardware)
    {
        foreach (IHardware child in hardware.SubHardware)
        {
            yield return child;

            foreach (IHardware nested in HardwareRecursive(child))
                yield return nested;
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

        PcGpuInfo winner = infos
            .OrderByDescending(g => g.Load ?? -1)
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

    private static double? ReadCpuLoad(IHardware? cpu)
    {
        ISensor[] sensors =
            SensorsOfType(cpu, SensorType.Load);

        if (sensors.Length == 0)
            return null;

        double? total = sensors
            .Where(s =>
                s.Name.Contains(
                    "CPU Total",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "Total CPU",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals(
                    "Total",
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
            .Select(s =>
                Math.Clamp(
                    (double)s.Value!.Value,
                    0,
                    100))
            .ToArray();

        double? fallback =
            coreLoads.Length > 0
                ? coreLoads.Average()
                : sensors
                    .Select(s =>
                        Math.Clamp(
                            (double)s.Value!.Value,
                            0,
                            100))
                    .DefaultIfEmpty()
                    .Max();

        if (!total.HasValue)
            return fallback;

        if (total.Value <= 0.01 &&
            fallback.HasValue &&
            fallback.Value > 0.01)
        {
            return fallback;
        }

        return Math.Clamp(total.Value, 0, 100);
    }

    private static double? ReadGpuLoad(IHardware gpu)
    {
        ISensor[] loads =
            SensorsOfType(gpu, SensorType.Load);

        if (loads.Length == 0)
            return null;

        ISensor[] useful = loads
            .Where(s =>
                !s.Name.Contains(
                    "Memory",
                    StringComparison.OrdinalIgnoreCase) &&
                !s.Name.Contains(
                    "Bus",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (useful.Length == 0)
            useful = loads;

        double preferred = useful
            .Where(s =>
                s.Name.Contains(
                    "GPU Core",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "3D",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "Graphics",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "Render",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "Compute",
                    StringComparison.OrdinalIgnoreCase))
            .Select(s =>
                Math.Clamp(
                    (double)s.Value!.Value,
                    0,
                    100))
            .DefaultIfEmpty(-1)
            .Max();

        double anyEngine = useful
            .Select(s =>
                Math.Clamp(
                    (double)s.Value!.Value,
                    0,
                    100))
            .DefaultIfEmpty(-1)
            .Max();

        double result =
            Math.Max(preferred, anyEngine);

        return result < 0
            ? null
            : result;
    }

    private double? ReadGlobalCpuTemperature()
    {
        var matches = AllHardware()
            .Where(h => !IsGpu(h))
            .SelectMany(h =>
                SensorsOfType(
                    h,
                    SensorType.Temperature))
            .Where(s =>
                s.Value!.Value > 5f &&
                s.Value!.Value < 125f &&
                (s.Name.Contains(
                     "CPU",
                     StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains(
                     "Package",
                     StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains(
                     "Tctl",
                     StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains(
                     "Tdie",
                     StringComparison.OrdinalIgnoreCase)))
            .Select(s => (double)s.Value!.Value)
            .ToArray();

        return matches.Length == 0
            ? null
            : matches.Max();
    }

    private static double? ReadTemperature(
        IHardware? hardware,
        params string[] preferredNames)
    {
        ISensor[] sensors =
            SensorsOfType(
                hardware,
                SensorType.Temperature)
            .Where(s =>
                s.Value!.Value > 5f &&
                s.Value!.Value < 125f)
            .ToArray();

        if (sensors.Length == 0)
            return null;

        foreach (string preferredName in preferredNames)
        {
            ISensor? preferred =
                sensors.FirstOrDefault(s =>
                    s.Name.Contains(
                        preferredName,
                        StringComparison.OrdinalIgnoreCase));

            if (preferred?.Value is float value)
                return value;
        }

        return sensors.Max(s =>
            (double)s.Value!.Value);
    }

    private static double? AverageCoreClock(
        IHardware? cpu)
    {
        double[] clocks =
            SensorsOfType(
                cpu,
                SensorType.Clock)
            .Where(s =>
                s.Value!.Value > 100 &&
                (s.Name.Contains(
                     "Core",
                     StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains(
                     "CPU",
                     StringComparison.OrdinalIgnoreCase) ||
                 s.Name.Contains(
                     "Bus",
                     StringComparison.OrdinalIgnoreCase) == false))
            .Select(s =>
                (double)s.Value!.Value)
            .ToArray();

        if (clocks.Length == 0)
            return null;

        // Ignore obvious bus/reference clocks when a CPU has multiple sensors.
        double[] plausible =
            clocks
                .Where(v => v >= 200)
                .ToArray();

        return plausible.Length > 0
            ? plausible.Average()
            : clocks.Average();
    }

    private static double? ReadGpuClock(
        IHardware gpu)
    {
        ISensor[] clocks =
            SensorsOfType(
                gpu,
                SensorType.Clock)
            .Where(s =>
                s.Value!.Value > 50)
            .ToArray();

        if (clocks.Length == 0)
            return null;

        ISensor? core =
            clocks.FirstOrDefault(s =>
                s.Name.Contains(
                    "Core",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "Graphics",
                    StringComparison.OrdinalIgnoreCase) ||
                s.Name.Contains(
                    "GPU",
                    StringComparison.OrdinalIgnoreCase));

        if (core?.Value is float value)
            return value;

        return clocks
            .Select(s => (double)s.Value!.Value)
            .DefaultIfEmpty()
            .Max();
    }

    private static Dictionary<int, double?>
        ReadWindowsGpuEngineLoads()
    {
        var result =
            new Dictionary<int, double?>();

        try
        {
            using var searcher =
                new ManagementObjectSearcher(
                    @"root\cimv2",
                    "SELECT Name, UtilizationPercentage " +
                    "FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");

            foreach (ManagementObject item in searcher.Get())
            {
                string name =
                    item["Name"]?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                Match match =
                    Regex.Match(
                        name,
                        @"phys_(\d+)",
                        RegexOptions.IgnoreCase);

                if (!match.Success ||
                    !int.TryParse(
                        match.Groups[1].Value,
                        out int physicalIndex))
                {
                    continue;
                }

                if (!double.TryParse(
                        item["UtilizationPercentage"]?.ToString(),
                        out double load))
                {
                    continue;
                }

                load = Math.Clamp(load, 0, 100);

                if (!result.TryGetValue(
                        physicalIndex,
                        out double? current) ||
                    !current.HasValue ||
                    load > current.Value)
                {
                    result[physicalIndex] = load;
                }
            }
        }
        catch
        {
            // Optional fallback. LHM remains the primary source.
        }

        return result;
    }

    private static double? ReadExternalMonitorCpuTemperature()
    {
        string[] scopes =
        [
            @"root\LibreHardwareMonitor",
            @"root\OpenHardwareMonitor"
        ];

        foreach (string scopePath in scopes)
        {
            try
            {
                var scope =
                    new ManagementScope(
                        $@"\\.\{scopePath}");
                scope.Connect();

                using var searcher =
                    new ManagementObjectSearcher(
                        scope,
                        new ObjectQuery(
                            "SELECT Name, Value, SensorType FROM Sensor"));

                double[] values =
                    searcher
                        .Get()
                        .Cast<ManagementObject>()
                        .Where(o =>
                            string.Equals(
                                o["SensorType"]?.ToString(),
                                "Temperature",
                                StringComparison.OrdinalIgnoreCase))
                        .Where(o =>
                        {
                            string name =
                                o["Name"]?.ToString() ?? "";

                            return
                                name.Contains("CPU", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Tctl", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                                name.Contains("Core Max", StringComparison.OrdinalIgnoreCase);
                        })
                        .Select(o =>
                        {
                            return double.TryParse(
                                o["Value"]?.ToString(),
                                out double value)
                                    ? value
                                    : double.NaN;
                        })
                        .Where(v =>
                            double.IsFinite(v) &&
                            v >= 5 &&
                            v <= 125)
                        .ToArray();

                if (values.Length > 0)
                    return values.Max();
            }
            catch
            {
            }
        }

        return null;
    }

    private static double? ReadWindowsCpuClock()
    {
        try
        {
            using var searcher =
                new ManagementObjectSearcher(
                    "SELECT CurrentClockSpeed " +
                    "FROM Win32_Processor");

            double[] values =
                searcher
                    .Get()
                    .Cast<ManagementObject>()
                    .Select(o =>
                        double.TryParse(
                            o["CurrentClockSpeed"]?.ToString(),
                            out double mhz)
                            ? mhz
                            : 0)
                    .Where(v => v > 100)
                    .ToArray();

            return values.Length == 0
                ? null
                : values.Average();
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadAcpiThermalZoneTemperature()
    {
        try
        {
            using var searcher =
                new ManagementObjectSearcher(
                    @"root\WMI",
                    "SELECT CurrentTemperature " +
                    "FROM MSAcpi_ThermalZoneTemperature");

            double[] values =
                searcher
                    .Get()
                    .Cast<ManagementObject>()
                    .Select(o =>
                    {
                        if (!double.TryParse(
                                o["CurrentTemperature"]?.ToString(),
                                out double raw))
                        {
                            return double.NaN;
                        }

                        return raw / 10d - 273.15d;
                    })
                    .Where(v =>
                        double.IsFinite(v) &&
                        v >= 10 &&
                        v <= 120)
                    .ToArray();

            return values.Length == 0
                ? null
                : values.Max();
        }
        catch
        {
            return null;
        }
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

        ulong idleDelta =
            idleNow - _lastCpuIdle;
        ulong kernelDelta =
            kernelNow - _lastCpuKernel;
        ulong userDelta =
            userNow - _lastCpuUser;

        _lastCpuIdle = idleNow;
        _lastCpuKernel = kernelNow;
        _lastCpuUser = userNow;

        ulong total =
            kernelDelta + userDelta;

        if (total == 0)
            return null;

        double busy =
            100d *
            (total - Math.Min(idleDelta, total)) /
            total;

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
            memory.ullTotalPhys -
            memory.ullAvailPhys;

        const double gib =
            1024d *
            1024d *
            1024d;

        usedGb =
            used / gib;
        totalGb =
            memory.ullTotalPhys / gib;
        loadPercent =
            used *
            100d /
            memory.ullTotalPhys;
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
