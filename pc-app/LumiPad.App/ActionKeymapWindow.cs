using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace LumiPad.App;

public sealed class ActionKeymapWindow : Window
{
    private readonly TextBlock _titleText = new();
    private readonly TextBlock _helpText = new();
    private readonly TextBlock _statusText = new();
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
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background =
            Application.Current.TryFindResource("Bg") as Brush ??
            Brushes.Black;
        Foreground =
            Application.Current.TryFindResource("TextPrimary") as Brush ??
            Brushes.White;

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

    private UIElement BuildUi()
    {
        var root = new Grid
        {
            Margin = new Thickness(18)
        };
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = GridLength.Auto
        });
        root.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(14)
        });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new Border
        {
            Background =
                Application.Current.TryFindResource("Card") as Brush,
            BorderBrush =
                Application.Current.TryFindResource("Line") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16)
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto
        });

        var text = new StackPanel();
        text.Children.Add(_titleText);
        _titleText.FontSize = 20;
        _titleText.FontWeight = FontWeights.SemiBold;

        _helpText.Margin = new Thickness(0, 5, 18, 0);
        _helpText.TextWrapping = TextWrapping.Wrap;
        _helpText.Foreground =
            Application.Current.TryFindResource("Muted") as Brush;
        text.Children.Add(_helpText);

        _statusText.Text = "https://zmk.studio/";
        _statusText.Margin = new Thickness(0, 5, 0, 0);
        _statusText.FontSize = 11;
        _statusText.Foreground =
            Application.Current.TryFindResource("Accent") as Brush;
        text.Children.Add(_statusText);

        headerGrid.Children.Add(text);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var reload = new Button
        {
            Content = "Reload"
        };
        reload.Click += async (_, _) =>
        {
            await EnsureStudioAsync();
            _studio.CoreWebView2?.Reload();
        };
        buttons.Children.Add(reload);

        var external = new Button
        {
            Content = "Open in Edge",
            Margin = new Thickness(0)
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

        Grid.SetColumn(buttons, 1);
        headerGrid.Children.Add(buttons);
        header.Child = headerGrid;
        root.Children.Add(header);

        var webBorder = new Border
        {
            Background = Brushes.White,
            BorderBrush =
                Application.Current.TryFindResource("Line") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            ClipToBounds = true
        };
        Grid.SetRow(webBorder, 2);
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
