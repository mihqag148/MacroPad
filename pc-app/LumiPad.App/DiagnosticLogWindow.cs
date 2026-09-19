using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LumiPad.App;

public sealed class DiagnosticLogWindow : Window
{
    private readonly System.Windows.Controls.TextBox _logBox;

    public DiagnosticLogWindow(string logText, string titleText)
    {
        Title = titleText;
        Width = 980;
        Height = 620;
        MinWidth = 720;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;
        Background =
            System.Windows.Application.Current.TryFindResource("Bg") as System.Windows.Media.Brush ??
            new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(8, 8, 8));

        var outer = new Border
        {
            Margin = new Thickness(18),
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(20),
            Background =
                System.Windows.Application.Current.TryFindResource("Card") as System.Windows.Media.Brush ??
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(21, 21, 21)),
            BorderBrush =
                System.Windows.Application.Current.TryFindResource("Line") as System.Windows.Media.Brush ??
                new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 58, 60)),
            BorderThickness = new Thickness(1)
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new StackPanel();
        heading.Children.Add(new TextBlock
        {
            Text = "DIAGNOSTIC LOG",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground =
                System.Windows.Application.Current.TryFindResource("Muted") as System.Windows.Media.Brush
        });
        heading.Children.Add(new TextBlock
        {
            Text = titleText,
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 5, 0, 0)
        });
        grid.Children.Add(heading);

        _logBox = new System.Windows.Controls.TextBox
        {
            Text = logText,
            IsReadOnly = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(12)
        };
        Grid.SetRow(_logBox, 2);
        grid.Children.Add(_logBox);

        var buttons = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var copy = new System.Windows.Controls.Button { Content = "Copy log" };
        copy.Click += (_, _) =>
        {
            try
            {
                System.Windows.Clipboard.SetText(_logBox.Text ?? "");
            }
            catch
            {
            }
        };

        var close = new System.Windows.Controls.Button
        {
            Content = "Close",
            Margin = new Thickness(0)
        };
        close.Click += (_, _) => Close();

        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        Grid.SetRow(buttons, 4);
        grid.Children.Add(buttons);

        outer.Child = grid;
        Content = outer;

        Loaded += (_, _) =>
        {
            _logBox.CaretIndex = _logBox.Text.Length;
            _logBox.ScrollToEnd();
        };
    }
}
