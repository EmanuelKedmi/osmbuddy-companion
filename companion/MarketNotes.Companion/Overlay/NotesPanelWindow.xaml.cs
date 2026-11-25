using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MarketNotes.Companion.Overlay;

public partial class NotesPanelWindow : Window
{
    public NotesPanelWindow()
    {
        InitializeComponent();
        OverlayChrome.ToolWindow(this);
    }

    public event Action? CloseRequested;

    public double BodyHeight
    {
        get
        {
            UpdateLayout();
            if (ActualHeight > 1) return ActualHeight;
            Measure(new Size(280, 1200));
            return Math.Max(1, DesiredSize.Height);
        }
    }

    public void SetStatus(string text)
    {
        if (StatusText.Text == text) return;
        StatusText.Text = text;
    }

    public void Bind(InGameUi ui, Action changed)
    {
        OptionsHost.Children.Clear();
        foreach (var option in ui.Options)
        {
            var box = new CheckBox
            {
                Content = option.Title,
                IsChecked = ui.IsEnabled(option.Id),
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x24, 0x1C)),
                Margin = new Thickness(0, 4, 0, 0),
            };
            var id = option.Id;
            box.Checked += (_, _) =>
            {
                ui.SetEnabled(id, true);
                changed();
            };
            box.Unchecked += (_, _) =>
            {
                ui.SetEnabled(id, false);
                changed();
            };
            OptionsHost.Children.Add(box);
            OptionsHost.Children.Add(new TextBlock
            {
                Text = option.Detail,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x53, 0x44)),
                FontSize = 12,
                Margin = new Thickness(22, 0, 0, 6),
            });
        }
    }

    public void MoveTo(int screenX, int screenY) => OverlayChrome.Move(this, screenX, screenY);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
