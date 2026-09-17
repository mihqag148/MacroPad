using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;
using MediaColor = System.Windows.Media.Color;

namespace LumiPad.App;

public partial class MainWindow : Window
{
    private readonly SerialLink _serial = new();
    private readonly NowPlayingService _nowPlaying = new();

    private bool _uiReady;
    private byte _r = 255;
    private byte _g = 120;
    private byte _b = 0;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            _uiReady = true;

            _nowPlaying.Updated += data =>
                Dispatcher.Invoke(() => ApplyNowPlaying(data));

            _nowPlaying.Cleared += () =>
                Dispatcher.Invoke(ClearNowPlaying);

            try
            {
                await _nowPlaying.StartAsync();
            }
            catch (Exception ex)
            {
                BottomStatus.Text = $"Now Playing unavailable: {ex.Message}";
            }

            await DetectAsync();
        };

        Closed += (_, _) =>
        {
            _nowPlaying.Dispose();
            _serial.Dispose();
        };
    }

    private void ApplyNowPlaying(NowPlayingData data)
    {
        TitleText.Text = data.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(data.Artist) ? "Unknown Artist" : data.Artist;

        var duration = Math.Max(0.001, data.Duration.TotalMilliseconds);
        TrackProgress.Value = Math.Clamp(data.Position.TotalMilliseconds / duration, 0, 1);

        ElapsedText.Text = FormatTime(data.Position);
        DurationText.Text = FormatTime(data.Duration);
        PlayButton.Content = data.IsPlaying ? "❚❚" : "▶";

        _serial.SendNowPlaying(data);
    }

    private void ClearNowPlaying()
    {
        TitleText.Text = "Nothing Playing";
        ArtistText.Text = "Lumi MacroPad";
        TrackProgress.Value = 0;
        ElapsedText.Text = "0:00";
        DurationText.Text = "0:00";
        PlayButton.Content = "▶";
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        DetectButton.IsEnabled = false;
        DeviceStatus.Text = "Detecting…";
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
        BottomStatus.Text = "Searching USB COM ports for LumiPad…";

        var port = await _serial.AutoDetectAsync();

        if (port is null)
        {
            DeviceStatus.Text = "Not connected";
            DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
            BottomStatus.Text = "LumiPad port not found. Flash the newest firmware and reconnect USB.";
        }
        else
        {
            DeviceStatus.Text = $"Connected · {port}";
            DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(48, 209, 88));
            BottomStatus.Text = "Connected. Now Playing and RGB controls are live.";
            SendAllRgb();
        }

        DetectButton.IsEnabled = true;
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _serial.Disconnect();
        DeviceStatus.Text = "Not connected";
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
        BottomStatus.Text = "Disconnected.";
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.PreviousAsync();

    private async void PlayButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.TogglePlayPauseAsync();

    private async void NextButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.NextAsync();

    private void LedEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _serial.SetEnabled(LedEnabled.IsChecked == true);
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is null) return;

        int value = (int)Math.Round(e.NewValue);
        BrightnessText.Text = $"{value}%";

        if (_uiReady)
            _serial.SetBrightness(value);
    }

    private void EffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_uiReady || EffectCombo.SelectedItem is not ComboBoxItem item)
            return;

        if (!int.TryParse(item.Tag?.ToString(), out int effect))
            return;

        if (effect < 0)
        {
            _serial.SetAutoLayer();
        }
        else if (effect == 3)
        {
            _serial.SetSolid(_r, _g, _b);
        }
        else
        {
            _serial.SetEffect(effect);
        }
    }

    private void ChooseColor_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(_r, _g, _b)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;

        _r = dialog.Color.R;
        _g = dialog.Color.G;
        _b = dialog.Color.B;

        ColorPreview.Background = new SolidColorBrush(MediaColor.FromRgb(_r, _g, _b));

        EffectCombo.SelectedIndex = 4;
        _serial.SetSolid(_r, _g, _b);
    }

    private void SendAllRgb()
    {
        _serial.SetEnabled(LedEnabled.IsChecked == true);
        _serial.SetBrightness((int)Math.Round(BrightnessSlider.Value));

        if (EffectCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int effect))
        {
            if (effect < 0)
                _serial.SetAutoLayer();
            else if (effect == 3)
                _serial.SetSolid(_r, _g, _b);
            else
                _serial.SetEffect(effect);
        }
    }
}
