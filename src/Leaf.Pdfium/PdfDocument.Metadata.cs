using System.Globalization;
using Leaf.Pdfium.Native;

namespace Leaf.Pdfium;

/// <summary>The document information dictionary, as shown in Document Properties. Any field may be empty.</summary>
public sealed record PdfMetadata(
    string Title,
    string Author,
    string Subject,
    string Keywords,
    string Creator,
    string Producer,
    DateTimeOffset? Created,
    DateTimeOffset? Modified);

public sealed unsafe partial class PdfDocument
{
    /// <summary>Bits of FPDF_GetDocPermissions. 0xFFFFFFFF means the document is unrestricted.</summary>
    private const uint PermissionPrint = 1 << 2;
    private const uint PermissionModify = 1 << 3;
    private const uint PermissionCopy = 1 << 4;

    public PdfMetadata GetMetadata()
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        nint doc = _doc;
        string Read(string tag) => NativeMethods.ReadUtf16((buffer, length) => NativeMethods.FPDF_GetMetaText(doc, tag, (void*)buffer, length));

        return new PdfMetadata(
            Read("Title"),
            Read("Author"),
            Read("Subject"),
            Read("Keywords"),
            Read("Creator"),
            Read("Producer"),
            ParsePdfDate(Read("CreationDate")),
            ParsePdfDate(Read("ModDate")));
    }

    public bool AllowsPrinting => HasPermission(PermissionPrint);

    public bool AllowsModification => HasPermission(PermissionModify);

    public bool AllowsCopying => HasPermission(PermissionCopy);

    private bool HasPermission(uint bit)
    {
        PdfiumThread.AssertCurrent();
        ThrowIfDisposed();
        uint permissions = NativeMethods.FPDF_GetDocPermissions(_doc);
        return permissions == 0xFFFFFFFF || (permissions & bit) != 0;
    }

    /// <summary>Two digits at <paramref name="start"/>, or <paramref name="fallback"/> when they are absent or malformed.</summary>
    private static int Part(ReadOnlySpan<char> s, int start, int fallback) =>
        s.Length >= start + 2 && int.TryParse(s.Slice(start, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    /// <summary>
    /// Parses a PDF date string, "D:YYYYMMDDHHmmSSOHH'mm'". Every part after the year is optional and real
    /// files get this wrong often enough that anything unparseable is simply reported as unknown.
    /// </summary>
    internal static DateTimeOffset? ParsePdfDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        ReadOnlySpan<char> s = value.AsSpan().Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        if (s.Length < 4 || !int.TryParse(s[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int year))
        {
            return null;
        }

        int month = Part(s, 4, 1);
        int day = Part(s, 6, 1);
        int hour = Part(s, 8, 0);
        int minute = Part(s, 10, 0);
        int second = Part(s, 12, 0);

        TimeSpan offset = TimeSpan.Zero;
        if (s.Length >= 15 && (s[14] == '+' || s[14] == '-'))
        {
            int offsetHours = Part(s, 15, 0);
            int offsetMinutes = Part(s, 18, 0);
            offset = new TimeSpan(Math.Clamp(offsetHours, 0, 14), Math.Clamp(offsetMinutes, 0, 59), 0);
            if (s[14] == '-')
            {
                offset = -offset;
            }
        }

        try
        {
            return new DateTimeOffset(year, month, day, hour, minute, Math.Min(second, 59), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // a date such as month 13 or day 32; treat it as absent
        }
    }
}
