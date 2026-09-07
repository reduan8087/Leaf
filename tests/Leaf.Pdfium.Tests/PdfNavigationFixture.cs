using System.Text;

namespace Leaf.Pdfium.Tests;

/// <summary>
/// Writes one fixed PDF with an outline and link annotations. Hand-authored rather than folded into
/// <see cref="PdfFixtures"/> because outlines and annotations need cross-references between objects, which
/// would complicate that builder for a single pair of tests.
/// </summary>
/// <remarks>
/// Three A4 pages. The outline has two top-level entries, the second with two children, pointing at pages
/// 1, 2, 2 and 3. Page 1 carries a URI link and an internal link to page 3; page 2 carries one internal link
/// to page 1; page 3 carries none.
/// </remarks>
internal static class PdfNavigationFixture
{
    public const double PageWidth = 595;
    public const double PageHeight = 842;

    /// <summary>Where the links sit on page 1, in PDF points (origin bottom-left).</summary>
    public static readonly (double Left, double Bottom, double Right, double Top) UriLinkRect = (72, 700, 300, 730);
    public static readonly (double Left, double Bottom, double Right, double Top) InternalLinkRect = (72, 640, 260, 670);

    public const string Uri = "https://example.org/leaf";

    public static string Write(string name)
    {
        var objects = new List<string>();

        // 1 catalog, 2 pages, 3 font, 4/6/8 pages, 5/7/9 contents,
        // 10 outline root, 11..14 outline items, 15..17 link annotations.
        objects.Add("<< /Type /Catalog /Pages 2 0 R /Outlines 10 0 R /PageMode /UseOutlines >>");
        objects.Add("<< /Type /Pages /Kids [ 4 0 R 6 0 R 8 0 R ] /Count 3 >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        string[] titles = ["page one", "page two", "page three"];
        string[] annots = ["/Annots [ 15 0 R 16 0 R ]", "/Annots [ 17 0 R ]", string.Empty];
        for (int i = 0; i < 3; i++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] " +
                        $"/Resources << /Font << /F1 3 0 R >> >> /Contents {5 + (i * 2)} 0 R {annots[i]} >>");
            string content = $"BT /F1 24 Tf 72 760 Td ({titles[i]}) Tj ET\n";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }

        // Outline: "Chapter one" -> page 1; "Chapter two" -> page 2, with two children.
        objects.Add("<< /Type /Outlines /First 11 0 R /Last 12 0 R /Count 4 >>");
        objects.Add("<< /Title (Chapter one) /Parent 10 0 R /Next 12 0 R /Dest [ 4 0 R /XYZ null null null ] >>");
        objects.Add("<< /Title (Chapter two) /Parent 10 0 R /Prev 11 0 R /First 13 0 R /Last 14 0 R /Count 2 " +
                    "/Dest [ 6 0 R /XYZ null null null ] >>");
        objects.Add("<< /Title (Section 2.1) /Parent 12 0 R /Next 14 0 R /Dest [ 6 0 R /XYZ null null null ] >>");
        objects.Add("<< /Title (Section 2.2) /Parent 12 0 R /Prev 13 0 R /Dest [ 8 0 R /XYZ null null null ] >>");

        // Links.
        objects.Add($"<< /Type /Annot /Subtype /Link /Border [0 0 0] " +
                    $"/Rect [ {UriLinkRect.Left} {UriLinkRect.Bottom} {UriLinkRect.Right} {UriLinkRect.Top} ] " +
                    $"/A << /Type /Action /S /URI /URI ({Uri}) >> >>");
        objects.Add($"<< /Type /Annot /Subtype /Link /Border [0 0 0] " +
                    $"/Rect [ {InternalLinkRect.Left} {InternalLinkRect.Bottom} {InternalLinkRect.Right} {InternalLinkRect.Top} ] " +
                    "/Dest [ 8 0 R /XYZ null null null ] >>");
        objects.Add("<< /Type /Annot /Subtype /Link /Border [0 0 0] /Rect [ 72 600 200 630 ] " +
                    "/A << /Type /Action /S /GoTo /D [ 4 0 R /XYZ null null null ] >> >>");

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach ((string body, int index) in objects.Select((b, i) => (b, i + 1)))
        {
            offsets.Add(pdf.Length);
            pdf.Append(index).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n');
        pdf.Append("0000000000 65535 f \n");
        foreach (int offset in offsets)
        {
            pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        }

        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1)
           .Append(" /Root 1 0 R /ID [<0102030405060708090A0B0C0D0E0F10><0102030405060708090A0B0C0D0E0F10>] >>\n")
           .Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        string path = Path.Combine(PdfFixtures.TempDir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;
    }
}
