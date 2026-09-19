using System.Diagnostics;
using Microsoft.Web.WebView2.Wpf;
using Wpf = System.Windows;
using Controls = System.Windows.Controls;
using Media = System.Windows.Media;

namespace LumiPad.App;

public sealed class ActionKeymapWindow : Wpf.Window
{
    private readonly Controls.TextBlock _titleText = new();
    private readonly Controls.TextBlock _helpText = new();
    private readonly Controls.TextBlock _statusText = new();
    private readonly WebView2 _studio = new();
    private int _actionId;
    private string _actionName = "";
    private string _language = "en";
    private bool _initialized;

    public ActionKeymapWindow(
        int actionId,
        string actionName,
        string language)
    {
        Title = "Lumi Action Key Map";
        Width = 1260;
        Height = 860;
        MinWidth = 980;
        MinHeight = 680;
        WindowStartupLocation = Wpf.WindowStartupLocation.CenterOwner;
        Background =
            Wpf.Application.Current.TryFindResource("Bg") as Media.Brush ??
            Media.Brushes.Black;
        Foreground =
            Wpf.Application.Current.TryFindResource("TextPrimary") as Media.Brush ??
            Media.Brushes.White;

        Content = BuildUi();
        SetAction(actionId, actionName, language);

        Loaded += async (_, _) => await EnsureStudioAsync();
    }

    public void SetAction(
        int actionId,
        string actionName,
        string language)
    {
        _actionId = Math.Clamp(actionId, 1, 32);
        _actionName =
            string.IsNullOrWhiteSpace(actionName)
                ? $"Action {_actionId}"
                : actionName.Trim();
        _language = language == "vi" ? "vi" : "en";

        _titleText.Text =
            $"Lumi Action {_actionId} · {_actionName}";

        _helpText.Text =
            _language == "vi"
                ? $"Trong key map bên dưới: chọn phím → Behavior “Lumi Action” → chọn “Action {_actionId}” → Save. Nếu ZMK Studio yêu cầu unlock, giữ phím 1 + phím 12."
                : $"In the key map below: select a key → Behavior “Lumi Action” → choose “Action {_actionId}” → Save. If ZMK Studio asks to unlock, hold key 1 + key 12.";
    }

    private Wpf.UIElement BuildUi()
    {
        var root = new Controls.Grid
        {
            Margin = new Wpf.Thickness(18)
        };
        root.RowDefinitions.Add(new Controls.RowDefinition
        {
            Height = Wpf.GridLength.Auto
        });
        root.RowDefinitions.Add(new Controls.RowDefinition
        {
            Height = new Wpf.GridLength(14)
        });
        root.RowDefinitions.Add(new Controls.RowDefinition());

        var header = new Controls.Border
        {
            Background =
                Wpf.Application.Current.TryFindResource("Card") as Media.Brush,
            BorderBrush =
                Wpf.Application.Current.TryFindResource("Line") as Media.Brush,
            BorderThickness = new Wpf.Thickness(1),
            CornerRadius = new Wpf.CornerRadius(16),
            Padding = new Wpf.Thickness(16)
        };

        var headerGrid = new Controls.Grid();
        headerGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition
        {
            Width = Wpf.GridLength.Auto
        });

        var text = new Controls.StackPanel();
        text.Children.Add(_titleText);
        _titleText.FontSize = 20;
        _titleText.FontWeight = Wpf.FontWeights.SemiBold;

        _helpText.Margin = new Wpf.Thickness(0, 5, 18, 0);
        _helpText.TextWrapping = Wpf.TextWrapping.Wrap;
        _helpText.Foreground =
            Wpf.Application.Current.TryFindResource("Muted") as Media.Brush;
        text.Children.Add(_helpText);

        _statusText.Text = "https://zmk.studio/";
        _statusText.Margin = new Wpf.Thickness(0, 5, 0, 0);
        _statusText.FontSize = 11;
        _statusText.Foreground =
            Wpf.Application.Current.TryFindResource("Accent") as Media.Brush;
        text.Children.Add(_statusText);

        headerGrid.Children.Add(text);

        var buttons = new Controls.StackPanel
        {
            Orientation = Controls.Orientation.Horizontal,
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };

        var reload = new Controls.Button
        {
            Content = "Reload"
        };
        reload.Click += async (_, _) =>
        {
            await EnsureStudioAsync();
            _studio.CoreWebView2?.Reload();
        };
        buttons.Children.Add(reload);

        var external = new Controls.Button
        {
            Content = "Open in Edge",
            Margin = new Wpf.Thickness(0)
        };
        external.Click += (_, _) =>
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
        };
        buttons.Children.Add(external);

        Controls.Grid.SetColumn(buttons, 1);
        headerGrid.Children.Add(buttons);
        header.Child = headerGrid;
        root.Children.Add(header);

        var webBorder = new Controls.Border
        {
            Background = Media.Brushes.White,
            BorderBrush =
                Wpf.Application.Current.TryFindResource("Line") as Media.Brush,
            BorderThickness = new Wpf.Thickness(1),
            CornerRadius = new Wpf.CornerRadius(16),
            ClipToBounds = true
        };
        Controls.Grid.SetRow(webBorder, 2);
        webBorder.Child = _studio;
        root.Children.Add(webBorder);

        return root;
    }

    private async Task EnsureStudioAsync()
    {
        if (_initialized)
            return;

        try
        {
            _statusText.Text =
                _language == "vi"
                    ? "Đang tải ZMK Studio…"
                    : "Loading ZMK Studio…";

            await _studio.EnsureCoreWebView2Async();
            _studio.Source = new Uri("https://zmk.studio/");
            _initialized = true;
            _statusText.Text = "https://zmk.studio/";
        }
        catch (Exception ex)
        {
            _statusText.Text =
                $"WebView2 unavailable: {ex.Message}";
        }
    }
}
