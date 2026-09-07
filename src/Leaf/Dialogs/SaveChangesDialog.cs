using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leaf.Dialogs;

/// <summary>What the user chose when asked about unsaved changes.</summary>
public enum SaveChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// The "save changes?" prompt shown before a document with page edits is closed. Built in code like the other
/// dialogs here. Cancel is the default so that dismissing it can never lose work.
/// </summary>
public static class SaveChangesDialog
{
    public static async Task<SaveChoice> ShowAsync(XamlRoot xamlRoot, string documentName, bool untitled)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"Save changes to {documentName}?",
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = untitled
                    ? "This document has not been saved anywhere yet. If you don't save it, it will be lost."
                    : "Your page changes will be lost if you don't save them.",
            },
            PrimaryButtonText = untitled ? "Save as…" : "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => SaveChoice.Save,
            ContentDialogResult.Secondary => SaveChoice.Discard,
            _ => SaveChoice.Cancel,
        };
    }
}
