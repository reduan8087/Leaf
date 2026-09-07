using Microsoft.UI.Xaml;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Leaf.Services;

/// <summary>
/// The file pickers, in one place. Unpackaged WinUI 3 pickers have no window of their own, so each one has to
/// be given the app's HWND before it is shown or it throws.
/// </summary>
public static class FileDialogs
{
    /// <summary>Image formats Combine Files can turn into pages. WIC decodes all of these out of the box.</summary>
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp"];

    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public static bool IsImage(string path)
    {
        string extension = Path.GetExtension(path);
        foreach (string candidate in ImageExtensions)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsJpeg(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Picks one or more PDFs to open. Returns an empty list when the user cancels.</summary>
    public static async Task<IReadOnlyList<string>> PickPdfsAsync(Window owner)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".pdf");
        return await PickManyAsync(owner, picker);
    }

    /// <summary>Picks PDFs and images to combine. Returns an empty list when the user cancels.</summary>
    public static async Task<IReadOnlyList<string>> PickCombineInputsAsync(Window owner)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.Thumbnail,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".pdf");
        foreach (string extension in ImageExtensions)
        {
            picker.FileTypeFilter.Add(extension);
        }

        return await PickManyAsync(owner, picker);
    }

    /// <summary>Asks where to write a PDF. Returns null when the user cancels.</summary>
    public static async Task<string?> PickSavePdfAsync(Window owner, string suggestedName)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) is { Length: > 0 } name ? name : "Document",
            DefaultFileExtension = ".pdf",
        };
        picker.FileTypeChoices.Add("PDF document", [".pdf"]);
        Initialize(owner, picker);

        StorageFile? file = await picker.PickSaveFileAsync();
        return file?.Path;
    }

    private static async Task<IReadOnlyList<string>> PickManyAsync(Window owner, FileOpenPicker picker)
    {
        Initialize(owner, picker);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        var paths = new List<string>(files.Count);
        foreach (StorageFile file in files)
        {
            paths.Add(file.Path);
        }

        return paths;
    }

    private static void Initialize(Window owner, object picker)
    {
        nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
