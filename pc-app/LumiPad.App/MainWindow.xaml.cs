using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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

    private byte _wallR1 = 0;
    private byte _wallG1 = 0;
    private byte _wallB1 = 0;
    private byte _wallR2 = 16;
    private byte _wallG2 = 21;
    private byte _wallB2 = 31;

    private byte _saverR1 = 74;
    private byte _saverG1 = 125;
    private byte _saverB1 = 255;
    private byte _saverR2 = 169;
    private byte _saverG2 = 85;
    private byte _saverB2 = 255;

    private int _screensaverStyle = 0;
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
            LoadCustomization();

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

    private static BitmapSource CreateArtworkBitmap(byte[] rgb332)
    {
        const int width = 76;
        const int height = 76;
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
            SendCustomization();
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
                SendCustomization();
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

    private void EffectCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!_uiReady ||
            EffectCombo.SelectedItem is not ComboBoxItem item)
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
            Color = Drawing.Color.FromArgb(_r, _g, _b)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return;

        _r = dialog.Color.R;
        _g = dialog.Color.G;
        _b = dialog.Color.B;

        ColorPreview.Background =
            new SolidColorBrush(MediaColor.FromRgb(_r, _g, _b));

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

    private string CustomizationFilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "customization.json");

    private sealed class CustomizationSettings
    {
        public byte WallR1 { get; set; }
        public byte WallG1 { get; set; }
        public byte WallB1 { get; set; }
        public byte WallR2 { get; set; }
        public byte WallG2 { get; set; }
        public byte WallB2 { get; set; }
        public byte SaverR1 { get; set; }
        public byte SaverG1 { get; set; }
        public byte SaverB1 { get; set; }
        public byte SaverR2 { get; set; }
        public byte SaverG2 { get; set; }
        public byte SaverB2 { get; set; }
        public int SaverStyle { get; set; } = 0;
        public int SaverDelaySeconds { get; set; } = 60;
        public int SleepDelaySeconds { get; set; } = 120;
    }

    private void LoadCustomization()
    {
        try
        {
            if (System.IO.File.Exists(CustomizationFilePath))
            {
                var settings = JsonSerializer.Deserialize<CustomizationSettings>(
                    System.IO.File.ReadAllText(CustomizationFilePath));

                if (settings is not null)
                {
                    _wallR1 = settings.WallR1;
                    _wallG1 = settings.WallG1;
                    _wallB1 = settings.WallB1;
                    _wallR2 = settings.WallR2;
                    _wallG2 = settings.WallG2;
                    _wallB2 = settings.WallB2;

                    _saverR1 = settings.SaverR1;
                    _saverG1 = settings.SaverG1;
                    _saverB1 = settings.SaverB1;
                    _saverR2 = settings.SaverR2;
                    _saverG2 = settings.SaverG2;
                    _saverB2 = settings.SaverB2;

                    _screensaverStyle = settings.SaverStyle;
                    _screensaverDelaySeconds = settings.SaverDelaySeconds;
                    _sleepDelaySeconds = settings.SleepDelaySeconds;
                }
            }
        }
        catch
        {
        }

        SelectComboTag(ScreensaverStyleCombo, _screensaverStyle.ToString());
        SelectComboTag(ScreensaverDelayCombo, _screensaverDelaySeconds.ToString());
        SelectComboTag(SleepDelayCombo, _sleepDelaySeconds.ToString());

        // Stored/custom colors should not be overwritten by a preset on startup.
        SelectComboTag(WallpaperPresetCombo, "custom");
        UpdateCustomizationPreview();
    }

    private void SaveCustomization()
    {
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(CustomizationFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                System.IO.Directory.CreateDirectory(folder);

            var settings = new CustomizationSettings
            {
                WallR1 = _wallR1,
                WallG1 = _wallG1,
                WallB1 = _wallB1,
                WallR2 = _wallR2,
                WallG2 = _wallG2,
                WallB2 = _wallB2,
                SaverR1 = _saverR1,
                SaverG1 = _saverG1,
                SaverB1 = _saverB1,
                SaverR2 = _saverR2,
                SaverG2 = _saverG2,
                SaverB2 = _saverB2,
                SaverStyle = _screensaverStyle,
                SaverDelaySeconds = _screensaverDelaySeconds,
                SleepDelaySeconds = _sleepDelaySeconds
            };

            System.IO.File.WriteAllText(
                CustomizationFilePath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
        }
        catch
        {
        }
    }

    private static void SelectComboTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        foreach (var entry in combo.Items)
        {
            if (entry is ComboBoxItem item &&
                string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private static int SelectedIntTag(System.Windows.Controls.ComboBox combo, int fallback)
    {
        if (combo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Tag?.ToString(), out int value))
            return value;

        return fallback;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) =>
        new(MediaColor.FromRgb(r, g, b));

    private void UpdateCustomizationPreview()
    {
        WallpaperColorAPreview.Background = Brush(_wallR1, _wallG1, _wallB1);
        WallpaperColorBPreview.Background = Brush(_wallR2, _wallG2, _wallB2);
        SaverColorAPreview.Background = Brush(_saverR1, _saverG1, _saverB1);
        SaverColorBPreview.Background = Brush(_saverR2, _saverG2, _saverB2);

        PreviewStopA.Color = MediaColor.FromRgb(_wallR1, _wallG1, _wallB1);
        PreviewStopB.Color = MediaColor.FromRgb(_wallR2, _wallG2, _wallB2);
    }

    private void WallpaperPresetCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (WallpaperPresetCombo.SelectedItem is not ComboBoxItem item)
            return;

        switch (item.Tag?.ToString())
        {
            case "midnight":
                (_wallR1, _wallG1, _wallB1) = (0, 0, 0);
                (_wallR2, _wallG2, _wallB2) = (16, 21, 31);
                break;
            case "ocean":
                (_wallR1, _wallG1, _wallB1) = (0, 18, 42);
                (_wallR2, _wallG2, _wallB2) = (0, 94, 130);
                break;
            case "purple":
                (_wallR1, _wallG1, _wallB1) = (22, 8, 40);
                (_wallR2, _wallG2, _wallB2) = (92, 42, 150);
                break;
            case "sunset":
                (_wallR1, _wallG1, _wallB1) = (68, 18, 42);
                (_wallR2, _wallG2, _wallB2) = (180, 72, 28);
                break;
            case "custom":
            default:
                break;
        }

        if (_uiReady)
            UpdateCustomizationPreview();
    }

    private bool ChooseDisplayColor(ref byte r, ref byte g, ref byte b)
    {
        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = Drawing.Color.FromArgb(r, g, b)
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
            return false;

        r = dialog.Color.R;
        g = dialog.Color.G;
        b = dialog.Color.B;
        return true;
    }

    private void WallpaperColorA_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseDisplayColor(ref _wallR1, ref _wallG1, ref _wallB1))
        {
            SelectComboTag(WallpaperPresetCombo, "custom");
            UpdateCustomizationPreview();
        }
    }

    private void WallpaperColorB_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseDisplayColor(ref _wallR2, ref _wallG2, ref _wallB2))
        {
            SelectComboTag(WallpaperPresetCombo, "custom");
            UpdateCustomizationPreview();
        }
    }

    private void SaverColorA_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseDisplayColor(ref _saverR1, ref _saverG1, ref _saverB1))
            UpdateCustomizationPreview();
    }

    private void SaverColorB_Click(object sender, RoutedEventArgs e)
    {
        if (ChooseDisplayColor(ref _saverR2, ref _saverG2, ref _saverB2))
            UpdateCustomizationPreview();
    }

    private void ApplyCustomization_Click(object sender, RoutedEventArgs e)
    {
        _screensaverStyle = SelectedIntTag(ScreensaverStyleCombo, 0);
        _screensaverDelaySeconds = SelectedIntTag(ScreensaverDelayCombo, 60);
        _sleepDelaySeconds = SelectedIntTag(SleepDelayCombo, 120);

        SaveCustomization();
        SendCustomization();

        BottomStatus.Text = _serial.IsConnected
            ? "Display customization applied to LumiPad."
            : "Customization saved. It will be sent when LumiPad reconnects.";
    }

    private void SendCustomization()
    {
        if (!_serial.IsConnected)
            return;

        _serial.SetWallpaper(
            _wallR1, _wallG1, _wallB1,
            _wallR2, _wallG2, _wallB2);

        _serial.SetScreensaver(
            _screensaverStyle != 2,
            _screensaverStyle,
            _screensaverDelaySeconds,
            _saverR1, _saverG1, _saverB1,
            _saverR2, _saverG2, _saverB2);

        _serial.SetSleepTimeout(_sleepDelaySeconds);
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
