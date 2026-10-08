using System.Windows;
using ImageTools.Treemap;

namespace ImageTools;

/// <summary>
/// Asks before something slow or consequential. Cancel is the default button, so pressing
/// Enter never starts it by accident.
/// </summary>
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(string title, string heading, string message, string warning, string confirm)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = heading;
        MessageText.Text = message;
        WarningText.Text = warning;
        WarningGlyph.Foreground = Theme.StatusWarning;
        WarningPanel.Visibility = warning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirm;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
