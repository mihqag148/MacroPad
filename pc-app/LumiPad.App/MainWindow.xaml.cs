using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using System.Windows.Input;
using Windows.Media;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MediaColor = System.Windows.Media.Color;
using IO = System.IO;

namespace LumiPad.App;

public partial class MainWindow : Window
{
    private ProductDefinition _activeProduct = ProductCatalog.DialDesk;
    private IDeviceLink _serial;
    private readonly NowPlayingService _nowPlaying = new();
    private readonly PcMonitorService _pcMonitorService = new();

    private bool _uiReady;
    private bool _lightTheme;
    private string _language = "en";
    private bool _allowExit;
    private bool _trayTipShown;
    private bool _zmkInitialized;
    private string _loadedConfiguratorUrl = "";
    private bool _autoReconnectEnabled = true;
    private string _connectionPreference = "auto";
    private bool _keyboardSleeping;
    private readonly CancellationTokenSource _reconnectCts = new();
    private static readonly HttpClient UpdateHttp = CreateUpdateHttpClient();
    private const string UpdateReleaseApi =
        "https://api.github.com/repos/mihqag148/MacroPad/releases/latest";
    private const string PixelProFirmwareReleaseApi =
        "https://api.github.com/repos/mihqag148/PIXEL-PRO---Lumi-Macropad/releases/latest";
    private bool _updateBusy;
    private bool _checkingUpdates;
    private bool _appUpdateAvailable;
    private bool _firmwareUpdateAvailable;
    private string _latestAppVersion = "";
    private string _latestFirmwareVersion = "";
    private string _latestReleaseTag = "";

    private byte _r = 255;
    private byte _g = 120;
    private byte _b = 0;
    private ScreensaverAnimation? _screensaverAnimation;
    private string? _screensaverMediaPath;
    private readonly DispatcherTimer _screensaverPreviewTimer = new();
    private readonly Stopwatch _screensaverPreviewClock = new();
    private readonly DispatcherTimer _memoryUsageTimer = new();
    private readonly DispatcherTimer _diagnosticTimer = new();
    private readonly DispatcherTimer _autoProfileTimer = new();
    private readonly DispatcherTimer _runningAppsTimer = new();
    private readonly DispatcherTimer _actionEventTimer = new();
    private readonly DispatcherTimer _productStatusTimer = new();
    private readonly DispatcherTimer _pcMonitorTimer = new();
    private readonly DispatcherTimer _updateCheckTimer = new();
    private readonly List<string> _logLines = new();
    private AutoProfileSettings _autoProfileSettings = new();
    private IReadOnlyList<RunningAppInfo> _runningApps = Array.Empty<RunningAppInfo>();
    private readonly Dictionary<string, ImageSource?> _applicationIconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private string? _lastForegroundAppPath;
    private int _lastAppliedAutoProfile = -1;
    private NowPlayingData? _currentNowPlaying;
    private bool _mediaSeekDragging;
    private bool _syncingMediaUi;
    private bool _volumeMuted;
    private DateTimeOffset _lastVolumeUiSync = DateTimeOffset.MinValue;
    private List<ActionScriptDefinition> _actionScripts = [];
    private bool _loadingActionScriptUi;
    private readonly HashSet<int> _runningActionIds = [];
    private ActionKeymapWindow? _actionKeymapWindow;
    private uint _lastActionEventSeq;
    private uint _firmwareLogSeq;
    private int _screensaverPreviewIndex;
    private int _rgbEffect = 3;
    private bool _rgbAuto;
    private int _screensaverDelaySeconds = 60;
    private int _sleepDelaySeconds = 120;
    private int _rgbIdleDelaySeconds = 60;
    private int _deepSleepDelaySeconds = 0;
    private int _rgbBrightness = 25;
    private int _rgbSpeed = 50;
    private bool _rgbEnabled = true;
    private int _rgbProfileIndex;
    private RgbProfileSetting[] _rgbProfiles = CreateDefaultRgbProfiles();
    private ScreensaverScaleMode _screensaverScaleMode = ScreensaverScaleMode.Fill;
    private string _screensaverSource = "Media";

    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _appIcon;
    private sealed record ProductCardVisual(
        ProductDefinition Product,
        Border Card,
        Ellipse Dot,
        TextBlock ConnectionText,
        TextBlock BatteryText);

    private readonly Dictionary<string, ProductCardVisual> _productCardVisuals =
        new(StringComparer.OrdinalIgnoreCase);
    private int? _activeBatteryPercent;
    private bool _pcMonitorEnabled = true;
    private int _pcMonitorIntervalMs = 1000;
    private bool _pcMonitorPolling;
    private PcMonitorSnapshot? _lastPcMonitorSnapshot;
    private string _pcMonitorGpuId = "auto";
    private string _pcMonitorConfigName = "MY PC";
    private bool _syncingPcMonitorUi;
    private bool _syncingPcMetricUi;
    private int[] _pcMonitorMetricSlots = [0, 3, 6, 9, 10, 11];

    private static readonly (int Id, string Name)[] PcMonitorMetricChoices =
    [
        (0, "CPU usage"),
        (1, "CPU temperature"),
        (2, "CPU clock"),
        (3, "GPU usage"),
        (4, "GPU temperature"),
        (5, "GPU clock"),
        (6, "RAM usage"),
        (7, "RAM used"),
        (8, "RAM total"),
        (9, "Network download"),
        (10, "Network upload"),
        (11, "FPS")
    ];

    private static HttpClient CreateUpdateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "LumiPad-Updater/1.12");
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
    }

    public MainWindow()
    {
        _serial = DeviceLinkFactory.Create(_activeProduct);
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
                _screensaverAnimation.PixelFormat != ScreensaverPixelFormat.Rgb332 ||
                _screensaverAnimation.Frames.Count < 2)
            {
                return;
            }

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
            int frameStart = 0;
            int frameDuration = _screensaverAnimation.FrameIntervalMs;
            int boundary = 0;

            for (int i = 0; i < frameCount; i++)
            {
                int duration =
                    _screensaverAnimation.FrameDurationsMs.Count == frameCount
                        ? _screensaverAnimation.FrameDurationsMs[i]
                        : _screensaverAnimation.FrameIntervalMs;

                duration = Math.Max(
                    ScreensaverMediaService.MinFrameIntervalMs,
                    duration);

                frameStart = boundary;
                boundary += duration;
                desiredIndex = i;
                frameDuration = duration;

                if (loopPosition < boundary)
                    break;
            }

            int nextIndex = (desiredIndex + 1) % frameCount;
            double blend =
                Math.Clamp(
                    (loopPosition - frameStart) /
                    (double)Math.Max(1, frameDuration),
                    0.0,
                    1.0);

            _screensaverPreviewIndex = desiredIndex;
            ScreensaverPreviewImage.Source =
                CreateRgb332InterpolatedBitmap(
                    _screensaverAnimation.Frames[desiredIndex],
                    _screensaverAnimation.Frames[nextIndex],
                    blend,
                    _screensaverAnimation.Width,
                    _screensaverAnimation.Height);
        };

        _memoryUsageTimer.Interval = TimeSpan.FromSeconds(5);
        _memoryUsageTimer.Tick += async (_, _) =>
            await UpdateMemoryUsageAsync();
        _memoryUsageTimer.Start();

        _diagnosticTimer.Interval = TimeSpan.FromSeconds(2);
        _diagnosticTimer.Tick += async (_, _) => await PollFirmwareDiagnosticsAsync();
        _diagnosticTimer.Start();

        _autoProfileTimer.Interval = TimeSpan.FromMilliseconds(700);
        _autoProfileTimer.Tick += (_, _) => PollAutoProfile();

        _runningAppsTimer.Interval = TimeSpan.FromSeconds(4);
        _runningAppsTimer.Tick += async (_, _) => await RefreshRunningAppsAsync();

        _actionEventTimer.Interval = TimeSpan.FromMilliseconds(120);
        _actionEventTimer.Tick += async (_, _) => await PollLumiActionAsync();

        _productStatusTimer.Interval = TimeSpan.FromSeconds(10);
        _productStatusTimer.Tick += async (_, _) =>
            await UpdateProductOverviewAsync();

        _pcMonitorTimer.Interval = TimeSpan.FromMilliseconds(_pcMonitorIntervalMs);
        _pcMonitorTimer.Tick += async (_, _) =>
            await PollPcMonitorAsync();

        _updateCheckTimer.Interval = TimeSpan.FromMinutes(30);
        _updateCheckTimer.Tick += async (_, _) =>
            await CheckForUpdatesAsync(silent: true);

        AttachDeviceLinkEvents(_serial);

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
            _autoProfileSettings = AutoProfileService.Load();
            _actionScripts = ActionScriptStore.Load();
            ApplyLanguage();
            InitializePcMetricSelectors();
            ApplyStoredControlValues();
            ApplyAutoProfileUiState();
            RefreshActionScriptsUi();
            BuildProductCards();
            UpdateDeviceConfiguratorUi();
            _uiReady = true;
            UpdateSettingsInfo();
            UpdateProductHubUi();
            AddLog("INFO", "APP", "Lumi Macropad started");
            BuildColorWheel();
            SetDeviceControlsEnabled(false);

            if (!string.IsNullOrWhiteSpace(_screensaverMediaPath) &&
                System.IO.File.Exists(_screensaverMediaPath))
            {
                await PrepareScreensaverMediaAsync();
            }

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
            await CheckForUpdatesAsync(silent: true);
            _updateCheckTimer.Start();
            _ = AutoReconnectLoopAsync(_reconnectCts.Token);
            _autoProfileTimer.Start();
            _runningAppsTimer.Start();
            _actionEventTimer.Start();
            _productStatusTimer.Start();
            if (_pcMonitorEnabled)
                _pcMonitorTimer.Start();
            await PollPcMonitorAsync(force: true);
            await RefreshRunningAppsAsync();
            await UpdateProductOverviewAsync();
            PollAutoProfile(force: true);
        };

        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;
    }

    private void BuildProductCards()
    {
        if (ProductCardsPanel is null)
            return;

        ProductCardsPanel.Children.Clear();
        _productCardVisuals.Clear();

        foreach (ProductDefinition product in ProductCatalog.All)
        {
            var button = new System.Windows.Controls.Button
            {
                Tag = product,
                Width = 394,
                Height = 502,
                Padding = new Thickness(0),
                Margin = new Thickness(10),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FocusVisualStyle = null,
                ClipToBounds = true
            };
            button.Click += ProductCard_Click;

            var card = new Border
            {
                Width = 388,
                Height = 496,
                Background =
                    TryFindResource("Card") as System.Windows.Media.Brush,
                BorderBrush =
                    TryFindResource("Line") as System.Windows.Media.Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(26),
                ClipToBounds = true
            };

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(304)
            });
            root.RowDefinitions.Add(new RowDefinition
            {
                Height = new GridLength(1)
            });
            root.RowDefinitions.Add(new RowDefinition());

            var preview = new Border
            {
                Background =
                    TryFindResource("Card2") as System.Windows.Media.Brush,
                Padding = new Thickness(24),
                Margin = new Thickness(1, 1, 1, 0),
                CornerRadius = new CornerRadius(25, 25, 0, 0),
                ClipToBounds = true
            };
            preview.Child = CreateProductPreview(product);
            root.Children.Add(preview);

            var divider = new Border
            {
                Background =
                    TryFindResource("Line") as System.Windows.Media.Brush
            };
            Grid.SetRow(divider, 1);
            root.Children.Add(divider);

            var info = new Grid
            {
                Margin = new Thickness(24, 22, 24, 20)
            };
            info.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            info.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });
            info.RowDefinitions.Add(new RowDefinition());
            info.RowDefinitions.Add(new RowDefinition
            {
                Height = GridLength.Auto
            });

            info.Children.Add(new TextBlock
            {
                Text = product.Name,
                FontSize = 25,
                FontWeight = FontWeights.SemiBold
            });

            var subtitle = new TextBlock
            {
                Text = product.Subtitle,
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                FontSize = 12,
                Margin = new Thickness(0, 5, 0, 0)
            };
            Grid.SetRow(subtitle, 1);
            info.Children.Add(subtitle);

            var status = new Grid
            {
                Margin = new Thickness(0, 18, 0, 0)
            };
            status.ColumnDefinitions.Add(new ColumnDefinition());
            status.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            var left = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var dot = new Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = new SolidColorBrush(
                    MediaColor.FromRgb(99, 99, 102)),
                Margin = new Thickness(0, 0, 8, 0)
            };
            left.Children.Add(dot);

            var connectionText = new TextBlock
            {
                Text = L("Not connected", "Chưa kết nối"),
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12
            };
            left.Children.Add(connectionText);
            status.Children.Add(left);

            var batteryText = new TextBlock
            {
                Text = "▰ --%",
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(batteryText, 1);
            status.Children.Add(batteryText);

            Grid.SetRow(status, 3);
            info.Children.Add(status);

            Grid.SetRow(info, 2);
            root.Children.Add(info);
            card.Child = root;
            button.Content = card;
            ProductCardsPanel.Children.Add(button);

            _productCardVisuals[product.Id] =
                new ProductCardVisual(
                    product,
                    card,
                    dot,
                    connectionText,
                    batteryText);

        }

        UpdateProductHubUi();
    }

    private UIElement CreateProductPreview(ProductDefinition product)
    {
        if (product.Driver != DeviceDriverKind.QmkRawHid)
            return CreateDialDeskPreview();

        var root = new Grid();
        var body = new Border
        {
            Width = 222,
            Height = 220,
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(MediaColor.FromRgb(12, 12, 14)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(58, 58, 64)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(new TextBlock
        {
            Text = "QMK",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        stack.Children.Add(new TextBlock
        {
            Text = "RAW HID",
            Foreground = new SolidColorBrush(MediaColor.FromRgb(255, 149, 0)),
            FontSize = 13,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        });
        body.Child = stack;
        root.Children.Add(body);
        return root;
    }

    private UIElement CreateDialDeskPreview()
    {
        var container = new Grid();

        var body = new Border
        {
            Width = 222,
            Height = 220,
            CornerRadius = new CornerRadius(24),
            Background = new SolidColorBrush(
                MediaColor.FromRgb(12, 12, 14)),
            BorderBrush = new SolidColorBrush(
                MediaColor.FromRgb(58, 58, 64)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var device = new Grid
        {
            Width = 176,
            Height = 190,
            HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        device.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(50)
        });
        device.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(8)
        });
        device.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(132)
        });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(120)
        });
        top.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(8)
        });
        top.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(48)
        });

        var display = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = new LinearGradientBrush(
                MediaColor.FromRgb(25, 40, 75),
                MediaColor.FromRgb(80, 42, 93),
                90)
        };
        display.Child = new TextBlock
        {
            Text = "DIAL DESK",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        top.Children.Add(display);

        var dial = new Ellipse
        {
            Width = 44,
            Height = 44,
            Fill = new LinearGradientBrush(
                MediaColor.FromRgb(95, 95, 102),
                MediaColor.FromRgb(30, 30, 34),
                45),
            Stroke = new SolidColorBrush(
                MediaColor.FromRgb(145, 145, 150)),
            StrokeThickness = 1,
            HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(dial, 2);
        top.Children.Add(dial);
        device.Children.Add(top);

        var keys = new System.Windows.Controls.Primitives.UniformGrid
        {
            Rows = 3,
            Columns = 4,
            Width = 176,
            Height = 132
        };

        for (int i = 0; i < 12; i++)
        {
            keys.Children.Add(new Border
            {
                Width = 38,
                Height = 38,
                HorizontalAlignment =
                    System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(
                    MediaColor.FromRgb(36, 36, 40)),
                BorderBrush = new SolidColorBrush(
                    MediaColor.FromRgb(70, 70, 76)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5)
            });
        }

        Grid.SetRow(keys, 2);
        device.Children.Add(keys);

        body.Child = device;
        container.Children.Add(body);
        return container;
    }

    private async void ProductCard_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button ||
            button.Tag is not ProductDefinition product)
            return;

        if (!string.Equals(
                _activeProduct.Id,
                product.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            await SwitchActiveProductAsync(product);
        }

        WorkspaceProductTitle.Text = product.Name;
        ProductHub.Visibility = Visibility.Collapsed;
        DeviceWorkspace.Visibility = Visibility.Visible;
        await UpdateProductOverviewAsync();
    }

    private async void BackToProducts_Click(
        object sender,
        RoutedEventArgs e)
    {
        DeviceWorkspace.Visibility = Visibility.Collapsed;
        ProductHub.Visibility = Visibility.Visible;
        await UpdateProductOverviewAsync();
    }

    private void AttachDeviceLinkEvents(IDeviceLink link)
    {
        link.Diagnostic += (level, message) =>
            Dispatcher.Invoke(() => AddLog(level, "APP", message));

        link.LinkError += message =>
            Dispatcher.Invoke(() =>
            {
                AddLog("ERROR", "LINK", message);
                DeviceStatus.Text = L("Device link error", "Lỗi kết nối thiết bị");
                DeviceDot.Fill =
                    new SolidColorBrush(MediaColor.FromRgb(255, 69, 58));
                BottomStatus.Text = message;
                _keyboardSleeping = false;
                _activeBatteryPercent = null;
                SetDeviceControlsEnabled(false);
                UpdateTransportIndicators();
                UpdateSleepButtonUi();
                UpdateProductHubUi();
            });
    }

    private async Task SwitchActiveProductAsync(ProductDefinition product)
    {
        _autoReconnectEnabled = false;

        try
        {
            try
            {
                _serial.Disconnect();
                _serial.Dispose();
            }
            catch
            {
            }

            _activeProduct = product;
            _activeBatteryPercent = null;
            _keyboardSleeping = false;
            _lastActionEventSeq = 0;
            _firmwareLogSeq = 0;
            _connectionPreference = "auto";

            _serial = DeviceLinkFactory.Create(product);
            AttachDeviceLinkEvents(_serial);

            _zmkInitialized = false;
            _loadedConfiguratorUrl = "";
            UpdateDeviceConfiguratorUi();

            SetDeviceControlsEnabled(false);
            UpdateTransportIndicators();
            UpdateSleepButtonUi();
            UpdateProductHubUi();

            AddLog(
                "INFO",
                "APP",
                $"Active product: {product.Name} ({product.Driver})");

            await DetectAsync();
        }
        catch (Exception ex)
        {
            AddLog("ERROR", "APP", $"Cannot activate {product.Name}: {ex.Message}");
            BottomStatus.Text = ex.Message;
        }
        finally
        {
            _autoReconnectEnabled = true;
        }
    }

    private void UpdateProductHubUi()
    {
        foreach (var pair in _productCardVisuals)
        {
            ProductCardVisual visual = pair.Value;
            bool active = string.Equals(
                visual.Product.Id,
                _activeProduct.Id,
                StringComparison.OrdinalIgnoreCase);
            bool connected = active && _serial.IsConnected;

            string connection = connected
                ? _serial.IsUsbConnected
                    ? L("Connected · USB", "Đã kết nối · USB")
                    : _serial.IsBluetoothConnected
                        ? L("Connected · Bluetooth", "Đã kết nối · Bluetooth")
                        : L("Connected", "Đã kết nối")
                : L("Not connected", "Chưa kết nối");

            visual.ConnectionText.Text = connection;
            visual.ConnectionText.Foreground =
                TryFindResource(connected ? "TextPrimary" : "Muted")
                    as System.Windows.Media.Brush;

            visual.Dot.Fill = new SolidColorBrush(
                connected
                    ? MediaColor.FromRgb(48, 209, 88)
                    : MediaColor.FromRgb(99, 99, 102));

            visual.BatteryText.Text =
                !visual.Product.SupportsBattery
                    ? ""
                    : connected && _activeBatteryPercent.HasValue
                        ? $"▰ {_activeBatteryPercent.Value}%"
                        : "▰ --%";

            visual.BatteryText.Foreground =
                TryFindResource(
                    connected && _activeBatteryPercent.HasValue
                        ? "TextPrimary"
                        : "Muted")
                    as System.Windows.Media.Brush;

            visual.Card.BorderBrush =
                TryFindResource(connected ? "Accent" : "Line")
                    as System.Windows.Media.Brush;
        }

        if (ProductHubStatusText is not null)
        {
            ProductHubStatusText.Text = _serial.IsConnected
                ? L(
                    $"{_activeProduct.Name} is online · {_serial.ConnectionName}",
                    $"{_activeProduct.Name} đang trực tuyến · {_serial.ConnectionName}")
                : L(
                    $"Searching for {_activeProduct.Name}…",
                    $"Đang tìm {_activeProduct.Name}…");
        }

        if (ProductHubVersionText is not null)
        {
            var version = System.Reflection.Assembly
                .GetExecutingAssembly().GetName().Version;
            ProductHubVersionText.Text = version is null
                ? "v--"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    private async Task UpdateProductOverviewAsync()
    {
        if (!_serial.IsConnected)
        {
            _activeBatteryPercent = null;
            UpdateProductHubUi();
            return;
        }

        try
        {
            int? battery = await _serial.ReadBatteryPercentAsync();
            if (battery.HasValue)
                _activeBatteryPercent = battery.Value;
        }
        catch
        {
        }

        UpdateProductHubUi();
    }

    private static readonly Dictionary<string, string> Vi = new()
    {
        ["Wireless MacroPad Control"] = "Điều khiển MacroPad không dây",
        ["Choose a product"] = "Chọn sản phẩm",
        ["Connected · USB"] = "Đã kết nối · USB",
        ["Connected · Bluetooth"] = "Đã kết nối · Bluetooth",
        ["Product Hub"] = "Trung tâm sản phẩm",
        ["Home"] = "Trang chủ",
        ["MEDIA"] = "MEDIA",
        ["Nothing Playing"] = "Không có nhạc đang phát",
        ["SCREENSAVER MEDIA"] = "MEDIA BẢO VỆ MÀN HÌNH",
        ["GIF / Image local"] = "GIF / Ảnh trên máy",
        ["Choose a GIF or image"] = "Chọn GIF hoặc ảnh",
        ["No file selected"] = "Chưa chọn tệp",
        ["Converted to a lightweight loop for DIAL DESK."] = "Tự chuyển thành vòng lặp nhẹ cho DIAL DESK.",
        ["Scale"] = "Co giãn",
        ["Fill"] = "Lấp đầy",
        ["Fit"] = "Vừa khung",
        ["Stretch"] = "Kéo giãn",
        ["Tile"] = "Lặp ô",
        ["Center"] = "Căn giữa",
        ["Span"] = "Phủ rộng",
        ["Choose GIF / Image"] = "Chọn GIF / Ảnh",
        ["Send to DIAL DESK"] = "Gửi tới DIAL DESK",
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
        ["Turn LEDs off after"] = "Tắt LED sau",
        ["Deep sleep after"] = "Ngủ sâu sau",
        ["UPDATES"] = "CẬP NHẬT",
        ["One-click updates"] = "Cập nhật một chạm",
        ["Check now"] = "Kiểm tra ngay",
        ["LumiPad app"] = "Ứng dụng LumiPad",
        ["Keyboard firmware"] = "Firmware bàn phím",
        ["Checking…"] = "Đang kiểm tra…",
        ["Checking for updates…"] = "Đang kiểm tra cập nhật…",
        ["Update available"] = "Có bản mới",
        ["Up to date"] = "Đã mới nhất",
        ["Connect keyboard to read firmware version"] = "Kết nối bàn phím để đọc phiên bản firmware",
        ["Connect by USB to update firmware"] = "Cắm USB để cập nhật firmware",
        ["Update firmware"] = "Cập nhật firmware",
        ["Update app"] = "Cập nhật ứng dụng",
        ["Ready"] = "Sẵn sàng",
        ["Deep sleep disconnects Bluetooth and uses very little power. Press a key to reboot and reconnect."] = "Ngủ sâu sẽ ngắt Bluetooth và tiết kiệm điện tối đa. Nhấn phím để khởi động lại và kết nối lại.",
        ["1 hour"] = "1 giờ",
        ["2 hours"] = "2 giờ",
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
        ["Wake keyboard"] = "Đánh thức bàn phím",
        ["Auto Profile"] = "Auto Profile",
        ["AUTO PROFILE"] = "AUTO PROFILE",
        ["Link Game/App"] = "Liên kết Game/App",
        ["Watching active application"] = "Theo dõi ứng dụng đang hoạt động",
        ["Enabled"] = "Bật",
        ["Default"] = "Mặc định",
        ["Select Application"] = "Chọn ứng dụng",
        ["RUNNING APPS"] = "ỨNG DỤNG ĐANG CHẠY",
        ["Suggestions"] = "Gợi ý",
        ["Refresh"] = "Làm mới",
        ["Actions"] = "Actions",
        ["ACTION / SCRIPT ENGINE"] = "ACTION / SCRIPT ENGINE",
        ["Scripts"] = "Script",
        ["New"] = "Mới",
        ["Delete"] = "Xóa",
        ["SCRIPT EDITOR"] = "TRÌNH SỬA SCRIPT",
        ["Select or create a script"] = "Chọn hoặc tạo một script",
        ["Name"] = "Tên",
        ["Add Step"] = "Thêm bước",
        ["Remove Step"] = "Xóa bước",
        ["Move Up"] = "Lên",
        ["Move Down"] = "Xuống",
        ["Save"] = "Lưu",
        ["Run Test"] = "Chạy thử",
        ["Key Map"] = "Gán phím",
        ["Ready"] = "Sẵn sàng",
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
        public int RgbIdleDelaySeconds { get; set; } = 60;
        public int DeepSleepDelaySeconds { get; set; } = 0;
        public string? ScreensaverMediaPath { get; set; }
        public ScreensaverScaleMode ScreensaverScaleMode { get; set; } = ScreensaverScaleMode.Fill;
        public bool PcMonitorEnabled { get; set; } = true;
        public int PcMonitorIntervalMs { get; set; } = 1000;
        public string PcMonitorGpuId { get; set; } = "auto";
        public string PcMonitorConfigName { get; set; } = "MY PC";
        public int[]? PcMonitorMetricSlots { get; set; }
        public string ScreensaverSource { get; set; } = "Media";
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
            _rgbIdleDelaySeconds = Math.Max(0, settings.RgbIdleDelaySeconds);
            _deepSleepDelaySeconds = Math.Max(0, settings.DeepSleepDelaySeconds);
            _screensaverMediaPath = settings.ScreensaverMediaPath;
            _screensaverScaleMode = settings.ScreensaverScaleMode;
            _screensaverSource =
                string.Equals(settings.ScreensaverSource, "PcMonitor", StringComparison.Ordinal)
                    ? "PcMonitor"
                    : "Media";
            _pcMonitorGpuId =
                string.IsNullOrWhiteSpace(settings.PcMonitorGpuId)
                    ? "auto"
                    : settings.PcMonitorGpuId;
            _pcMonitorConfigName =
                string.IsNullOrWhiteSpace(settings.PcMonitorConfigName)
                    ? "MY PC"
                    : settings.PcMonitorConfigName.Trim();

            if (settings.PcMonitorMetricSlots is { Length: 6 } savedSlots &&
                savedSlots.All(id => id is >= 0 and <= 11))
            {
                _pcMonitorMetricSlots = savedSlots.ToArray();
            }

            _pcMonitorEnabled = settings.PcMonitorEnabled;
            _pcMonitorIntervalMs =
                settings.PcMonitorIntervalMs is 500 or 1000 or 2000
                    ? settings.PcMonitorIntervalMs
                    : 1000;
            _pcMonitorTimer.Interval =
                TimeSpan.FromMilliseconds(_pcMonitorIntervalMs);
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
                RgbIdleDelaySeconds = _rgbIdleDelaySeconds,
                DeepSleepDelaySeconds = _deepSleepDelaySeconds,
                ScreensaverMediaPath = _screensaverMediaPath,
                ScreensaverScaleMode = SelectedScreensaverScaleMode(),
                PcMonitorEnabled = _pcMonitorEnabled,
                PcMonitorIntervalMs = _pcMonitorIntervalMs,
                PcMonitorGpuId = _pcMonitorGpuId,
                PcMonitorConfigName = _pcMonitorConfigName,
                PcMonitorMetricSlots = _pcMonitorMetricSlots.ToArray(),
                ScreensaverSource = _screensaverSource
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
        SelectComboTag(RgbIdleDelayCombo, _rgbIdleDelaySeconds.ToString());
        SelectComboTag(DeepSleepDelayCombo, _deepSleepDelaySeconds.ToString());
        SelectComboTag(ScreensaverScaleCombo, _screensaverScaleMode.ToString());
        if (ScreensaverSourceCombo is not null)
            SelectComboTag(ScreensaverSourceCombo, _screensaverSource);

        if (PcMonitorEnabledCheckBox is not null)
            PcMonitorEnabledCheckBox.IsChecked = _pcMonitorEnabled;
        if (PcMonitorIntervalCombo is not null)
            SelectComboTag(PcMonitorIntervalCombo, _pcMonitorIntervalMs.ToString());
        if (PcMonitorConfigNameText is not null)
            PcMonitorConfigNameText.Text = _pcMonitorConfigName;

        ApplyPcMetricSelections();

        UpdateScreensaverSourceUi();
        UpdatePcMonitorConfigSummary(_lastPcMonitorSnapshot);
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
            Text = "Lumi Macropad",
            Visible = true
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Lumi Macropad", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
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
                L("Lumi Macropad is still running", "Lumi Macropad vẫn đang chạy");
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
        _autoProfileTimer.Stop();
        _runningAppsTimer.Stop();
        _actionEventTimer.Stop();
        _productStatusTimer.Stop();
        _pcMonitorTimer.Stop();
        _reconnectCts.Cancel();
        _reconnectCts.Dispose();
        _nowPlaying.Dispose();
        _pcMonitorService.Dispose();
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

    private static void SetSystemResourceColor(
        System.Windows.ResourceKey key,
        string hex)
    {
        if (System.Windows.Media.ColorConverter.ConvertFromString(hex) is MediaColor color)
        {
            System.Windows.Application.Current.Resources[key] =
                new SolidColorBrush(color);
        }
    }

    private void ApplyTheme()
    {
        if (_lightTheme)
        {
            SetResourceColor("Bg", "#F2F2F7");
            SetResourceColor("Card", "#FFFFFF");
            SetResourceColor("Card2", "#F8F8FA");
            SetResourceColor("ControlBg", "#F2F2F5");
            SetResourceColor("TextPrimary", "#171719");
            SetResourceColor("Muted", "#6E6E73");
            SetResourceColor("Line", "#D5D5DA");
            SetResourceColor("Accent", "#FF7A00");
            SetResourceColor("Selection", "#F5E8DE");

            SetSystemResourceColor(System.Windows.SystemColors.WindowBrushKey, "#FFFFFF");
            SetSystemResourceColor(System.Windows.SystemColors.ControlBrushKey, "#F2F2F5");
            SetSystemResourceColor(System.Windows.SystemColors.WindowTextBrushKey, "#171719");
            SetSystemResourceColor(System.Windows.SystemColors.ControlTextBrushKey, "#171719");
            SetSystemResourceColor(System.Windows.SystemColors.HighlightBrushKey, "#F5E8DE");
            SetSystemResourceColor(System.Windows.SystemColors.HighlightTextBrushKey, "#171719");
            SetSystemResourceColor(System.Windows.SystemColors.InactiveSelectionHighlightBrushKey, "#ECECEF");

            ThemeButton.Content = "Dark mode";
        }
        else
        {
            SetResourceColor("Bg", "#0D0D0F");
            SetResourceColor("Card", "#16171A");
            SetResourceColor("Card2", "#1B1C20");
            SetResourceColor("ControlBg", "#202126");
            SetResourceColor("TextPrimary", "#ECECF0");
            SetResourceColor("Muted", "#9899A1");
            SetResourceColor("Line", "#303138");
            SetResourceColor("Accent", "#FF7A00");
            SetResourceColor("Selection", "#2B2521");

            SetSystemResourceColor(System.Windows.SystemColors.WindowBrushKey, "#1B1C20");
            SetSystemResourceColor(System.Windows.SystemColors.ControlBrushKey, "#202126");
            SetSystemResourceColor(System.Windows.SystemColors.WindowTextBrushKey, "#ECECF0");
            SetSystemResourceColor(System.Windows.SystemColors.ControlTextBrushKey, "#ECECF0");
            SetSystemResourceColor(System.Windows.SystemColors.HighlightBrushKey, "#2B2521");
            SetSystemResourceColor(System.Windows.SystemColors.HighlightTextBrushKey, "#F4F4F6");
            SetSystemResourceColor(System.Windows.SystemColors.InactiveSelectionHighlightBrushKey, "#252529");

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
        _currentNowPlaying = data;
        _syncingMediaUi = true;

        try
        {
            SourceText.Text = string.IsNullOrWhiteSpace(data.SourceName)
                ? "MEDIA SESSION"
                : data.SourceName;
            TitleText.Text = data.Title;
            ArtistText.Text = string.IsNullOrWhiteSpace(data.Artist)
                ? "Unknown Artist"
                : data.Artist;

            var durationMs = Math.Max(0.001, data.Duration.TotalMilliseconds);
            double progress = Math.Clamp(
                data.Position.TotalMilliseconds / durationMs,
                0,
                1);

            if (!_mediaSeekDragging)
                TrackSlider.Value = progress;

            TrackSlider.IsEnabled =
                data.CanSeek && data.Duration > TimeSpan.Zero;

            ElapsedText.Text = FormatTime(data.Position);
            TimeSpan remaining = data.Duration - data.Position;
            DurationText.Text = data.Duration > TimeSpan.Zero
                ? $"-{FormatTime(remaining)}"
                : "0:00";

            PlayButton.Content = data.IsPlaying ? "❚❚" : "▶";
            PlayButton.ToolTip = data.IsPlaying
                ? L("Pause", "Tạm dừng")
                : L("Play", "Phát");

            PrevButton.IsEnabled = data.CanPrevious;
            PlayButton.IsEnabled = data.CanPlayPause;
            NextButton.IsEnabled = data.CanNext;
            ShuffleButton.IsEnabled = data.CanShuffle;
            RepeatButton.IsEnabled = data.CanRepeat;

            RepeatButton.Content = data.RepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track => "↻1",
                MediaPlaybackAutoRepeatMode.List => "↻∞",
                _ => "↻"
            };

            RepeatButton.ToolTip = data.RepeatMode switch
            {
                MediaPlaybackAutoRepeatMode.Track =>
                    L("Repeat one · click for repeat all", "Lặp 1 bài · bấm để lặp toàn bộ"),
                MediaPlaybackAutoRepeatMode.List =>
                    L("Repeat all · click to turn off", "Lặp toàn bộ · bấm để tắt"),
                _ =>
                    L("Repeat off · click for repeat one", "Đang tắt lặp · bấm để lặp 1 bài")
            };

            ShuffleButton.ToolTip = data.IsShuffleActive
                ? L("Shuffle on", "Xáo trộn đang bật")
                : L("Shuffle off", "Xáo trộn đang tắt");

            SetMediaModeButton(
                ShuffleButton,
                data.CanShuffle && data.IsShuffleActive);
            SetMediaModeButton(
                RepeatButton,
                data.CanRepeat &&
                data.RepeatMode != MediaPlaybackAutoRepeatMode.None);

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

            SyncSystemVolumeUi();
        }
        finally
        {
            _syncingMediaUi = false;
        }

        _serial.SendNowPlaying(data);
    }

    private void ClearNowPlaying()
    {
        _currentNowPlaying = null;
        _mediaSeekDragging = false;
        _syncingMediaUi = true;

        try
        {
            SourceText.Text = "MEDIA SESSION";
            TitleText.Text = "Nothing Playing";
            ArtistText.Text = "LumiPad";
            AlbumArtImage.Source = null;
            AlbumArtImage.Visibility = Visibility.Collapsed;
            AlbumArtFallback.Visibility = Visibility.Visible;

            TrackSlider.Value = 0;
            TrackSlider.IsEnabled = false;
            ElapsedText.Text = "0:00";
            DurationText.Text = "-0:00";

            PrevButton.IsEnabled = false;
            PlayButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            ShuffleButton.IsEnabled = false;
            RepeatButton.IsEnabled = false;

            PlayButton.Content = "▶";
            PlayButton.ToolTip = L("Play", "Phát");
            ShuffleButton.Content = "🔀";
            RepeatButton.Content = "↻";
            SetMediaModeButton(ShuffleButton, false);
            SetMediaModeButton(RepeatButton, false);
            SyncSystemVolumeUi(force: true);
        }
        finally
        {
            _syncingMediaUi = false;
        }

        _serial.ClearNowPlaying();
    }

    private void SetMediaModeButton(
        System.Windows.Controls.Button button,
        bool active)
    {
        button.Background =
            TryFindResource(active ? "Selection" : "ControlBg")
                as System.Windows.Media.Brush;
        button.BorderBrush =
            TryFindResource(active ? "Accent" : "Line")
                as System.Windows.Media.Brush;
    }

    private void SyncSystemVolumeUi(bool force = false)
    {
        if (!force &&
            DateTimeOffset.UtcNow - _lastVolumeUiSync <
                TimeSpan.FromMilliseconds(650))
        {
            return;
        }

        _lastVolumeUiSync = DateTimeOffset.UtcNow;

        if (!SystemVolumeService.TryGetState(out SystemVolumeState state))
            return;

        VolumeSlider.Value = state.Percent;
        VolumeText.Text = $"{Math.Round(state.Percent):0}%";
        _volumeMuted = state.IsMuted;
        MuteButton.Content =
            state.IsMuted || state.Percent <= 0.5 ? "🔇" : "🔊";
        MuteButton.ToolTip = state.IsMuted
            ? L("Unmute", "Bật tiếng")
            : L("Mute", "Tắt tiếng");
    }

    private static BitmapSource CreateArtworkBitmap(byte[] rgb332) =>
        CreateRgb332Bitmap(rgb332, 76, 76);

    private static BitmapSource CreateRgb332InterpolatedBitmap(
        byte[] first,
        byte[] second,
        double blend,
        int width,
        int height)
    {
        if (first.Length != width * height ||
            second.Length != width * height)
        {
            throw new ArgumentException(
                "RGB332 buffers do not match dimensions.");
        }

        blend = Math.Clamp(blend, 0.0, 1.0);
        int stride = width * 4;
        byte[] bgra = new byte[stride * height];

        for (int i = 0; i < width * height; i++)
        {
            byte a = first[i];
            byte b = second[i];

            int ar = (((a >> 5) & 0x07) * 255) / 7;
            int ag = (((a >> 2) & 0x07) * 255) / 7;
            int ab = ((a & 0x03) * 255) / 3;

            int br = (((b >> 5) & 0x07) * 255) / 7;
            int bg = (((b >> 2) & 0x07) * 255) / 7;
            int bb = ((b & 0x03) * 255) / 3;

            byte r = (byte)Math.Round(ar + (br - ar) * blend);
            byte g = (byte)Math.Round(ag + (bg - ag) * blend);
            byte bl = (byte)Math.Round(ab + (bb - ab) * blend);

            int p = i * 4;
            bgra[p] = bl;
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

    private static BitmapSource CreateRgb565Bitmap(
        byte[] rgb565,
        int width,
        int height)
    {
        if (rgb565.Length != width * height * 2)
            throw new ArgumentException(
                "RGB565 buffer size does not match dimensions.");

        int stride = width * 4;
        byte[] bgra = new byte[stride * height];

        for (int i = 0; i < width * height; i++)
        {
            ushort v = (ushort)(
                rgb565[i * 2] |
                (rgb565[i * 2 + 1] << 8));

            byte r = (byte)((((v >> 11) & 0x1F) * 255) / 31);
            byte g = (byte)((((v >> 5) & 0x3F) * 255) / 63);
            byte b = (byte)(((v & 0x1F) * 255) / 31);

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

    private void UpdateTransportIndicators()
    {
        if (HeaderUsbPath is null ||
            HeaderUsbDot is null ||
            HeaderBluetoothPath is null)
        {
            return;
        }

        var active = new SolidColorBrush(
            MediaColor.FromRgb(48, 209, 88));
        var inactive =
            TryFindResource("Muted") as System.Windows.Media.Brush ??
            new SolidColorBrush(MediaColor.FromRgb(154, 154, 160));
        var normalBorder =
            TryFindResource("Line") as System.Windows.Media.Brush ??
            new SolidColorBrush(MediaColor.FromRgb(58, 58, 60));

        bool usb = _serial.IsUsbConnected;
        bool bluetooth =
            !_serial.IsUsbConnected &&
            _serial.IsBluetoothConnected;

        HeaderUsbPath.Stroke = usb ? active : inactive;
        HeaderUsbDot.Fill = usb ? active : inactive;
        HeaderBluetoothPath.Stroke =
            bluetooth ? active : inactive;

        if (ConnectUsbButton is not null)
            ConnectUsbButton.BorderBrush =
                usb ? active : normalBorder;

        if (ConnectBluetoothButton is not null)
            ConnectBluetoothButton.BorderBrush =
                bluetooth ? active : normalBorder;

        UpdateProductHubUi();
    }

    private void UpdateSleepButtonUi()
    {
        if (SleepKeyboardButton is null ||
            SleepKeyboardIcon is null)
        {
            return;
        }

        var active =
            TryFindResource("Accent") as System.Windows.Media.Brush ??
            new SolidColorBrush(MediaColor.FromRgb(255, 122, 0));
        var normal =
            TryFindResource("ControlBg") as System.Windows.Media.Brush ??
            new SolidColorBrush(MediaColor.FromRgb(39, 39, 42));
        var text =
            TryFindResource("TextPrimary") as System.Windows.Media.Brush ??
            System.Windows.Media.Brushes.White;

        SleepKeyboardButton.Background =
            _keyboardSleeping ? active : normal;
        SleepKeyboardIcon.Foreground = text;
        SleepKeyboardButton.ToolTip =
            _keyboardSleeping
                ? L("Wake keyboard", "Đánh thức bàn phím")
                : L("Sleep keyboard", "Ngủ bàn phím");
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
                connected &&
                _screensaverAnimation is not null &&
                !string.Equals(
                    _screensaverSource,
                    "PcMonitor",
                    StringComparison.Ordinal);
        }

        if (ShowScreensaverNowButton is not null)
            ShowScreensaverNowButton.IsEnabled = connected;

        if (DisconnectButton is not null)
            DisconnectButton.IsEnabled = connected;

        if (RestartKeyboardButton is not null)
            RestartKeyboardButton.IsEnabled = connected;

        if (KeyboardDfuButton is not null)
            KeyboardDfuButton.IsEnabled = connected;

        if (FirmwareUpdateButton is not null)
            FirmwareUpdateButton.IsEnabled =
                !_updateBusy &&
                _firmwareUpdateAvailable &&
                connected &&
                _serial.IsUsbConnected;

        if (AppUpdateButton is not null)
            AppUpdateButton.IsEnabled =
                !_updateBusy && _appUpdateAvailable;

        if (CheckUpdatesButton is not null)
            CheckUpdatesButton.IsEnabled =
                !_updateBusy && !_checkingUpdates;

        if (SleepKeyboardButton is not null)
            SleepKeyboardButton.IsEnabled = connected;

        UpdateSettingsInfo();
        UpdateTransportIndicators();
        UpdateSleepButtonUi();
        UpdateScreensaverSourceUi();
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
            if (string.IsNullOrWhiteSpace(_serial.FirmwareHello))
            {
                FirmwareVersionText.Text = "--";
            }
            else
            {
                string legacy =
                    _serial.ProtocolVersion is > 0 and < 3
                        ? L(" · Legacy compatible", " · Tương thích firmware cũ")
                        : "";

                FirmwareVersionText.Text =
                    $"Protocol v{Math.Max(0, _serial.ProtocolVersion)}{legacy}";
            }
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
    }

    private async Task PollFirmwareDiagnosticsAsync()
    {
        if (!_serial.IsConnected)
            return;

        if (!_serial.SupportsDiagnostics)
            return;

        // Keep background diagnostics lightweight so USB/BLE control and
        // Now Playing remain responsive. The full queue is still drained over
        // subsequent ticks.
        for (int i = 0; i < 2; i++)
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
        var window = new DiagnosticLogWindow(
            string.Join(Environment.NewLine, _logLines),
            L("App + firmware events and errors",
              "Sự kiện và lỗi của app + firmware"))
        {
            Owner = this
        };

        window.ShowDialog();
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
            UpdateTransportIndicators();
        }
        else
        {
            _keyboardSleeping = false;
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
            _lastAppliedAutoProfile = -1;
            PollAutoProfile(force: true);
            SendAllRgb();
            SendPowerTiming();
            await UpdateMemoryUsageAsync();
            await UpdatePanelInfoAsync();
            UpdateTransportIndicators();
            UpdateSleepButtonUi();
            await CheckForUpdatesAsync(silent: true);
        }

        ConnectUsbButton.IsEnabled = true;
        ConnectBluetoothButton.IsEnabled = true;
        await UpdateProductOverviewAsync();
    }

    private void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        _autoReconnectEnabled = false;
        AddLog("INFO", "LINK", "Manual disconnect");
        _serial.Disconnect();
        DeviceStatus.Text = L("Not connected", "Chưa kết nối");
        DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(99, 99, 102));
        BottomStatus.Text = L("Disconnected.", "Đã ngắt kết nối.");
        _keyboardSleeping = false;
        _activeBatteryPercent = null;
        SetDeviceControlsEnabled(false);
        UpdateTransportIndicators();
        UpdateSleepButtonUi();
        UpdateProductHubUi();
    }

    private async Task AutoReconnectLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(3000, token);

                if (!_autoReconnectEnabled)
                    continue;

                if (_serial.IsConnected)
                {
                    // In default Auto mode, a plugged USB cable always wins
                    // over the Bluetooth companion link. Promote without
                    // clearing media state or re-uploading the screensaver.
                    if (_connectionPreference == "auto" &&
                        _serial.IsBluetoothConnected)
                    {
                        var promoted =
                            await _serial.PromoteToUsbIfAvailableAsync(token);

                        if (promoted is not null)
                        {
                            DeviceStatus.Text = promoted;
                            DeviceDot.Fill =
                                new SolidColorBrush(
                                    MediaColor.FromRgb(48, 209, 88));
                            BottomStatus.Text =
                                L("USB detected and selected automatically.",
                                  "Đã phát hiện USB và tự động chuyển sang USB.");
                            AddLog(
                                "INFO",
                                "LINK",
                                $"Auto-promoted to {promoted}");
                            SetDeviceControlsEnabled(true);
                            SendAllRgb();
                            SendPowerTiming();
                            await UpdateMemoryUsageAsync();
                            await UpdatePanelInfoAsync();
                            UpdateTransportIndicators();
                            await UpdateProductOverviewAsync();
                        }
                    }

                    continue;
                }

                var connection = _connectionPreference switch
                {
                    "usb" => await _serial.ConnectUsbAsync(token),
                    "bluetooth" => await _serial.ConnectBluetoothAsync(token),
                    _ => await _serial.AutoDetectAsync(token)
                };
                if (connection is null)
                    continue;

                _keyboardSleeping = false;
                DeviceStatus.Text = connection;
                DeviceDot.Fill = new SolidColorBrush(MediaColor.FromRgb(48, 209, 88));
                BottomStatus.Text = connection.StartsWith("Bluetooth", StringComparison.Ordinal)
                    ? L("Reconnected wirelessly after wake.", "Đã kết nối lại Bluetooth sau khi wake.")
                    : L("Reconnected over USB.", "Đã kết nối lại qua USB.");

                SetDeviceControlsEnabled(true);
                _lastAppliedAutoProfile = -1;
                PollAutoProfile(force: true);
                SendAllRgb();
                SendPowerTiming();
                await UpdateMemoryUsageAsync();
                await UpdatePanelInfoAsync();
                await RestoreScreensaverAfterReconnectAsync();
                UpdateTransportIndicators();
                UpdateSleepButtonUi();
                await UpdateProductOverviewAsync();
                await CheckForUpdatesAsync(silent: true);
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
        _serial.SetScreensaverSource(
            string.Equals(
                _screensaverSource,
                "PcMonitor",
                StringComparison.Ordinal));
        _serial.SetSleepTimeout(_sleepDelaySeconds);
        _serial.SetRgbIdleTimeout(_rgbIdleDelaySeconds);
        _serial.SetDeepSleepTimeout(_deepSleepDelaySeconds);
    }

    private void ScreensaverSourceCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (ScreensaverSourceCombo?.SelectedItem is not ComboBoxItem item)
            return;

        _screensaverSource =
            string.Equals(item.Tag?.ToString(), "PcMonitor", StringComparison.Ordinal)
                ? "PcMonitor"
                : "Media";

        UpdateScreensaverSourceUi();

        if (_uiReady)
            SaveAppSettings();

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetScreensaverSource(
                string.Equals(
                    _screensaverSource,
                    "PcMonitor",
                    StringComparison.Ordinal));
        }
    }

    private void UpdateScreensaverSourceUi()
    {
        if (ScreensaverSourceHint is null)
            return;

        bool pc =
            string.Equals(
                _screensaverSource,
                "PcMonitor",
                StringComparison.Ordinal);

        ScreensaverSourceHint.Text = pc
            ? L("Live PC telemetry", "Thông số PC trực tiếp")
            : L("Uploaded media", "GIF / ảnh đã tải lên");

        if (SendScreensaverButton is not null)
            SendScreensaverButton.IsEnabled =
                !pc &&
                _serial.IsConnected &&
                _screensaverAnimation is not null;
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

    private void RgbIdleDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _rgbIdleDelaySeconds =
            ComboSeconds(sender, _rgbIdleDelaySeconds);

        if (_uiReady)
            SaveAppSettings();

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetRgbIdleTimeout(_rgbIdleDelaySeconds);
            BottomStatus.Text = L(
                _rgbIdleDelaySeconds == 0
                    ? "LED idle timeout: Never"
                    : $"LED idle timeout: {_rgbIdleDelaySeconds}s",
                _rgbIdleDelaySeconds == 0
                    ? "Tắt LED khi rảnh: Không bao giờ"
                    : $"Tắt LED khi rảnh: {_rgbIdleDelaySeconds} giây");
        }
    }

    private void DeepSleepDelayCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        _deepSleepDelaySeconds =
            ComboSeconds(sender, _deepSleepDelaySeconds);

        if (_uiReady)
            SaveAppSettings();

        if (_uiReady && _serial.IsConnected)
        {
            _serial.SetDeepSleepTimeout(_deepSleepDelaySeconds);
            BottomStatus.Text = L(
                _deepSleepDelaySeconds == 0
                    ? "Deep sleep: Never"
                    : $"Deep sleep: {_deepSleepDelaySeconds}s",
                _deepSleepDelaySeconds == 0
                    ? "Ngủ sâu: Không bao giờ"
                    : $"Ngủ sâu: {_deepSleepDelaySeconds} giây");
        }
    }

    private async void SleepKeyboard_Click(object sender, RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            BottomStatus.Text =
                L("Connect LumiPad before using sleep.",
                  "Hãy kết nối LumiPad trước khi dùng chế độ ngủ.");
            return;
        }

        try
        {
            if (_keyboardSleeping)
            {
                await _serial.WakeKeyboardAsync();
                _keyboardSleeping = false;
                BottomStatus.Text =
                    L("Keyboard display and RGB are awake.",
                      "Màn hình và RGB của bàn phím đã bật lại.");
            }
            else
            {
                await _serial.SleepKeyboardAsync();
                _keyboardSleeping = true;
                BottomStatus.Text =
                    L("Keyboard display and RGB are sleeping. Press again to wake.",
                      "Màn hình và RGB đang ngủ. Nhấn lại để bật lên.");
            }

            UpdateSleepButtonUi();
        }
        catch (Exception ex)
        {
            BottomStatus.Text =
                L($"Sleep/wake failed: {ex.Message}",
                  $"Ngủ/đánh thức thất bại: {ex.Message}");
        }
    }


    private sealed record LatestReleaseInfo(
        string Tag,
        string AppVersion,
        string FirmwareVersion,
        string ManifestUrl);

    private string CurrentAppVersion()
    {
        Version? version =
            System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetName()
                .Version;

        return version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

    private string? CurrentFirmwareVersion()
    {
        string hello = _serial.FirmwareHello ?? "";

        foreach (string part in hello.Split('|'))
        {
            if (part.StartsWith(
                    "FW=",
                    StringComparison.OrdinalIgnoreCase))
            {
                string value = part[3..].Trim();
                return string.IsNullOrWhiteSpace(value)
                    ? null
                    : value;
            }
        }

        return null;
    }

    private static Version ParseVersionLoose(string value)
    {
        string clean =
            (value ?? "")
                .Trim()
                .TrimStart('v', 'V');

        string numeric =
            new string(
                clean.TakeWhile(
                    c => char.IsDigit(c) || c == '.')
                     .ToArray());

        if (Version.TryParse(numeric, out Version? version))
            return version;

        return new Version(0, 0, 0);
    }

    private static bool IsNewerVersion(
        string latest,
        string current) =>
        ParseVersionLoose(latest) >
        ParseVersionLoose(current);

    private async Task<LatestReleaseInfo> GetLatestReleaseInfoAsync()
    {
        using var response =
            await UpdateHttp.GetAsync(UpdateReleaseApi);
        response.EnsureSuccessStatusCode();

        using JsonDocument release =
            JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

        string tag =
            release.RootElement.TryGetProperty(
                "tag_name",
                out JsonElement tagElement)
                ? tagElement.GetString() ?? ""
                : "";

        string manifestUrl = "";

        if (release.RootElement.TryGetProperty(
                "assets",
                out JsonElement assets))
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name =
                    asset.GetProperty("name").GetString() ?? "";

                if (!string.Equals(
                        name,
                        "release-manifest.json",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                manifestUrl =
                    asset.GetProperty(
                        "browser_download_url").GetString() ?? "";
                break;
            }
        }

        string appVersion =
            tag.TrimStart('v', 'V');
        string firmwareVersion = appVersion;

        if (!string.IsNullOrWhiteSpace(manifestUrl))
        {
            using var manifestResponse =
                await UpdateHttp.GetAsync(manifestUrl);
            manifestResponse.EnsureSuccessStatusCode();

            using JsonDocument manifest =
                JsonDocument.Parse(
                    await manifestResponse.Content.ReadAsStringAsync());

            if (manifest.RootElement.TryGetProperty(
                    "appVersion",
                    out JsonElement appElement))
            {
                appVersion =
                    appElement.GetString() ?? appVersion;
            }
            else if (manifest.RootElement.TryGetProperty(
                         "version",
                         out JsonElement legacyElement))
            {
                appVersion =
                    legacyElement.GetString() ?? appVersion;
            }

            if (manifest.RootElement.TryGetProperty(
                    "firmwareVersion",
                    out JsonElement fwElement))
            {
                firmwareVersion =
                    fwElement.GetString() ?? firmwareVersion;
            }
        }

        if (string.Equals(
                _activeProduct.Id,
                ProductCatalog.PixelPro.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            using var pixelResponse =
                await UpdateHttp.GetAsync(PixelProFirmwareReleaseApi);
            pixelResponse.EnsureSuccessStatusCode();

            using JsonDocument pixelRelease =
                JsonDocument.Parse(
                    await pixelResponse.Content.ReadAsStringAsync());

            string pixelTag =
                pixelRelease.RootElement.TryGetProperty(
                    "tag_name",
                    out JsonElement pixelTagElement)
                    ? pixelTagElement.GetString() ?? ""
                    : "";

            string cleanPixelTag =
                pixelTag.Trim().TrimStart('v', 'V');

            if (!string.IsNullOrWhiteSpace(cleanPixelTag))
                firmwareVersion = cleanPixelTag;

            if (pixelRelease.RootElement.TryGetProperty(
                    "assets",
                    out JsonElement pixelAssets))
            {
                foreach (JsonElement asset in pixelAssets.EnumerateArray())
                {
                    string name =
                        asset.GetProperty("name").GetString() ?? "";

                    if (!string.Equals(
                            name,
                            "firmware-manifest.json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string pixelManifestUrl =
                        asset.GetProperty(
                            "browser_download_url").GetString() ?? "";

                    if (string.IsNullOrWhiteSpace(pixelManifestUrl))
                        break;

                    using var pixelManifestResponse =
                        await UpdateHttp.GetAsync(pixelManifestUrl);
                    pixelManifestResponse.EnsureSuccessStatusCode();

                    using JsonDocument pixelManifest =
                        JsonDocument.Parse(
                            await pixelManifestResponse.Content.ReadAsStringAsync());

                    if (pixelManifest.RootElement.TryGetProperty(
                            "version",
                            out JsonElement pixelVersion))
                    {
                        firmwareVersion =
                            pixelVersion.GetString() ?? firmwareVersion;
                    }

                    break;
                }
            }
        }

        return new LatestReleaseInfo(
            tag,
            appVersion,
            firmwareVersion,
            manifestUrl);
    }

    private void RefreshUpdateUi()
    {
        if (AppUpdateVersionText is null ||
            FirmwareUpdateVersionText is null)
        {
            return;
        }

        string currentApp = CurrentAppVersion();
        string? currentFirmware =
            _serial.IsConnected
                ? CurrentFirmwareVersion()
                : null;

        AppUpdateVersionText.Text =
            $"Current v{currentApp} · Latest " +
            (string.IsNullOrWhiteSpace(_latestAppVersion)
                ? "--"
                : $"v{_latestAppVersion}");

        FirmwareUpdateVersionText.Text =
            "Current " +
            (currentFirmware is null
                ? (_serial.IsConnected ? "legacy / unknown" : "--")
                : $"v{currentFirmware}") +
            " · Latest " +
            (string.IsNullOrWhiteSpace(_latestFirmwareVersion)
                ? "--"
                : $"v{_latestFirmwareVersion}");

        if (AppUpdateStateText is not null)
        {
            AppUpdateStateText.Text =
                _appUpdateAvailable
                    ? L("Update available", "Có bản mới")
                    : L("Up to date", "Đã mới nhất");
        }

        if (FirmwareUpdateStateText is not null)
        {
            if (!_serial.IsConnected)
            {
                FirmwareUpdateStateText.Text =
                    L(
                        "Connect keyboard to read firmware version",
                        "Kết nối bàn phím để đọc phiên bản firmware");
            }
            else if (_firmwareUpdateAvailable)
            {
                bool pixelBootstrapRequired =
                    string.Equals(
                        _activeProduct.Id,
                        ProductCatalog.PixelPro.Id,
                        StringComparison.OrdinalIgnoreCase) &&
                    _serial is QmkRawHidLink pixelLink &&
                    !pixelLink.SupportsFirmwareOta;

                FirmwareUpdateStateText.Text =
                    pixelBootstrapRequired
                        ? L(
                            "One-time v0.1.5 bootstrap required; future updates are one-click",
                            "Cần nạp bootstrap v0.1.5 một lần; các bản sau cập nhật 1 nút")
                        : _serial.IsUsbConnected
                            ? L("Update available", "Có bản mới")
                            : L(
                                "Connect by USB to update firmware",
                                "Cắm USB để cập nhật firmware");
            }
            else
            {
                FirmwareUpdateStateText.Text =
                    L("Up to date", "Đã mới nhất");
            }
        }

        int count =
            (_appUpdateAvailable ? 1 : 0) +
            (_firmwareUpdateAvailable ? 1 : 0);

        if (UpdateStatusText is not null)
        {
            UpdateStatusText.Text =
                count switch
                {
                    0 => L(
                        "Everything is up to date.",
                        "Tất cả đã là bản mới nhất."),
                    1 => L(
                        "1 update available.",
                        "Có 1 bản cập nhật mới."),
                    _ => L(
                        "2 updates available.",
                        "Có 2 bản cập nhật mới.")
                };
        }

        SetDeviceControlsEnabled(
            _serial.IsConnected);
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_checkingUpdates || _updateBusy)
            return;

        _checkingUpdates = true;

        try
        {
            if (!silent && UpdateStatusText is not null)
            {
                UpdateStatusText.Text =
                    L(
                        "Checking for updates…",
                        "Đang kiểm tra cập nhật…");
            }

            LatestReleaseInfo info =
                await GetLatestReleaseInfoAsync();

            _latestReleaseTag = info.Tag;
            _latestAppVersion = info.AppVersion;
            _latestFirmwareVersion =
                info.FirmwareVersion;

            string currentApp =
                CurrentAppVersion();
            string? currentFirmware =
                CurrentFirmwareVersion();

            _appUpdateAvailable =
                IsNewerVersion(
                    _latestAppVersion,
                    currentApp);

            _firmwareUpdateAvailable =
                _serial.IsConnected &&
                (currentFirmware is null ||
                 IsNewerVersion(
                    _latestFirmwareVersion,
                    currentFirmware));

            RefreshUpdateUi();

            AddLog(
                "INFO",
                "UPDATE",
                $"Check complete: app {currentApp}->{_latestAppVersion}, " +
                $"firmware {currentFirmware ?? "legacy"}->{_latestFirmwareVersion}");
        }
        catch (Exception ex)
        {
            if (UpdateStatusText is not null)
            {
                UpdateStatusText.Text =
                    L(
                        $"Update check failed: {ex.Message}",
                        $"Kiểm tra cập nhật lỗi: {ex.Message}");
            }

            AddLog(
                "WARN",
                "UPDATE",
                $"Update check failed: {ex.Message}");
        }
        finally
        {
            _checkingUpdates = false;

            if (CheckUpdatesButton is not null)
                CheckUpdatesButton.IsEnabled =
                    !_updateBusy;
        }
    }

    private async void CheckUpdates_Click(
        object sender,
        RoutedEventArgs e)
    {
        await CheckForUpdatesAsync(silent: false);
    }

    private async Task<(string Tag, string Url)> FindLatestAssetAsync(
        string assetName,
        string? releaseApi = null)
    {
        using var response =
            await UpdateHttp.GetAsync(
                string.IsNullOrWhiteSpace(releaseApi)
                    ? UpdateReleaseApi
                    : releaseApi);
        response.EnsureSuccessStatusCode();

        using JsonDocument json =
            JsonDocument.Parse(
                await response.Content.ReadAsStringAsync());

        string tag =
            json.RootElement.TryGetProperty(
                "tag_name",
                out JsonElement tagElement)
                ? tagElement.GetString() ?? ""
                : "";

        if (!json.RootElement.TryGetProperty(
                "assets",
                out JsonElement assets))
        {
            throw new InvalidOperationException(
                "Latest release has no assets.");
        }

        foreach (JsonElement asset in assets.EnumerateArray())
        {
            string name =
                asset.GetProperty("name").GetString() ?? "";

            if (!string.Equals(
                    name,
                    assetName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string url =
                asset.GetProperty(
                    "browser_download_url").GetString() ?? "";

            if (string.IsNullOrWhiteSpace(url))
                break;

            return (tag, url);
        }

        throw new InvalidOperationException(
            $"Release asset not found: {assetName}");
    }

    private static async Task DownloadFileAsync(
        string url,
        string destination)
    {
        using var response =
            await UpdateHttp.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using IO.Stream input =
            await response.Content.ReadAsStreamAsync();
        await using IO.FileStream output =
            new(
                destination,
                IO.FileMode.Create,
                IO.FileAccess.Write,
                IO.FileShare.None);

        await input.CopyToAsync(output);
    }

    private static string? FindUf2Drive(
        ISet<string>? exclude = null)
    {
        foreach (IO.DriveInfo drive in IO.DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                    continue;

                string root = drive.RootDirectory.FullName;
                if (exclude is not null &&
                    exclude.Contains(root))
                {
                    continue;
                }

                if (IO.File.Exists(
                        IO.Path.Combine(root, "INFO_UF2.TXT")))
                {
                    return root;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static HashSet<string> CurrentUf2Drives()
    {
        var result =
            new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

        foreach (IO.DriveInfo drive in IO.DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady &&
                    IO.File.Exists(
                        IO.Path.Combine(
                            drive.RootDirectory.FullName,
                            "INFO_UF2.TXT")))
                {
                    result.Add(
                        drive.RootDirectory.FullName);
                }
            }
            catch
            {
            }
        }

        return result;
    }

    private static HashSet<string> CurrentSerialPorts() =>
        new(
            System.IO.Ports.SerialPort.GetPortNames(),
            StringComparer.OrdinalIgnoreCase);

    private static string? FindNewSerialPort(
        ISet<string> before)
    {
        string[] ports =
            System.IO.Ports.SerialPort.GetPortNames();

        string? fresh =
            ports.FirstOrDefault(
                port => !before.Contains(port));

        if (!string.IsNullOrWhiteSpace(fresh))
            return fresh;

        // ESP32-S2 ROM USB CDC is normally the only transient COM port here.
        // If Windows reused a COM number, prefer the sole port when possible.
        return ports.Length == 1
            ? ports[0]
            : null;
    }

    private async Task<string> EnsureEspToolAsync(
        string toolRoot)
    {
        string? existing =
            IO.Directory.Exists(toolRoot)
                ? IO.Directory
                    .EnumerateFiles(
                        toolRoot,
                        "esptool.exe",
                        IO.SearchOption.AllDirectories)
                    .FirstOrDefault()
                : null;

        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        IO.Directory.CreateDirectory(toolRoot);

        string zipPath =
            IO.Path.Combine(
                toolRoot,
                "esptool-windows-amd64.zip");

        UpdateStatusText.Text =
            L(
                "Downloading Espressif flashing engine for the one-time bootstrap…",
                "Đang tải bộ nạp chính thức của Espressif cho lần bootstrap duy nhất…");

        await DownloadFileAsync(
            "https://github.com/espressif/esptool/releases/download/v5.4.0/esptool-v5.4.0-windows-amd64.zip",
            zipPath);

        string extractPath =
            IO.Path.Combine(
                toolRoot,
                "esptool-v5.4.0");

        if (IO.Directory.Exists(extractPath))
            IO.Directory.Delete(extractPath, true);

        IO.Directory.CreateDirectory(extractPath);
        ZipFile.ExtractToDirectory(
            zipPath,
            extractPath,
            true);

        try
        {
            IO.File.Delete(zipPath);
        }
        catch
        {
        }

        string? exe =
            IO.Directory
                .EnumerateFiles(
                    extractPath,
                    "esptool.exe",
                    IO.SearchOption.AllDirectories)
                .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(exe))
        {
            throw new InvalidOperationException(
                "Espressif esptool.exe was not found after extraction.");
        }

        return exe;
    }

    private async Task BootstrapPixelProFirmwareAsync()
    {
        var confirm = System.Windows.MessageBox.Show(
            L(
                "This PIXEL PRO needs a one-time bootstrap before in-app OTA can work. Lumi Macropad can do it itself: it will download the official Espressif flashing engine and the latest PIXEL PRO firmware. You only need to put the board into BOOT mode when prompted. Continue?",
                "PIXEL PRO này cần bootstrap một lần trước khi OTA trong app hoạt động. Lumi Macropad sẽ tự tải bộ nạp chính thức của Espressif và firmware mới nhất. Bạn chỉ cần đưa mạch vào BOOT khi app yêu cầu. Tiếp tục?"),
            "PIXEL PRO · Enable one-click updates",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        SetDeviceControlsEnabled(true);

        string workRoot =
            IO.Path.Combine(
                IO.Path.GetTempPath(),
                "LumiPad-PixelPro-Bootstrap-" +
                Guid.NewGuid().ToString("N"));
        string mergedPath =
            IO.Path.Combine(
                workRoot,
                "PIXEL_PRO_merged.bin");

        try
        {
            IO.Directory.CreateDirectory(workRoot);

            UpdateStatusText.Text =
                L(
                    "Downloading PIXEL PRO bootstrap firmware…",
                    "Đang tải firmware bootstrap PIXEL PRO…");

            var firmware =
                await FindLatestAssetAsync(
                    "PIXEL_PRO_merged.bin",
                    PixelProFirmwareReleaseApi);

            await DownloadFileAsync(
                firmware.Url,
                mergedPath);

            string localTools =
                IO.Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData),
                    "LumiPad",
                    "Tools",
                    "esptool-5.4.0");

            string esptool =
                await EnsureEspToolAsync(localTools);

            HashSet<string> portsBefore =
                CurrentSerialPorts();

            _autoReconnectEnabled = false;
            _serial.Disconnect();

            UpdateStatusText.Text =
                L(
                    "Waiting for PIXEL PRO BOOT mode…",
                    "Đang chờ PIXEL PRO vào BOOT…");

            var bootPrompt = System.Windows.MessageBox.Show(
                L(
                    "Put PIXEL PRO into ROM BOOT mode now:\n\n1. Hold BOOT.\n2. Press RESET once.\n3. Release RESET.\n4. Release BOOT.\n5. Click OK here.\n\nLumi Macropad will detect the new COM port and flash automatically.",
                    "Đưa PIXEL PRO vào ROM BOOT ngay:\n\n1. Giữ BOOT.\n2. Nhấn RESET một lần.\n3. Thả RESET.\n4. Thả BOOT.\n5. Bấm OK ở đây.\n\nLumi Macropad sẽ tự tìm cổng COM mới và tự nạp."),
                "PIXEL PRO · BOOT mode",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information);

            if (bootPrompt != MessageBoxResult.OK)
                return;

            string? bootPort = null;

            for (int i = 0; i < 30 && bootPort is null; i++)
            {
                await Task.Delay(250);
                bootPort =
                    FindNewSerialPort(
                        portsBefore);
            }

            if (string.IsNullOrWhiteSpace(bootPort))
            {
                throw new InvalidOperationException(
                    L(
                        "No new ESP32-S2 boot COM port appeared. Repeat BOOT + RESET and try again.",
                        "Không thấy cổng COM boot mới của ESP32-S2. Làm lại BOOT + RESET rồi thử lại."));
            }

            UpdateStatusText.Text =
                L(
                    $"Flashing PIXEL PRO on {bootPort}…",
                    $"Đang nạp PIXEL PRO trên {bootPort}…");

            var startInfo =
                new ProcessStartInfo
                {
                    FileName = esptool,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

            startInfo.ArgumentList.Add("--chip");
            startInfo.ArgumentList.Add("esp32s2");
            startInfo.ArgumentList.Add("--port");
            startInfo.ArgumentList.Add(bootPort);
            startInfo.ArgumentList.Add("--baud");
            startInfo.ArgumentList.Add("460800");
            startInfo.ArgumentList.Add("--before");
            startInfo.ArgumentList.Add("no-reset");
            startInfo.ArgumentList.Add("--after");
            startInfo.ArgumentList.Add("hard-reset");
            startInfo.ArgumentList.Add("write-flash");
            startInfo.ArgumentList.Add("0x0");
            startInfo.ArgumentList.Add(mergedPath);

            using Process process =
                Process.Start(startInfo) ??
                throw new InvalidOperationException(
                    "Could not start Espressif esptool.");

            Task<string> stdoutTask =
                process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask =
                process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            string stdout = await stdoutTask;
            string stderr = await stderrTask;

            AddLog(
                process.ExitCode == 0 ? "INFO" : "ERROR",
                "ESPTOOL",
                stdout + Environment.NewLine + stderr);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    L(
                        $"Espressif flashing failed (exit {process.ExitCode}). Open Diagnostics for details.",
                        $"Nạp bằng Espressif lỗi (mã {process.ExitCode}). Mở Diagnostics để xem chi tiết."));
            }

            UpdateStatusText.Text =
                L(
                    $"PIXEL PRO {firmware.Tag} bootstrap installed. Reconnecting…",
                    $"Đã nạp bootstrap PIXEL PRO {firmware.Tag}. Đang kết nối lại…");

            _autoReconnectEnabled = true;

            string? connection = null;
            for (int i = 0; i < 12 && connection is null; i++)
            {
                await Task.Delay(500);
                connection =
                    await _serial.ConnectUsbAsync();
            }

            if (connection is null)
            {
                System.Windows.MessageBox.Show(
                    L(
                        "Flash completed. ESP32-S2 sometimes needs one manual RESET after flashing. Press RESET once, then click Connect USB. After this bootstrap, future firmware updates are fully automatic inside Lumi Macropad.",
                        "Đã nạp xong. ESP32-S2 đôi khi cần nhấn RESET một lần sau khi flash. Nhấn RESET rồi bấm Kết nối USB. Sau lần bootstrap này, các firmware sau sẽ cập nhật hoàn toàn tự động trong Lumi Macropad."),
                    "PIXEL PRO bootstrap complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                DeviceStatus.Text = connection;
                DeviceDot.Fill =
                    new SolidColorBrush(
                        MediaColor.FromRgb(48, 209, 88));

                System.Windows.MessageBox.Show(
                    L(
                        "Bootstrap complete. PIXEL PRO now supports one-click firmware updates directly from GitHub inside Lumi Macropad.",
                        "Bootstrap hoàn tất. Từ giờ PIXEL PRO có thể cập nhật firmware 1 nút trực tiếp từ GitHub trong Lumi Macropad."),
                    "PIXEL PRO",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            await CheckForUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            _autoReconnectEnabled = true;
            UpdateStatusText.Text =
                L(
                    $"PIXEL PRO bootstrap failed: {ex.Message}",
                    $"Bootstrap PIXEL PRO lỗi: {ex.Message}");
            AddLog(
                "ERROR",
                "UPDATE",
                $"PIXEL PRO bootstrap failed: {ex}");
        }
        finally
        {
            try
            {
                if (IO.Directory.Exists(workRoot))
                    IO.Directory.Delete(workRoot, true);
            }
            catch
            {
            }

            _updateBusy = false;
            SetDeviceControlsEnabled(
                _serial.IsConnected);
        }
    }

    private async Task UpdatePixelProFirmwareAsync()
    {
        if (!_serial.IsConnected ||
            !_serial.IsUsbConnected ||
            _serial is not QmkRawHidLink pixelLink)
        {
            System.Windows.MessageBox.Show(
                L(
                    "Connect PIXEL PRO by USB first.",
                    "Hãy kết nối PIXEL PRO bằng USB trước."),
                "PIXEL PRO Firmware Update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!pixelLink.SupportsFirmwareOta)
        {
            await BootstrapPixelProFirmwareAsync();
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            L(
                "Download the latest PIXEL PRO firmware from GitHub and install it directly over USB now?",
                "Tải firmware PIXEL PRO mới nhất từ GitHub và nạp trực tiếp qua USB ngay?"),
            "PIXEL PRO Firmware Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        SetDeviceControlsEnabled(true);

        string tempFile =
            IO.Path.Combine(
                IO.Path.GetTempPath(),
                $"pixel-pro-ota-{Guid.NewGuid():N}.bin");

        try
        {
            UpdateStatusText.Text =
                L(
                    "Downloading latest PIXEL PRO firmware…",
                    "Đang tải firmware PIXEL PRO mới nhất…");

            var asset =
                await FindLatestAssetAsync(
                    "PIXEL_PRO_OTA.bin",
                    PixelProFirmwareReleaseApi);

            await DownloadFileAsync(
                asset.Url,
                tempFile);

            byte[] image =
                await IO.File.ReadAllBytesAsync(tempFile);

            if (image.Length < 4096)
            {
                throw new InvalidOperationException(
                    "Downloaded PIXEL PRO firmware image is invalid.");
            }

            var progress = new Progress<int>(value =>
            {
                UpdateStatusText.Text =
                    L(
                        $"Installing PIXEL PRO firmware… {value}%",
                        $"Đang nạp firmware PIXEL PRO… {value}%");
            });

            await pixelLink.InstallFirmwareAsync(
                image,
                progress);

            UpdateStatusText.Text =
                L(
                    $"Firmware {asset.Tag} installed. Reconnecting…",
                    $"Đã nạp firmware {asset.Tag}. Đang kết nối lại…");

            await Task.Delay(1200);
            _serial.Disconnect();

            _autoReconnectEnabled = true;

            string? connection = null;
            for (int i = 0; i < 12 && connection is null; i++)
            {
                await Task.Delay(500);
                connection =
                    await _serial.ConnectUsbAsync();
            }

            if (connection is null)
            {
                throw new IO.IOException(
                    L(
                        "Firmware installed, but PIXEL PRO did not reconnect yet. Press RESET once, then Connect USB.",
                        "Đã nạp firmware nhưng PIXEL PRO chưa kết nối lại. Nhấn RESET một lần rồi bấm Kết nối USB."));
            }

            DeviceStatus.Text = connection;
            DeviceDot.Fill =
                new SolidColorBrush(
                    MediaColor.FromRgb(48, 209, 88));

            UpdateStatusText.Text =
                L(
                    $"PIXEL PRO firmware update complete · {asset.Tag}",
                    $"Cập nhật firmware PIXEL PRO hoàn tất · {asset.Tag}");

            await CheckForUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text =
                L(
                    $"PIXEL PRO firmware update failed: {ex.Message}",
                    $"Cập nhật firmware PIXEL PRO lỗi: {ex.Message}");
            AddLog(
                "ERROR",
                "UPDATE",
                $"PIXEL PRO firmware update failed: {ex}");
        }
        finally
        {
            try
            {
                if (IO.File.Exists(tempFile))
                    IO.File.Delete(tempFile);
            }
            catch
            {
            }

            _updateBusy = false;
            SetDeviceControlsEnabled(
                _serial.IsConnected);
        }
    }

    private async void FirmwareUpdate_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_updateBusy)
            return;

        if (string.Equals(
                _activeProduct.Id,
                ProductCatalog.PixelPro.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            await UpdatePixelProFirmwareAsync();
            return;
        }

        if (!_serial.IsConnected ||
            !_serial.IsUsbConnected)
        {
            System.Windows.MessageBox.Show(
                L(
                    "Connect DIAL DESK by USB first. The app will enter UF2 bootloader and flash the latest firmware automatically.",
                    "Hãy cắm DIAL DESK bằng USB trước. App sẽ tự vào UF2 bootloader và tự nạp firmware mới nhất."),
                "LumiPad Firmware Update",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            L(
                "Download and install the latest DIAL DESK firmware now?",
                "Tải và tự nạp firmware DIAL DESK mới nhất ngay bây giờ?"),
            "LumiPad Firmware Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        SetDeviceControlsEnabled(true);

        string tempFile =
            IO.Path.Combine(
                IO.Path.GetTempPath(),
                $"dial-desk-{Guid.NewGuid():N}.uf2");

        try
        {
            UpdateStatusText.Text =
                L(
                    "Downloading latest firmware…",
                    "Đang tải firmware mới nhất…");

            var asset =
                await FindLatestAssetAsync(
                    "firmware.uf2");
            await DownloadFileAsync(
                asset.Url,
                tempFile);

            HashSet<string> before =
                CurrentUf2Drives();

            UpdateStatusText.Text =
                L(
                    "Entering UF2 bootloader…",
                    "Đang vào UF2 bootloader…");

            _autoReconnectEnabled = false;
            await _serial.EnterDfuAsync();
            await Task.Delay(250);
            _serial.Disconnect();

            string? uf2Root = null;
            for (int i = 0; i < 40 && uf2Root is null; i++)
            {
                await Task.Delay(250);
                uf2Root = FindUf2Drive(before);
            }

            uf2Root ??= FindUf2Drive();

            if (string.IsNullOrWhiteSpace(uf2Root))
            {
                throw new InvalidOperationException(
                    L(
                        "UF2 drive did not appear. Check the USB cable and retry.",
                        "Không thấy ổ UF2. Kiểm tra cáp USB rồi thử lại."));
            }

            UpdateStatusText.Text =
                L(
                    $"Flashing {asset.Tag}…",
                    $"Đang nạp {asset.Tag}…");

            string target =
                IO.Path.Combine(
                    uf2Root,
                    "firmware.uf2");

            IO.File.Copy(
                tempFile,
                target,
                true);

            await Task.Delay(1800);

            _autoReconnectEnabled = true;
            UpdateStatusText.Text =
                L(
                    "Firmware installed. Reconnecting…",
                    "Đã nạp firmware. Đang kết nối lại…");

            await DetectAsync();

            UpdateStatusText.Text =
                L(
                    $"Firmware update complete · {asset.Tag}",
                    $"Cập nhật firmware hoàn tất · {asset.Tag}");
            await CheckForUpdatesAsync(silent: true);
        }
        catch (Exception ex)
        {
            _autoReconnectEnabled = true;
            UpdateStatusText.Text =
                L(
                    $"Firmware update failed: {ex.Message}",
                    $"Cập nhật firmware lỗi: {ex.Message}");
            AddLog(
                "ERROR",
                "UPDATE",
                $"Firmware update failed: {ex}");
        }
        finally
        {
            try
            {
                if (IO.File.Exists(tempFile))
                    IO.File.Delete(tempFile);
            }
            catch
            {
            }

            _updateBusy = false;
            SetDeviceControlsEnabled(
                _serial.IsConnected);
        }
    }

    private async void AppUpdate_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_updateBusy)
            return;

        var confirm = System.Windows.MessageBox.Show(
            L(
                "Download the latest LumiPad app, replace this version, then reopen it automatically?",
                "Tải LumiPad mới nhất, thay bản hiện tại rồi tự mở lại app?"),
            "LumiPad App Update",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        _updateBusy = true;
        SetDeviceControlsEnabled(
            _serial.IsConnected);

        string updateRoot =
            IO.Path.Combine(
                IO.Path.GetTempPath(),
                "LumiPadUpdate-" +
                Guid.NewGuid().ToString("N"));
        string zipPath =
            IO.Path.Combine(
                updateRoot,
                "app.zip");
        string stagePath =
            IO.Path.Combine(
                updateRoot,
                "stage");

        try
        {
            IO.Directory.CreateDirectory(updateRoot);

            UpdateStatusText.Text =
                L(
                    "Downloading latest app…",
                    "Đang tải app mới nhất…");

            var asset =
                await FindLatestAssetAsync(
                    "LumiPad-Windows-x64.zip");

            await DownloadFileAsync(
                asset.Url,
                zipPath);

            IO.Directory.CreateDirectory(stagePath);
            ZipFile.ExtractToDirectory(
                zipPath,
                stagePath,
                true);

            string currentExe =
                Environment.ProcessPath ??
                throw new InvalidOperationException(
                    "Current executable path is unavailable.");
            string targetDir =
                IO.Path.GetDirectoryName(currentExe) ??
                throw new InvalidOperationException(
                    "Current app directory is unavailable.");
            string exeName =
                IO.Path.GetFileName(currentExe);

            string stagedExe =
                IO.Directory
                    .EnumerateFiles(
                        stagePath,
                        exeName,
                        IO.SearchOption.AllDirectories)
                    .FirstOrDefault()
                ?? IO.Directory
                    .EnumerateFiles(
                        stagePath,
                        "*.exe",
                        IO.SearchOption.AllDirectories)
                    .FirstOrDefault()
                ?? throw new InvalidOperationException(
                    "Downloaded app package has no executable.");

            string sourceDir =
                IO.Path.GetDirectoryName(stagedExe)!;

            string scriptPath =
                IO.Path.Combine(
                    updateRoot,
                    "install-update.ps1");

            string script =
$@"$ErrorActionPreference = 'Stop'
$pidToWait = {Environment.ProcessId}
$source = '{sourceDir.Replace("'", "''")}'
$target = '{targetDir.Replace("'", "''")}'
$exe = '{exeName.Replace("'", "''")}'
try {{
    Wait-Process -Id $pidToWait -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
    Start-Process -FilePath (Join-Path $target $exe)
}} finally {{
    Start-Sleep -Milliseconds 500
    Remove-Item -LiteralPath '{updateRoot.Replace("'", "''")}' -Recurse -Force -ErrorAction SilentlyContinue
}}";

            IO.File.WriteAllText(
                scriptPath,
                script,
                new UTF8Encoding(false));

            UpdateStatusText.Text =
                L(
                    $"Installing {asset.Tag}. LumiPad will reopen automatically…",
                    $"Đang cài {asset.Tag}. LumiPad sẽ tự mở lại…");

            Process.Start(
                new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments =
                        $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                    UseShellExecute = true,
                    WindowStyle =
                        ProcessWindowStyle.Hidden
                });

            _allowExit = true;
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text =
                L(
                    $"App update failed: {ex.Message}",
                    $"Cập nhật app lỗi: {ex.Message}");
            AddLog(
                "ERROR",
                "UPDATE",
                $"App update failed: {ex}");

            _updateBusy = false;
            SetDeviceControlsEnabled(
                _serial.IsConnected);
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

    private async void ShuffleButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await _nowPlaying.ToggleShuffleAsync();

    private async void RepeatButton_Click(
        object sender,
        RoutedEventArgs e) =>
        await _nowPlaying.CycleRepeatModeAsync();

    private void TrackSlider_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        if (_currentNowPlaying is not { CanSeek: true } data ||
            data.Duration <= TimeSpan.Zero ||
            sender is not Slider slider)
        {
            return;
        }

        _mediaSeekDragging = true;
        SetSliderValueFromPointer(slider, e);
        UpdateSeekPreview(slider.Value);
    }

    private async void TrackSlider_PreviewMouseLeftButtonUp(
        object sender,
        MouseButtonEventArgs e)
    {
        if (!_mediaSeekDragging ||
            _currentNowPlaying is not { CanSeek: true } data ||
            data.Duration <= TimeSpan.Zero ||
            sender is not Slider slider)
        {
            _mediaSeekDragging = false;
            return;
        }

        SetSliderValueFromPointer(slider, e);
        double fraction = Math.Clamp(slider.Value, 0, 1);
        _mediaSeekDragging = false;

        await _nowPlaying.SeekAsync(
            TimeSpan.FromTicks(
                (long)Math.Round(data.Duration.Ticks * fraction)));
    }

    private void TrackSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mediaSeekDragging)
            UpdateSeekPreview(e.NewValue);
    }

    private static void SetSliderValueFromPointer(
        Slider slider,
        MouseButtonEventArgs e)
    {
        if (slider.ActualWidth <= 1)
            return;

        double fraction =
            Math.Clamp(
                e.GetPosition(slider).X / slider.ActualWidth,
                0,
                1);
        slider.Value =
            slider.Minimum +
            (slider.Maximum - slider.Minimum) * fraction;
    }

    private void UpdateSeekPreview(double fraction)
    {
        if (_currentNowPlaying is not { } data ||
            data.Duration <= TimeSpan.Zero)
        {
            return;
        }

        fraction = Math.Clamp(fraction, 0, 1);
        TimeSpan preview =
            TimeSpan.FromTicks(
                (long)Math.Round(data.Duration.Ticks * fraction));
        ElapsedText.Text = FormatTime(preview);
        DurationText.Text =
            $"-{FormatTime(data.Duration - preview)}";
    }

    private void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady || _syncingMediaUi)
            return;

        double percent = Math.Clamp(e.NewValue, 0, 100);
        VolumeText.Text = $"{Math.Round(percent):0}%";

        if (SystemVolumeService.TrySetVolume(percent))
        {
            if (_volumeMuted && percent > 0)
            {
                SystemVolumeService.TrySetMute(false);
                _volumeMuted = false;
            }

            MuteButton.Content =
                percent <= 0.5 || _volumeMuted ? "🔇" : "🔊";
        }
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SystemVolumeService.TrySetMute(!_volumeMuted))
            return;

        _volumeMuted = !_volumeMuted;
        _syncingMediaUi = true;
        try
        {
            SyncSystemVolumeUi(force: true);
        }
        finally
        {
            _syncingMediaUi = false;
        }
    }

    private System.Windows.Controls.ComboBox[] PcMetricCombos() =>
    [
        PcMetric1Combo,
        PcMetric2Combo,
        PcMetric3Combo,
        PcMetric4Combo,
        PcMetric5Combo,
        PcMetric6Combo
    ];

    private void InitializePcMetricSelectors()
    {
        _syncingPcMetricUi = true;
        try
        {
            foreach (System.Windows.Controls.ComboBox combo in PcMetricCombos())
            {
                combo.Items.Clear();

                foreach ((int id, string name) in PcMonitorMetricChoices)
                {
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = name,
                        Tag = id.ToString()
                    });
                }
            }

            ApplyPcMetricSelections();
        }
        finally
        {
            _syncingPcMetricUi = false;
        }
    }

    private void ApplyPcMetricSelections()
    {
        if (PcMetric1Combo is null)
            return;

        _syncingPcMetricUi = true;
        try
        {
            System.Windows.Controls.ComboBox[] combos = PcMetricCombos();

            for (int i = 0; i < combos.Length; i++)
            {
                combos[i].SelectedValue =
                    Math.Clamp(_pcMonitorMetricSlots[i], 0, 11).ToString();
            }
        }
        finally
        {
            _syncingPcMetricUi = false;
        }
    }

    private async void PcMetricSlot_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingPcMetricUi || !_uiReady ||
            sender is not System.Windows.Controls.ComboBox combo ||
            combo.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int metricId))
        {
            return;
        }

        System.Windows.Controls.ComboBox[] combos = PcMetricCombos();
        int slot = Array.IndexOf(combos, combo);

        if (slot < 0)
            return;

        _pcMonitorMetricSlots[slot] =
            Math.Clamp(metricId, 0, 11);
        SaveAppSettings();

        if (_serial.IsConnected && _serial.SupportsPcMonitor)
        {
            await _serial.SendPcMonitorConfigAsync(
                _pcMonitorConfigName,
                _pcMonitorMetricSlots);
        }
    }

    private async Task PollPcMonitorAsync(bool force = false)
    {
        if ((!_pcMonitorEnabled && !force) || _pcMonitorPolling)
            return;

        _pcMonitorPolling = true;
        try
        {
            PcMonitorSnapshot snapshot =
                await Task.Run(() =>
                    _pcMonitorService.ReadSnapshot(_pcMonitorGpuId));
            _lastPcMonitorSnapshot = snapshot;
            RefreshPcGpuSelector(snapshot);
            ApplyPcMonitorUi(snapshot);
            UpdatePcMonitorConfigSummary(snapshot);

            if (_pcMonitorEnabled &&
                _serial.IsConnected &&
                _serial.SupportsPcMonitor)
            {
                bool configSent =
                    await _serial.SendPcMonitorConfigAsync(
                        _pcMonitorConfigName,
                        _pcMonitorMetricSlots);
                bool telemetrySent =
                    await _serial.SendPcMonitorAsync(
                        snapshot,
                        _pcMonitorMetricSlots);

                if (configSent && telemetrySent)
                {
                    PcMonitorLinkText.Text =
                        _serial.IsBluetoothConnected
                            ? L(
                                "Live · Bluetooth telemetry",
                                "Trực tiếp · dữ liệu Bluetooth")
                            : L(
                                "Live · USB telemetry",
                                "Trực tiếp · dữ liệu USB");
                }
                else
                {
                    PcMonitorLinkText.Text =
                        _serial.IsBluetoothConnected
                            ? L(
                                "Bluetooth telemetry send failed",
                                "Gửi dữ liệu Bluetooth thất bại")
                            : L(
                                "USB telemetry send failed",
                                "Gửi dữ liệu USB thất bại");
                }
            }
            else if (_pcMonitorEnabled && _serial.IsConnected)
            {
                PcMonitorLinkText.Text =
                    L("Firmware does not support PC Monitor yet.",
                      "Firmware chưa hỗ trợ PC Monitor.");
            }
            else if (_pcMonitorEnabled)
            {
                PcMonitorLinkText.Text =
                    L("PC sensors live · DIAL DESK is offline",
                      "Cảm biến PC đang chạy · DIAL DESK chưa kết nối");
            }
            else
            {
                PcMonitorLinkText.Text =
                    L("PC Monitor streaming is off", "Đã tắt truyền PC Monitor");
            }
        }
        catch (Exception ex)
        {
            PcMonitorLinkText.Text =
                L($"PC sensors unavailable: {ex.Message}",
                  $"Không đọc được cảm biến PC: {ex.Message}");
            AddLog("WARN", "PCMON", ex.Message);
        }
        finally
        {
            _pcMonitorPolling = false;
        }
    }

    private static string FormatNetworkRate(double mbps)
    {
        if (!double.IsFinite(mbps) || mbps < 0)
            return "--";

        if (mbps < 0.001)
            return "0 Kbps";

        if (mbps < 1)
            return $"{mbps * 1000d:0} Kbps";

        return $"{mbps:0.0} Mbps";
    }

    private void ApplyPcMonitorUi(PcMonitorSnapshot snapshot)
    {
        PcCpuLoadText.Text = $"{snapshot.CpuLoad:0}%";
        PcCpuTempText.Text = snapshot.CpuTemperature.HasValue
            ? $"{snapshot.CpuTemperature.Value:0} °C" : "-- °C";
        PcCpuClockText.Text = snapshot.CpuClockMHz.HasValue
            ? $"{snapshot.CpuClockMHz.Value:0} MHz" : "-- MHz";

        PcGpuLoadText.Text = snapshot.GpuLoad.HasValue
            ? $"{snapshot.GpuLoad.Value:0}%"
            : "--%";
        PcGpuNameText.Text = snapshot.GpuName;
        PcGpuActiveText.Text =
            string.Equals(_pcMonitorGpuId, "auto", StringComparison.OrdinalIgnoreCase)
                ? $"Auto active: {snapshot.GpuName}"
                : $"Pinned: {snapshot.GpuName}";
        PcGpuTempText.Text = snapshot.GpuTemperature.HasValue
            ? $"{snapshot.GpuTemperature.Value:0} °C" : "-- °C";
        PcGpuClockText.Text = snapshot.GpuClockMHz.HasValue
            ? $"{snapshot.GpuClockMHz.Value:0} MHz" : "-- MHz";

        PcRamLoadText.Text = $"{snapshot.MemoryLoad:0}%";
        PcRamDetailText.Text =
            $"{snapshot.MemoryUsedGb:0.0} / {snapshot.MemoryTotalGb:0.0} GB";
        PcNetDownText.Text = FormatNetworkRate(snapshot.NetworkDownloadMbps);
        PcNetUpText.Text = FormatNetworkRate(snapshot.NetworkUploadMbps);
        PcFpsText.Text = snapshot.Fps.HasValue
            ? $"FPS {snapshot.Fps.Value}" : "FPS --";
    }

    private void RefreshPcGpuSelector(PcMonitorSnapshot snapshot)
    {
        if (PcGpuCombo is null)
            return;

        string desired = _pcMonitorGpuId;
        string[] currentIds = PcGpuCombo.Items
            .OfType<ComboBoxItem>()
            .Select(i => i.Tag?.ToString() ?? "")
            .ToArray();

        string[] nextIds =
            new[] { "auto" }
                .Concat(snapshot.AvailableGpus.Select(g => g.Id))
                .ToArray();

        if (currentIds.SequenceEqual(nextIds, StringComparer.OrdinalIgnoreCase))
            return;

        _syncingPcMonitorUi = true;
        try
        {
            PcGpuCombo.Items.Clear();
            PcGpuCombo.Items.Add(new ComboBoxItem
            {
                Content = "Auto · active GPU",
                Tag = "auto"
            });

            foreach (PcGpuInfo gpu in snapshot.AvailableGpus)
            {
                PcGpuCombo.Items.Add(new ComboBoxItem
                {
                    Content = gpu.Load.HasValue
                        ? $"{gpu.Name} · {gpu.Load.Value:0}%"
                        : $"{gpu.Name} · --%",
                    Tag = gpu.Id
                });
            }

            PcGpuCombo.SelectedValue =
                nextIds.Contains(desired, StringComparer.OrdinalIgnoreCase)
                    ? desired
                    : "auto";

            if (PcGpuCombo.SelectedValue?.ToString() is string selected)
                _pcMonitorGpuId = selected;
        }
        finally
        {
            _syncingPcMonitorUi = false;
        }
    }

    private async void PcGpuCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingPcMonitorUi || !_uiReady)
            return;

        if (PcGpuCombo?.SelectedItem is not ComboBoxItem item)
            return;

        _pcMonitorGpuId =
            string.IsNullOrWhiteSpace(item.Tag?.ToString())
                ? "auto"
                : item.Tag!.ToString()!;

        SaveAppSettings();
        await PollPcMonitorAsync(force: true);
    }

    private async void PcMonitorConfigNameText_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (!_uiReady || PcMonitorConfigNameText is null)
            return;

        string next = PcMonitorConfigNameText.Text.Trim();
        _pcMonitorConfigName =
            string.IsNullOrWhiteSpace(next)
                ? "MY PC"
                : next;

        SaveAppSettings();
        UpdatePcMonitorConfigSummary(_lastPcMonitorSnapshot);

        if (_serial.IsConnected && _serial.SupportsPcMonitor)
            await _serial.SendPcMonitorConfigAsync(
                _pcMonitorConfigName,
                _pcMonitorMetricSlots);
    }

    private void UpdatePcMonitorConfigSummary(PcMonitorSnapshot? snapshot)
    {
        if (PcMonitorConfigSummaryText is null)
            return;

        string gpu =
            string.Equals(_pcMonitorGpuId, "auto", StringComparison.OrdinalIgnoreCase)
                ? snapshot is null
                    ? "Auto GPU"
                    : $"Auto · {snapshot.GpuName}"
                : snapshot?.GpuName ?? "Selected GPU";

        PcMonitorConfigSummaryText.Text =
            $"{_pcMonitorConfigName} · {gpu}";
    }

    private async void PcMonitorEnabled_Changed(
        object sender,
        RoutedEventArgs e)
    {
        if (!_uiReady)
            return;

        _pcMonitorEnabled =
            PcMonitorEnabledCheckBox?.IsChecked == true;

        if (_pcMonitorEnabled)
        {
            _pcMonitorTimer.Start();
            await PollPcMonitorAsync(force: true);
        }
        else
        {
            _pcMonitorTimer.Stop();

            if (_serial.IsConnected &&
                _serial.SupportsPcMonitor)
            {
                await _serial.ClearPcMonitorAsync();
            }

            PcMonitorLinkText.Text =
                L("PC Monitor streaming is off", "Đã tắt truyền PC Monitor");
        }

        SaveAppSettings();
    }

    private void PcMonitorIntervalCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (PcMonitorIntervalCombo?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int interval) ||
            interval is not (500 or 1000 or 2000))
        {
            return;
        }

        _pcMonitorIntervalMs = interval;
        _pcMonitorTimer.Interval =
            TimeSpan.FromMilliseconds(_pcMonitorIntervalMs);

        if (_uiReady)
            SaveAppSettings();
    }

    private static string ProfileName(int index) =>
        index switch
        {
            0 => "OFFICE",
            1 => "MEDIA",
            2 => "FUSION 360",
            3 => "CUSTOM 4",
            4 => "CUSTOM 5",
            _ => $"PROFILE {index + 1}"
        };

    private void ApplyAutoProfileUiState()
    {
        _autoProfileSettings.EnsureNormalized();

        if (AutoProfileEnabledCheckBox is not null)
            AutoProfileEnabledCheckBox.IsChecked = _autoProfileSettings.Enabled;

        if (AutoProfileDefaultCombo is not null)
        {
            AutoProfileDefaultCombo.SelectedValue =
                Math.Clamp(_autoProfileSettings.DefaultProfile, 0, 4).ToString();
        }

        RefreshAutoProfileMappingsUi();
        RefreshRunningAppsUi();
    }

    private void AutoProfileEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady)
            return;

        _autoProfileSettings.Enabled =
            AutoProfileEnabledCheckBox.IsChecked == true;
        AutoProfileService.Save(_autoProfileSettings);
        _lastAppliedAutoProfile = -1;
        _lastForegroundAppPath = null;
        PollAutoProfile(force: true);
    }

    private void AutoProfileDefaultCombo_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (AutoProfileDefaultCombo?.SelectedItem is not ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out int profile))
        {
            return;
        }

        _autoProfileSettings.DefaultProfile = Math.Clamp(profile, 0, 4);

        if (_uiReady)
        {
            AutoProfileService.Save(_autoProfileSettings);
            _lastAppliedAutoProfile = -1;
            PollAutoProfile(force: true);
        }
    }

    private void SelectAutoProfileApplication_Click(
        object sender,
        RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L("Select Application", "Chọn ứng dụng"),
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
            AddAutoProfileMapping(dialog.FileName);
    }

    private async void RefreshRunningApps_Click(
        object sender,
        RoutedEventArgs e) =>
        await RefreshRunningAppsAsync();

    private async Task RefreshRunningAppsAsync()
    {
        try
        {
            _runningApps =
                await Task.Run(AutoProfileService.ScanRunningApplications);
            RefreshRunningAppsUi();
        }
        catch (Exception ex)
        {
            AddLog("WARN", "AUTO", $"Running app scan failed: {ex.Message}");
        }
    }

    private void AddAutoProfileMapping(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return;

        string path;
        try
        {
            path = System.IO.Path.GetFullPath(executablePath);
        }
        catch
        {
            path = executablePath;
        }

        var existing = _autoProfileSettings.Mappings.FirstOrDefault(
            m => AutoProfileService.PathsEqual(m.ExecutablePath, path));

        if (existing is null)
        {
            if (_autoProfileSettings.Mappings.Count >= 10)
            {
                AutoProfileStatusText.Text =
                    L("Maximum 10 application profiles.",
                      "Tối đa 10 profile ứng dụng.");
                return;
            }

            string name = System.IO.Path.GetFileNameWithoutExtension(path);

            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                    name = info.FileDescription.Trim();
            }
            catch
            {
            }

            _autoProfileSettings.Mappings.Add(new AutoProfileMapping
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Application" : name,
                ExecutablePath = path,
                ProfileIndex = Math.Clamp(_autoProfileSettings.DefaultProfile, 0, 4)
            });

            AutoProfileService.Save(_autoProfileSettings);
            AddLog("INFO", "AUTO", $"Added app mapping: {path}");
        }

        RefreshAutoProfileMappingsUi();
        RefreshRunningAppsUi();
        _lastAppliedAutoProfile = -1;
        PollAutoProfile(force: true);
    }

    private System.Windows.Controls.ComboBox CreateProfileSelector(int selectedProfile)
    {
        var combo = new System.Windows.Controls.ComboBox
        {
            Width = 160,
            SelectedValuePath = "Tag",
            VerticalAlignment = VerticalAlignment.Center
        };

        for (int i = 0; i < 5; i++)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = ProfileName(i),
                Tag = i.ToString()
            });
        }

        combo.SelectedValue = Math.Clamp(selectedProfile, 0, 4).ToString();
        return combo;
    }

    private void RefreshAutoProfileMappingsUi()
    {
        if (AutoProfileMappingsPanel is null)
            return;

        AutoProfileMappingsPanel.Children.Clear();

        if (AutoProfileMappingCountText is not null)
        {
            AutoProfileMappingCountText.Text =
                $"{_autoProfileSettings.Mappings.Count} / 10 linked apps";
        }

        if (_autoProfileSettings.Mappings.Count == 0)
        {
            AutoProfileMappingsPanel.Children.Add(new TextBlock
            {
                Text = L(
                    "Add an application from the list on the right, then choose one of your existing profiles here.",
                    "Thêm ứng dụng từ danh sách bên phải, sau đó chọn một profile có sẵn tại đây."),
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 4, 4, 0)
            });
            return;
        }

        foreach (AutoProfileMapping mapping in
                 _autoProfileSettings.Mappings.ToArray())
        {
            var row = new Border
            {
                Background =
                    TryFindResource("Card2") as System.Windows.Media.Brush,
                BorderBrush =
                    TryFindResource("Line") as System.Windows.Media.Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 10)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(48)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(178)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            FrameworkElement icon =
                CreateApplicationIcon(
                    mapping.ExecutablePath,
                    mapping.Name,
                    40);
            grid.Children.Add(icon);

            var text = new StackPanel
            {
                Margin = new Thickness(4, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(new TextBlock
            {
                Text = mapping.Name,
                FontWeight = FontWeights.SemiBold,
                FontSize = 15
            });
            text.Children.Add(new TextBlock
            {
                Text = mapping.ExecutablePath,
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 470,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            System.Windows.Controls.ComboBox profile =
                CreateProfileSelector(mapping.ProfileIndex);
            profile.Width = 166;
            profile.Tag = mapping;
            profile.Margin = new Thickness(0, 0, 8, 0);
            profile.SelectionChanged += (_, _) =>
            {
                if (profile.Tag is not AutoProfileMapping current ||
                    profile.SelectedItem is not ComboBoxItem selected ||
                    !int.TryParse(
                        selected.Tag?.ToString(),
                        out int index))
                {
                    return;
                }

                current.ProfileIndex = Math.Clamp(index, 0, 4);
                AutoProfileService.Save(_autoProfileSettings);
                _lastAppliedAutoProfile = -1;
                PollAutoProfile(force: true);
            };
            Grid.SetColumn(profile, 2);
            grid.Children.Add(profile);

            var remove = new System.Windows.Controls.Button
            {
                Content = "×",
                Width = 38,
                Height = 38,
                Padding = new Thickness(0),
                Tag = mapping,
                ToolTip = L("Delete", "Xóa"),
                Margin = new Thickness(0)
            };
            remove.Click += (_, _) =>
            {
                if (remove.Tag is not AutoProfileMapping current)
                    return;

                _autoProfileSettings.Mappings.Remove(current);
                AutoProfileService.Save(_autoProfileSettings);
                RefreshAutoProfileMappingsUi();
                RefreshRunningAppsUi();
                _lastAppliedAutoProfile = -1;
                PollAutoProfile(force: true);
            };
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);

            row.Child = grid;
            AutoProfileMappingsPanel.Children.Add(row);
        }
    }

    private void RefreshRunningAppsUi()
    {
        if (RunningAppsPanel is null)
            return;

        RunningAppsPanel.Children.Clear();

        foreach (RunningAppInfo app in _runningApps)
        {
            bool added = _autoProfileSettings.Mappings.Any(
                m => AutoProfileService.PathsEqual(
                    m.ExecutablePath,
                    app.ExecutablePath));

            var row = new Border
            {
                Background =
                    TryFindResource("Card2") as System.Windows.Media.Brush,
                BorderBrush =
                    TryFindResource("Line") as System.Windows.Media.Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11),
                Margin = new Thickness(0, 0, 0, 9)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(46)
            });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = GridLength.Auto
            });

            FrameworkElement icon =
                CreateApplicationIcon(
                    app.ExecutablePath,
                    app.Name,
                    38);
            grid.Children.Add(icon);

            var text = new StackPanel
            {
                Margin = new Thickness(4, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            text.Children.Add(new TextBlock
            {
                Text = app.Name,
                FontWeight = FontWeights.SemiBold
            });
            text.Children.Add(new TextBlock
            {
                Text = app.ExecutablePath,
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 330,
                Margin = new Thickness(0, 3, 0, 0)
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var add = new System.Windows.Controls.Button
            {
                Content = added ? L("Added", "Đã thêm") : L("Add", "Thêm"),
                IsEnabled = !added,
                Tag = app.ExecutablePath,
                MinWidth = 68,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0)
            };
            add.Click += (_, _) =>
            {
                if (add.Tag is string path)
                    AddAutoProfileMapping(path);
            };
            Grid.SetColumn(add, 2);
            grid.Children.Add(add);

            row.Child = grid;
            RunningAppsPanel.Children.Add(row);
        }

        if (_runningApps.Count == 0)
        {
            RunningAppsPanel.Children.Add(new TextBlock
            {
                Text = L(
                    "No foreground-capable apps found.",
                    "Không tìm thấy ứng dụng có cửa sổ đang chạy."),
                Foreground =
                    TryFindResource("Muted") as System.Windows.Media.Brush
            });
        }
    }

    private FrameworkElement CreateApplicationIcon(
        string executablePath,
        string fallbackName,
        double size)
    {
        var border = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(Math.Max(8, size * 0.24)),
            Background =
                TryFindResource("ControlBg") as System.Windows.Media.Brush,
            BorderBrush =
                TryFindResource("Line") as System.Windows.Media.Brush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };

        ImageSource? source = GetApplicationIcon(executablePath);
        if (source is not null)
        {
            border.Child = new System.Windows.Controls.Image
            {
                Source = source,
                Width = Math.Max(20, size - 8),
                Height = Math.Max(20, size - 8),
                Stretch = Stretch.Uniform,
                HorizontalAlignment =
                    System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return border;
        }

        string initial =
            string.IsNullOrWhiteSpace(fallbackName)
                ? "•"
                : fallbackName.Trim()[0].ToString().ToUpperInvariant();

        border.Child = new TextBlock
        {
            Text = initial,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment =
                System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        return border;
    }

    private ImageSource? GetApplicationIcon(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return null;

        if (_applicationIconCache.TryGetValue(
                executablePath,
                out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? result = null;

        try
        {
            using Drawing.Icon? icon =
                Drawing.Icon.ExtractAssociatedIcon(executablePath);

            if (icon is not null)
            {
                BitmapSource source =
                    Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle,
                        Int32Rect.Empty,
                        BitmapSizeOptions.FromWidthAndHeight(32, 32));
                source.Freeze();
                result = source;
            }
        }
        catch
        {
        }

        _applicationIconCache[executablePath] = result;
        return result;
    }

    private void PollAutoProfile(bool force = false)
    {
        if (!_uiReady || AutoProfileStatusText is null)
            return;

        if (!_autoProfileSettings.Enabled)
        {
            AutoProfileStatusText.Text =
                L("Auto Profile is disabled", "Auto Profile đang tắt");
            return;
        }

        RunningAppInfo? app = AutoProfileService.GetForegroundApplication();

        if (app is not null &&
            AutoProfileService.PathsEqual(
                app.ExecutablePath,
                Environment.ProcessPath))
        {
            AutoProfileStatusText.Text =
                L(
                    "LumiPad is active · keeping current profile",
                    "LumiPad đang được chọn · giữ nguyên profile");
            return;
        }

        AutoProfileMapping? mapping = app is null
            ? null
            : _autoProfileSettings.Mappings.FirstOrDefault(
                m => AutoProfileService.PathsEqual(
                    m.ExecutablePath,
                    app.ExecutablePath));

        int target = mapping?.ProfileIndex ??
                     Math.Clamp(_autoProfileSettings.DefaultProfile, 0, 4);

        string appName = app?.Name ??
                         L("Desktop", "Màn hình chính");
        string profileName = ProfileName(target);

        AutoProfileStatusText.Text =
            mapping is null
                ? $"{appName} → Default · {profileName}"
                : $"{appName} → {profileName}";

        string? foregroundPath = app?.ExecutablePath;

        bool changed =
            force ||
            target != _lastAppliedAutoProfile ||
            !AutoProfileService.PathsEqual(
                foregroundPath,
                _lastForegroundAppPath);

        if (!changed || !_serial.IsConnected)
            return;

        _serial.SetActiveProfile(target);
        _lastAppliedAutoProfile = target;
        _lastForegroundAppPath = foregroundPath;

        AddLog(
            "INFO",
            "AUTO",
            $"Profile {target + 1} ({profileName}) for {appName}");
    }

    private async Task PollLumiActionAsync()
    {
        if (!_uiReady ||
            !_serial.IsConnected ||
            !_serial.SupportsActions ||
            _actionScripts.Count == 0)
        {
            return;
        }

        var actionEvent =
            await _serial.ReadActionEventAsync(_lastActionEventSeq);

        if (actionEvent is null)
            return;

        _lastActionEventSeq = actionEvent.Value.Seq;

        ActionScriptDefinition? script =
            _actionScripts.FirstOrDefault(
                s => s.ActionId == actionEvent.Value.ActionId);

        if (script is null)
        {
            AddLog(
                "WARN",
                "ACTION",
                $"No script assigned to Lumi Action {actionEvent.Value.ActionId}");
            return;
        }

        if (!_runningActionIds.Add(script.ActionId))
        {
            AddLog(
                "WARN",
                "ACTION",
                $"Lumi Action {script.ActionId} ignored because it is already running");
            return;
        }

        try
        {
            if (SelectedActionScript?.Id == script.Id &&
                ActionScriptStatusText is not null)
            {
                ActionScriptStatusText.Text =
                    L("Triggered from keyboard…", "Đã kích hoạt từ bàn phím…");
            }

            AddLog(
                "INFO",
                "ACTION",
                $"Run #{script.ActionId:00} {script.Name} from key position {actionEvent.Value.Position}");

            await ActionScriptEngine.ExecuteAsync(
                script,
                step =>
                {
                    if (SelectedActionScript?.Id != script.Id ||
                        ActionScriptStatusText is null)
                    {
                        return;
                    }

                    Dispatcher.Invoke(() =>
                        ActionScriptStatusText.Text = step);
                });

            if (SelectedActionScript?.Id == script.Id &&
                ActionScriptStatusText is not null)
            {
                ActionScriptStatusText.Text =
                    L("Completed", "Hoàn tất");
            }
        }
        catch (Exception ex)
        {
            AddLog(
                "ERROR",
                "ACTION",
                $"Lumi Action {script.ActionId} failed: {ex.Message}");

            if (SelectedActionScript?.Id == script.Id &&
                ActionScriptStatusText is not null)
            {
                ActionScriptStatusText.Text =
                    L($"Failed: {ex.Message}", $"Lỗi: {ex.Message}");
            }
        }
        finally
        {
            _runningActionIds.Remove(script.ActionId);
        }
    }

    private ActionScriptDefinition? SelectedActionScript =>
        ActionScriptsList?.SelectedItem as ActionScriptDefinition;

    private void RefreshActionScriptsUi(string? selectId = null)
    {
        if (ActionScriptsList is null)
            return;

        string? desired =
            selectId ??
            (ActionScriptsList.SelectedItem as ActionScriptDefinition)?.Id;

        _loadingActionScriptUi = true;
        try
        {
            ActionScriptsList.ItemsSource = null;
            ActionScriptsList.ItemsSource = _actionScripts;

            ActionScriptDefinition? selected =
                _actionScripts.FirstOrDefault(s => s.Id == desired) ??
                _actionScripts.FirstOrDefault();

            ActionScriptsList.SelectedItem = selected;
            LoadSelectedActionScriptUi(selected);
        }
        finally
        {
            _loadingActionScriptUi = false;
        }
    }

    private void LoadSelectedActionScriptUi(ActionScriptDefinition? script)
    {
        if (ActionScriptNameText is null ||
            ActionStepsList is null ||
            ActionEditorTitle is null)
        {
            return;
        }

        _loadingActionScriptUi = true;
        try
        {
            ActionScriptNameText.Text = script?.Name ?? "";
            ActionEditorTitle.Text =
                script?.Name ??
                L(
                    "Select or create a script",
                    "Chọn hoặc tạo một script");

            ActionStepsList.ItemsSource = null;
            ActionStepsList.ItemsSource = script?.Steps;

            if (ActionScriptIdText is not null)
            {
                ActionScriptIdText.Text =
                    script is null || script.ActionId <= 0
                        ? "Lumi Action —"
                        : $"Lumi Action {script.ActionId} · ZMK Studio → Lumi Action → Action {script.ActionId}";
            }

            ActionScriptStatusText.Text =
                script is null
                    ? L("Ready", "Sẵn sàng")
                    : $"{script.Steps.Count} step(s)";
        }
        finally
        {
            _loadingActionScriptUi = false;
        }
    }

    private void ActionScriptsList_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingActionScriptUi)
            return;

        LoadSelectedActionScriptUi(SelectedActionScript);
    }

    private void NewActionScript_Click(object sender, RoutedEventArgs e)
    {
        int actionId =
            ActionScriptStore.NextAvailableActionId(_actionScripts);

        if (actionId == 0)
        {
            ActionScriptStatusText.Text =
                L("Maximum 32 Lumi Actions.", "Tối đa 32 Lumi Action.");
            return;
        }

        var script = new ActionScriptDefinition
        {
            ActionId = actionId,
            Name = $"Script {_actionScripts.Count + 1}"
        };

        _actionScripts.Add(script);
        ActionScriptStore.Save(_actionScripts);
        RefreshActionScriptsUi(script.Id);
        ActionScriptNameText.Focus();
        ActionScriptNameText.SelectAll();
    }

    private void DeleteActionScript_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
            return;

        _actionScripts.Remove(script);
        ActionScriptStore.Save(_actionScripts);
        RefreshActionScriptsUi();
    }

    private void ActionScriptNameText_TextChanged(
        object sender,
        TextChangedEventArgs e)
    {
        if (_loadingActionScriptUi)
            return;

        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
            return;

        script.Name = string.IsNullOrWhiteSpace(ActionScriptNameText.Text)
            ? "Untitled Script"
            : ActionScriptNameText.Text.Trim();

        ActionEditorTitle.Text = script.Name;
    }

    private void AddActionStep_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
        {
            NewActionScript_Click(sender, e);
            script = SelectedActionScript;
        }

        if (script is null ||
            ActionStepTypeCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        string type = item.Tag?.ToString() ?? "Delay";
        string value = ActionStepValueText.Text.Trim();

        script.Steps.Add(new ActionScriptStep
        {
            Type = type,
            Value = value
        });

        ActionStepValueText.Clear();
        ActionScriptStore.Save(_actionScripts);
        LoadSelectedActionScriptUi(script);
        ActionStepsList.SelectedIndex = script.Steps.Count - 1;
    }

    private void RemoveActionStep_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null ||
            ActionStepsList.SelectedIndex < 0 ||
            ActionStepsList.SelectedIndex >= script.Steps.Count)
        {
            return;
        }

        int index = ActionStepsList.SelectedIndex;
        script.Steps.RemoveAt(index);
        ActionScriptStore.Save(_actionScripts);
        LoadSelectedActionScriptUi(script);
        ActionStepsList.SelectedIndex =
            Math.Min(index, script.Steps.Count - 1);
    }

    private void MoveActionStepUp_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        int index = ActionStepsList.SelectedIndex;

        if (script is null || index <= 0 || index >= script.Steps.Count)
            return;

        (script.Steps[index - 1], script.Steps[index]) =
            (script.Steps[index], script.Steps[index - 1]);

        ActionScriptStore.Save(_actionScripts);
        LoadSelectedActionScriptUi(script);
        ActionStepsList.SelectedIndex = index - 1;
    }

    private void MoveActionStepDown_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        int index = ActionStepsList.SelectedIndex;

        if (script is null ||
            index < 0 ||
            index >= script.Steps.Count - 1)
        {
            return;
        }

        (script.Steps[index + 1], script.Steps[index]) =
            (script.Steps[index], script.Steps[index + 1]);

        ActionScriptStore.Save(_actionScripts);
        LoadSelectedActionScriptUi(script);
        ActionStepsList.SelectedIndex = index + 1;
    }

    private void AssignActionKey_Click(
        object sender,
        RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null || script.ActionId <= 0)
        {
            ActionScriptStatusText.Text =
                L(
                    "Create or select an Action first.",
                    "Hãy tạo hoặc chọn một Action trước.");
            return;
        }

        if (_actionKeymapWindow is null ||
            !_actionKeymapWindow.IsLoaded)
        {
            _actionKeymapWindow =
                new ActionKeymapWindow(
                    script.ActionId,
                    script.Name,
                    _language)
                {
                    Owner = this
                };

            _actionKeymapWindow.Closed += (_, _) =>
                _actionKeymapWindow = null;
            _actionKeymapWindow.Show();
        }
        else
        {
            _actionKeymapWindow.SetAction(
                script.ActionId,
                script.Name,
                _language);
            _actionKeymapWindow.Activate();
        }

        ActionScriptStatusText.Text =
            L(
                $"Assign Lumi Action {script.ActionId} to a key in ZMK Studio.",
                $"Gán Lumi Action {script.ActionId} vào phím trong ZMK Studio.");
    }

    private void SaveActionScript_Click(object sender, RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
            return;

        script.Name = string.IsNullOrWhiteSpace(ActionScriptNameText.Text)
            ? "Untitled Script"
            : ActionScriptNameText.Text.Trim();

        ActionScriptStore.Save(_actionScripts);
        RefreshActionScriptsUi(script.Id);
        ActionScriptStatusText.Text =
            L("Saved", "Đã lưu");
    }

    private async void RunActionScript_Click(
        object sender,
        RoutedEventArgs e)
    {
        ActionScriptDefinition? script = SelectedActionScript;
        if (script is null)
            return;

        try
        {
            ActionScriptStatusText.Text =
                L("Running…", "Đang chạy…");

            await ActionScriptEngine.ExecuteAsync(
                script,
                step => Dispatcher.Invoke(() =>
                    ActionScriptStatusText.Text = step));

            ActionScriptStatusText.Text =
                L("Completed", "Hoàn tất");
        }
        catch (Exception ex)
        {
            ActionScriptStatusText.Text =
                L($"Failed: {ex.Message}", $"Lỗi: {ex.Message}");
            AddLog("ERROR", "SCRIPT", ex.Message);
        }
    }

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
        _screensaverSource = "Media";
        if (ScreensaverSourceCombo is not null)
            SelectComboTag(ScreensaverSourceCombo, "Media");
        UpdateScreensaverSourceUi();
        SaveAppSettings();

        if (_serial.IsConnected)
            await _serial.SetScreensaverSourceAsync(false);

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

            if (_screensaverAnimation.PixelFormat ==
                ScreensaverPixelFormat.Rgb565)
            {
                ScreensaverMediaInfo.Text =
                    L(
                        $"Static image · {_screensaverAnimation.Width}×{_screensaverAnimation.Height} · RGB565 high quality · {scaleMode}",
                        $"Ảnh tĩnh · {_screensaverAnimation.Width}×{_screensaverAnimation.Height} · RGB565 chất lượng cao · {scaleMode}");

                ScreensaverPreviewImage.Source =
                    CreateRgb565Bitmap(
                        _screensaverAnimation.Frames[0],
                        _screensaverAnimation.Width,
                        _screensaverAnimation.Height);
            }
            else
            {
                ScreensaverMediaInfo.Text =
                    L(
                        $"{_screensaverAnimation.Frames.Count} stored GIF frames · {_screensaverAnimation.Width}×{_screensaverAnimation.Height} -> 320×172 integer 2× · max {ScreensaverMediaService.MaxPlaybackFps} FPS · {scaleMode}",
                        $"{_screensaverAnimation.Frames.Count} khung GIF lưu · {_screensaverAnimation.Width}×{_screensaverAnimation.Height} -> 320×172 phóng nguyên 2× · tối đa {ScreensaverMediaService.MaxPlaybackFps} FPS · {scaleMode}");

                ScreensaverPreviewImage.Source =
                    CreateRgb332Bitmap(
                        _screensaverAnimation.Frames[0],
                        _screensaverAnimation.Width,
                        _screensaverAnimation.Height);
            }

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
            SendScreensaverButton.IsEnabled =
                _serial.IsConnected &&
                !string.Equals(
                    _screensaverSource,
                    "PcMonitor",
                    StringComparison.Ordinal);
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
                _screensaverSource = "Media";
                if (ScreensaverSourceCombo is not null)
                    SelectComboTag(ScreensaverSourceCombo, "Media");
                UpdateScreensaverSourceUi();
                await _serial.SetScreensaverSourceAsync(false);

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
                _serial.IsConnected &&
                _screensaverAnimation is not null &&
                !string.Equals(
                    _screensaverSource,
                    "PcMonitor",
                    StringComparison.Ordinal);
        }
    }

    private async void ShowScreensaverNow_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (!_serial.IsConnected)
        {
            ScreensaverSendStatus.Text =
                L("Connect LumiPad first.", "Hãy kết nối LumiPad trước.");
            return;
        }

        bool pcMonitor =
            string.Equals(
                _screensaverSource,
                "PcMonitor",
                StringComparison.Ordinal);

        await _serial.ShowScreensaverNowAsync(pcMonitor);

        ScreensaverSendStatus.Text =
            pcMonitor
                ? L("Showing PC Monitor screensaver now.",
                    "Đang bật PC Monitor làm bảo vệ màn hình.")
                : L("Showing the uploaded GIF / image now.",
                    "Đang hiển thị GIF / ảnh đã tải lên ngay.");
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
        if (string.Equals(
                _screensaverSource,
                "PcMonitor",
                StringComparison.Ordinal))
        {
            return;
        }

        if (_screensaverAnimation is null || !_serial.IsConnected)
            return;

        try
        {
            string? state = await _serial.GetScreensaverStateAsync();

            if (string.Equals(state, "READY", StringComparison.Ordinal))
            {
                ScreensaverSendProgress.Value = 100;
                SetScreensaverUploadState(
                    L("Stored on keyboard", "Đã lưu trên bàn phím"),
                    MediaColor.FromRgb(48, 209, 88));
                ScreensaverSendStatus.Text =
                    L("Screensaver is already stored in keyboard flash.",
                      "Bảo vệ màn hình đã có sẵn trong flash của bàn phím.");
                AddLog(
                    "INFO",
                    "SAVER",
                    "Persisted screensaver already READY; reconnect restore skipped");
                return;
            }

            if (!string.Equals(state, "EMPTY", StringComparison.Ordinal) &&
                !string.Equals(state, "ERROR", StringComparison.Ordinal))
            {
                // An unrelated GATT response or temporary read failure must
                // never trigger a large automatic upload.
                AddLog(
                    "WARN",
                    "SAVER",
                    $"Saver state unavailable ({state ?? "unknown"}); automatic restore skipped");

                ScreensaverSendStatus.Text =
                    L("Could not verify keyboard screensaver; no restore was attempted.",
                      "Không xác minh được bảo vệ màn hình trên bàn phím; không tự tải lại.");
                return;
            }

            AddLog(
                "INFO",
                "SAVER",
                $"Keyboard reported {state}; restoring local screensaver");

            var progress = new Progress<int>(value =>
            {
                ScreensaverSendProgress.Value = value;
                ScreensaverSendStatus.Text =
                    L($"Restoring screensaver… {value}%",
                      $"Đang khôi phục bảo vệ màn hình… {value}%");
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
                    L("Custom screensaver restored because keyboard flash was empty.",
                      "Đã khôi phục bảo vệ màn hình vì flash bàn phím đang trống.");
            }
        }
        catch (Exception ex)
        {
            AddLog(
                "WARN",
                "SAVER",
                $"Automatic screensaver state check failed: {ex.Message}");
            // Keep the keyboard connection alive even if state verification fails.
        }
    }

    private void OpenShopee_Click(
        object sender,
        RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://shopee.vn/lumi3d.hn",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AddLog("WARN", "SHOP", $"Open Shopee failed: {ex.Message}");
        }
    }

    private string CurrentConfiguratorName() =>
        _activeProduct.Driver switch
        {
            DeviceDriverKind.LumiZmk => "ZMK Studio",
            DeviceDriverKind.QmkRawHid => "VIA",
            DeviceDriverKind.Esp32Companion => "Device Config",
            _ => "Device Config"
        };

    private string CurrentConfiguratorUrl() =>
        _activeProduct.Driver switch
        {
            DeviceDriverKind.LumiZmk => "https://zmk.studio/",
            DeviceDriverKind.QmkRawHid => "https://usevia.app/",
            _ => ""
        };

    private void UpdateDeviceConfiguratorUi()
    {
        string name = CurrentConfiguratorName();
        string url = CurrentConfiguratorUrl();

        ZmkTab.Header = name;
        if (DeviceConfiguratorTitle is not null)
            DeviceConfiguratorTitle.Text = name;

        if (ZmkStatus is not null)
        {
            ZmkStatus.Text = string.IsNullOrWhiteSpace(url)
                ? L(
                    "No embedded configurator for this product.",
                    "Sản phẩm này chưa có trình cấu hình tích hợp.")
                : _activeProduct.Driver == DeviceDriverKind.QmkRawHid
                    ? L(
                        "Embedded usevia.app · WebHID will be checked after loading",
                        "Tích hợp usevia.app · sẽ kiểm tra WebHID sau khi tải")
                    : $"Embedded {url}";
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
        UpdateDeviceConfiguratorUi();

        if (ZmkTab.IsSelected)
            await EnsureDeviceConfiguratorAsync();
    }

    private async Task EnsureDeviceConfiguratorAsync(bool force = false)
    {
        string url = CurrentConfiguratorUrl();
        string name = CurrentConfiguratorName();

        UpdateDeviceConfiguratorUi();

        if (string.IsNullOrWhiteSpace(url))
            return;

        if (!force &&
            _zmkInitialized &&
            string.Equals(
                _loadedConfiguratorUrl,
                url,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            ZmkStatus.Text = L(
                $"Loading {url} …",
                $"Đang tải {url} …");

            await ZmkWebView.EnsureCoreWebView2Async();
            ZmkWebView.Source = new Uri(url);
            _loadedConfiguratorUrl = url;
            _zmkInitialized = true;

            await Task.Delay(750);

            if (_activeProduct.Driver == DeviceDriverKind.QmkRawHid &&
                ZmkWebView.CoreWebView2 is not null)
            {
                string webHidResult =
                    await ZmkWebView.CoreWebView2.ExecuteScriptAsync(
                        "typeof navigator.hid !== 'undefined'");

                bool webHidAvailable =
                    string.Equals(
                        webHidResult?.Trim(),
                        "true",
                        StringComparison.OrdinalIgnoreCase);

                ZmkStatus.Text = webHidAvailable
                    ? L(
                        "VIA loaded · WebHID ready",
                        "VIA đã tải · WebHID sẵn sàng")
                    : L(
                        "VIA loaded · WebHID unavailable here — use Open in Edge",
                        "VIA đã tải · WebHID không khả dụng tại đây — dùng Open in Edge");
            }
            else
            {
                ZmkStatus.Text = $"{name} · {url}";
            }
        }
        catch (Exception ex)
        {
            ZmkStatus.Text =
                $"WebView2 unavailable: {ex.Message}";
        }
    }

    private async void ReloadZmk_Click(object sender, RoutedEventArgs e)
    {
        _zmkInitialized = false;
        await EnsureDeviceConfiguratorAsync(force: true);

        if (ZmkWebView.CoreWebView2 is not null)
            ZmkWebView.Reload();
    }

    private void OpenZmkExternal_Click(object sender, RoutedEventArgs e)
    {
        string url = CurrentConfiguratorUrl();
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AddLog(
                "WARN",
                "CONFIG",
                $"Open {CurrentConfiguratorName()} failed: {ex.Message}");
        }
    }
}
