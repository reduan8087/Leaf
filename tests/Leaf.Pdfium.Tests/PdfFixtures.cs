using System.Security.Cryptography;
using System.Text;

namespace Leaf.Pdfium.Tests;

/// <summary>
/// Builds small, valid PDF files from scratch (no external tools) so tests never depend on the machine.
/// Supports multiple pages, custom sizes, /Rotate, one line of Helvetica text per page, and
/// RC4 40-bit "standard security handler" encryption (PDF 1.7 algorithms 3.2-3.5, revision 2).
/// </summary>
internal static class PdfFixtures
{
    public sealed record PageSpec(double WidthPt, double HeightPt, string Text, int Rotate = 0, double TextX = 72, double TextY = 700, double FontSize = 24);

    public static string TempDir { get; } = Directory.CreateTempSubdirectory("leaf-tests-").FullName;

    public static string Write(string name, params PageSpec[] pages) => Write(name, null, pages);

    public static string Write(string name, string? userPassword, params PageSpec[] pages)
    {
        string path = Path.Combine(TempDir, name);
        File.WriteAllBytes(path, Build(pages, userPassword));
        return path;
    }

    public static byte[] Build(PageSpec[] pages, string? userPassword = null)
    {
        // Object numbering: 1 catalog, 2 pages, 3 font, then per page: page obj, content obj.
        var objects = new List<byte[]>();
        objects.Add(Ascii("<< /Type /Catalog /Pages 2 0 R >>"));
        var kids = new StringBuilder();
        for (int i = 0; i < pages.Length; i++)
        {
            kids.Append(4 + i * 2).Append(" 0 R ");
        }

        objects.Add(Ascii($"<< /Type /Pages /Kids [ {kids}] /Count {pages.Length} >>"));
        objects.Add(Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));

        byte[]? key = null;
        byte[]? o = null;
        byte[]? u = null;
        byte[] id = MD5.HashData(Encoding.ASCII.GetBytes("leaf-fixture-" + string.Join("|", pages.Select(p => p.Text))));
        if (userPassword is not null)
        {
            (key, o, u) = ComputeRc4Keys(userPassword, ownerPassword: "owner-" + userPassword, permissions: -1, id);
        }

        for (int i = 0; i < pages.Length; i++)
        {
            PageSpec p = pages[i];
            int pageObj = 4 + i * 2;
            int contentObj = pageObj + 1;
            string rotate = p.Rotate != 0 ? $" /Rotate {p.Rotate}" : string.Empty;
            objects.Add(Ascii($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(p.WidthPt)} {F(p.HeightPt)}]{rotate} /Resources << /Font << /F1 3 0 R >> >> /Contents {contentObj} 0 R >>"));

            string content = $"BT /F1 {F(p.FontSize)} Tf {F(p.TextX)} {F(p.TextY)} Td ({Escape(p.Text)}) Tj ET\n";
            byte[] data = Encoding.ASCII.GetBytes(content);
            if (key is not null)
            {
                data = Rc4(ObjectKey(key, contentObj, 0), data);
            }

            objects.Add(Concat(Ascii($"<< /Length {data.Length} >>\nstream\n"), data, Ascii("\nendstream")));
        }

        using var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        W("%PDF-1.4\n%âãÏÓ\n");
        var offsets = new long[objects.Count + 1];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i + 1] = ms.Position;
            W($"{i + 1} 0 obj\n");
            ms.Write(objects[i]);
            W("\nendobj\n");
        }

        long xref = ms.Position;
        int total = objects.Count + (key is not null ? 2 : 1);
        W($"xref\n0 {total}\n");
        W("0000000000 65535 f \n");
        for (int i = 1; i <= objects.Count; i++)
        {
            W($"{offsets[i]:D10} 00000 n \n");
        }

        string trailerExtra = string.Empty;
        if (key is not null)
        {
            // Encrypt dictionary as an indirect object appended after the xref? Simpler: inline direct dictionary in the trailer.
            trailerExtra = $" /Encrypt << /Filter /Standard /V 1 /R 2 /Length 40 /P -1 /O <{Hex(o!)}> /U <{Hex(u!)}> >>";
            total = objects.Count + 1;
        }

        string idHex = Hex(id);
        W($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R /ID [<{idHex}> <{idHex}>]{trailerExtra} >>\nstartxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    // ------------------------------------------------------------------ RC4 standard security handler (R2, 40-bit)

    private static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    private static (byte[] Key, byte[] O, byte[] U) ComputeRc4Keys(string userPassword, string ownerPassword, int permissions, byte[] id)
    {
        byte[] userPadded = PadPassword(userPassword);
        byte[] ownerPadded = PadPassword(ownerPassword);

        // Algorithm 3.3 (R2): O = RC4(MD5(ownerPadded)[0..5], userPadded)
        byte[] ownerKey = MD5.HashData(ownerPadded)[..5];
        byte[] o = Rc4(ownerKey, userPadded);

        // Algorithm 3.2 (R2): key = MD5(userPadded + O + P(le int32) + ID[0])[0..5]
        var input = new List<byte>(userPadded);
        input.AddRange(o);
        input.AddRange(BitConverter.GetBytes(permissions));
        input.AddRange(id);
        byte[] key = MD5.HashData(input.ToArray())[..5];

        // Algorithm 3.4 (R2): U = RC4(key, PAD)
        byte[] u = Rc4(key, Pad);
        return (key, o, u);
    }

    private static byte[] PadPassword(string password)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(password);
        var padded = new byte[32];
        int n = Math.Min(32, bytes.Length);
        Array.Copy(bytes, padded, n);
        Array.Copy(Pad, 0, padded, n, 32 - n);
        return padded;
    }

    private static byte[] ObjectKey(byte[] key, int objectNumber, int generation)
    {
        var input = new byte[key.Length + 5];
        key.CopyTo(input, 0);
        input[key.Length] = (byte)objectNumber;
        input[key.Length + 1] = (byte)(objectNumber >> 8);
        input[key.Length + 2] = (byte)(objectNumber >> 16);
        input[key.Length + 3] = (byte)generation;
        input[key.Length + 4] = (byte)(generation >> 8);
        byte[] hash = MD5.HashData(input);
        return hash[..Math.Min(key.Length + 5, 16)];
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            s[i] = (byte)i;
        }

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }

        return output;
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new byte[parts.Sum(p => p.Length)];
        int pos = 0;
        foreach (byte[] p in parts)
        {
            p.CopyTo(result, pos);
            pos += p.Length;
        }

        return result;
    }

    private static string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Hex(byte[] b) => Convert.ToHexString(b);

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
}
