using HidSharp;
using System.IO;
using System.Text;

namespace LumiPad.App;

/// <summary>
/// QMK Raw HID transport for Lumi products.
///
/// Raw HID is a fixed 32-byte payload. HidSharp includes the HID report ID as
/// byte zero, therefore the transport uses 33-byte input/output reports.
/// PIXEL PRO exposes a dedicated QMK/VIA-compatible Raw HID interface using
/// report ID 0. Lumi fragments UTF-8 protocol lines over the same 32-byte
/// Raw HID reports and verifies the target with HELLO before accepting it.
/// </summary>
public sealed class QmkRawHidLink : IDeviceLink
{
    private const byte Magic0 = (byte)'L';
    private const byte Magic1 = (byte)'Q';
    private const byte FrameVersion = 1;
    private const byte FlagStart = 0x01;
    private const byte FlagEnd = 0x02;
    private const byte FlagResponse = 0x04;
    private const int RawPayloadBytes = 32;
    private const int FrameHeaderBytes = 7;
    private const int FramePayloadBytes = RawPayloadBytes - FrameHeaderBytes;

    private readonly ProductDefinition _product;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly HashSet<string> _capabilities =
        new(StringComparer.OrdinalIgnoreCase);

    private HidDevice? _device;
    private HidStream? _stream;
    private int _sequence;
    private string _connectionName = "";

    public QmkRawHidLink(ProductDefinition product)
    {
        _product = product;
    }

    public bool IsConnected => _stream is not null;
    public bool IsUsbConnected => IsConnected;
    public bool IsBluetoothConnected => false;
    public string ConnectionName => _connectionName;

    public event Action<string>? LinkError;
    public event Action<string, string>? Diagnostic;

    public string FirmwareHello { get; private set; } = "";
    public int ProtocolVersion { get; private set; }

    public bool SupportsDiagnostics => HasCapability("LOG");
    public bool SupportsMemoryInfo => HasCapability("MEM");
    public bool SupportsPanelInfo => HasCapability("PANEL");
    public bool SupportsSaverState => HasCapability("SAVERSTATE");
    public bool SupportsProfileSwitch => HasCapability("PROFILE");
    public bool SupportsActions => HasCapability("ACTION");
    public bool SupportsVariableArtwork => false;
    public bool SupportsBatteryInfo => HasCapability("BAT");
    public bool SupportsPcMonitor => HasCapability("PCMON");
    public bool SupportsFirmwareOta => HasCapability("FWOTA");

    private bool HasCapability(string capability) =>
        _capabilities.Contains(capability);

    private void Log(string level, string message) =>
        Diagnostic?.Invoke(level, message);

    public Task<string?> AutoDetectAsync(
        CancellationToken cancellationToken = default) =>
        ConnectUsbAsync(cancellationToken);

    public async Task<string?> ConnectUsbAsync(
        CancellationToken cancellationToken = default)
    {
        Disconnect();

        if (!_product.UsbVendorId.HasValue ||
            !_product.UsbProductId.HasValue)
        {
            Log(
                "WARN",
                $"QMK product {_product.ProductCode} has no USB VID/PID.");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        IEnumerable<HidDevice> candidates =
            DeviceList.Local.GetHidDevices(
                _product.UsbVendorId.Value,
                _product.UsbProductId.Value);

        foreach (HidDevice device in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int inputLength;
            int outputLength;
            try
            {
                inputLength = device.GetMaxInputReportLength();
                outputLength = device.GetMaxOutputReportLength();
            }
            catch
            {
                continue;
            }

            // QMK Raw HID is 32 bytes plus the report-ID byte used by HidSharp.
            if (inputLength < RawPayloadBytes + 1 ||
                outputLength < RawPayloadBytes + 1)
            {
                continue;
            }

            if (!device.TryOpen(out HidStream stream))
                continue;

            _device = device;
            _stream = stream;

            try
            {
                stream.ReadTimeout = 900;
                stream.WriteTimeout = 900;

                string? hello =
                    await QueryLineAsync("HELLO", 900, cancellationToken);

                if (hello is null ||
                    !hello.StartsWith("LUMIPAD|", StringComparison.Ordinal))
                {
                    stream.Dispose();
                    _stream = null;
                    _device = null;
                    continue;
                }

                ParseHello(hello);

                string productName;
                try
                {
                    productName = device.GetProductName();
                }
                catch
                {
                    productName = _product.Name;
                }

                if (string.IsNullOrWhiteSpace(productName))
                    productName = _product.Name;

                _connectionName = $"QMK Raw HID · {productName}";

                Log(
                    "INFO",
                    $"Connected {_connectionName}; VID=0x{device.VendorID:X4}, " +
                    $"PID=0x{device.ProductID:X4}; expected usage " +
                    $"0x{_product.RawUsagePage:X4}/0x{_product.RawUsageId:X4}");

                return _connectionName;
            }
            catch (Exception ex)
            {
                Log("WARN", $"QMK candidate rejected: {ex.Message}");
                try { stream.Dispose(); } catch { }
                _stream = null;
                _device = null;
            }
        }

        return null;
    }

    public Task<string?> ConnectBluetoothAsync(
        CancellationToken cancellationToken = default)
    {
        Log("INFO", "QMK Raw HID driver is USB-only.");
        return Task.FromResult<string?>(null);
    }

    public Task<string?> PromoteToUsbIfAvailableAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(IsConnected ? _connectionName : null);

    private void ParseHello(string hello)
    {
        FirmwareHello = hello;
        ProtocolVersion = 0;
        _capabilities.Clear();

        string[] parts = hello.Split('|');
        if (parts.Length >= 2 &&
            string.Equals(parts[0], "LUMIPAD", StringComparison.Ordinal) &&
            int.TryParse(parts[1], out int version))
        {
            ProtocolVersion = version;
        }

        foreach (string part in parts)
        {
            if (!part.StartsWith("CAPS=", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (string cap in part["CAPS=".Length..].Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                _capabilities.Add(cap);
            }
        }

        Log(
            "INFO",
            $"QMK Lumi protocol v{ProtocolVersion}; caps=" +
            string.Join(",", _capabilities));
    }

    public void Disconnect()
    {
        HidStream? stream = _stream;
        _stream = null;
        _device = null;

        if (stream is not null)
        {
            try { stream.Dispose(); } catch { }
        }

        _connectionName = "";
        FirmwareHello = "";
        ProtocolVersion = 0;
        _capabilities.Clear();
    }

    private byte NextSequence()
    {
        int next = Interlocked.Increment(ref _sequence);
        byte value = (byte)(next & 0xFF);
        return value == 0 ? (byte)1 : value;
    }

    private async Task SendLineAsync(
        string line,
        CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            byte sequence = NextSequence();
            WriteMessageLocked(sequence, line, response: false);
        }
        catch (Exception ex)
        {
            HandleIoFailure("QMK write", ex);
            throw;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<string?> QueryLineAsync(
        string line,
        int timeoutMs = 800,
        CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken);
        try
        {
            EnsureConnected();
            byte sequence = NextSequence();
            WriteMessageLocked(sequence, line, response: false);
            return await ReadResponseLockedAsync(
                sequence,
                timeoutMs,
                cancellationToken);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex)
        {
            HandleIoFailure("QMK query", ex);
            return null;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private void EnsureConnected()
    {
        if (_stream is null || _device is null)
            throw new IOException("QMK Raw HID device is not connected.");
    }

    private void WriteMessageLocked(
        byte sequence,
        string message,
        bool response)
    {
        HidStream stream = _stream ??
            throw new IOException("QMK Raw HID stream is closed.");
        HidDevice device = _device ??
            throw new IOException("QMK Raw HID device is missing.");

        byte[] bytes = Encoding.UTF8.GetBytes(message);
        int fragments = Math.Max(
            1,
            (bytes.Length + FramePayloadBytes - 1) / FramePayloadBytes);

        for (int fragment = 0; fragment < fragments; fragment++)
        {
            int offset = fragment * FramePayloadBytes;
            int length = Math.Min(
                FramePayloadBytes,
                Math.Max(0, bytes.Length - offset));

            int reportLength = Math.Max(
                RawPayloadBytes + 1,
                device.GetMaxOutputReportLength());

            byte[] report = new byte[reportLength];
            report[0] = _product.RawReportId;
            int p = 1;

            report[p + 0] = Magic0;
            report[p + 1] = Magic1;
            report[p + 2] = FrameVersion;

            byte flags = 0;
            if (fragment == 0)
                flags |= FlagStart;
            if (fragment == fragments - 1)
                flags |= FlagEnd;
            if (response)
                flags |= FlagResponse;

            report[p + 3] = flags;
            report[p + 4] = sequence;
            report[p + 5] = (byte)fragment;
            report[p + 6] = (byte)length;

            if (length > 0)
            {
                Buffer.BlockCopy(
                    bytes,
                    offset,
                    report,
                    p + FrameHeaderBytes,
                    length);
            }

            stream.Write(report);
        }
    }

    private async Task<string?> ReadResponseLockedAsync(
        byte sequence,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        HidStream stream = _stream ??
            throw new IOException("QMK Raw HID stream is closed.");
        HidDevice device = _device ??
            throw new IOException("QMK Raw HID device is missing.");

        stream.ReadTimeout = timeoutMs;
        using var message = new MemoryStream();
        int expectedFragment = 0;
        bool started = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] report =
                new byte[Math.Max(
                    RawPayloadBytes + 1,
                    device.GetMaxInputReportLength())];

            int count = await Task.Run(
                () => stream.Read(report, 0, report.Length),
                cancellationToken);

            int p =
                count >= RawPayloadBytes + 1
                    ? 1
                    : 0;

            if (p == 1 &&
                report[0] != _product.RawReportId)
            {
                continue;
            }

            if (count - p < RawPayloadBytes)
                continue;

            if (report[p + 0] != Magic0 ||
                report[p + 1] != Magic1 ||
                report[p + 2] != FrameVersion)
            {
                continue;
            }

            byte flags = report[p + 3];
            if ((flags & FlagResponse) == 0 ||
                report[p + 4] != sequence)
            {
                continue;
            }

            int fragment = report[p + 5];
            int length = Math.Min(
                FramePayloadBytes,
                (int)report[p + 6]);

            if ((flags & FlagStart) != 0)
            {
                message.SetLength(0);
                expectedFragment = 0;
                started = true;
            }

            if (!started || fragment != expectedFragment)
                continue;

            if (length > 0)
            {
                message.Write(
                    report,
                    p + FrameHeaderBytes,
                    length);
            }

            expectedFragment++;

            if ((flags & FlagEnd) != 0)
                return Encoding.UTF8.GetString(message.ToArray()).Trim();
        }
    }

    private void HandleIoFailure(string context, Exception ex)
    {
        Log("ERROR", $"{context}: {ex.Message}");
        LinkError?.Invoke(ex.Message);
        Disconnect();
    }

    private void FireAndForget(string line)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (IsConnected)
                    await SendLineAsync(line);
            }
            catch
            {
            }
        });
    }

    public void SendNowPlaying(NowPlayingData data)
    {
        if (!IsConnected)
            return;

        string source = Uri.EscapeDataString(data.SourceName ?? "MUSIC");
        string title = Uri.EscapeDataString(data.Title ?? "");
        string artist = Uri.EscapeDataString(data.Artist ?? "");

        FireAndForget(
            $"NP|{Math.Max(0, (long)data.Position.TotalMilliseconds)}|" +
            $"{Math.Max(0, (long)data.Duration.TotalMilliseconds)}|" +
            $"{(data.IsPlaying ? 1 : 0)}|{source}|{title}|{artist}");
    }

    public void ClearNowPlaying() => FireAndForget("NPCLR");

    public async Task<bool> SendScreensaverAnimationAsync(
        ScreensaverAnimation animation,
        IProgress<int>? progress = null)
    {
        if (!IsConnected)
            return false;

        if (animation.PixelFormat == ScreensaverPixelFormat.Rgb565)
        {
            if (animation.Frames.Count != 1)
                return false;

            byte[] image = animation.Frames[0];
            await SendLineAsync($"IMGBEGIN|{image.Length}");

            const int chunkSize = 120;
            int chunks = (image.Length + chunkSize - 1) / chunkSize;
            int sent = 0;

            for (int offset = 0; offset < image.Length; offset += chunkSize)
            {
                int length = Math.Min(chunkSize, image.Length - offset);
                string payload =
                    Convert.ToBase64String(image, offset, length);
                await SendLineAsync($"IMGCHUNK|{offset}|{payload}");
                sent++;
                progress?.Report(
                    (int)Math.Round(sent * 100.0 / Math.Max(1, chunks)));
            }

            await SendLineAsync("IMGEND");
            return true;
        }

        if (animation.PixelFormat != ScreensaverPixelFormat.Rgb332)
            return false;

        string timings =
            animation.FrameDurationsMs.Count == animation.Frames.Count
                ? string.Join(",", animation.FrameDurationsMs)
                : "";

        await SendLineAsync(
            $"SAVBEGIN|{animation.Frames.Count}|{animation.FrameIntervalMs}" +
            (timings.Length > 0 ? $"|{timings}" : ""));

        const int rawChunk = 120;
        int frameBytes = animation.Width * animation.Height;
        int chunksPerFrame =
            Math.Max(1, (frameBytes + rawChunk - 1) / rawChunk);
        int totalChunks =
            Math.Max(1, chunksPerFrame * animation.Frames.Count);
        int sentChunks = 0;

        for (int frameIndex = 0;
             frameIndex < animation.Frames.Count;
             frameIndex++)
        {
            byte[] frame = animation.Frames[frameIndex];

            for (int offset = 0; offset < frame.Length; offset += rawChunk)
            {
                int length = Math.Min(rawChunk, frame.Length - offset);
                string payload =
                    Convert.ToBase64String(frame, offset, length);

                await SendLineAsync(
                    $"SAVCHUNK|{frameIndex}|{offset}|{payload}");

                sentChunks++;
                progress?.Report(
                    (int)Math.Round(
                        sentChunks * 100.0 / totalChunks));
            }
        }

        await SendLineAsync("SAVEND");
        return true;
    }

    public void ClearScreensaverAnimation() =>
        FireAndForget("SAVCLEAR");

    public async Task<string?> GetScreensaverStateAsync()
    {
        if (!SupportsSaverState)
            return null;

        string? response = await QueryLineAsync("SAVERSTATE");
        return response is not null &&
               response.StartsWith(
                   "SAVERSTATE|",
                   StringComparison.Ordinal)
            ? response["SAVERSTATE|".Length..]
            : null;
    }

    public Task ShowScreensaverNowAsync(bool pcMonitor) =>
        SendLineAsync($"CFG|SAVERNOW|{(pcMonitor ? 1 : 0)}");

    public Task SetScreensaverSourceAsync(bool pcMonitor) =>
        SendLineAsync($"CFG|SAVERSRC|{(pcMonitor ? 1 : 0)}");

    public void SetScreensaverSource(bool pcMonitor) =>
        FireAndForget($"CFG|SAVERSRC|{(pcMonitor ? 1 : 0)}");

    public void SetScreensaverDelay(int seconds) =>
        FireAndForget($"CFG|SAVERDELAY|{Math.Max(0, seconds)}");

    public void SetSleepTimeout(int seconds) =>
        FireAndForget($"CFG|SLEEP|{Math.Max(0, seconds)}");

    public void SetRgbIdleTimeout(int seconds) =>
        FireAndForget($"CFG|RGBIDLE|{Math.Max(0, seconds)}");

    public void SetDeepSleepTimeout(int seconds) =>
        FireAndForget($"CFG|DEEPSLEEP|{Math.Max(0, seconds)}");

    public async Task<(uint Seq, string Level, string Message)?>
        ReadFirmwareLogAsync(uint afterSeq)
    {
        if (!SupportsDiagnostics)
            return null;

        string? response =
            await QueryLineAsync($"LOG|{afterSeq}");

        if (response is null ||
            response.StartsWith("LOG|NONE|", StringComparison.Ordinal))
            return null;

        string[] parts = response.Split('|', 4);
        if (parts.Length != 4 ||
            parts[0] != "LOG" ||
            !uint.TryParse(parts[1], out uint seq))
            return null;

        return (seq, parts[2], parts[3]);
    }

    public async Task<int?> ReadBatteryPercentAsync()
    {
        if (!SupportsBatteryInfo)
            return null;

        string? response = await QueryLineAsync("BAT");
        string[] parts = response?.Split('|') ?? [];

        return parts.Length == 2 &&
               parts[0] == "BAT" &&
               int.TryParse(parts[1], out int value)
            ? Math.Clamp(value, 0, 100)
            : null;
    }

    public async Task<(string Panel, int RefreshHz, int SpiHz, int GifMaxFps)?>
        ReadPanelInfoAsync()
    {
        if (!SupportsPanelInfo)
            return null;

        string? response = await QueryLineAsync("PANEL");
        string[] parts = response?.Split('|') ?? [];

        if (parts.Length != 5 ||
            parts[0] != "PANEL" ||
            !int.TryParse(parts[2], out int refresh) ||
            !int.TryParse(parts[3], out int spi) ||
            !int.TryParse(parts[4], out int gif))
            return null;

        return (parts[1], refresh, spi, gif);
    }

    public async Task<(long FlashUsed, long FlashTotal, long RamUsed, long RamTotal)?>
        ReadMemoryUsageAsync()
    {
        if (!SupportsMemoryInfo)
            return null;

        string? response = await QueryLineAsync("MEM");
        string[] parts = response?.Split('|') ?? [];

        if (parts.Length != 5 ||
            parts[0] != "MEM" ||
            !long.TryParse(parts[1], out long flashUsed) ||
            !long.TryParse(parts[2], out long flashTotal) ||
            !long.TryParse(parts[3], out long ramUsed) ||
            !long.TryParse(parts[4], out long ramTotal))
            return null;

        return (flashUsed, flashTotal, ramUsed, ramTotal);
    }

    public async Task<(uint Seq, int ActionId, int Position)?>
        ReadActionEventAsync(uint afterSeq)
    {
        if (!SupportsActions)
            return null;

        string? response =
            await QueryLineAsync($"ACTION|{afterSeq}", 350);

        if (response is null ||
            response.StartsWith(
                "ACTION|NONE|",
                StringComparison.Ordinal))
            return null;

        string[] parts = response.Split('|');
        if (parts.Length != 4 ||
            parts[0] != "ACTION" ||
            !uint.TryParse(parts[1], out uint seq) ||
            !int.TryParse(parts[2], out int actionId) ||
            !int.TryParse(parts[3], out int position))
            return null;

        return (seq, actionId, position);
    }

    public void SetActiveProfile(int profile)
    {
        if (SupportsProfileSwitch)
            FireAndForget($"CFG|PROFILE|{Math.Clamp(profile, 0, 9)}");
    }

    public void SetRgbProfile(
        int index,
        int effect,
        byte r,
        byte g,
        byte b) =>
        FireAndForget(
            $"RGB|PROFILE|{Math.Clamp(index, 0, 9)}|" +
            $"{Math.Clamp(effect, 0, 255)}|{r}|{g}|{b}");

    public void SetEnabled(bool enabled) =>
        FireAndForget($"RGB|EN|{(enabled ? 1 : 0)}");

    public void SetBrightness(int percent) =>
        FireAndForget($"RGB|BRI|{Math.Clamp(percent, 0, 100)}");

    public void SetSpeed(int percent) =>
        FireAndForget($"RGB|SPD|{Math.Clamp(percent, 0, 100)}");

    public void SetAutoLayer() => FireAndForget("RGB|AUTO");
    public void SetEffect(int effect) =>
        FireAndForget($"RGB|FX|{effect}");

    public void SetSolid(byte r, byte g, byte b) =>
        FireAndForget($"RGB|SOLID|{r}|{g}|{b}");

    public async Task InstallFirmwareAsync(
        byte[] image,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!SupportsFirmwareOta)
        {
            throw new NotSupportedException(
                "This PIXEL PRO firmware does not support in-app updates yet.");
        }

        if (image is null || image.Length == 0)
            throw new ArgumentException("Firmware image is empty.", nameof(image));

        string? begin =
            await QueryLineAsync(
                $"FWBEGIN|{image.Length}",
                5000,
                cancellationToken);

        if (begin is null ||
            !begin.StartsWith("FWREADY|", StringComparison.Ordinal))
        {
            throw new IOException(
                $"PIXEL PRO rejected firmware begin: {begin ?? "no response"}");
        }

        const int chunkSize = 180;
        int offset = 0;
        progress?.Report(0);

        try
        {
            while (offset < image.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int length = Math.Min(chunkSize, image.Length - offset);
                string payload =
                    Convert.ToBase64String(image, offset, length);

                int expected = offset + length;
                bool acknowledged = false;

                for (int attempt = 0; attempt < 3 && !acknowledged; attempt++)
                {
                    string? ack =
                        await QueryLineAsync(
                            $"FWCHUNK|{offset}|{payload}",
                            5000,
                            cancellationToken);

                    if (TryParseFirmwarePosition(ack, "FWACK", out int position) &&
                        position >= expected)
                    {
                        acknowledged = true;
                        offset = expected;
                        break;
                    }

                    string? status =
                        await QueryLineAsync(
                            "FWSTAT",
                            2500,
                            cancellationToken);

                    if (TryParseFirmwareStatus(status, out int written, out int total) &&
                        total == image.Length &&
                        written >= expected)
                    {
                        acknowledged = true;
                        offset = expected;
                        break;
                    }
                }

                if (!acknowledged)
                {
                    throw new IOException(
                        $"Firmware transfer stopped near byte {offset}.");
                }

                progress?.Report(
                    (int)Math.Clamp(
                        Math.Round(offset * 100.0 / image.Length),
                        0,
                        99));
            }

            string? done =
                await QueryLineAsync(
                    "FWEND",
                    10000,
                    cancellationToken);

            if (done is null ||
                !done.StartsWith("FWDONE|", StringComparison.Ordinal))
            {
                throw new IOException(
                    $"PIXEL PRO did not confirm firmware: {done ?? "no response"}");
            }

            progress?.Report(100);
        }
        catch
        {
            try
            {
                await QueryLineAsync(
                    "FWABORT",
                    1500,
                    CancellationToken.None);
            }
            catch
            {
            }

            throw;
        }
    }

    private static bool TryParseFirmwarePosition(
        string? response,
        string prefix,
        out int position)
    {
        position = 0;

        if (string.IsNullOrWhiteSpace(response))
            return false;

        string[] parts = response.Split('|');
        return parts.Length >= 2 &&
               string.Equals(parts[0], prefix, StringComparison.Ordinal) &&
               int.TryParse(parts[1], out position);
    }

    private static bool TryParseFirmwareStatus(
        string? response,
        out int written,
        out int total)
    {
        written = 0;
        total = 0;

        if (string.IsNullOrWhiteSpace(response))
            return false;

        string[] parts = response.Split('|');
        return parts.Length == 4 &&
               string.Equals(parts[0], "FWSTAT", StringComparison.Ordinal) &&
               parts[1] == "1" &&
               int.TryParse(parts[2], out written) &&
               int.TryParse(parts[3], out total);
    }

    public Task RestartKeyboardAsync() =>
        SendLineAsync("SYS|RESTART");

    public Task EnterDfuAsync() =>
        SendLineAsync("SYS|DFU");

    public Task SleepKeyboardAsync() =>
        SendLineAsync("SYS|SLEEP");

    public Task WakeKeyboardAsync() =>
        SendLineAsync("SYS|WAKE");

    public Task<bool> SendPcMonitorConfigAsync(
        string name,
        IReadOnlyList<int>? metricSlots = null)
    {
        if (!SupportsPcMonitor)
            return Task.FromResult(false);

        string safe = new string(
            (name ?? "MY PC")
                .Where(c => c >= ' ' && c <= '~' && c != '|')
                .Take(18)
                .ToArray());

        if (string.IsNullOrWhiteSpace(safe))
            safe = "MY PC";

        int[] slots =
            metricSlots is { Count: 6 }
                ? metricSlots.Select(v => Math.Clamp(v, 0, 11)).ToArray()
                : [0, 3, 6, 9, 10, 11];

        return SendBoolLineAsync(
            $"PCCFG|{safe}|{string.Join("|", slots)}");
    }

    public Task<bool> SendPcMonitorAsync(
        PcMonitorSnapshot data,
        IReadOnlyList<int>? metricSlots = null)
    {
        if (!SupportsPcMonitor)
            return Task.FromResult(false);

        static int I(double value) => (int)Math.Round(value);
        static int N(double? value) =>
            value.HasValue ? (int)Math.Round(value.Value) : -1;

        int usedMb =
            (int)Math.Clamp(
                Math.Round(data.MemoryUsedGb * 1024d),
                0,
                int.MaxValue);
        int totalMb =
            (int)Math.Clamp(
                Math.Round(data.MemoryTotalGb * 1024d),
                0,
                int.MaxValue);
        int downKbps =
            (int)Math.Clamp(
                Math.Round(data.NetworkDownloadMbps * 1000d),
                0,
                int.MaxValue);
        int upKbps =
            (int)Math.Clamp(
                Math.Round(data.NetworkUploadMbps * 1000d),
                0,
                int.MaxValue);

        int[] slots =
            metricSlots is { Count: 6 }
                ? metricSlots.Select(v => Math.Clamp(v, 0, 11)).ToArray()
                : [0, 3, 6, 9, 10, 11];

        string line =
            $"PCMON|{I(data.CpuLoad)}|{N(data.CpuTemperature)}|" +
            $"{N(data.CpuClockMHz)}|{N(data.GpuLoad)}|" +
            $"{N(data.GpuTemperature)}|{N(data.GpuClockMHz)}|" +
            $"{I(data.MemoryLoad)}|{usedMb}|{totalMb}|" +
            $"{downKbps}|{upKbps}|{data.Fps ?? -1}|" +
            string.Join("|", slots);

        return SendBoolLineAsync(line);
    }

    public Task<bool> ClearPcMonitorAsync() =>
        SendBoolLineAsync("PCMONCLR");

    private async Task<bool> SendBoolLineAsync(string line)
    {
        try
        {
            await SendLineAsync(line);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        Disconnect();
        _ioGate.Dispose();
    }
}
