using System.Globalization;
using Leaf.Pdfium;
using Leaf.Viewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Leaf.Dialogs;

/// <summary>
/// Shows what the file says about itself: the information dictionary, page geometry and the permission bits.
/// Built in code like <see cref="PasswordDialog"/> rather than in XAML, because it is a plain list of rows.
/// </summary>
public static class DocumentPropertiesDialog
{
    private const double PtToMm = 25.4 / 72.0;
    private const double PtToIn = 1.0 / 72.0;

    public static async Task ShowAsync(XamlRoot xamlRoot, DocumentSession session, int currentPage)
    {
        PdfDocument document = session.Document;
        DocumentFacts facts = await PdfiumThread.Instance.RunAsync(
            () => new DocumentFacts(
                document.GetMetadata(),
                document.FileVersion,
                document.AllowsPrinting,
                document.AllowsCopying,
                document.AllowsModification),
            priority: 100);

        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        void Section(string title)
        {
            AddRow(grid, row, null, null);
            var header = new TextBlock
            {
                Text = title,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 2),
            };
            Grid.SetRow(header, row);
            Grid.SetColumn(header, 0);
            Grid.SetColumnSpan(header, 2);
            grid.Children.Add(header);
            row++;
        }

        void Field(string label, string? value)
        {
            AddRow(grid, row, label, string.IsNullOrWhiteSpace(value) ? "—" : value);
            row++;
        }

        PdfPageInfo page = session.Pages[Math.Clamp(currentPage, 0, session.PageCount - 1)];

        Section("File");
        Field("Name", session.FileName);
        Field("Folder", Path.GetDirectoryName(session.Path));
        Field("Size", FormatFileSize(session.Path));
        Field("PDF version", facts.FileVersion > 0 ? $"1.{facts.FileVersion % 10}" : null);

        Section("Document");
        Field("Title", facts.Metadata.Title);
        Field("Author", facts.Metadata.Author);
        Field("Subject", facts.Metadata.Subject);
        Field("Keywords", facts.Metadata.Keywords);
        Field("Created", FormatDate(facts.Metadata.Created));
        Field("Modified", FormatDate(facts.Metadata.Modified));
        Field("Application", facts.Metadata.Creator);
        Field("PDF producer", facts.Metadata.Producer);

        Section("Pages");
        Field("Count", session.PageCount.ToString(CultureInfo.CurrentCulture));
        Field($"Page {currentPage + 1} size", FormatPageSize(page));

        Section("Security");
        Field("Printing", Allowed(facts.AllowsPrinting));
        Field("Copying text", Allowed(facts.AllowsCopying));
        Field("Changing the document", Allowed(facts.AllowsModification));

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "Document properties",
            Content = new ScrollViewer
            {
                Content = grid,
                MaxHeight = 460,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        await dialog.ShowAsync();
    }

    private static void AddRow(Grid grid, int row, string? label, string? value)
    {
        if (grid.RowDefinitions.Count <= row)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        if (label is null)
        {
            return;
        }

        var name = new TextBlock { Text = label, Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(name, row);
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        // Selectable so a producer string or a long path can be copied out.
        var text = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
    }

    private static string Allowed(bool value) => value ? "Allowed" : "Not allowed";

    private static string FormatDate(DateTimeOffset? value) =>
        value is null ? string.Empty : value.Value.LocalDateTime.ToString("f", CultureInfo.CurrentCulture);

    private static string FormatPageSize(PdfPageInfo page)
    {
        double mmW = page.WidthPt * PtToMm;
        double mmH = page.HeightPt * PtToMm;
        double inW = page.WidthPt * PtToIn;
        double inH = page.HeightPt * PtToIn;
        return string.Create(
            CultureInfo.CurrentCulture,
            $"{mmW:F0} × {mmH:F0} mm   ({inW:F2} × {inH:F2} in)");
    }

    private static string FormatFileSize(string path)
    {
        try
        {
            long bytes = new FileInfo(path).Length;
            string[] units = ["bytes", "KB", "MB", "GB"];
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }

            string rounded = unit == 0 ? size.ToString("F0", CultureInfo.CurrentCulture) : size.ToString("F1", CultureInfo.CurrentCulture);
            return string.Create(CultureInfo.CurrentCulture, $"{rounded} {units[unit]} ({bytes:N0} bytes)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private sealed record DocumentFacts(
        PdfMetadata Metadata,
        int FileVersion,
        bool AllowsPrinting,
        bool AllowsCopying,
        bool AllowsModification);
}
