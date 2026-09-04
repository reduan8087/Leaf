using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Leaf.Dialogs;

/// <summary>Asks for a document password. Returns null when the user cancels.</summary>
public static class PasswordDialog
{
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, string fileName, bool previousAttemptFailed)
    {
        var box = new PasswordBox { PlaceholderText = "Password", Margin = new Thickness(0, 8, 0, 0) };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = $"\"{fileName}\" is password protected.", TextWrapping = TextWrapping.Wrap });
        if (previousAttemptFailed)
        {
            panel.Children.Add(new TextBlock { Text = "That password was not accepted. Try again.", Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] });
        }

        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Enter password",
            Content = panel,
            PrimaryButtonText = "Open",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                dialog.Hide();
                e.Handled = true;
                dialog.Tag = "enter";
            }
        };
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);

        ContentDialogResult result = await dialog.ShowAsync();
        bool accepted = result == ContentDialogResult.Primary || (dialog.Tag is string tag && tag == "enter");
        return accepted && box.Password.Length > 0 ? box.Password : null;
    }
}
