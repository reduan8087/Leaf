using Leaf.Pdfium;

namespace Leaf.Services;

/// <summary>
/// Writes documents to disk. Saving over the file that is currently open needs care on two counts: pdfium reads
/// pages lazily from a handle that is still open, and a write that fails half way must not leave the user with a
/// truncated PDF. So every save goes to a temporary file first and is swapped in only once it is complete, and
/// the caller reopens the document afterwards.
/// </summary>
public static class SaveService
{
    private const string TempSuffix = ".leaf-tmp";

    /// <summary>Writes the document to a different path. The document itself keeps reading from its own file.</summary>
    public static Task SaveCopyAsync(PdfDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        return PdfiumThread.Instance.RunAsync(() => document.SaveAs(path), priority: -50);
    }

    /// <summary>
    /// Replaces the document's own file. The caller must reopen the document afterwards: the open handle still
    /// refers to the file that was just replaced, so anything not yet read would come back as the old bytes.
    /// </summary>
    public static async Task SaveInPlaceAsync(PdfDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrEmpty(path);

        // The temporary file must sit on the same volume for the swap to be atomic.
        string temp = path + TempSuffix;
        try
        {
            await PdfiumThread.Instance.RunAsync(() => document.SaveAs(temp), priority: -50);
            Swap(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void Swap(string temp, string path)
    {
        try
        {
            // ReplaceFile keeps the destination's identity, so shortcuts and file IDs survive.
            File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            try
            {
                // Replace refuses across some filesystems and on files it considers in use; a move still works
                // because Leaf never takes a write lock on the documents it opens.
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception moveFailure) when (moveFailure is IOException or UnauthorizedAccessException)
            {
                throw new PdfException(PdfError.Write, $"The file could not be replaced. {moveFailure.Message}");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stray temp file is better than hiding the real failure.
        }
    }
}
