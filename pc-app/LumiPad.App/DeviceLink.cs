using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace LumiPad.App;

/// <summary>
/// Physical connection lifecycle. A future product can implement this with
/// USB CDC/BLE, QMK Raw HID, WinUSB, or another transport without changing
/// the application UI.
/// </summary>
public interface IDeviceTransport : IDisposable
{
    bool IsConnected { get; }
    bool IsUsbConnected { get; }
    bool IsBluetoothConnected { get; }
    string ConnectionName { get; }

    event Action<string>? LinkError;
    event Action<string, string>? Diagnostic;

    Task<string?> AutoDetectAsync(CancellationToken cancellationToken = default);
    Task<string?> ConnectUsbAsync(CancellationToken cancellationToken = default);
    Task<string?> ConnectBluetoothAsync(CancellationToken cancellationToken = default);
    Task<string?> PromoteToUsbIfAvailableAsync(
        CancellationToken cancellationToken = default);
    void Disconnect();
}

/// <summary>
/// Product-level commands exposed to the Lumi Macropad UI. Firmware-specific
/// packet formats stay behind this contract.
/// </summary>
public interface IDeviceProtocol
{
    string FirmwareHello { get; }
    int ProtocolVersion { get; }

    bool SupportsDiagnostics { get; }
    bool SupportsMemoryInfo { get; }
    bool SupportsPanelInfo { get; }
    bool SupportsSaverState { get; }
    bool SupportsProfileSwitch { get; }
    bool SupportsActions { get; }
    bool SupportsVariableArtwork { get; }
    bool SupportsBatteryInfo { get; }
    bool SupportsPcMonitor { get; }

    void SendNowPlaying(NowPlayingData data);
    void ClearNowPlaying();

    Task<bool> SendScreensaverAnimationAsync(
        ScreensaverAnimation animation,
        IProgress<int>? progress = null);
    void ClearScreensaverAnimation();
    Task<string?> GetScreensaverStateAsync();
    Task ShowScreensaverNowAsync(bool pcMonitor);
    Task SetScreensaverSourceAsync(bool pcMonitor);
    void SetScreensaverSource(bool pcMonitor);
    void SetScreensaverDelay(int seconds);
    void SetSleepTimeout(int seconds);
    void SetRgbIdleTimeout(int seconds);
    void SetDeepSleepTimeout(int seconds);

    Task<(uint Seq, string Level, string Message)?> ReadFirmwareLogAsync(
        uint afterSeq);
    Task<int?> ReadBatteryPercentAsync();
    Task<(string Panel, int RefreshHz, int SpiHz, int GifMaxFps)?>
        ReadPanelInfoAsync();
    Task<(long FlashUsed, long FlashTotal, long RamUsed, long RamTotal)?>
        ReadMemoryUsageAsync();
    Task<(uint Seq, int ActionId, int Position)?>
        ReadActionEventAsync(uint afterSeq);

    void SetActiveProfile(int profile);

    void SetRgbProfile(int index, int effect, byte r, byte g, byte b);
    void SetEnabled(bool enabled);
    void SetBrightness(int percent);
    void SetSpeed(int percent);
    void SetAutoLayer();
    void SetEffect(int effect);
    void SetSolid(byte r, byte g, byte b);

    Task RestartKeyboardAsync();
    Task EnterDfuAsync();
    Task SleepKeyboardAsync();
    Task WakeKeyboardAsync();

    Task<bool> SendPcMonitorConfigAsync(
        string name,
        IReadOnlyList<int>? metricSlots = null);
    Task<bool> SendPcMonitorAsync(
        PcMonitorSnapshot data,
        IReadOnlyList<int>? metricSlots = null);
    Task<bool> ClearPcMonitorAsync();
}

public interface IDeviceLink : IDeviceTransport, IDeviceProtocol
{
}

/// <summary>
/// Single point where a product selects its firmware/transport driver.
/// Adding a QMK product no longer requires changing MainWindow.
/// </summary>
public static class DeviceLinkFactory
{
    public static IDeviceLink Create(ProductDefinition product) =>
        product.Driver switch
        {
            DeviceDriverKind.LumiZmk => new SerialLink(),
            DeviceDriverKind.QmkRawHid => new QmkRawHidLink(product),
            DeviceDriverKind.Esp32Companion => throw new NotSupportedException(
                $"ESP32 companion driver is not registered for {product.ProductCode}."),
            _ => throw new ArgumentOutOfRangeException(
                nameof(product),
                product.Driver,
                "Unknown device driver.")
        };
}
