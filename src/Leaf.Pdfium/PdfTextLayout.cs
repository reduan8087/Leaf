namespace Leaf.Pdfium;

/// <summary>
/// Immutable snapshot of one page's text: the characters pdfium sees plus a box per character in PDF points.
/// Built once on the pdfium thread; afterwards hit-testing, selection rectangles and copy run on any thread.
/// </summary>
public sealed class PdfTextLayout
{
    private readonly PdfRect[] _boxes;

    internal PdfTextLayout(int pageIndex, float widthPt, float heightPt, string text, PdfRect[] boxes)
    {
        PageIndex = pageIndex;
        WidthPt = widthPt;
        HeightPt = heightPt;
        Text = text;
        _boxes = boxes;
    }

    public static PdfTextLayout Empty(int pageIndex, float widthPt, float heightPt) => new(pageIndex, widthPt, heightPt, string.Empty, []);

    public int PageIndex { get; }
    public float WidthPt { get; }
    public float HeightPt { get; }

    /// <summary>All characters in pdfium order; index i corresponds to <see cref="GetBox"/>(i).</summary>
    public string Text { get; }

    public int CharCount => _boxes.Length;

    public PdfRect GetBox(int index) => _boxes[index];

    /// <summary>Index of the character under (x, y) in page points, or -1. Tolerance widens the hit area.</summary>
    public int HitTest(double xPt, double yPt, double tolerancePt = 3)
    {
        int best = -1;
        double bestDist = double.MaxValue;
        for (int i = 0; i < _boxes.Length; i++)
        {
            PdfRect b = _boxes[i];
            if (b.IsEmpty)
            {
                continue;
            }

            if (b.Contains(xPt, yPt))
            {
                return i;
            }

            double dx = xPt < b.Left ? b.Left - xPt : xPt > b.Right ? xPt - b.Right : 0;
            double dy = yPt < b.Bottom ? b.Bottom - yPt : yPt > b.Top ? yPt - b.Top : 0;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = i;
            }
        }

        return bestDist <= tolerancePt * tolerancePt ? best : -1;
    }

    /// <summary>
    /// Nearest character for a point that may be outside any text (used while dragging a selection):
    /// prefers characters on the same line, then the closest line.
    /// </summary>
    public int NearestChar(double xPt, double yPt)
    {
        int best = -1;
        double bestScore = double.MaxValue;
        for (int i = 0; i < _boxes.Length; i++)
        {
            PdfRect b = _boxes[i];
            if (b.IsEmpty)
            {
                continue;
            }

            double dy = yPt < b.Bottom ? b.Bottom - yPt : yPt > b.Top ? yPt - b.Top : 0;
            double dx = xPt < b.Left ? b.Left - xPt : xPt > b.Right ? xPt - b.Right : 0;
            double score = dy * 1000 + dx; // line distance dominates
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    public string GetText(int start, int count)
    {
        if (count <= 0 || start < 0 || start >= Text.Length)
        {
            return string.Empty;
        }

        count = Math.Min(count, Text.Length - start);
        return Text.Substring(start, count);
    }

    /// <summary>Word boundaries around <paramref name="index"/> (letters/digits joined; whitespace/punctuation break).</summary>
    public (int Start, int Count) WordAt(int index)
    {
        if (index < 0 || index >= Text.Length || !IsWordChar(Text[index]))
        {
            return (index, index >= 0 && index < Text.Length ? 1 : 0);
        }

        int s = index;
        while (s > 0 && IsWordChar(Text[s - 1]))
        {
            s--;
        }

        int e = index;
        while (e + 1 < Text.Length && IsWordChar(Text[e + 1]))
        {
            e++;
        }

        return (s, e - s + 1);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\'';

    /// <summary>
    /// Merges the character boxes of a range into one rectangle per visual line (in page points).
    /// Consecutive characters whose vertical extents overlap are joined; line breaks start a new rect.
    /// </summary>
    public IReadOnlyList<PdfRect> GetRects(int start, int count)
    {
        var result = new List<PdfRect>();
        if (count <= 0 || start < 0)
        {
            return result;
        }

        int end = Math.Min(_boxes.Length, start + count);
        PdfRect? current = null;
        for (int i = start; i < end; i++)
        {
            PdfRect b = _boxes[i];
            if (b.IsEmpty)
            {
                continue;
            }

            if (current is { } c && VerticalOverlap(c, b) > 0.4)
            {
                current = c.Union(b);
            }
            else
            {
                if (current is { } done)
                {
                    result.Add(done);
                }

                current = b;
            }
        }

        if (current is { } last)
        {
            result.Add(last);
        }

        return result;
    }

    private static double VerticalOverlap(PdfRect a, PdfRect b)
    {
        double overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        double smaller = Math.Min(a.Height, b.Height);
        return smaller <= 0 ? 0 : overlap / smaller;
    }
}

/// <summary>One search hit: a character range on a page.</summary>
public readonly record struct PdfSearchHit(int PageIndex, int CharIndex, int CharCount);
