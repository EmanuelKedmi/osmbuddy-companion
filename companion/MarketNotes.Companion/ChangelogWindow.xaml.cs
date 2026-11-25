using System.Windows;

namespace MarketNotes.Companion;

public partial class ChangelogWindow : Window
{
    public ChangelogWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadNotesAsync();
    }

    private async Task LoadNotesAsync()
    {
        NotesText.Text = "Loading…";
        var entries = await CompanionChangelog.LoadAsync();
        NotesText.Text = ChangelogNotes.FormatPlain(entries);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
