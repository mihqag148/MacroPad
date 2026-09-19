using System.IO;
using System.IO.Ports;
using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace LumiPad.App;

public sealed class SerialLink : IDisposable
{
    private static readonly Guid ServiceUuid = Guid.Parse("D8A90001-6B5A-4C3B-9F2A-7C4E4C554D49");
    private static readonly Guid CharacteristicUuid = Guid.Parse("D8A90002-6B5A-4C3B-9F2A-7C4E4C554D49");

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly SemaphoreSlim _mediaGate = new(1, 1);

    private SerialPort? _port;
    private BluetoothLEDevice? _bleDevice;
    private GattDeviceService? _bleService;
    private GattCharacteristic? _bleCharacteristic;

    private string _connectionName = "";
    private string _lastBitmapKey = "";
    private string _lastArtworkKey = "";
    private string _lastNowPlayingKey = "";
    private bool _lastNowPlayingPlaying;
    private DateTimeOffset _lastNowPlayingSent = DateTimeOffset.MinValue;

    public bool IsConnected => _bleCharacteristic is not null || _port?.IsOpen == true;
    public string ConnectionName => _connectionName;
    public event Action<string>? LinkError;
    public event Action<string, string>? Diagnostic;
    public string FirmwareHello { get; private set; } = "";

    private void Log(string level, string message) =>
        Diagnostic?.Invoke(level, message);

    public async Task<string?> AutoDetectAsync(CancellationToken cancellationToken = default)
    {
        Log("INFO", "Auto detect started");
        Disconnect();

        // Prefer the dedicated USB CDC link when the keyboard is physically
        // connected. It is much faster for media/screensaver transfers.
        var usb = await TryUsbAsync(cancellationToken);
        if (usb is not null)
            return usb;

        return await TryBluetoothAsync(cancellationToken);
    }

    public async Task<string?> ConnectUsbAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        return await TryUsbAsync(cancellationToken);
    }

    public async Task<string?> ConnectBluetoothAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();
        return await TryBluetoothAsync(cancellationToken);
    }

    private async Task<string?> TryBluetoothAsync(CancellationToken cancellationToken)
    {
        // Windows can expose a BLE HID keyboard through different device
        // interfaces depending on whether it is already connected, merely
        // paired, or cached. Probe paired devices first, then the complete BLE
        // device set. This fixes cases where the keyboard works as HID but the
        // companion app cannot see the custom GATT service.
        string[] selectors =
        [
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelector()
        ];

        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string selector in selectors)
        {
            try
            {
                DeviceInformationCollection devices =
                    await DeviceInformation.FindAllAsync(selector);

                Log("INFO", $"BLE scan returned {devices.Count} device(s)");

                foreach (var info in devices)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!seenIds.Add(info.Id))
                        continue;

                    BluetoothLEDevice? candidate = null;
                    GattDeviceService? service = null;

                    try
                    {
                        candidate = await BluetoothLEDevice.FromIdAsync(info.Id);
                        if (candidate is null)
                            continue;

                        var services = await candidate.GetGattServicesForUuidAsync(
                            ServiceUuid,
                            BluetoothCacheMode.Uncached);

                        if (services.Status != GattCommunicationStatus.Success ||
                            services.Services.Count == 0)
                        {
                            candidate.Dispose();
                            continue;
                        }

                        service = services.Services[0];
                        var chars = await service.GetCharacteristicsForUuidAsync(
                            CharacteristicUuid,
                            BluetoothCacheMode.Uncached);

                        if (chars.Status != GattCommunicationStatus.Success ||
                            chars.Characteristics.Count == 0)
                        {
                            service.Dispose();
                            candidate.Dispose();
                            continue;
                        }

                        var characteristic = chars.Characteristics[0];
                        var props = characteristic.CharacteristicProperties;
                        bool canWrite =
                            (props & GattCharacteristicProperties.Write) != 0 ||
                            (props & GattCharacteristicProperties.WriteWithoutResponse) != 0;

                        if (!canWrite)
                        {
                            Log("WARN", $"BLE characteristic is not writable: {info.Name}");
                            service.Dispose();
                            candidate.Dispose();
                            continue;
                        }

                        // Do not reject a valid LumiPad service just because a
                        // one-shot status read is temporarily unavailable. The
                        // service + characteristic UUID pair uniquely identifies
                        // this firmware. Read the hello/status opportunistically.
                        string hello = "LUMIPAD|2";
                        if ((props & GattCharacteristicProperties.Read) != 0)
                        {
                            try
                            {
                                var read = await characteristic.ReadValueAsync(
                                    BluetoothCacheMode.Uncached);

                                if (read.Status == GattCommunicationStatus.Success)
                                {
                                    using var reader = DataReader.FromBuffer(read.Value);
                                    string value =
                                        reader.ReadString(reader.UnconsumedBufferLength)
                                              .Trim('\0', '\r', '\n', ' ');

                                    if (!string.IsNullOrWhiteSpace(value))
                                        hello = value;
                                }
                                else
                                {
                                    Log("WARN", $"BLE status read: {read.Status}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Log("WARN", $"BLE status read failed: {ex.Message}");
                            }
                        }

                        try
                        {
                            service.Session.MaintainConnection = true;
                        }
                        catch
                        {
                            // Older Windows builds can reject MaintainConnection;
                            // normal GATT operations still keep the link usable.
                        }

                        _bleDevice = candidate;
                        _bleService = service;
                        _bleCharacteristic = characteristic;
                        candidate.ConnectionStatusChanged += OnBleConnectionStatusChanged;

                        FirmwareHello = hello;
                        _connectionName =
                            $"Bluetooth · {(!string.IsNullOrWhiteSpace(info.Name) ? info.Name : candidate.Name)}";

                        if (string.IsNullOrWhiteSpace(_connectionName.TrimEnd()))
                            _connectionName = "Bluetooth · Lumi MacroPad";

                        Log("INFO", $"Connected {_connectionName}; {FirmwareHello}");
                        return _connectionName;
                    }
                    catch (Exception ex)
                    {
                        Log("WARN", $"BLE probe {info.Name}: {ex.Message}");
                        service?.Dispose();
                        candidate?.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Log("WARN", $"Bluetooth selector failed: {ex.Message}");
            }
        }

        Log("WARN", "Bluetooth LumiPad service not found");
        return null;
    }

    private void OnBleConnectionStatusChanged(
        BluetoothLEDevice sender,
        object args)
    {
        if (sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
            return;

        Log("WARN", "Bluetooth disconnected");
        LinkError?.Invoke("Bluetooth disconnected");
        Disconnect();
    }

    private async Task<string?> TryUsbAsync(CancellationToken cancellationToken)
    {
        foreach (var name in SerialPort.GetPortNames().OrderBy(x => x))
        {
            cancellationToken.ThrowIfCancellationRequested();

            SerialPort? candidate = null;
            try
            {
                candidate = new SerialPort(name, 115200)
                {
                    ReadTimeout = 300,
                    WriteTimeout = 300,
                    DtrEnable = true,
                    NewLine = "\n"
                };

                candidate.Open();
                await Task.Delay(120, cancellationToken);

                candidate.DiscardInBuffer();
                candidate.WriteLine("HELLO");

                var response = await Task.Run(() =>
                {
                    try { return candidate.ReadLine().Trim(); }
                    catch { return ""; }
                }, cancellationToken);

                if (response.StartsWith("LUMIPAD|", StringComparison.Ordinal))
                {
                    _port = candidate;
                    FirmwareHello = response;
                    _connectionName = $"USB · {name}";
                    Log("INFO", $"Connected {_connectionName}; {FirmwareHello}");
                    return _connectionName;
                }
            }
            catch (Exception ex)
            {
                Log("WARN", $"USB probe {name}: {ex.Message}");
            }

            candidate?.Dispose();
        }

        Log("WARN", "USB LumiPad CDC not found");
        return null;
    }

    private async Task<bool> EnsureUsbForBulkAsync(
        CancellationToken cancellationToken = default)
    {
        if (_port?.IsOpen == true)
            return true;

        foreach (var name in SerialPort.GetPortNames().OrderBy(x => x))
        {
            cancellationToken.ThrowIfCancellationRequested();

            SerialPort? candidate = null;
            try
            {
                candidate = new SerialPort(name, 115200)
                {
                    ReadTimeout = 350,
                    WriteTimeout = 1200,
                    DtrEnable = true,
                    NewLine = "\n"
                };

                candidate.Open();
                await Task.Delay(80, cancellationToken);

                candidate.DiscardInBuffer();
                candidate.WriteLine("HELLO");

                string response = await Task.Run(() =>
                {
                    try { return candidate.ReadLine().Trim(); }
                    catch { return ""; }
                }, cancellationToken);

                if (response.StartsWith("LUMIPAD|", StringComparison.Ordinal))
                {
                    _port = candidate;
                    return true;
                }
            }
            catch
            {
            }

            candidate?.Dispose();
        }

        return false;
    }

    private async Task SendUsbLineAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            if (_port?.IsOpen != true)
                throw new IOException("LumiPad USB link is not available.");

            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            _port.Write(data, 0, data.Length);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<string> SendUsbSaverLineAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            if (_port?.IsOpen != true)
                throw new IOException("LumiPad USB link is not available.");

            _port.ReadTimeout = 8000;
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");
            _port.Write(data, 0, data.Length);

            string ack = await Task.Run(() => _port.ReadLine().Trim());
            Log(ack.EndsWith("|ERROR", StringComparison.Ordinal) ? "ERROR" : "FW",
                $"USB <- {ack}");
            if (!ack.StartsWith("SAVACK|", StringComparison.Ordinal))
                throw new IOException($"Unexpected LumiPad USB response: {ack}");

            if (ack.EndsWith("|ERROR", StringComparison.Ordinal))
                throw new IOException("LumiPad rejected a screensaver chunk.");

            return ack;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Disconnect()
    {
        if (_port is not null)
        {
            try { _port.Close(); } catch { }
            _port.Dispose();
            _port = null;
        }

        _bleCharacteristic = null;

        if (_bleDevice is not null)
            _bleDevice.ConnectionStatusChanged -= OnBleConnectionStatusChanged;

        _bleService?.Dispose();
        _bleService = null;

        _bleDevice?.Dispose();
        _bleDevice = null;

        if (!string.IsNullOrWhiteSpace(_connectionName))
            Log("INFO", $"Disconnected {_connectionName}");

        _connectionName = "";
        FirmwareHello = "";
        _lastBitmapKey = "";
        _lastArtworkKey = "";
        _lastNowPlayingKey = "";
        _lastNowPlayingSent = DateTimeOffset.MinValue;
    }

    public void SendNowPlaying(NowPlayingData data)
    {
        if (!IsConnected)
            return;

        string key = $"{data.SourceName}\u001F{data.Title}\u001F{data.Artist}";
        bool important =
            !string.Equals(key, _lastNowPlayingKey, StringComparison.Ordinal) ||
            data.IsPlaying != _lastNowPlayingPlaying;

        if (!important &&
            DateTimeOffset.UtcNow - _lastNowPlayingSent <
                TimeSpan.FromMilliseconds(1600))
        {
            return;
        }

        // Do not build a queue of stale media updates. If a previous BLE
        // transmission is still running, keep the newest state for the next poll.
        if (!_mediaGate.Wait(0))
            return;

        _ = SendNowPlayingAsync(data, key);
    }

    private async Task SendNowPlayingAsync(NowPlayingData data, string key)
    {
        try
        {
            var source = Uri.EscapeDataString(data.SourceName ?? "MUSIC");
            var title = Uri.EscapeDataString(data.Title ?? "");
            var artist = Uri.EscapeDataString(data.Artist ?? "");

            await SendLineAsync(
                $"NP|{Math.Max(0, (long)data.Position.TotalMilliseconds)}|" +
                $"{Math.Max(0, (long)data.Duration.TotalMilliseconds)}|" +
                $"{(data.IsPlaying ? 1 : 0)}|{source}|{title}|{artist}");

            _lastNowPlayingKey = key;
            _lastNowPlayingPlaying = data.IsPlaying;
            _lastNowPlayingSent = DateTimeOffset.UtcNow;

            string bitmapKey = $"{data.Title}\u001F{data.Artist}";
            if (!string.Equals(bitmapKey, _lastBitmapKey, StringComparison.Ordinal))
            {
                _lastBitmapKey = bitmapKey;
                await SendUnicodeBitmapsAsync(data.Title ?? "", data.Artist ?? "");
            }

            if (data.ArtworkRgb332 is { Length: 5776 } artwork &&
                !string.Equals(bitmapKey, _lastArtworkKey, StringComparison.Ordinal))
            {
                _lastArtworkKey = bitmapKey;
                await SendArtworkAsync(artwork);
            }
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    private async Task SendUnicodeBitmapsAsync(string title, string artist)
    {
        try
        {
            var titleBitmap = TextBitmapRenderer.RenderScrollable(
                title, 206, 480, 24, 16, true);
            var artistBitmap = TextBitmapRenderer.RenderScrollable(
                artist, 206, 360, 20, 13, false);

            await SendTextBitmapAsync("T", titleBitmap, 24);
            await SendTextBitmapAsync("A", artistBitmap, 20);

            Log("INFO",
                $"Now Playing text sent: title={titleBitmap.Width}px, artist={artistBitmap.Width}px");
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Now Playing text transfer failed: {ex.Message}");
        }
    }

    private async Task SendTextBitmapAsync(
        string kind,
        RenderedTextBitmap bitmap,
        int height)
    {
        byte[] packed = Convert.FromHexString(bitmap.Hex);
        const int rawChunk = 200;

        await SendLineAsync(
            $"TXTBEGIN|{kind}|{bitmap.Width}|{height}|{packed.Length}");

        for (int offset = 0; offset < packed.Length; offset += rawChunk)
        {
            int len = Math.Min(rawChunk, packed.Length - offset);
            string hex = Convert.ToHexString(packed, offset, len);
            await SendLineAsync($"TXTCHUNK|{kind}|{offset}|{hex}");
        }

        await SendLineAsync($"TXTEND|{kind}");
    }

    private async Task SendArtworkAsync(byte[] artwork)
    {
        try
        {
            const int rawChunk = 180;
            await SendLineAsync($"ARTBEGIN|{artwork.Length}");

            for (int offset = 0; offset < artwork.Length; offset += rawChunk)
            {
                int len = Math.Min(rawChunk, artwork.Length - offset);
                string base64 = Convert.ToBase64String(artwork, offset, len);
                await SendLineAsync($"ARTCHUNK|{offset}|{base64}");
            }

            await SendLineAsync("ARTEND");
            Log("INFO", $"Now Playing artwork sent: {artwork.Length} bytes");
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Now Playing artwork transfer failed: {ex.Message}");
        }
    }

    public async Task<bool> SendScreensaverAnimationAsync(
        ScreensaverAnimation animation,
        IProgress<int>? progress = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("LumiPad is not connected.");

        await _mediaGate.WaitAsync();
        try
        {

        if (animation.Frames.Count < 1 ||
            animation.Frames.Count > ScreensaverMediaService.MaxFrames)
        {
            throw new InvalidOperationException("Invalid screensaver frame count.");
        }

        // Even when the app is currently linked over Bluetooth, probe the
        // dedicated CDC port before a large upload. USB wins automatically
        // when present; Bluetooth remains the fallback.
        bool useUsb = await EnsureUsbForBulkAsync();
        int loopMs = animation.FrameDurationsMs.Sum();
        Log("INFO", $"Screensaver upload: {animation.Frames.Count} frames, loop={loopMs} ms, avg={animation.FrameIntervalMs} ms, transport={(useUsb ? "USB" : "BLE")}");

        int rawChunkSize = useUsb ? 240 : 180;
        int frameBytes =
            ScreensaverMediaService.Width * ScreensaverMediaService.Height;
        int chunksPerFrame =
            (frameBytes + rawChunkSize - 1) / rawChunkSize;
        int totalChunks = chunksPerFrame * animation.Frames.Count;
        int sentChunks = 0;

        string timingCsv =
            animation.FrameDurationsMs.Count == animation.Frames.Count
                ? string.Join(",", animation.FrameDurationsMs.Select(
                    ms => Math.Clamp(
                        ms,
                        ScreensaverMediaService.MinFrameIntervalMs,
                        5000)))
                : string.Empty;

        string begin =
            $"SAVBEGIN|{animation.Frames.Count}|{animation.FrameIntervalMs}" +
            (timingCsv.Length > 0 ? $"|{timingCsv}" : string.Empty);

        if (useUsb)
            await SendUsbSaverLineAsync(begin);
        else
            await SendLineAsync(begin);

        for (int i = 0; i < animation.Frames.Count; i++)
        {
            byte[] frame = animation.Frames[i];

            if (frame.Length != frameBytes)
                throw new InvalidOperationException("Invalid screensaver frame size.");

            for (int offset = 0; offset < frame.Length; offset += rawChunkSize)
            {
                int len = Math.Min(rawChunkSize, frame.Length - offset);
                string base64 =
                    Convert.ToBase64String(frame, offset, len);

                string line = $"SAVCHUNK|{i}|{offset}|{base64}";

                if (useUsb)
                    await SendUsbSaverLineAsync(line);
                else
                    await SendBulkLineAsync(line);

                if (!useUsb)
                    await Task.Delay(2);

                sentChunks++;
                if ((offset + len) >= frame.Length)
                    Log("INFO", $"Screensaver frame {i + 1}/{animation.Frames.Count} sent");
                progress?.Report(
                    (int)Math.Round(sentChunks * 100.0 / totalChunks));
            }
        }

        if (useUsb)
        {
            string finalAck = await SendUsbSaverLineAsync("SAVEND");
            if (!finalAck.EndsWith("|READY", StringComparison.Ordinal)) {
                Log("ERROR", $"Screensaver final ACK not READY: {finalAck}");
                return false;
            }
        }
        else
        {
            await SendLineAsync("SAVEND");
        }

        await Task.Delay(useUsb ? 20 : 120);

        if (!useUsb && _bleCharacteristic is not null)
        {
            string status = await ReadBleStatusAsync();
            bool ready = status.Contains("SAVER:READY", StringComparison.Ordinal);
            Log(ready ? "INFO" : "ERROR", $"BLE saver verify: {status}");
            return ready;
        }

        // USB serial writes are lossless and the firmware only commits the
        // flash header after SAVEND.
        return true;
        }
        finally
        {
            _mediaGate.Release();
        }
    }

    private async Task<string> ReadBleStatusAsync()
    {
        var characteristic = _bleCharacteristic;
        if (characteristic is null)
            return "";

        var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (read.Status != GattCommunicationStatus.Success)
            return "";

        using var reader = DataReader.FromBuffer(read.Value);
        return reader.ReadString(reader.UnconsumedBufferLength);
    }

    public async Task<(uint Seq, string Level, string Message)?> ReadFirmwareLogAsync(
        uint afterSeq)
    {
        string response;

        try
        {
            if (_port?.IsOpen == true)
            {
                await _writeGate.WaitAsync();
                try
                {
                    _port.ReadTimeout = 900;
                    _port.DiscardInBuffer();
                    byte[] data = Encoding.UTF8.GetBytes($"LOG|{afterSeq}\n");
                    _port.Write(data, 0, data.Length);
                    response = await Task.Run(() => _port.ReadLine().Trim());
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            else if (_bleCharacteristic is not null)
            {
                await SendLineAsync($"LOG|{afterSeq}");
                await Task.Delay(35);
                response = await ReadBleStatusAsync();
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Read firmware log failed: {ex.Message}");
            return null;
        }

        if (response.StartsWith("LOG|NONE|", StringComparison.Ordinal))
            return null;

        string[] parts = response.Split('|', 4);
        if (parts.Length != 4 ||
            parts[0] != "LOG" ||
            !uint.TryParse(parts[1], out uint seq))
        {
            return null;
        }

        return (seq, parts[2], parts[3]);
    }

    public async Task<bool> IsScreensaverReadyAsync()
    {
        if (_bleCharacteristic is null)
            return false;

        string status = await ReadBleStatusAsync();
        return status.Contains("SAVER:READY", StringComparison.Ordinal);
    }

    public void ShowScreensaverNow() =>
        _ = SendLineAsync("CFG|SAVERNOW");

    public async Task<(string Panel, int RefreshHz, int SpiHz, int GifMaxFps)?>
        ReadPanelInfoAsync()
    {
        string response;

        try
        {
            if (_port?.IsOpen == true)
            {
                await _writeGate.WaitAsync();
                try
                {
                    _port.ReadTimeout = 800;
                    _port.DiscardInBuffer();
                    byte[] data = Encoding.UTF8.GetBytes("PANEL\n");
                    _port.Write(data, 0, data.Length);
                    response = await Task.Run(() => _port.ReadLine().Trim());
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            else if (_bleCharacteristic is not null)
            {
                await SendLineAsync("PANEL");
                await Task.Delay(40);
                response = await ReadBleStatusAsync();
            }
            else
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            Log("WARN", $"Read panel info failed: {ex.Message}");
            return null;
        }

        string[] parts = response.Split('|');
        if (parts.Length != 5 ||
            parts[0] != "PANEL" ||
            !int.TryParse(parts[2], out int refreshHz) ||
            !int.TryParse(parts[3], out int spiHz) ||
            !int.TryParse(parts[4], out int gifMaxFps))
        {
            return null;
        }

        return (parts[1], refreshHz, spiHz, gifMaxFps);
    }

    public async Task<(long FlashUsed, long FlashTotal, long RamUsed, long RamTotal)?>
        ReadMemoryUsageAsync()
    {
        string response;

        if (_port?.IsOpen == true)
        {
            await _writeGate.WaitAsync();
            try
            {
                _port.ReadTimeout = 800;
                _port.DiscardInBuffer();
                byte[] data = Encoding.UTF8.GetBytes("MEM\n");
                _port.Write(data, 0, data.Length);
                response = await Task.Run(() => _port.ReadLine().Trim());
            }
            catch
            {
                return null;
            }
            finally
            {
                _writeGate.Release();
            }
        }
        else if (_bleCharacteristic is not null)
        {
            try
            {
                await SendLineAsync("MEM");
                await Task.Delay(35);
                response = await ReadBleStatusAsync();
            }
            catch
            {
                return null;
            }
        }
        else
        {
            return null;
        }

        string[] parts = response.Split('|');
        if (parts.Length != 5 ||
            !string.Equals(parts[0], "MEM", StringComparison.Ordinal) ||
            !long.TryParse(parts[1], out long flashUsed) ||
            !long.TryParse(parts[2], out long flashTotal) ||
            !long.TryParse(parts[3], out long ramUsed) ||
            !long.TryParse(parts[4], out long ramTotal) ||
            flashTotal <= 0 ||
            ramTotal <= 0)
        {
            return null;
        }

        return (flashUsed, flashTotal, ramUsed, ramTotal);
    }

    public void ClearScreensaverAnimation() =>
        _ = SendLineAsync("SAVCLEAR");

    public void ClearNowPlaying()
    {
        _lastBitmapKey = "";
        _lastArtworkKey = "";
        _ = SendLineAsync("CLEAR");
    }

    public void SetRgbState(bool enabled, int brightness, int speed,
                            bool autoByLayer, int effect,
                            byte r, byte g, byte b) =>
        _ = SendLineAsync(
            $"RGB|STATE|{(enabled ? 1 : 0)}|" +
            $"{Math.Clamp(brightness, 5, 50)}|" +
            $"{Math.Clamp(speed, 10, 100)}|" +
            $"{(autoByLayer ? 1 : 0)}|" +
            $"{Math.Clamp(effect, 0, 4)}|{r}|{g}|{b}");

    public void SetRgbProfile(int index, int effect, byte r, byte g, byte b) =>
        _ = SendLineAsync(
            $"RGB|PROFILE|{Math.Clamp(index, 0, 4)}|" +
            $"{Math.Clamp(effect, 0, 4)}|{r}|{g}|{b}");

    public void SetEnabled(bool enabled) => _ = SendLineAsync($"RGB|EN|{(enabled ? 1 : 0)}");
    public void SetBrightness(int percent) => _ = SendLineAsync($"RGB|BRI|{Math.Clamp(percent, 5, 50)}");
    public void SetSpeed(int percent) => _ = SendLineAsync($"RGB|SPD|{Math.Clamp(percent, 10, 100)}");
    public void SetAutoLayer() => _ = SendLineAsync("RGB|AUTO");
    public void SetEffect(int effect) => _ = SendLineAsync($"RGB|FX|{effect}");
    public void SetSolid(byte r, byte g, byte b) => _ = SendLineAsync($"RGB|SOLID|{r}|{g}|{b}");

    public void SetWallpaper(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2) =>
        _ = SendLineAsync($"CFG|WALL|{r1}|{g1}|{b1}|{r2}|{g2}|{b2}");

    public void SetScreensaver(bool enabled, int style, int delaySeconds,
                               byte r1, byte g1, byte b1,
                               byte r2, byte g2, byte b2) =>
        _ = SendLineAsync(
            $"CFG|SAVER|{(enabled ? 1 : 0)}|{style}|{delaySeconds}|" +
            $"{r1}|{g1}|{b1}|{r2}|{g2}|{b2}");

    public void SetScreensaverDelay(int seconds) =>
        _ = SendLineAsync($"CFG|SAVERDELAY|{Math.Max(0, seconds)}");

    public void SetSleepTimeout(int seconds) =>
        _ = SendLineAsync($"CFG|SLEEP|{Math.Max(0, seconds)}");

    public Task RestartKeyboardAsync() =>
        SendLineAsync("SYS|RESTART");

    public Task EnterDfuAsync() =>
        SendLineAsync("SYS|DFU");

    private async Task SendBulkLineAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");

            if (_bleCharacteristic is not null)
            {
                // Screensaver data must be lossless. WriteWithoutResponse can
                // overrun the Windows/BLE transmit queue during a long GIF
                // upload and still report success to the app. Use acknowledged
                // ATT writes so every fragment reaches the nRF52840 in order.
                const int chunkSize = 20;

                for (int offset = 0; offset < data.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, data.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(data.AsSpan(offset, len).ToArray());

                    var status = await _bleCharacteristic.WriteValueAsync(
                        writer.DetachBuffer(),
                        GattWriteOption.WriteWithResponse);

                    if (status != GattCommunicationStatus.Success)
                        throw new IOException($"Bluetooth bulk write failed: {status}");
                }

                return;
            }

            if (_port?.IsOpen == true)
                _port.Write(data, 0, data.Length);
        }
        catch (Exception ex)
        {
            Log("ERROR", ex.Message);
            LinkError?.Invoke(ex.Message);
            Disconnect();
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task SendLineAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            byte[] data = Encoding.UTF8.GetBytes(line + "\n");

            if (_bleCharacteristic is not null)
            {
                var characteristic = _bleCharacteristic;

                // Keep each packet inside the default BLE ATT payload and require
                // an acknowledgement. This is slower than WriteWithoutResponse,
                // but much more reliable on Windows with HID keyboards.
                const int chunkSize = 20;
                for (int offset = 0; offset < data.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, data.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(data.AsSpan(offset, len).ToArray());

                    var status = await characteristic.WriteValueAsync(
                        writer.DetachBuffer(), GattWriteOption.WriteWithResponse);

                    if (status != GattCommunicationStatus.Success)
                        throw new IOException($"Bluetooth write failed: {status}");
                }

                return;
            }

            if (_port?.IsOpen == true)
            {
                _port.Write(data, 0, data.Length);
            }
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Link write failed: {ex.Message}");
            LinkError?.Invoke(ex.Message);
            Disconnect();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        Disconnect();
        _writeGate.Dispose();
        _mediaGate.Dispose();
    }
}
