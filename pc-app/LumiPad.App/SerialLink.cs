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

    public async Task<string?> AutoDetectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();

        var ble = await TryBluetoothAsync(cancellationToken);
        if (ble is not null)
            return ble;

        return await TryUsbAsync(cancellationToken);
    }

    private async Task<string?> TryBluetoothAsync(CancellationToken cancellationToken)
    {
        try
        {
            string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

            foreach (var info in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                BluetoothLEDevice? candidate = null;
                try
                {
                    candidate = await BluetoothLEDevice.FromIdAsync(info.Id);
                    if (candidate is null)
                        continue;

                    var services = await candidate.GetGattServicesForUuidAsync(
                        ServiceUuid, BluetoothCacheMode.Uncached);

                    if (services.Status != GattCommunicationStatus.Success ||
                        services.Services.Count == 0)
                    {
                        candidate.Dispose();
                        continue;
                    }

                    var service = services.Services[0];
                    var chars = await service.GetCharacteristicsForUuidAsync(
                        CharacteristicUuid, BluetoothCacheMode.Uncached);

                    if (chars.Status != GattCommunicationStatus.Success ||
                        chars.Characteristics.Count == 0)
                    {
                        service.Dispose();
                        candidate.Dispose();
                        continue;
                    }

                    var characteristic = chars.Characteristics[0];

                    var read = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (read.Status != GattCommunicationStatus.Success)
                    {
                        service.Dispose();
                        candidate.Dispose();
                        continue;
                    }

                    using var reader = DataReader.FromBuffer(read.Value);
                    string hello = reader.ReadString(reader.UnconsumedBufferLength);
                    if (!hello.StartsWith("LUMIPAD|", StringComparison.Ordinal))
                    {
                        service.Dispose();
                        candidate.Dispose();
                        continue;
                    }

                    _bleDevice = candidate;
                    _bleService = service;
                    _bleCharacteristic = characteristic;

                    _connectionName = $"Bluetooth · {(!string.IsNullOrWhiteSpace(info.Name) ? info.Name : "LumiPad")}";
                    return _connectionName;
                }
                catch
                {
                    candidate?.Dispose();
                }
            }
        }
        catch
        {
        }

        return null;
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
                    _connectionName = $"USB · {name}";
                    return _connectionName;
                }
            }
            catch
            {
            }

            candidate?.Dispose();
        }

        return null;
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

        _bleService?.Dispose();
        _bleService = null;

        _bleDevice?.Dispose();
        _bleDevice = null;

        _connectionName = "";
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

            await SendLineAsync($"TXT|T|{titleBitmap.Width}|24|{titleBitmap.Hex}");
            await SendLineAsync($"TXT|A|{artistBitmap.Width}|20|{artistBitmap.Hex}");
        }
        catch
        {
        }
    }

    private async Task SendArtworkAsync(byte[] artwork)
    {
        try
        {
            string base64 = Convert.ToBase64String(artwork);
            await SendLineAsync($"ART|{base64}");
        }
        catch
        {
        }
    }

    public async Task<bool> SendScreensaverAnimationAsync(
        ScreensaverAnimation animation,
        IProgress<int>? progress = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("LumiPad is not connected.");

        if (animation.Frames.Count < 1 ||
            animation.Frames.Count > ScreensaverMediaService.MaxFrames)
        {
            throw new InvalidOperationException("Invalid screensaver frame count.");
        }

        const int rawChunkSize = 180;
        int frameBytes =
            ScreensaverMediaService.Width * ScreensaverMediaService.Height;
        int chunksPerFrame =
            (frameBytes + rawChunkSize - 1) / rawChunkSize;
        int totalChunks = chunksPerFrame * animation.Frames.Count;
        int sentChunks = 0;

        await SendLineAsync(
            $"SAVBEGIN|{animation.Frames.Count}|{animation.FrameIntervalMs}");

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

                await SendBulkLineAsync(
                    $"SAVCHUNK|{i}|{offset}|{base64}");

                // Briefly yield airtime to the keyboard HID/encoder traffic.
                if (_bleCharacteristic is not null)
                    await Task.Delay(2);

                sentChunks++;
                progress?.Report(
                    (int)Math.Round(sentChunks * 100.0 / totalChunks));
            }
        }

        await SendLineAsync("SAVEND");
        await Task.Delay(120);

        if (_bleCharacteristic is not null)
        {
            string status = await ReadBleStatusAsync();
            return status.Contains("SAVER:READY", StringComparison.Ordinal);
        }

        // USB transport has already acknowledged every write at the serial layer,
        // but the current protocol has no reverse status packet.
        return true;
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

    public void ClearScreensaverAnimation() =>
        _ = SendLineAsync("SAVCLEAR");

    public void ClearNowPlaying()
    {
        _lastBitmapKey = "";
        _lastArtworkKey = "";
        _ = SendLineAsync("CLEAR");
    }

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

            if (_bleCharacteristic is not null &&
                _bleCharacteristic.CharacteristicProperties.HasFlag(
                    GattCharacteristicProperties.WriteWithoutResponse))
            {
                const int packetSize = 20;

                for (int offset = 0; offset < data.Length; offset += packetSize)
                {
                    int len = Math.Min(packetSize, data.Length - offset);
                    using var writer = new DataWriter();
                    writer.WriteBytes(data.AsSpan(offset, len).ToArray());

                    var status = await _bleCharacteristic.WriteValueAsync(
                        writer.DetachBuffer(),
                        GattWriteOption.WriteWithoutResponse);

                    if (status != GattCommunicationStatus.Success)
                        throw new IOException($"Bluetooth bulk write failed: {status}");

                    if ((offset / packetSize & 7) == 7)
                        await Task.Delay(1);
                }

                return;
            }

            if (_bleCharacteristic is not null)
            {
                // Older firmware fallback.
                const int packetSize = 20;
                for (int offset = 0; offset < data.Length; offset += packetSize)
                {
                    int len = Math.Min(packetSize, data.Length - offset);
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
