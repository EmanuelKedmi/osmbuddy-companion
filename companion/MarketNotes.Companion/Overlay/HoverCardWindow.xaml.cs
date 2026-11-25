using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace MarketNotes.Companion.Overlay;

public partial class HoverCardWindow : Window
{
    private Action<string>? _toggle;

    public HoverCardWindow()
    {
        InitializeComponent();
        OverlayChrome.ToolWindow(this);
    }

    public double BodyHeight
    {
        get
        {
            UpdateLayout();
            if (ActualHeight > 1) return ActualHeight;
            Measure(new Size(280, 1600));
            return Math.Max(1, DesiredSize.Height);
        }
    }

    public void Show(HoverCardView card, Action<string> toggle)
    {
        _toggle = toggle;
        ItemTitle.Text = card.ItemName;
        SectionsHost.Children.Clear();
        foreach (var section in card.Sections)
        {
            SectionsHost.Children.Add(SectionBlock(section));
        }
    }

    public void MoveTo(int screenX, int screenY)
    {
        OverlayChrome.Move(this, screenX, screenY);
        if (!IsVisible) Show();
    }

    private UIElement SectionBlock(HoverSectionView section)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = section.Title,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x53, 0x44)),
        });
        foreach (var line in section.Lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line.Text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = new SolidColorBrush(line.Emphasis
                    ? Color.FromRgb(0x5C, 0x24, 0x1C)
                    : Color.FromRgb(0x3F, 0x2E, 0x27)),
                FontWeight = line.Emphasis ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }

        if (section.Trend.Count >= 2) stack.Children.Add(Spark(section.Trend));
        if (section.CanExpand)
        {
            var button = new Button
            {
                Content = section.ExpandLabel,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 6, 0, 0),
                Padding = new Thickness(8, 2, 8, 2),
                Tag = section.Id,
            };
            if (TryFindResource("OutlineButton") is Style style) button.Style = style;
            button.Click += (_, _) => _toggle?.Invoke((string)button.Tag);
            stack.Children.Add(button);
        }

        return stack;
    }

    private static UIElement Spark(IReadOnlyList<double> trend)
    {
        const double width = 248;
        const double height = 36;
        var canvas = new Canvas
        {
            Width = width,
            Height = height,
            Margin = new Thickness(0, 6, 0, 0),
        };
        canvas.Children.Add(new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(Color.FromRgb(0xF0, 0xE6, 0xD4)),
            RadiusX = 4,
            RadiusY = 4,
        });
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x9E, 0x34, 0x2C)),
            StrokeThickness = 1.6,
        };
        var last = Math.Max(1, trend.Count - 1);
        for (var i = 0; i < trend.Count; i++)
        {
            var x = 6 + (width - 12) * i / last;
            var y = 6 + (height - 12) * (1 - Math.Clamp(trend[i], 0, 1));
            line.Points.Add(new Point(x, y));
        }

        canvas.Children.Add(line);
        return canvas;
    }
}
