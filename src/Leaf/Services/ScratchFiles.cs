namespace Leaf.Services;

/// <summary>
/// A scratch folder for documents that exist before they have a home, such as the result of Combine Files.
/// Swept once at startup so a crash cannot leave the folder growing forever.
/// </summary>
public static class ScratchFiles
{
    /// <summary>Files older than this are assumed to be from a session that ended badly.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public static string Directory { get; } = Path.Combine(SettingsStore.Directory, "scratch");

    /// <summary>A path for a new scratch document. The file is not created.</summary>
    public static string NewPath(string baseName)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string safe = string.Join('_', baseName.Split(Path.GetInvalidFileNameChars()));
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "Document";
        }

        return Path.Combine(Directory, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.pdf");
    }

    /// <summary>Removes leftovers from earlier runs. Never throws: this is housekeeping, not a feature.</summary>
    public static void SweepOldFiles()
    {
        try
        {
            if (!System.IO.Directory.Exists(Directory))
            {
                return;
            }

            DateTime cutoff = DateTime.Now - MaxAge;
            foreach (string file in System.IO.Directory.EnumerateFiles(Directory, "*.pdf"))
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    TryDelete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore
        }
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (path.StartsWith(Directory, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ignore
        }
    }
}
