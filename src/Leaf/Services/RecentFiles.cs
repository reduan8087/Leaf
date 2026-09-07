namespace Leaf.Services;

/// <summary>
/// The Open Recent list. Entries are kept even when the file has gone missing so the menu can show it greyed
/// out (a moved file is worth seeing); they are removed only when the user picks a dead entry or clears the list.
/// </summary>
public static class RecentFiles
{
    public const int MaxEntries = 15;

    /// <summary>Most recently opened first.</summary>
    public static IReadOnlyList<RecentEntry> Entries => SettingsStore.Current.Recent;

    public static void Add(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return;
        }

        List<RecentEntry> list = SettingsStore.Current.Recent;
        list.RemoveAll(e => string.Equals(e.Path, full, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, new RecentEntry { Path = full, LastOpened = DateTimeOffset.Now });
        if (list.Count > MaxEntries)
        {
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        }

        SettingsStore.Save();
    }

    public static void Remove(string path)
    {
        if (SettingsStore.Current.Recent.RemoveAll(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            SettingsStore.Save();
        }
    }

    public static void Clear()
    {
        if (SettingsStore.Current.Recent.Count == 0)
        {
            return;
        }

        SettingsStore.Current.Recent.Clear();
        SettingsStore.Save();
    }

    /// <summary>True when the file is still where it was. Checked when the menu opens, so it stays cheap.</summary>
    public static bool Exists(RecentEntry entry)
    {
        try
        {
            return File.Exists(entry.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
