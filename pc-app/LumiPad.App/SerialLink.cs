using System.IO.Ports;

namespace LumiPad.App;

public sealed class SerialLink : IDisposable
{
    private SerialPort? _port;

    public bool IsConnected => _port?.IsOpen == true;
    public string PortName => _port?.PortName ?? "";

    public async Task<string?> AutoDetectAsync(CancellationToken cancellationToken = default)
    {
        Disconnect();

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
                    return name;
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
        if (_port is null)
            return;

        try { _port.Close(); } catch { }
        _port.Dispose();
        _port = null;
    }

    public void SendNowPlaying(NowPlayingData data)
    {
        if (!IsConnected)
            return;

        var title = Uri.EscapeDataString(data.Title ?? "");
        var artist = Uri.EscapeDataString(data.Artist ?? "");

        SendLine($"NP|{Math.Max(0, (long)data.Position.TotalMilliseconds)}|" +
                 $"{Math.Max(0, (long)data.Duration.TotalMilliseconds)}|" +
                 $"{(data.IsPlaying ? 1 : 0)}|{title}|{artist}");
    }

    public void SetEnabled(bool enabled) => SendLine($"RGB|EN|{(enabled ? 1 : 0)}");
    public void SetBrightness(int percent) => SendLine($"RGB|BRI|{Math.Clamp(percent, 5, 50)}");
    public void SetAutoLayer() => SendLine("RGB|AUTO");
    public void SetEffect(int effect) => SendLine($"RGB|FX|{effect}");
    public void SetSolid(byte r, byte g, byte b) => SendLine($"RGB|SOLID|{r}|{g}|{b}");

    private void SendLine(string line)
    {
        if (_port?.IsOpen != true)
            return;

        try
        {
            _port.Write(line);
            _port.Write("\n");
        }
        catch
        {
            Disconnect();
        }
    }

    public void Dispose() => Disconnect();
}
