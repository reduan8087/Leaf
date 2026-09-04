namespace Leaf.Pdfium;

/// <summary>A rectangle in PDF user space: points, origin bottom-left, Top &gt; Bottom.</summary>
public readonly record struct PdfRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;
    public double Height => Top - Bottom;
    public bool IsEmpty => Right <= Left || Top <= Bottom;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    public PdfRect Union(PdfRect other) => new(
        Math.Min(Left, other.Left), Math.Min(Bottom, other.Bottom),
        Math.Max(Right, other.Right), Math.Max(Top, other.Top));
}

/// <summary>Size of a page in points after the page's own /Rotate has been applied.</summary>
public readonly record struct PdfPageInfo(int Index, float WidthPt, float HeightPt);

/// <summary>
/// Pure math shared by the engine and the viewer: maps PDF user-space points to device pixels for a
/// page displayed with the given view rotation (0..3 = 0/90/180/270 clockwise) at <paramref name="scale"/>
/// pixels per point. Matches FPDF_PageToDevice for a page rendered at (0,0,w,h).
/// </summary>
public static class PdfGeometry
{
    public static (int W, int H) DisplayedSize(float widthPt, float heightPt, int rotation, double scale)
    {
        bool swap = (rotation & 1) == 1;
        double w = (swap ? heightPt : widthPt) * scale;
        double h = (swap ? widthPt : heightPt) * scale;
        return ((int)Math.Ceiling(w - 1e-6), (int)Math.Ceiling(h - 1e-6));
    }

    public static (double X, double Y) PageToDevice(double xPt, double yPt, float widthPt, float heightPt, int rotation, double scale)
    {
        return (rotation & 3) switch
        {
            0 => (xPt * scale, (heightPt - yPt) * scale),
            1 => (yPt * scale, xPt * scale),
            2 => ((widthPt - xPt) * scale, yPt * scale),
            _ => ((heightPt - yPt) * scale, (widthPt - xPt) * scale),
        };
    }

    public static (double X, double Y) DeviceToPage(double dx, double dy, float widthPt, float heightPt, int rotation, double scale)
    {
        return (rotation & 3) switch
        {
            0 => (dx / scale, heightPt - dy / scale),
            1 => (dy / scale, dx / scale),
            2 => (widthPt - dx / scale, dy / scale),
            _ => (widthPt - dy / scale, heightPt - dx / scale),
        };
    }

    /// <summary>Device-space axis-aligned box (left, top, right, bottom in pixels) of a page rect.</summary>
    public static (double Left, double Top, double Right, double Bottom) RectToDevice(PdfRect r, float widthPt, float heightPt, int rotation, double scale)
    {
        var a = PageToDevice(r.Left, r.Bottom, widthPt, heightPt, rotation, scale);
        var b = PageToDevice(r.Right, r.Top, widthPt, heightPt, rotation, scale);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }
}
