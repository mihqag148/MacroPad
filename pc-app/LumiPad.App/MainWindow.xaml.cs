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

    private byte _r = 255;
    private byte _g = 120;
    private byte _b = 0;

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
        };

        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void InitializeTrayIcon()
    {
        _appIcon = CreateColorIcon();

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

    private static Drawing.Icon CreateColorIcon()
    {
        using var bitmap = new Drawing.Bitmap(64, 64);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                new Drawing.Rectangle(4, 4, 56, 56),
                Drawing.Color.FromArgb(255, 149, 0),
                Drawing.Color.FromArgb(88, 86, 214),
                45f);

            g.FillEllipse(brush, 4, 4, 56, 56);

            using var font = new Drawing.Font(
                "Segoe UI",
                30,
                Drawing.FontStyle.Bold,
                Drawing.GraphicsUnit.Pixel);

            var size = g.MeasureString("L", font);
            using var textBrush = new Drawing.SolidBrush(Drawing.Color.White);
            g.DrawString(
                "L",
                font,
                textBrush,
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
            ThemeButton.Content = "🌙 Dark";
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
            ThemeButton.Content = "☀ Light";
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

        _serial.ClearNowPlaying();
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
            value = TimeSpan.Zero;

        return $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    private async void DetectButton_Click(object sender, RoutedEventArgs e) =>
        await DetectAsync();

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
