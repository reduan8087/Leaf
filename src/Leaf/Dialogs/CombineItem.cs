using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Leaf.Services;

namespace Leaf.Dialogs;

/// <summary>How an image should be sized when it becomes a page.</summary>
public enum ImagePageSize
{
    /// <summary>One image pixel becomes one 96-DPI device pixel, so the page is exactly the size of the image.</summary>
    MatchImage,
    A4,
    Letter,
}

/// <summary>One file queued for Combine Files.</summary>
public sealed partial class CombineItem : INotifyPropertyChanged
{
    private string _position = string.Empty;
    private string _detail = "…";
    private bool _failed;

    public CombineItem(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        IsImage = FileDialogs.IsImage(path);
        try
        {
            var info = new FileInfo(path);
            Size = info.Length;
            Modified = info.LastWriteTime;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Size = 0;
            Modified = DateTime.MinValue;
        }
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsImage { get; }

    public long Size { get; }

    public DateTime Modified { get; }

    /// <summary>Pages this file contributes: a PDF's page count, or 1 for an image. Zero until it has been read.</summary>
    public int PageCount { get; private set; }

    /// <summary>Decoded pixel size, needed to lay an image out on its page.</summary>
    public int PixelWidth { get; private set; }

    public int PixelHeight { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>One-based position in the list.</summary>
    public string Position
    {
        get => _position;
        set => Set(ref _position, value);
    }

    /// <summary>The right-hand line: page count, or why the file cannot be used.</summary>
    public string Detail
    {
        get => _detail;
        set => Set(ref _detail, value);
    }

    /// <summary>True when the file could not be read; it is kept in the list but skipped.</summary>
    public bool Failed
    {
        get => _failed;
        set
        {
            if (Set(ref _failed, value))
            {
                Notify(nameof(Opacity));
            }
        }
    }

    public double Opacity => Failed ? 0.5 : 1.0;

    public string Glyph => IsImage ? "" : "";

    public void Describe(int pageCount, int pixelWidth, int pixelHeight)
    {
        PageCount = pageCount;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        Failed = false;
        Detail = pageCount == 1 ? "1 page" : string.Create(CultureInfo.CurrentCulture, $"{pageCount} pages");
    }

    public void Fail(string reason)
    {
        PageCount = 0;
        Failed = true;
        Detail = reason;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
