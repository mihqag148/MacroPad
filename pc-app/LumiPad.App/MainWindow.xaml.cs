using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MediaColor = System.Windows.Media.Color;

namespace LumiPad.App;

public partial class MainWindow : Window
{
    private readonly SerialLink _serial = new();
    private readonly NowPlayingService _nowPlaying = new();

    private bool _uiReady;
    private bool _lightTheme;
    private bool _allowExit;
    private bool _trayTipShown;
    private bool _zmkInitialized;
    private bool _autoReconnectEnabled = true;
    private readonly CancellationTokenSource _reconnectCts = new();

    private byte _r = 255;
    private byte _g = 120;
    private byte _b = 0;
    private ScreensaverAnimation? _screensaverAnimation;
    private string? _screensaverMediaPath;
    private int _rgbEffect = 3;
    private bool _rgbAuto;
    private int _screensaverDelaySeconds = 60;
    private int _sleepDelaySeconds = 120;

    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _appIcon;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();

        Loaded += async (_, _) =>
        {
            _uiReady = true;
            LoadTheme();
            BuildColorWheel();

            _serial.LinkError += message =>
                Dispatcher.Invoke(() =>
                {
                    DeviceStatus.Text = "Bluetooth write error";
                    DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 69, 58));
                    BottomStatus.Text = message;
                });

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
            _ = AutoReconnectLoopAsync(_reconnectCts.Token);
        };

        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void InitializeTrayIcon()
    {
        _appIcon = CreateLogoIcon();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "LumiPad",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open LumiPad", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);

        Icon = Imaging.CreateBitmapSourceFromHIcon(
            _appIcon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(64, 64));
    }

    private static Drawing.Icon CreateLogoIcon()
    {
        using var bitmap = new Drawing.Bitmap(64, 64);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Black);

            using var border = new Drawing.Pen(Drawing.Color.FromArgb(70, 255, 255, 255), 1);
            g.DrawRectangle(border, 2, 2, 59, 59);

            using var font = new Drawing.Font(
                "Arial",
                20,
                Drawing.FontStyle.Bold,
                Drawing.GraphicsUnit.Pixel);
            using var brush = new Drawing.SolidBrush(Drawing.Color.FromArgb(232, 229, 225));

            const string text = "L3D";
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, brush,
                (64 - size.Width) / 2f,
                (64 - size.Height) / 2f - 1f);
        }

        IntPtr handle = bitmap.GetHicon();
        using var temp = Drawing.Icon.FromHandle(handle);
        var icon = (Drawing.Icon)temp.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowExit)
            return;

        e.Cancel = true;
        Hide();

        if (!_trayTipShown && _trayIcon is not null)
        {
            _trayTipShown = true;
            _trayIcon.BalloonTipTitle = "LumiPad is still running";
            _trayIcon.BalloonTipText =
                "Now Playing and Bluetooth control continue in the system tray.";
            _trayIcon.ShowBalloonTip(1800);
        }
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void ExitApplication()
    {
        _allowExit = true;

        _reconnectCts.Cancel();
        _reconnectCts.Dispose();
        _nowPlaying.Dispose();
        _serial.Dispose();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _appIcon?.Dispose();
        _appIcon = null;

        System.Windows.Application.Current.Shutdown();
    }

    private string ThemeFilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "theme.txt");

    private void LoadTheme()
    {
        try
        {
            _lightTheme =
                System.IO.File.Exists(ThemeFilePath) &&
                string.Equals(
                    System.IO.File.ReadAllText(ThemeFilePath).Trim(),
                    "light",
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            _lightTheme = false;
        }

        ApplyTheme();
    }

    private void SaveTheme()
    {
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(ThemeFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                System.IO.Directory.CreateDirectory(folder);

            System.IO.File.WriteAllText(
                ThemeFilePath,
                _lightTheme ? "light" : "dark");
        }
        catch
        {
        }
    }

    private static void SetResourceColor(string key, string hex)
    {
        if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is MediaColor color)
        {
            System.Windows.Application.Current.Resources[key] = new SolidColorBrush(color);
        }
    }

    private void ApplyTheme()
    {
        if (_lightTheme)
        {
            SetResourceColor("Bg", "#F2F2F7");
            SetResourceColor("Card", "#FFFFFF");
            SetResourceColor("Card2", "#F8F8FA");
            SetResourceColor("ControlBg", "#FFFFFF");
            SetResourceColor("TextPrimary", "#111113");
            SetResourceColor("Muted", "#6E6E73");
            SetResourceColor("Line", "#D1D1D6");
            SetResourceColor("Accent", "#FF7A00");
            SetResourceColor("Selection", "#FFE6D0");
            ThemeButton.Content = "Dark mode";
        }
        else
        {
            SetResourceColor("Bg", "#080808");
            SetResourceColor("Card", "#151515");
            SetResourceColor("Card2", "#1D1D1F");
            SetResourceColor("ControlBg", "#27272A");
            SetResourceColor("TextPrimary", "#FFFFFF");
            SetResourceColor("Muted", "#9A9AA0");
            SetResourceColor("Line", "#3A3A3C");
            SetResourceColor("Accent", "#FF7A00");
            SetResourceColor("Selection", "#3A2414");
            ThemeButton.Content = "Light mode";
        }
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        _lightTheme = !_lightTheme;
        ApplyTheme();
        SaveTheme();
    }

    private void ApplyNowPlaying(NowPlayingData data)
    {
        TitleText.Text = data.Title;
        ArtistText.Text = string.IsNullOrWhiteSpace(data.Artist)
            ? "Unknown Artist"
            : data.Artist;

        var duration = Math.Max(0.001, data.Duration.TotalMilliseconds);
        TrackProgress.Value = Math.Clamp(
            data.Position.TotalMilliseconds / duration,
            0,
            1);

        ElapsedText.Text = FormatTime(data.Position);
        DurationText.Text = FormatTime(data.Duration);
        PlayButton.Content = "❚❚";

        if (data.ArtworkRgb332 is { Length: 5776 } artwork)
        {
            AlbumArtImage.Source = CreateArtworkBitmap(artwork);
            AlbumArtImage.Visibility = Visibility.Visible;
            AlbumArtFallback.Visibility = Visibility.Collapsed;
        }
        else
        {
            AlbumArtImage.Source = null;
            AlbumArtImage.Visibility = Visibility.Collapsed;
            AlbumArtFallback.Visibility = Visibility.Visible;
        }

        _serial.SendNowPlaying(data);
    }

    private void ClearNowPlaying()
    {
        TitleText.Text = "Nothing Playing";
        ArtistText.Text = "LumiPad";
        AlbumArtImage.Source = null;
        AlbumArtImage.Visibility = Visibility.Collapsed;
        AlbumArtFallback.Visibility = Visibility.Visible;
        TrackProgress.Value = 0;
        ElapsedText.Text = "0:00";
        DurationText.Text = "0:00";
        PlayButton.Content = "▶";

        _serial.ClearNowPlaying();
    }

    private static BitmapSource CreateArtworkBitmap(byte[] rgb332) =>
        CreateRgb332Bitmap(rgb332, 76, 76);

    private static BitmapSource CreateRgb332Bitmap(
        byte[] rgb332,
        int width,
        int height)
    {
        if (rgb332.Length != width * height)
            throw new ArgumentException("RGB332 buffer size does not match dimensions.");

        int stride = width * 4;
        byte[] bgra = new byte[stride * height];

        for (int i = 0; i < width * height; i++)
        {
            byte v = rgb332[i];
            byte r = (byte)((((v >> 5) & 0x07) * 255) / 7);
            byte g = (byte)((((v >> 2) & 0x07) * 255) / 7);
            byte b = (byte)(((v & 0x03) * 255) / 3);

            int p = i * 4;
            bgra[p] = b;
            bgra[p + 1] = g;
            bgra[p + 2] = r;
            bgra[p + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            bgra,
            stride);

        bitmap.Freeze();
        return bitmap;
    }


    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        _autoReconnectEnabled = true;
        await DetectAsync();
    }

    private async Task DetectAsync()
    {
        DetectButton.IsEnabled = false;
        DeviceStatus.Text = "Detecting…";
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
        BottomStatus.Text = "Searching Bluetooth first, then USB fallback…";

        var connection = await _serial.AutoDetectAsync();

        if (connection is null)
        {
            DeviceStatus.Text = "Not connected";
            DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
            BottomStatus.Text =
                "LumiPad not found. Pair the keyboard over Bluetooth, or connect USB as fallback.";
        }
        else
        {
            DeviceStatus.Text = connection;
            DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(48, 209, 88));
            BottomStatus.Text = connection.StartsWith("Bluetooth", StringComparison.Ordinal)
                ? "Connected wirelessly. Now Playing and RGB are live."
                : "Connected over USB fallback. Now Playing and RGB are live.";

            SendAllRgb();
            SendPowerTiming();
        }

        DetectButton.IsEnabled = true;
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _autoReconnectEnabled = false;
        _serial.Disconnect();
        DeviceStatus.Text = "Not connected";
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
        BottomStatus.Text = "Disconnected.";
    }

    private async Task AutoReconnectLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, token);

                if (!_autoReconnectEnabled || _serial.IsConnected)
                    continue;

                var connection = await _serial.AutoDetectAsync(token);
                if (connection is null)
                    continue;

                DeviceStatus.Text = connection;
                DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(48, 209, 88));
                BottomStatus.Text = connection.StartsWith("Bluetooth", StringComparison.Ordinal)
                    ? "Reconnected wirelessly after wake."
                    : "Reconnected over USB fallback.";

                SendAllRgb();
                SendPowerTiming();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private static int ComboSeconds(ComboBox combo, int fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int seconds))
        {
            return Math.Max(0, seconds);
        }

        return fallback;
    }

    private void SendPowerTiming()
    {
        _serial.SetScreensaverDelay(_screensaverDelaySeconds);
        _serial.SetSleepTimeout(_sleepDelaySeconds);
    }

    private void ScreensaverDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _screensaverDelaySeconds =
            ComboSeconds(ScreensaverDelayCombo, _screensaverDelaySeconds);

        if (_uiReady && _serial.IsConnected)
            _serial.SetScreensaverDelay(_screensaverDelaySeconds);
    }

    private void SleepDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _sleepDelaySeconds =
            ComboSeconds(SleepDelayCombo, _sleepDelaySeconds);

        if (_uiReady && _serial.IsConnected)
            _serial.SetSleepTimeout(_sleepDelaySeconds);
    }

    private async void RestartKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text = "Connect LumiPad before restarting the keyboard.";
            return;
        }

        BottomStatus.Text = "Restarting keyboard…";

        try
        {
            await _serial.RestartKeyboardAsync();
            await Task.Delay(150);
            _serial.Disconnect();

            DeviceStatus.Text = "Restarting…";
            DeviceDot.Fill =
                new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
            BottomStatus.Text =
                "Keyboard is restarting. LumiPad will reconnect automatically.";
        }
        catch (Exception ex)
        {
            BottomStatus.Text = $"Restart failed: {ex.Message}";
        }
    }

    private async void KeyboardDfu_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text = "Connect LumiPad before entering DFU.";
            return;
        }

        var result = System.Windows.MessageBox.Show(
            "Put the keyboard into DFU/bootloader mode for firmware flashing?",
            "LumiPad DFU",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        _autoReconnectEnabled = false;
        BottomStatus.Text = "Entering keyboard DFU…";

        try
        {
            await _serial.EnterDfuAsync();
            await Task.Delay(150);
            _serial.Disconnect();

            DeviceStatus.Text = "DFU / Bootloader";
            DeviceDot.Fill =
                new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
            BottomStatus.Text =
                "Keyboard is in DFU. Flash firmware, then press Connect when it boots normally.";
        }
        catch (Exception ex)
        {
            BottomStatus.Text = $"DFU failed: {ex.Message}";
        }
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.PreviousAsync();

    private async void PlayButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.TogglePlayPauseAsync();

    private async void NextButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.NextAsync();

    private void LedEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
            return;

        _serial.SetEnabled(LedEnabled.IsChecked == true);
    }

    private void BrightnessSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (BrightnessText is null)
            return;

        int value = (int)Math.Round(e.NewValue);
        BrightnessText.Text = $"{value}%";

        if (_uiReady)
            _serial.SetBrightness(value);
    }

    private void BuildColorWheel()
    {
        const int width = 220;
        const int height = 185;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            double saturation = 1.0 - y / (double)(height - 1);

            for (int x = 0; x < width; x++)
            {
                double hue = x * 360.0 / (width - 1);
                (byte r, byte g, byte b) = HsvToRgb(hue, saturation, 1.0);

                int p = y * stride + x * 4;
                pixels[p] = b;
                pixels[p + 1] = g;
                pixels[p + 2] = r;
                pixels[p + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96,
            PixelFormats.Bgra32,
            null, pixels, stride);

        bitmap.Freeze();
        ColorWheelImage.Source = bitmap;
        UpdateRgbReadout();
    }

    private void ColorWheelImage_MouseLeftButtonDown(
        object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        var pos = e.GetPosition(ColorWheelImage);

        double width = Math.Max(1.0, ColorWheelImage.ActualWidth);
        double height = Math.Max(1.0, ColorWheelImage.ActualHeight);

        double hue = Math.Clamp(pos.X / width, 0.0, 1.0) * 360.0;
        double saturation = 1.0 - Math.Clamp(pos.Y / height, 0.0, 1.0);

        (_r, _g, _b) = HsvToRgb(hue, saturation, 1.0);
        ApplySelectedRgbColor(true);
    }

    private static (byte r, byte g, byte b) HsvToRgb(
        double hue,
        double saturation,
        double value)
    {
        double c = value * saturation;
        double x = c * (1.0 - Math.Abs((hue / 60.0) % 2.0 - 1.0));
        double m = value - c;

        double r1, g1, b1;

        if (hue < 60)       (r1, g1, b1) = (c, x, 0);
        else if (hue < 120) (r1, g1, b1) = (x, c, 0);
        else if (hue < 180) (r1, g1, b1) = (0, c, x);
        else if (hue < 240) (r1, g1, b1) = (0, x, c);
        else if (hue < 300) (r1, g1, b1) = (x, 0, c);
        else                (r1, g1, b1) = (c, 0, x);

        return (
            (byte)Math.Round((r1 + m) * 255.0),
            (byte)Math.Round((g1 + m) * 255.0),
            (byte)Math.Round((b1 + m) * 255.0));
    }

    private void UpdateRgbReadout()
    {
        if (ColorPreview is not null)
            ColorPreview.Background =
                new SolidColorBrush(MediaColor.FromRgb(_r, _g, _b));

        if (RgbHexText is not null)
            RgbHexText.Text = $"#{_r:X2}{_g:X2}{_b:X2}";

        if (RgbRText is not null) RgbRText.Text = _r.ToString();
        if (RgbGText is not null) RgbGText.Text = _g.ToString();
        if (RgbBText is not null) RgbBText.Text = _b.ToString();
    }

    private void ApplySelectedRgbColor(bool send)
    {
        UpdateRgbReadout();

        if (send && _uiReady)
        {
            _rgbAuto = false;
            _rgbEffect = 3;
            _serial.SetSolid(_r, _g, _b);
        }
    }

    private void RgbSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.Tag is not string hex ||
            System.Windows.Media.ColorConverter.ConvertFromString(hex)
                is not MediaColor color)
            return;

        _r = color.R;
        _g = color.G;
        _b = color.B;
        ApplySelectedRgbColor(true);
    }

    private void RgbMode_Click(object sender, RoutedEventArgs e)
    {
        string mode = (sender as Button)?.Tag?.ToString() ?? "Static";

        RgbStaticPresets.Visibility =
            mode == "Static" ? Visibility.Visible : Visibility.Collapsed;
        RgbDynamicPresets.Visibility =
            mode == "Dynamic" ? Visibility.Visible : Visibility.Collapsed;
        RgbReactivePresets.Visibility =
            mode == "Reactive" ? Visibility.Visible : Visibility.Collapsed;

        if (!_uiReady)
            return;

        if (mode == "Static")
        {
            _rgbAuto = false;
            _rgbEffect = 3;
            _serial.SetSolid(_r, _g, _b);
        }
        else if (mode == "Reactive")
        {
            _rgbAuto = false;
            _rgbEffect = 4;
            _serial.SetEffect(4);
        }
    }

    private void RgbPreset_Click(object sender, RoutedEventArgs e)
    {
        string tag = (sender as Button)?.Tag?.ToString() ?? "";

        if (tag == "AUTO")
        {
            _rgbAuto = true;
            _serial.SetAutoLayer();
            return;
        }

        if (!int.TryParse(tag, out int effect))
            return;

        _rgbAuto = false;
        _rgbEffect = effect;

        if (effect == 3)
            _serial.SetSolid(_r, _g, _b);
        else
            _serial.SetEffect(effect);
    }

    private void RgbSpeedSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        int value = (int)Math.Round(e.NewValue);

        if (RgbSpeedText is not null)
            RgbSpeedText.Text = $"{value}%";

        if (_uiReady)
            _serial.SetSpeed(value);
    }

    private void SendAllRgb()
    {
        _serial.SetEnabled(LedEnabled.IsChecked == true);
        _serial.SetBrightness((int)Math.Round(BrightnessSlider.Value));
        _serial.SetSpeed((int)Math.Round(RgbSpeedSlider.Value));

        if (_rgbAuto)
        {
            _serial.SetAutoLayer();
        }
        else if (_rgbEffect == 3)
        {
            _serial.SetSolid(_r, _g, _b);
        }
        else
        {
            _serial.SetEffect(_rgbEffect);
        }
    }

    private async void ChooseScreensaverMedia_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose LumiPad screensaver",
            Filter = "GIF / Video|*.gif;*.mp4;*.m4v;*.mov|GIF|*.gif|Video|*.mp4;*.m4v;*.mov",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        _screensaverMediaPath = dialog.FileName;
        await PrepareScreensaverMediaAsync();
    }

    private ScreensaverScaleMode SelectedScreensaverScaleMode()
    {
        if (ScreensaverScaleCombo.SelectedItem is ComboBoxItem item &&
            Enum.TryParse<ScreensaverScaleMode>(
                item.Tag?.ToString(),
                true,
                out var mode))
        {
            return mode;
        }

        return ScreensaverScaleMode.Fill;
    }

    private async void ScreensaverScaleCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady || string.IsNullOrWhiteSpace(_screensaverMediaPath))
            return;

        await PrepareScreensaverMediaAsync();
    }

    private async Task PrepareScreensaverMediaAsync()
    {
        if (string.IsNullOrWhiteSpace(_screensaverMediaPath))
            return;

        SendScreensaverButton.IsEnabled = false;
        ScreensaverSendProgress.Value = 0;
        ScreensaverSendStatus.Text = "Preparing local media…";

        try
        {
            var scaleMode = SelectedScreensaverScaleMode();

            _screensaverAnimation =
                await ScreensaverMediaService.LoadAsync(
                    _screensaverMediaPath,
                    scaleMode);

            ScreensaverFileName.Text = _screensaverAnimation.FileName;
            ScreensaverMediaInfo.Text =
                $"{_screensaverAnimation.Frames.Count} frames · " +
                $"{ScreensaverMediaService.Width}×{ScreensaverMediaService.Height} · " +
                $"{_screensaverAnimation.FrameIntervalMs} ms/frame · {scaleMode}";

            ScreensaverPreviewImage.Source = CreateRgb332Bitmap(
                _screensaverAnimation.Frames[0],
                ScreensaverMediaService.Width,
                ScreensaverMediaService.Height);

            ScreensaverPreviewImage.Visibility = Visibility.Visible;
            ScreensaverPreviewHint.Visibility = Visibility.Collapsed;
            ScreensaverSendStatus.Text =
                "Ready. Send once to store the lightweight loop in LumiPad RAM.";
            SendScreensaverButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _screensaverAnimation = null;
            ScreensaverPreviewImage.Source = null;
            ScreensaverPreviewImage.Visibility = Visibility.Collapsed;
            ScreensaverPreviewHint.Visibility = Visibility.Visible;
            ScreensaverSendStatus.Text = $"Cannot prepare file: {ex.Message}";
        }
    }

    private async void SendScreensaverMedia_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_screensaverAnimation is null)
            return;

        if (!_serial.IsConnected)
        {
            ScreensaverSendStatus.Text =
                "Connect LumiPad first, then send the screensaver.";
            return;
        }

        SendScreensaverButton.IsEnabled = false;
        ScreensaverSendProgress.Value = 0;
        ScreensaverSendStatus.Text =
            "Sending frames… Bluetooth can take a little while.";

        var progress = new Progress<int>(value =>
        {
            ScreensaverSendProgress.Value = value;
            ScreensaverSendStatus.Text = $"Sending… {value}%";
        });

        try
        {
            await _serial.SendScreensaverAnimationAsync(
                _screensaverAnimation,
                progress);

            ScreensaverSendProgress.Value = 100;
            ScreensaverSendStatus.Text =
                "Sent. The custom GIF/video loop will play when the screensaver starts.";
        }
        catch (Exception ex)
        {
            ScreensaverSendStatus.Text = $"Send failed: {ex.Message}";
        }
        finally
        {
            SendScreensaverButton.IsEnabled =
                _screensaverAnimation is not null;
        }
    }

    private void ClearScreensaverMedia_Click(
        object sender,
        RoutedEventArgs e)
    {
        _screensaverAnimation = null;
        _screensaverMediaPath = null;
        _serial.ClearScreensaverAnimation();

        ScreensaverPreviewImage.Source = null;
        ScreensaverPreviewImage.Visibility = Visibility.Collapsed;
        ScreensaverPreviewHint.Visibility = Visibility.Visible;
        ScreensaverFileName.Text = "No file selected";
        ScreensaverMediaInfo.Text =
            "Converted to a lightweight loop for LumiPad.";
        ScreensaverSendProgress.Value = 0;
        ScreensaverSendStatus.Text =
            "Custom screensaver cleared; LumiPad falls back to its built-in saver.";
        SendScreensaverButton.IsEnabled = false;
    }

    private async void MainTabs_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady || !ZmkTab.IsSelected)
            return;

        await EnsureZmkStudioAsync();
    }

    private async Task EnsureZmkStudioAsync()
    {
        if (_zmkInitialized)
            return;

        try
        {
            ZmkStatus.Text = "Loading https://zmk.studio/ …";
            await ZmkWebView.EnsureCoreWebView2Async();
            ZmkWebView.Source = new Uri("https://zmk.studio/");
            _zmkInitialized = true;
            ZmkStatus.Text = "https://zmk.studio/";
        }
        catch (Exception ex)
        {
            ZmkStatus.Text =
                $"WebView2 unavailable: {ex.Message}";
        }
    }

    private async void ReloadZmk_Click(object sender, RoutedEventArgs e)
    {
        await EnsureZmkStudioAsync();

        if (ZmkWebView.CoreWebView2 is not null)
            ZmkWebView.Reload();
    }

    private void OpenZmkExternal_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://zmk.studio/",
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }
}
