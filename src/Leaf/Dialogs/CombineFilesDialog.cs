using Leaf.Pdfium;
using Leaf.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leaf.Dialogs;

/// <summary>
/// Combine Files: pick PDFs and images, arrange them, and merge them into one document. The result is written
/// to the scratch folder and handed back as a path; the caller opens it as an unsaved tab so the user reviews
/// it before choosing where it lives.
/// </summary>
public static class CombineFilesDialog
{
    /// <summary>Returns the path of the combined document, or null if the user cancelled or nothing was usable.</summary>
    public static async Task<string?> ShowAsync(MainWindow owner)
    {
        var panel = new CombineFilesPanel { Owner = owner };
        var dialog = new ContentDialog
        {
            XamlRoot = owner.Content.XamlRoot,
            Title = "Combine files",
            Content = panel,
            PrimaryButtonText = "Combine",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
        };
        panel.CanCombineChanged += canCombine => dialog.IsPrimaryButtonEnabled = canCombine;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        IReadOnlyList<CombineItem> items = panel.UsableItems;
        if (items.Count == 0)
        {
            return null;
        }

        try
        {
            return await CombineAsync(items, panel.ImagePagePoints);
        }
        catch (Exception ex) when (ex is PdfException or IOException or UnauthorizedAccessException)
        {
            owner.ShowError("Can't combine those files", ex.Message);
            return null;
        }
    }

    /// <summary>The merge itself. Internal so the LEAF_TEST_ACTIONS script can drive it without the dialog.</summary>
    internal static async Task<string> CombineAsync(IReadOnlyList<CombineItem> items, (double Width, double Height) pageSize)
    {
        // Images are decoded on the UI side, where WIC lives, so the engine project stays free of Windows types.
        var prepared = new List<Prepared>(items.Count);
        foreach (CombineItem item in items)
        {
            if (!item.IsImage)
            {
                prepared.Add(new Prepared(item.Path, null, 0, 0));
                continue;
            }

            if (await CombineFilesPanel.CanEmbedJpegDirectlyAsync(item.Path))
            {
                // A JPEG with no EXIF rotation goes in byte-for-byte: no re-encoding, no size blow-up.
                prepared.Add(new Prepared(item.Path, [], item.PixelWidth, item.PixelHeight));
                continue;
            }

            (byte[] pixels, int width, int height) = await CombineFilesPanel.DecodeImageAsync(item.Path);
            prepared.Add(new Prepared(item.Path, pixels, width, height));
        }

        string output = ScratchFiles.NewPath("Combined");
        await PdfiumThread.Instance.RunAsync(
            () =>
            {
                using var builder = new PdfBuilder();
                bool copiedPreferences = false;

                foreach (Prepared entry in prepared)
                {
                    if (entry.Pixels is null)
                    {
                        using PdfDocument source = PdfDocument.Open(entry.Path);
                        builder.AppendPages(source);
                        if (!copiedPreferences)
                        {
                            builder.CopyViewerPreferencesFrom(source);
                            copiedPreferences = true;
                        }
                    }
                    else
                    {
                        var placement = new PdfImagePlacement(entry.Width, entry.Height, pageSize.Width, pageSize.Height);
                        if (entry.Pixels.Length == 0)
                        {
                            builder.AppendJpegPage(entry.Path, placement);
                        }
                        else
                        {
                            builder.AppendImagePage(placement, entry.Pixels, entry.Width * 4);
                        }
                    }
                }

                builder.Save(output);
            },
            priority: -50);

        return output;
    }

    /// <summary>
    /// A file ready for the engine thread. Pixels is null for a PDF, empty for a JPEG that will be embedded
    /// as-is, and the decoded BGRA otherwise.
    /// </summary>
    private readonly record struct Prepared(string Path, byte[]? Pixels, int Width, int Height);
}
