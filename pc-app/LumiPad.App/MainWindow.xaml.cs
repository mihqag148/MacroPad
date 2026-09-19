using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private string _language = "en";
    private bool _allowExit;
    private bool _trayTipShown;
    private bool _zmkInitialized;
    private bool _autoReconnectEnabled = true;
    private string _connectionPreference = "auto";
    private readonly CancellationTokenSource _reconnectCts = new();

    private byte _r = 255;
    private byte _g = 120;
    private byte _b = 0;
    private ScreensaverAnimation? _screensaverAnimation;
    private string? _screensaverMediaPath;
    private readonly DispatcherTimer _screensaverPreviewTimer = new();
    private readonly Stopwatch _screensaverPreviewClock = new();
    private readonly DispatcherTimer _memoryUsageTimer = new();
    private readonly DispatcherTimer _diagnosticTimer = new();
    private readonly List<string> _logLines = new();
    private uint _firmwareLogSeq;
    private int _screensaverPreviewIndex;
    private int _rgbEffect = 3;
    private bool _rgbAuto;
    private int _screensaverDelaySeconds = 60;
    private int _sleepDelaySeconds = 120;
    private int _rgbBrightness = 25;
    private int _rgbSpeed = 50;
    private bool _rgbEnabled = true;
    private int _rgbProfileIndex;
    private RgbProfileSetting[] _rgbProfiles = CreateDefaultRgbProfiles();
    private ScreensaverScaleMode _screensaverScaleMode = ScreensaverScaleMode.Fill;

    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _appIcon;

    public MainWindow()
    {
        InitializeComponent();
        InitializeTrayIcon();

        // Keep the preview on an absolute playback timeline, just like the
        // firmware. If the UI thread is briefly late, skip a stale frame
        // instead of stretching the whole GIF and drifting out of sync.
        // Output cadence is fixed at 25 Hz (40 ms). The desired source frame
        // is still selected from the GIF's original timeline, so low-FPS GIFs
        // repeat frames and high-FPS GIFs drop frames instead of changing speed.
        _screensaverPreviewTimer.Interval =
            TimeSpan.FromMilliseconds(ScreensaverMediaService.MinFrameIntervalMs);
        _screensaverPreviewTimer.Tick += (_, _) =>
        {
            if (_screensaverAnimation is null ||
                _screensaverAnimation.Frames.Count < 2)
                return;

            int frameCount = _screensaverAnimation.Frames.Count;
            int loopMs =
                _screensaverAnimation.FrameDurationsMs.Count == frameCount
                    ? _screensaverAnimation.FrameDurationsMs.Sum()
                    : _screensaverAnimation.FrameIntervalMs * frameCount;

            if (loopMs <= 0)
                return;

            int loopPosition =
                (int)(_screensaverPreviewClock.ElapsedMilliseconds % loopMs);
            int desiredIndex = 0;
            int boundary = 0;

            for (int i = 0; i < frameCount; i++)
            {
                int duration =
                    _screensaverAnimation.FrameDurationsMs.Count == frameCount
                        ? _screensaverAnimation.FrameDurationsMs[i]
                        : _screensaverAnimation.FrameIntervalMs;

                boundary += Math.Max(
                    ScreensaverMediaService.MinFrameIntervalMs,
                    duration);
                desiredIndex = i;

                if (loopPosition < boundary)
                    break;
            }

            if (desiredIndex == _screensaverPreviewIndex)
                return;

            _screensaverPreviewIndex = desiredIndex;
            ScreensaverPreviewImage.Source = CreateRgb332Bitmap(
                _screensaverAnimation.Frames[_screensaverPreviewIndex],
                ScreensaverMediaService.Width,
                ScreensaverMediaService.Height);
        };

        _memoryUsageTimer.Interval = TimeSpan.FromSeconds(5);
        _memoryUsageTimer.Tick += async (_, _) =>
            await UpdateMemoryUsageAsync();
        _memoryUsageTimer.Start();

        _diagnosticTimer.Interval = TimeSpan.FromSeconds(1);
        _diagnosticTimer.Tick += async (_, _) => await PollFirmwareDiagnosticsAsync();
        _diagnosticTimer.Start();

        _serial.Diagnostic += (level, message) =>
            Dispatcher.Invoke(() => AddLog(level, "APP", message));

        System.Windows.Application.Current.DispatcherUnhandledException += (_, args) =>
        {
            AddLog("ERROR", "APP", $"Unhandled UI exception: {args.Exception}");
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Dispatcher.Invoke(() => AddLog("FATAL", "APP", $"Unhandled: {args.ExceptionObject}"));

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Dispatcher.Invoke(() =>
                AddLog("ERROR", "APP", $"Unobserved task exception: {args.Exception}"));
            args.SetObserved();
        };

        Loaded += async (_, _) =>
        {
            LoadTheme();
            LoadLanguage();
            LoadAppSettings();
            ApplyLanguage();
            ApplyStoredControlValues();
            _uiReady = true;
            UpdateSettingsInfo();
            AddLog("INFO", "APP", "LumiPad started");
            BuildColorWheel();
            SetDeviceControlsEnabled(false);

            if (!string.IsNullOrWhiteSpace(_screensaverMediaPath) &&
                System.IO.File.Exists(_screensaverMediaPath))
            {
                await PrepareScreensaverMediaAsync();
            }

            _serial.LinkError += message =>
                Dispatcher.Invoke(() =>
                {
                    AddLog("ERROR", "LINK", message);
                    DeviceStatus.Text = L("Bluetooth write error", "Lỗi ghi Bluetooth");
                    DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 69, 58));
                    BottomStatus.Text = message;
                    SetDeviceControlsEnabled(false);
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
                BottomStatus.Text = L($"Now Playing unavailable: {ex.Message}", $"Không dùng được Now Playing: {ex.Message}");
            }

            _connectionPreference = "auto";
            _autoReconnectEnabled = true;
            AddLog("INFO", "APP", "Auto-connect enabled by default (USB first, Bluetooth fallback)");
            await DetectAsync();
            _ = AutoReconnectLoopAsync(_reconnectCts.Token);
        };

        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private static readonly Dictionary<string, string> Vi = new()
    {
        ["Wireless MacroPad Control"] = "Điều khiển MacroPad không dây",
        ["Home"] = "Trang chủ",
        ["NOW PLAYING"] = "ĐANG PHÁT",
        ["Nothing Playing"] = "Không có nhạc đang phát",
        ["SCREENSAVER MEDIA"] = "MEDIA BẢO VỆ MÀN HÌNH",
        ["GIF / Image local"] = "GIF / Ảnh trên máy",
        ["Choose a GIF or image"] = "Chọn GIF hoặc ảnh",
        ["No file selected"] = "Chưa chọn tệp",
        ["Converted to a lightweight loop for LumiPad."] = "Tự chuyển thành vòng lặp nhẹ cho LumiPad.",
        ["Scale"] = "Co giãn",
        ["Fill"] = "Lấp đầy",
        ["Fit"] = "Vừa khung",
        ["Stretch"] = "Kéo giãn",
        ["Tile"] = "Lặp ô",
        ["Center"] = "Căn giữa",
        ["Span"] = "Phủ rộng",
        ["Choose GIF / Image"] = "Chọn GIF / Ảnh",
        ["Send to LumiPad"] = "Gửi tới LumiPad",
        ["Clear"] = "Xóa",
        ["The file stays local. Only reduced animation frames are sent."] = "Tệp vẫn nằm trên máy. Chỉ các frame đã giảm được gửi đi.",
        ["Screensaver after"] = "Bảo vệ màn hình sau",
        ["Sleep after"] = "Ngủ sau",
        ["15 seconds"] = "15 giây",
        ["30 seconds"] = "30 giây",
        ["1 minute"] = "1 phút",
        ["2 minutes"] = "2 phút",
        ["5 minutes"] = "5 phút",
        ["10 minutes"] = "10 phút",
        ["15 minutes"] = "15 phút",
        ["30 minutes"] = "30 phút",
        ["Never"] = "Không bao giờ",
        ["Not connected"] = "Chưa kết nối",
        ["Bluetooth preferred; USB fallback."] = "Ưu tiên Bluetooth; USB dự phòng.",
        ["Auto connect · USB first · Bluetooth fallback"] = "Tự động kết nối · ưu tiên USB · Bluetooth dự phòng",
        ["Connect"] = "Kết nối",
        ["Disconnect"] = "Ngắt kết nối",
        ["Restart keyboard"] = "Khởi động lại bàn phím",
        ["Keyboard DFU"] = "Bàn phím DFU",
        ["RGB"] = "RGB",
        ["RGB Profile"] = "Profile RGB",
        ["Save to profile"] = "Lưu vào profile",
        ["Show now"] = "Bật ngay",
        ["Auto"] = "Tự động",
        ["COLOR"] = "MÀU",
        ["Mode"] = "Chế độ",
        ["Static"] = "Tĩnh",
        ["Dynamic"] = "Động",
        ["Reactive"] = "Phản hồi",
        ["Presets"] = "Mẫu có sẵn",
        ["Solid Color"] = "Màu đơn",
        ["Auto by Layer"] = "Tự động theo Layer",
        ["Rainbow"] = "Cầu vồng",
        ["Purple Ping-Pong"] = "Tím qua lại",
        ["Orange Blink"] = "Cam nhấp nháy",
        ["Reactive Splash"] = "Phản hồi khi bấm",
        ["Switch"] = "Bật / Tắt",
        ["Brightness"] = "Độ sáng",
        ["Effect Speed"] = "Tốc độ hiệu ứng",
        ["Selected color"] = "Màu đã chọn",
        ["ZMK Studio"] = "ZMK Studio",
        ["Embedded zmk.studio"] = "ZMK Studio tích hợp",
        ["Reload"] = "Tải lại",
        ["Open in Edge"] = "Mở bằng Edge",
        ["Light mode"] = "Chế độ sáng",
        ["Dark mode"] = "Chế độ tối",
        ["Detecting…"] = "Đang tìm…",
        ["Disconnected."] = "Đã ngắt kết nối.",
        ["Restarting…"] = "Đang khởi động lại…",
        ["DFU / Bootloader"] = "DFU / Bootloader",
        ["Ready to upload"] = "Sẵn sàng tải lên",
        ["Uploading…"] = "Đang tải lên…",
        ["Uploaded & verified"] = "Đã tải lên và xác nhận",
        ["Upload failed"] = "Tải lên thất bại",
        ["Not uploaded"] = "Chưa tải lên",
        ["Prepare failed"] = "Xử lý thất bại",
        ["Light mode"] = "Chế độ sáng",
        ["Dark mode"] = "Chế độ tối",
        ["Settings"] = "Cài đặt",
        ["SETTINGS"] = "CÀI ĐẶT",
        ["Appearance & device"] = "Giao diện & thiết bị",
        ["Language"] = "Ngôn ngữ",
        ["Appearance"] = "Giao diện",
        ["VERSION"] = "PHIÊN BẢN",
        ["Firmware"] = "Firmware",
        ["Connection"] = "Kết nối",
        ["Disconnected"] = "Đã ngắt kết nối",
        ["DISPLAY"] = "MÀN HÌNH",
        ["Panel timing"] = "Thông số màn hình",
        ["DIAGNOSTIC LOG"] = "NHẬT KÝ CHẨN ĐOÁN",
        ["App + firmware events and errors"] = "Sự kiện và lỗi của app + firmware",
        ["View log"] = "Xem log",
        ["Hide log"] = "Ẩn log",
        ["Save log"] = "Tải log",
        ["Copy log"] = "Sao chép log",
        ["Clear log"] = "Xóa log",
        ["SYSTEM"] = "HỆ THỐNG",
        ["Flash usage"] = "Sử dụng Flash",
        ["RAM usage"] = "Sử dụng RAM",
        ["Sleep keyboard"] = "Ngủ bàn phím",
    };

    private string L(string en, string vi) => _language == "vi" ? vi : en;

    private string TranslateUiText(string current)
    {
        if (_language == "vi")
        {
            return Vi.TryGetValue(current, out var vi) ? vi : current;
        }

        foreach (var pair in Vi)
        {
            if (pair.Value == current)
                return pair.Key;
        }

        return current;
    }

    private string LanguageFilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "language.txt");

    private void LoadLanguage()
    {
        try
        {
            if (System.IO.File.Exists(LanguageFilePath))
            {
                string value = System.IO.File.ReadAllText(LanguageFilePath).Trim().ToLowerInvariant();
                _language = value == "vi" ? "vi" : "en";
            }
        }
        catch
        {
            _language = "en";
        }

        if (LanguageCombo is not null)
        {
            foreach (ComboBoxItem item in LanguageCombo.Items)
            {
                if (string.Equals(item.Tag?.ToString(), _language, StringComparison.OrdinalIgnoreCase))
                {
                    item.IsSelected = true;
                    break;
                }
            }
        }
    }

    private void SaveLanguage()
    {
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(LanguageFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                System.IO.Directory.CreateDirectory(folder);

            System.IO.File.WriteAllText(LanguageFilePath, _language);
        }
        catch
        {
        }
    }

    private void ApplyLanguage()
    {
        var visited = new HashSet<DependencyObject>();
        TranslateElement(this, visited);

        if (_trayIcon?.ContextMenuStrip is not null)
        {
            if (_trayIcon.ContextMenuStrip.Items.Count > 0)
                _trayIcon.ContextMenuStrip.Items[0].Text =
                    L("Open LumiPad", "Mở LumiPad");
            if (_trayIcon.ContextMenuStrip.Items.Count > 1)
                _trayIcon.ContextMenuStrip.Items[1].Text =
                    L("Exit", "Thoát");
        }
    }

    private void TranslateElement(
        DependencyObject node,
        HashSet<DependencyObject> visited)
    {
        if (!visited.Add(node))
            return;

        if (node is TextBlock textBlock &&
            !string.IsNullOrWhiteSpace(textBlock.Text))
        {
            textBlock.Text = TranslateUiText(textBlock.Text);
        }

        if (node is ContentControl contentControl &&
            contentControl.Content is string content &&
            !string.IsNullOrWhiteSpace(content))
        {
            contentControl.Content = TranslateUiText(content);
        }

        if (node is HeaderedContentControl headered &&
            headered.Header is string header &&
            !string.IsNullOrWhiteSpace(header))
        {
            headered.Header = TranslateUiText(header);
        }

        // Logical tree contains content of tabs that have never been selected,
        // which the visual tree alone does not expose.
        foreach (object child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dependencyChild)
                TranslateElement(dependencyChild, visited);
        }

        // Visual tree catches generated controls/templates that are not present
        // as direct logical children.
        int visualCount = 0;
        try
        {
            visualCount =
                System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
        }
        catch
        {
            visualCount = 0;
        }

        for (int i = 0; i < visualCount; i++)
        {
            DependencyObject child =
                System.Windows.Media.VisualTreeHelper.GetChild(node, i);
            TranslateElement(child, visited);
        }
    }

    private void LanguageCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (LanguageCombo?.SelectedItem is not ComboBoxItem item)
            return;

        string next = item.Tag?.ToString() == "vi" ? "vi" : "en";

        if (_language == next && _uiReady)
            return;

        _language = next;

        if (_uiReady)
        {
            SaveLanguage();
            ApplyLanguage();
            Dispatcher.BeginInvoke(new Action(ApplyLanguage));
        }
    }

    private string AppSettingsFilePath =>
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LumiPad",
            "settings.json");

    private sealed class RgbProfileSetting
    {
        public int Effect { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }
    }

    private static RgbProfileSetting[] CreateDefaultRgbProfiles() =>
    [
        new() { Effect = 0, R = 255, G = 120, B = 0 },
        new() { Effect = 1, R = 180, G = 40, B = 255 },
        new() { Effect = 2, R = 255, G = 90, B = 0 },
        new() { Effect = 3, R = 0, G = 170, B = 255 },
        new() { Effect = 3, R = 80, G = 255, B = 100 },
    ];

    private sealed class AppSettings
    {
        public bool RgbEnabled { get; set; } = true;
        public int RgbBrightness { get; set; } = 25;
        public int RgbSpeed { get; set; } = 50;
        public bool RgbAuto { get; set; }
        public int RgbEffect { get; set; } = 3;
        public byte R { get; set; } = 255;
        public byte G { get; set; } = 120;
        public byte B { get; set; }
        public RgbProfileSetting[]? RgbProfiles { get; set; }
        public int ScreensaverDelaySeconds { get; set; } = 60;
        public int SleepDelaySeconds { get; set; } = 120;
        public string? ScreensaverMediaPath { get; set; }
        public ScreensaverScaleMode ScreensaverScaleMode { get; set; } = ScreensaverScaleMode.Fill;
    }

    private void LoadAppSettings()
    {
        try
        {
            if (!System.IO.File.Exists(AppSettingsFilePath))
                return;

            var settings = JsonSerializer.Deserialize<AppSettings>(
                System.IO.File.ReadAllText(AppSettingsFilePath));

            if (settings is null)
                return;

            _rgbEnabled = settings.RgbEnabled;
            _rgbBrightness = Math.Clamp(settings.RgbBrightness, 5, 50);
            _rgbSpeed = Math.Clamp(settings.RgbSpeed, 10, 100);
            _rgbAuto = settings.RgbAuto;
            _rgbEffect = Math.Clamp(settings.RgbEffect, 0, 4);
            _r = settings.R;
            _g = settings.G;
            _b = settings.B;

            if (settings.RgbProfiles is { Length: >= 5 })
            {
                _rgbProfiles = settings.RgbProfiles
                    .Take(5)
                    .Select(p => new RgbProfileSetting
                    {
                        Effect = Math.Clamp(p.Effect, 0, 4),
                        R = p.R,
                        G = p.G,
                        B = p.B
                    })
                    .ToArray();
            }

            _screensaverDelaySeconds = Math.Max(0, settings.ScreensaverDelaySeconds);
            _sleepDelaySeconds = Math.Max(0, settings.SleepDelaySeconds);
            _screensaverMediaPath = settings.ScreensaverMediaPath;
            _screensaverScaleMode = settings.ScreensaverScaleMode;
        }
        catch
        {
        }
    }

    private void SaveAppSettings()
    {
        try
        {
            string? folder = System.IO.Path.GetDirectoryName(AppSettingsFilePath);
            if (!string.IsNullOrWhiteSpace(folder))
                System.IO.Directory.CreateDirectory(folder);

            var settings = new AppSettings
            {
                RgbEnabled = LedEnabled?.IsChecked ?? _rgbEnabled,
                RgbBrightness = BrightnessSlider is null ? _rgbBrightness : (int)Math.Round(BrightnessSlider.Value),
                RgbSpeed = RgbSpeedSlider is null ? _rgbSpeed : (int)Math.Round(RgbSpeedSlider.Value),
                RgbAuto = _rgbAuto,
                RgbEffect = _rgbEffect,
                R = _r,
                G = _g,
                B = _b,
                RgbProfiles = _rgbProfiles
                    .Select(p => new RgbProfileSetting
                    {
                        Effect = p.Effect,
                        R = p.R,
                        G = p.G,
                        B = p.B
                    })
                    .ToArray(),
                ScreensaverDelaySeconds = _screensaverDelaySeconds,
                SleepDelaySeconds = _sleepDelaySeconds,
                ScreensaverMediaPath = _screensaverMediaPath,
                ScreensaverScaleMode = SelectedScreensaverScaleMode()
            };

            System.IO.File.WriteAllText(
                AppSettingsFilePath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private void ApplyStoredControlValues()
    {
        if (LedEnabled is not null)
            LedEnabled.IsChecked = _rgbEnabled;

        if (BrightnessSlider is not null)
            BrightnessSlider.Value = _rgbBrightness;

        if (RgbSpeedSlider is not null)
            RgbSpeedSlider.Value = _rgbSpeed;

        SelectComboTag(RgbProfileCombo, _rgbProfileIndex.ToString());
        SelectComboTag(ScreensaverDelayCombo, _screensaverDelaySeconds.ToString());
        SelectComboTag(SleepDelayCombo, _sleepDelaySeconds.ToString());
        SelectComboTag(ScreensaverScaleCombo, _screensaverScaleMode.ToString());
        UpdateRgbReadout();
    }

    private static void SelectComboTag(System.Windows.Controls.ComboBox combo, string tag)
    {
        combo.SelectedValue = tag;
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
            _trayIcon.BalloonTipTitle =
                L("LumiPad is still running", "LumiPad vẫn đang chạy");
            _trayIcon.BalloonTipText =
                L("Now Playing and Bluetooth control continue in the system tray.",
                  "Now Playing và điều khiển Bluetooth vẫn tiếp tục chạy ở khay hệ thống.");
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

        _screensaverPreviewTimer.Stop();
        _screensaverPreviewClock.Stop();
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
        ApplyLanguage();
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
        PlayButton.Content = data.IsPlaying ? "❚❚" : "▶";

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

    private void SetDeviceControlsEnabled(bool enabled)
    {
        bool connected = enabled && _serial.IsConnected;
        if (RgbDevicePanel is not null)
            RgbDevicePanel.IsEnabled = connected;

        if (DeviceTimingPanel is not null)
            DeviceTimingPanel.IsEnabled = true;
        if (ScreensaverDelayCombo is not null)
            ScreensaverDelayCombo.IsEnabled = true;
        if (SleepDelayCombo is not null)
            SleepDelayCombo.IsEnabled = true;

        if (SendScreensaverButton is not null)
        {
            SendScreensaverButton.IsEnabled =
                connected && _screensaverAnimation is not null;
        }

        if (ShowScreensaverNowButton is not null)
            ShowScreensaverNowButton.IsEnabled = connected;

        if (DisconnectButton is not null)
            DisconnectButton.IsEnabled = connected;

        if (RestartKeyboardButton is not null)
            RestartKeyboardButton.IsEnabled = connected;

        if (KeyboardDfuButton is not null)
            KeyboardDfuButton.IsEnabled = connected;

        if (SleepKeyboardButton is not null)
            SleepKeyboardButton.IsEnabled = connected;

        UpdateSettingsInfo();
    }

    private void UpdateSettingsInfo()
    {
        if (AppVersionText is not null)
        {
            var version = System.Reflection.Assembly
                .GetExecutingAssembly().GetName().Version;
            AppVersionText.Text = version is null
                ? "--"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        if (FirmwareVersionText is not null)
        {
            FirmwareVersionText.Text = string.IsNullOrWhiteSpace(_serial.FirmwareHello)
                ? "--"
                : _serial.FirmwareHello.Replace("LUMIPAD|", "Protocol v");
        }

        if (ConnectionInfoText is not null)
        {
            ConnectionInfoText.Text = _serial.IsConnected
                ? _serial.ConnectionName
                : L("Disconnected", "Đã ngắt kết nối");
        }
    }

    private void AddLog(string level, string source, string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{source}] {message}";
        _logLines.Add(line);

        const int maxLines = 1500;
        if (_logLines.Count > maxLines)
            _logLines.RemoveRange(0, _logLines.Count - maxLines);

        if (LogTextBox is not null)
        {
            LogTextBox.Text = string.Join(Environment.NewLine, _logLines);
            LogTextBox.ScrollToEnd();
        }
    }

    private async Task PollFirmwareDiagnosticsAsync()
    {
        if (!_serial.IsConnected)
            return;

        // Drain a few queued firmware entries per tick without monopolising the link.
        for (int i = 0; i < 6; i++)
        {
            var entry = await _serial.ReadFirmwareLogAsync(_firmwareLogSeq);
            if (entry is null)
                break;

            _firmwareLogSeq = entry.Value.Seq;
            AddLog(
                entry.Value.Level == "E" ? "ERROR" :
                entry.Value.Level == "W" ? "WARN" : "INFO",
                "FW",
                $"#{entry.Value.Seq} {entry.Value.Message}");
        }

        UpdateSettingsInfo();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _logLines.Clear();
        if (LogTextBox is not null)
            LogTextBox.Clear();
        AddLog("INFO", "APP", "Log cleared");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, _logLines));
            AddLog("INFO", "APP", "Log copied to clipboard");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", "APP", $"Copy log failed: {ex.Message}");
        }
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        bool show = LogPanel.Visibility != Visibility.Visible;
        LogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ViewLogButton.Content = show
            ? L("Hide log", "Ẩn log")
            : L("View log", "Xem log");
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = L("Save LumiPad diagnostic log", "Lưu nhật ký chẩn đoán LumiPad"),
                Filter = "Text log|*.txt|All files|*.*",
                FileName = $"LumiPad-log-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                AddExtension = true,
                DefaultExt = ".txt"
            };

            if (dialog.ShowDialog() != true)
                return;

            System.IO.File.WriteAllText(
                dialog.FileName,
                string.Join(Environment.NewLine, _logLines));

            AddLog("INFO", "APP", $"Log saved: {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AddLog("ERROR", "APP", $"Save log failed: {ex.Message}");
        }
    }

    private async Task UpdateMemoryUsageAsync()
    {
        if (!_serial.IsConnected)
        {
            FlashUsageText.Text = "FLASH --";
            RamUsageText.Text = "RAM --";
            return;
        }

        var usage = await _serial.ReadMemoryUsageAsync();
        if (usage is null)
            return;

        double flashPct =
            usage.Value.FlashUsed * 100.0 / usage.Value.FlashTotal;
        double ramPct =
            usage.Value.RamUsed * 100.0 / usage.Value.RamTotal;

        FlashUsageText.Text = $"FLASH {flashPct:0.0}%";
        RamUsageText.Text = $"RAM {ramPct:0.0}%";
    }

    private async Task UpdatePanelInfoAsync()
    {
        const string fallback =
            "ST7789 ≈60 Hz default · SPI 32 MHz · GIF ≤25 FPS";

        if (!_serial.IsConnected)
        {
            PanelInfoText.Text = fallback;
            return;
        }

        var info = await _serial.ReadPanelInfoAsync();
        if (info is null)
        {
            PanelInfoText.Text = fallback;
            return;
        }

        double spiMhz = info.Value.SpiHz / 1_000_000.0;
        PanelInfoText.Text =
            $"{info.Value.Panel} ≈{info.Value.RefreshHz} Hz default · " +
            $"SPI {spiMhz:0.#} MHz · GIF ≤{info.Value.GifMaxFps} FPS";
    }

    private async void ConnectUsbButton_Click(object sender, RoutedEventArgs e)
    {
        _connectionPreference = "usb";
        _autoReconnectEnabled = true;
        await DetectAsync();
    }

    private async void ConnectBluetoothButton_Click(object sender, RoutedEventArgs e)
    {
        _connectionPreference = "bluetooth";
        _autoReconnectEnabled = true;
        await DetectAsync();
    }

    private async Task DetectAsync()
    {
        ConnectUsbButton.IsEnabled = false;
        ConnectBluetoothButton.IsEnabled = false;
        AddLog("INFO", "APP", $"Connect requested ({_connectionPreference})");
        DeviceStatus.Text = L("Detecting…", "Đang tìm…");
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
        BottomStatus.Text = _connectionPreference switch
        {
            "usb" => L("Searching USB…", "Đang tìm USB…"),
            "bluetooth" => L("Searching Bluetooth…", "Đang tìm Bluetooth…"),
            _ => L("Searching USB first, then Bluetooth…", "Đang tìm USB trước, sau đó Bluetooth…")
        };

        var connection = _connectionPreference switch
        {
            "usb" => await _serial.ConnectUsbAsync(),
            "bluetooth" => await _serial.ConnectBluetoothAsync(),
            _ => await _serial.AutoDetectAsync()
        };

        if (connection is null)
        {
            DeviceStatus.Text = L("Not connected", "Chưa kết nối");
            DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
            BottomStatus.Text =
                L("LumiPad not found. Pair the keyboard over Bluetooth, or connect USB as fallback.",
                  "Không tìm thấy LumiPad. Hãy ghép Bluetooth hoặc cắm USB dự phòng.");
            SetDeviceControlsEnabled(false);
        }
        else
        {
            DeviceStatus.Text = connection;
            DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(48, 209, 88));
            BottomStatus.Text = connection.StartsWith("Bluetooth", StringComparison.Ordinal)
                ? L("Connected wirelessly. Now Playing and RGB are live.",
                    "Đã kết nối không dây. Now Playing và RGB đang hoạt động.")
                : L("Connected over USB fallback. Now Playing and RGB are live.",
                    "Đã kết nối qua USB dự phòng. Now Playing và RGB đang hoạt động.");

            _firmwareLogSeq = 0;
            AddLog("INFO", "LINK", $"Connected: {connection}; {_serial.FirmwareHello}");
            SetDeviceControlsEnabled(true);
            SendAllRgb();
            SendPowerTiming();
            await UpdateMemoryUsageAsync();
            await UpdatePanelInfoAsync();
        }

        ConnectUsbButton.IsEnabled = true;
        ConnectBluetoothButton.IsEnabled = true;
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _autoReconnectEnabled = false;
        AddLog("INFO", "LINK", "Manual disconnect");
        _serial.Disconnect();
        DeviceStatus.Text = L("Not connected", "Chưa kết nối");
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
        BottomStatus.Text = L("Disconnected.", "Đã ngắt kết nối.");
        SetDeviceControlsEnabled(false);
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

                var connection = _connectionPreference switch
                {
                    "usb" => await _serial.ConnectUsbAsync(token),
                    "bluetooth" => await _serial.ConnectBluetoothAsync(token),
                    _ => await _serial.AutoDetectAsync(token)
                };
                if (connection is null)
                    continue;

                DeviceStatus.Text = connection;
                DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(48, 209, 88));
                BottomStatus.Text = connection.StartsWith("Bluetooth", StringComparison.Ordinal)
                    ? L("Reconnected wirelessly after wake.", "Đã kết nối lại Bluetooth sau khi wake.")
                    : L("Reconnected over USB fallback.", "Đã kết nối lại qua USB dự phòng.");

                SetDeviceControlsEnabled(true);
                SendAllRgb();
                SendPowerTiming();
                await UpdateMemoryUsageAsync();
                await UpdatePanelInfoAsync();
                await RestoreScreensaverAfterReconnectAsync();
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

    private static int ComboSeconds(
        object sender,
        int fallback)
    {
        // Read the ComboBox's current SelectedValue (Tag) instead of relying
        // on SelectionChangedEventArgs.AddedItems. AddedItems can be stale or
        // empty after WPF style/language refreshes, which made the timing
        // controls appear stuck on the first selected value.
        if (sender is System.Windows.Controls.ComboBox combo &&
            int.TryParse(combo.SelectedValue?.ToString(), out int seconds))
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
            ComboSeconds(sender, _screensaverDelaySeconds);

        if (_uiReady)
        {
            SaveAppSettings();
        }

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetScreensaverDelay(_screensaverDelaySeconds);
            BottomStatus.Text = L(
                $"Screensaver: {_screensaverDelaySeconds}s",
                $"Bảo vệ màn hình: {_screensaverDelaySeconds} giây");
        }
    }

    private void SleepDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _sleepDelaySeconds =
            ComboSeconds(sender, _sleepDelaySeconds);

        if (_uiReady)
        {
            SaveAppSettings();
        }

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetSleepTimeout(_sleepDelaySeconds);
            BottomStatus.Text = L(
                _sleepDelaySeconds == 0 ? "Sleep: Never" : $"Sleep: {_sleepDelaySeconds}s",
                _sleepDelaySeconds == 0 ? "Ngủ: Không bao giờ" : $"Ngủ: {_sleepDelaySeconds} giây");
        }
    }

    private async void SleepKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text =
                L("Connect LumiPad before sleeping the keyboard.",
                  "Hãy kết nối LumiPad trước khi cho bàn phím ngủ.");
            return;
        }

        try
        {
            await _serial.SleepKeyboardAsync();
            BottomStatus.Text =
                L("Keyboard entered soft sleep. Press any key to wake.",
                  "Bàn phím đã ngủ mềm. Nhấn phím bất kỳ để đánh thức.");
        }
        catch (Exception ex)
        {
            BottomStatus.Text =
                L($"Sleep failed: {ex.Message}", $"Ngủ thất bại: {ex.Message}");
        }
    }

    private async void RestartKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text = L("Connect LumiPad before restarting the keyboard.", "Hãy kết nối LumiPad trước khi khởi động lại bàn phím.");
            return;
        }

        BottomStatus.Text = L("Restarting keyboard…", "Đang khởi động lại bàn phím…");

        try
        {
            await _serial.RestartKeyboardAsync();
            await Task.Delay(150);
            _serial.Disconnect();

            DeviceStatus.Text = L("Restarting…", "Đang khởi động lại…");
            DeviceDot.Fill =
                new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
            BottomStatus.Text =
                L("Keyboard is restarting. LumiPad will reconnect automatically.",
                  "Bàn phím đang khởi động lại. LumiPad sẽ tự kết nối lại.");
        }
        catch (Exception ex)
        {
            BottomStatus.Text = L($"Restart failed: {ex.Message}", $"Khởi động lại thất bại: {ex.Message}");
        }
    }

    private async void KeyboardDfu_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text = L("Connect LumiPad before entering DFU.", "Hãy kết nối LumiPad trước khi vào DFU.");
            return;
        }

        var result = System.Windows.MessageBox.Show(
            L("Put the keyboard into DFU/bootloader mode for firmware flashing?", "Đưa bàn phím vào chế độ DFU/bootloader để nạp firmware?"),
            "LumiPad DFU",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        _autoReconnectEnabled = false;
        BottomStatus.Text = L("Entering keyboard DFU…", "Đang đưa bàn phím vào DFU…");

        try
        {
            await _serial.EnterDfuAsync();
            await Task.Delay(150);
            _serial.Disconnect();

            DeviceStatus.Text = "DFU / Bootloader";
            DeviceDot.Fill =
                new SolidColorBrush(MediaColor.FromRgb(255, 159, 10));
            BottomStatus.Text =
                L("Keyboard is in DFU. Flash firmware, then press Connect when it boots normally.",
                  "Bàn phím đang ở DFU. Nạp firmware xong rồi bấm Kết nối khi máy khởi động lại.");
        }
        catch (Exception ex)
        {
            BottomStatus.Text = L($"DFU failed: {ex.Message}", $"DFU thất bại: {ex.Message}");
        }
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.PreviousAsync();

    private async void PlayButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.TogglePlayPauseAsync();

    private async void NextButton_Click(object sender, RoutedEventArgs e) =>
        await _nowPlaying.NextAsync();

    private void RgbProfileCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0 ||
            e.AddedItems[0] is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int index))
        {
            return;
        }

        _rgbProfileIndex = Math.Clamp(index, 0, _rgbProfiles.Length - 1);
        var profile = _rgbProfiles[_rgbProfileIndex];

        _rgbEffect = profile.Effect;
        _r = profile.R;
        _g = profile.G;
        _b = profile.B;

        UpdateRgbReadout();
    }

    private void RgbSaveProfile_Click(object sender, RoutedEventArgs e)
    {
        int index = Math.Clamp(_rgbProfileIndex, 0, _rgbProfiles.Length - 1);
        _rgbProfiles[index] = new RgbProfileSetting
        {
            Effect = Math.Clamp(_rgbEffect, 0, 4),
            R = _r,
            G = _g,
            B = _b
        };

        SaveAppSettings();

        if (_serial.IsConnected)
        {
            _serial.SetRgbProfile(
                index,
                _rgbProfiles[index].Effect,
                _rgbProfiles[index].R,
                _rgbProfiles[index].G,
                _rgbProfiles[index].B);
        }

        BottomStatus.Text = L(
            $"RGB profile {index + 1} saved.",
            $"Đã lưu RGB cho profile {index + 1}.");
    }

    private void LedEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
            return;

        _rgbEnabled = LedEnabled.IsChecked == true;
        SaveAppSettings();
        _serial.SetEnabled(_rgbEnabled);
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
        {
            _rgbBrightness = value;
            SaveAppSettings();
            _serial.SetBrightness(value);
        }
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
            SaveAppSettings();
            _serial.SetSolid(_r, _g, _b);
        }
    }

    private void RgbSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
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
        string mode = (sender as System.Windows.Controls.Button)?.Tag?.ToString() ?? "Static";

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

        SaveAppSettings();
    }

    private void RgbPreset_Click(object sender, RoutedEventArgs e)
    {
        string tag = (sender as System.Windows.Controls.Button)?.Tag?.ToString() ?? "";

        if (tag == "AUTO")
        {
            _rgbAuto = true;
            SaveAppSettings();
            _serial.SetAutoLayer();
            return;
        }

        if (!int.TryParse(tag, out int effect))
            return;

        _rgbAuto = false;
        _rgbEffect = effect;

        SaveAppSettings();

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
        {
            _rgbSpeed = value;
            SaveAppSettings();
            _serial.SetSpeed(value);
        }
    }

    private void SendAllRgb()
    {
        if (!_serial.IsConnected)
            return;

        _rgbEnabled = LedEnabled.IsChecked == true;
        _rgbBrightness = (int)Math.Round(BrightnessSlider.Value);
        _rgbSpeed = (int)Math.Round(RgbSpeedSlider.Value);

        // Preserve the known-working pre-redesign protocol: individual
        // commands are sent in a deterministic order instead of RGB|STATE.
        for (int i = 0; i < _rgbProfiles.Length; i++)
        {
            var profile = _rgbProfiles[i];
            _serial.SetRgbProfile(i, profile.Effect, profile.R, profile.G, profile.B);
        }

        _serial.SetEnabled(_rgbEnabled);
        _serial.SetBrightness(_rgbBrightness);
        _serial.SetSpeed(_rgbSpeed);

        if (_rgbAuto)
            _serial.SetAutoLayer();
        else if (_rgbEffect == 3)
            _serial.SetSolid(_r, _g, _b);
        else
            _serial.SetEffect(_rgbEffect);
    }

    private void SetScreensaverUploadState(
        string state,
        MediaColor color)
    {
        ScreensaverUploadState.Text = state;
        ScreensaverUploadDot.Fill = new SolidColorBrush(color);
    }

    private async void ChooseScreensaverMedia_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("Choose LumiPad screensaver", "Chọn bảo vệ màn hình LumiPad"),
            Filter = "GIF / Image|*.gif;*.png;*.jpg;*.jpeg;*.bmp|GIF|*.gif|Image|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        _screensaverMediaPath = dialog.FileName;
        SaveAppSettings();
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
        if (!_uiReady)
            return;

        _screensaverScaleMode = SelectedScreensaverScaleMode();
        SaveAppSettings();

        if (string.IsNullOrWhiteSpace(_screensaverMediaPath))
            return;

        await PrepareScreensaverMediaAsync();
    }

    private async Task PrepareScreensaverMediaAsync()
    {
        if (string.IsNullOrWhiteSpace(_screensaverMediaPath))
            return;

        SendScreensaverButton.IsEnabled = false;
        ScreensaverSendProgress.Value = 0;
        ScreensaverSendStatus.Text = L("Preparing local media…", "Đang xử lý media trên máy…");

        try
        {
            var scaleMode = SelectedScreensaverScaleMode();

            _screensaverAnimation =
                await ScreensaverMediaService.LoadAsync(
                    _screensaverMediaPath,
                    scaleMode);

            ScreensaverFileName.Text = _screensaverAnimation.FileName;

            if (_screensaverAnimation.Frames.Count == 1)
            {
                ScreensaverMediaInfo.Text =
                    L(
                        $"Static image · {ScreensaverMediaService.Width}×{ScreensaverMediaService.Height} · output {ScreensaverMediaService.MaxPlaybackFps} FPS · {scaleMode}",
                        $"Ảnh tĩnh · {ScreensaverMediaService.Width}×{ScreensaverMediaService.Height} · đầu ra {ScreensaverMediaService.MaxPlaybackFps} FPS · {scaleMode}");
            }
            else
            {
                int playbackAverageFps =
                    (int)Math.Round(
                        1000.0 /
                        Math.Max(1, _screensaverAnimation.FrameIntervalMs));

                ScreensaverMediaInfo.Text =
                    L(
                        $"{_screensaverAnimation.Frames.Count} GIF frames · Playback avg {playbackAverageFps} FPS · output {ScreensaverMediaService.MaxPlaybackFps} FPS · {scaleMode}",
                        $"{_screensaverAnimation.Frames.Count} khung GIF · Playback avg {playbackAverageFps} FPS · đầu ra {ScreensaverMediaService.MaxPlaybackFps} FPS · {scaleMode}");
            }

            ScreensaverPreviewImage.Source = CreateRgb332Bitmap(
                _screensaverAnimation.Frames[0],
                ScreensaverMediaService.Width,
                ScreensaverMediaService.Height);

            ScreensaverPreviewImage.Visibility = Visibility.Visible;
            ScreensaverPreviewHint.Visibility = Visibility.Collapsed;

            _screensaverPreviewIndex = 0;
            _screensaverPreviewTimer.Stop();
            _screensaverPreviewClock.Reset();

            if (_screensaverAnimation.Frames.Count > 1)
            {
                _screensaverPreviewClock.Restart();
                _screensaverPreviewTimer.Start();
            }

            ScreensaverSendStatus.Text =
                L("Ready. Send once to store the lightweight loop in LumiPad flash.",
                  "Đã sẵn sàng. Gửi một lần để lưu vòng lặp nhẹ vào flash LumiPad.");
            SetScreensaverUploadState(
                L("Ready to upload", "Sẵn sàng tải lên"),
                MediaColor.FromRgb(255, 159, 10));
            SendScreensaverButton.IsEnabled = _serial.IsConnected;
        }
        catch (Exception ex)
        {
            _screensaverAnimation = null;
            _screensaverPreviewTimer.Stop();
            SetScreensaverUploadState(
                L("Prepare failed", "Xử lý thất bại"),
                MediaColor.FromRgb(255, 69, 58));
            ScreensaverPreviewImage.Source = null;
            ScreensaverPreviewImage.Visibility = Visibility.Collapsed;
            ScreensaverPreviewHint.Visibility = Visibility.Visible;
            ScreensaverSendStatus.Text = L($"Cannot prepare file: {ex.Message}", $"Không thể xử lý tệp: {ex.Message}");
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
                L("Connect LumiPad first, then send the screensaver.",
                  "Hãy kết nối LumiPad trước rồi mới gửi bảo vệ màn hình.");
            return;
        }

        SendScreensaverButton.IsEnabled = false;
        ScreensaverSendProgress.Value = 0;
        SetScreensaverUploadState(
            L("Uploading…", "Đang tải lên…"),
            MediaColor.FromRgb(255, 159, 10));
        ScreensaverSendStatus.Text =
            L("Sending frames… Bluetooth can take a little while.",
              "Đang gửi frame… Bluetooth có thể mất một lúc.");

        var progress = new Progress<int>(value =>
        {
            ScreensaverSendProgress.Value = value;
            ScreensaverSendStatus.Text = L($"Sending… {value}%", $"Đang gửi… {value}%");
        });

        try
        {
            bool verified = await _serial.SendScreensaverAnimationAsync(
                _screensaverAnimation,
                progress);

            ScreensaverSendProgress.Value = 100;

            if (verified)
            {
                SetScreensaverUploadState(
                    L("Uploaded & verified", "Đã tải lên và xác nhận"),
                    MediaColor.FromRgb(48, 209, 88));
                ScreensaverSendStatus.Text =
                    L("LumiPad confirmed the custom screensaver is ready.",
                      "LumiPad đã xác nhận bảo vệ màn hình tùy chỉnh sẵn sàng.");
                SaveAppSettings();
            }
            else
            {
                SetScreensaverUploadState(
                    L("Upload failed", "Tải lên thất bại"),
                    MediaColor.FromRgb(255, 69, 58));
                ScreensaverSendStatus.Text =
                    L("LumiPad did not confirm the upload. Flash the matching firmware and try again.",
                      "LumiPad chưa xác nhận dữ liệu. Hãy flash đúng firmware đi kèm rồi thử lại.");
            }
        }
        catch (Exception ex)
        {
            AddLog("ERROR", "APP", $"Screensaver upload failed: {ex}");
            SetScreensaverUploadState(
                L("Upload failed", "Tải lên thất bại"),
                MediaColor.FromRgb(255, 69, 58));
            ScreensaverSendStatus.Text = L($"Send failed: {ex.Message}", $"Gửi thất bại: {ex.Message}");
        }
        finally
        {
            SendScreensaverButton.IsEnabled =
                _serial.IsConnected && _screensaverAnimation is not null;
        }
    }

    private void ShowScreensaverNow_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            ScreensaverSendStatus.Text =
                L("Connect LumiPad first.", "Hãy kết nối LumiPad trước.");
            return;
        }

        _serial.ShowScreensaverNow();
        ScreensaverSendStatus.Text =
            L("Showing the custom screensaver now.",
              "Đang bật bảo vệ màn hình tùy chỉnh ngay.");
    }

    private void ClearScreensaverMedia_Click(
        object sender,
        RoutedEventArgs e)
    {
        _screensaverAnimation = null;
        _screensaverMediaPath = null;
        SaveAppSettings();
        _screensaverPreviewTimer.Stop();
        _screensaverPreviewClock.Reset();
        _serial.ClearScreensaverAnimation();

        ScreensaverPreviewImage.Source = null;
        ScreensaverPreviewImage.Visibility = Visibility.Collapsed;
        ScreensaverPreviewHint.Visibility = Visibility.Visible;
        ScreensaverFileName.Text = L("No file selected", "Chưa chọn tệp");
        ScreensaverMediaInfo.Text =
            L("Converted to a lightweight loop for LumiPad.",
              "Tự chuyển thành vòng lặp nhẹ cho LumiPad.");
        ScreensaverSendProgress.Value = 0;
        SetScreensaverUploadState(
            L("Not uploaded", "Chưa tải lên"),
            MediaColor.FromRgb(99, 99, 102));
        ScreensaverSendStatus.Text =
            L("Custom screensaver cleared. No screensaver will be shown until another GIF or image is uploaded.",
              "Đã xóa bảo vệ màn hình. Sẽ không hiện screensaver cho tới khi tải GIF hoặc ảnh mới.");
        SendScreensaverButton.IsEnabled = false;
    }

    private async Task RestoreScreensaverAfterReconnectAsync()
    {
        if (_screensaverAnimation is null || !_serial.IsConnected)
            return;

        try
        {
            if (await _serial.IsScreensaverReadyAsync())
                return;

            var progress = new Progress<int>(value =>
            {
                ScreensaverSendProgress.Value = value;
                ScreensaverSendStatus.Text =
                    L($"Restoring screensaver… {value}%", $"Đang khôi phục bảo vệ màn hình… {value}%");
            });

            bool verified = await _serial.SendScreensaverAnimationAsync(
                _screensaverAnimation,
                progress);

            if (verified)
            {
                ScreensaverSendProgress.Value = 100;
                SetScreensaverUploadState(
                    L("Uploaded & verified", "Đã tải lên và xác nhận"),
                    MediaColor.FromRgb(48, 209, 88));
                ScreensaverSendStatus.Text =
                    L("Custom screensaver restored after reconnect.",
                      "Đã khôi phục bảo vệ màn hình tùy chỉnh sau khi kết nối lại.");
            }
        }
        catch
        {
            // Keep the keyboard connection alive even if automatic restore fails.
        }
    }

    private async void MainTabs_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs) || !_uiReady)
            return;

        ApplyLanguage();
        Dispatcher.BeginInvoke(new Action(ApplyLanguage));
        SetDeviceControlsEnabled(_serial.IsConnected);

        if (ZmkTab.IsSelected)
            await EnsureZmkStudioAsync();
    }

    private async Task EnsureZmkStudioAsync()
    {
        if (_zmkInitialized)
            return;

        try
        {
            ZmkStatus.Text = L("Loading https://zmk.studio/ …", "Đang tải https://zmk.studio/ …");
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
