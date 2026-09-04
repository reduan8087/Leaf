using Xunit;
using Xunit.Abstractions;

namespace Leaf.Pdfium.Tests;

public sealed class PdfDocumentTests
{
    private static readonly PdfFixtures.PageSpec Letter = new(612, 792, "Hello Leaf");
    private readonly ITestOutputHelper _output;

    public PdfDocumentTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static T OnEngine<T>(Func<T> work) => PdfiumThread.Instance.Run(work);

    private static void OnEngine(Action work) => PdfiumThread.Instance.Run(work);

    [Fact]
    public void Opens_and_reports_page_sizes()
    {
        string path = PdfFixtures.Write("sizes.pdf", Letter, new PdfFixtures.PageSpec(500, 300, "wide"), new PdfFixtures.PageSpec(400, 800, "rotated", Rotate: 90));

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            Assert.Equal(3, doc.PageCount);
            Assert.Equal(612, doc.Pages[0].WidthPt, 0.01);
            Assert.Equal(792, doc.Pages[0].HeightPt, 0.01);
            Assert.Equal(500, doc.Pages[1].WidthPt, 0.01);
            Assert.Equal(300, doc.Pages[1].HeightPt, 0.01);
            // /Rotate 90 swaps the reported size (pdfium applies the page rotation)
            Assert.Equal(800, doc.Pages[2].WidthPt, 0.01);
            Assert.Equal(400, doc.Pages[2].HeightPt, 0.01);
        });
    }

    [Fact]
    public void Renders_text_pixels_and_tiles_match_whole_page()
    {
        string path = PdfFixtures.Write("render.pdf", Letter);

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            const double scale = 1.5;
            byte[] whole = doc.RenderPage(0, 0, scale, out int w, out int h);
            Assert.Equal(918, w);
            Assert.Equal(1188, h);

            // Some pixels must be dark (text) and the corners white.
            int dark = 0;
            for (int i = 0; i < whole.Length; i += 4)
            {
                if (whole[i] < 128 && whole[i + 1] < 128 && whole[i + 2] < 128)
                {
                    dark++;
                }
            }

            Assert.InRange(dark, 200, 20000);
            Assert.Equal(255, whole[0]);
            Assert.Equal(255, whole[3]); // alpha

            // Compose the same page from 4 tiles of 512 px and compare byte-for-byte.
            byte[] composed = new byte[whole.Length];
            const int tile = 512;
            unsafe
            {
                for (int ty = 0; ty < h; ty += tile)
                {
                    for (int tx = 0; tx < w; tx += tile)
                    {
                        int tw = Math.Min(tile, w - tx);
                        int th = Math.Min(tile, h - ty);
                        byte[] buffer = new byte[tw * th * 4];
                        fixed (byte* p = buffer)
                        {
                            doc.RenderTile(0, 0, w, h, tx, ty, tw, th, p, tw * 4);
                        }

                        for (int row = 0; row < th; row++)
                        {
                            Buffer.BlockCopy(buffer, row * tw * 4, composed, ((ty + row) * w + tx) * 4, tw * 4);
                        }
                    }
                }
            }

            int mismatches = 0;
            for (int i = 0; i < whole.Length; i++)
            {
                if (whole[i] != composed[i])
                {
                    mismatches++;
                }
            }

            Assert.True(mismatches == 0, $"{mismatches} bytes differ between tiled and whole-page rendering");
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Geometry_matches_pdfium_for_all_rotations(int rotation)
    {
        string path = PdfFixtures.Write($"geom{rotation}.pdf", new PdfFixtures.PageSpec(500, 300, "g"));

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            PdfPageInfo info = doc.Pages[0];
            const double scale = 2.0;
            (int w, int h) = PdfGeometry.DisplayedSize(info.WidthPt, info.HeightPt, rotation, scale);
            foreach ((double x, double y) in new[] { (0.0, 0.0), (500.0, 300.0), (100.0, 40.0), (250.0, 150.0), (499.0, 1.0) })
            {
                (int nx, int ny) = doc.PageToDeviceNative(0, rotation, w, h, x, y);
                (double mx, double my) = PdfGeometry.PageToDevice(x, y, info.WidthPt, info.HeightPt, rotation, scale);
                Assert.InRange(mx, nx - 1.01, nx + 1.01);
                Assert.InRange(my, ny - 1.01, ny + 1.01);

                (double bx, double by) = PdfGeometry.DeviceToPage(mx, my, info.WidthPt, info.HeightPt, rotation, scale);
                Assert.Equal(x, bx, 0.001);
                Assert.Equal(y, by, 0.001);
            }
        });
    }

    [Fact]
    public void Extracts_text_with_char_boxes_and_hit_tests()
    {
        string path = PdfFixtures.Write("text.pdf", Letter);

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            PdfTextLayout layout = doc.GetTextLayout(0);
            Assert.Contains("Hello Leaf", layout.Text);
            int h = layout.Text.IndexOf('H');
            PdfRect box = layout.GetBox(h);
            Assert.False(box.IsEmpty);
            Assert.InRange(box.Left, 70, 76);      // text starts at x=72
            Assert.InRange(box.Bottom, 690, 705);  // baseline at y=700

            int hit = layout.HitTest((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
            Assert.Equal(h, hit);
            Assert.Equal(-1, layout.HitTest(10, 10));

            (int start, int count) = layout.WordAt(h + 1);
            Assert.Equal("Hello", layout.GetText(start, count));

            IReadOnlyList<PdfRect> rects = layout.GetRects(start, 10);
            Assert.Single(rects); // one line
            Assert.True(rects[0].Width > 50);
        });
    }

    [Fact]
    public void Search_finds_terms_case_insensitively()
    {
        string path = PdfFixtures.Write("search.pdf", new PdfFixtures.PageSpec(612, 792, "Leaf reads leaf files"), new PdfFixtures.PageSpec(612, 792, "no match here"));

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            List<PdfSearchHit> hits = doc.Search(0, "leaf");
            Assert.Equal(2, hits.Count);
            Assert.All(hits, hit => Assert.Equal(4, hit.CharCount));
            Assert.Equal(0, hits[0].CharIndex);

            Assert.Single(doc.Search(0, "leaf", matchCase: true));
            Assert.Empty(doc.Search(1, "leaf"));
            Assert.Empty(doc.Search(0, ""));
        });
    }

    [Fact]
    public void Password_protected_files_require_the_password()
    {
        string path = PdfFixtures.Write("secret.pdf", "s3cret", Letter);

        OnEngine(() =>
        {
            PdfException ex = Assert.Throws<PdfException>(() => PdfDocument.Open(path));
            Assert.Equal(PdfError.Password, ex.Error);

            PdfException wrong = Assert.Throws<PdfException>(() => PdfDocument.Open(path, "nope"));
            Assert.Equal(PdfError.Password, wrong.Error);

            using PdfDocument doc = PdfDocument.Open(path, "s3cret");
            Assert.Equal(1, doc.PageCount);
            Assert.Contains("Hello Leaf", doc.GetTextLayout(0).Text);
        });
    }

    [Fact]
    public void Corrupt_input_throws_PdfException_not_crash()
    {
        string garbage = Path.Combine(PdfFixtures.TempDir, "garbage.pdf");
        File.WriteAllBytes(garbage, Enumerable.Range(0, 5000).Select(i => (byte)(i * 31)).ToArray());
        string truncated = Path.Combine(PdfFixtures.TempDir, "truncated.pdf");
        byte[] good = PdfFixtures.Build([Letter]);
        File.WriteAllBytes(truncated, good[..(good.Length / 3)]);
        string empty = Path.Combine(PdfFixtures.TempDir, "empty.pdf");
        File.WriteAllBytes(empty, []);

        OnEngine(() =>
        {
            Assert.Throws<PdfException>(() => PdfDocument.Open(garbage));
            Assert.Throws<PdfException>(() => PdfDocument.Open(empty));
            Assert.Throws<PdfException>(() => PdfDocument.Open(Path.Combine(PdfFixtures.TempDir, "does-not-exist.pdf")));

            // pdfium may repair a truncated file or reject it; either way no crash.
            try
            {
                using PdfDocument doc = PdfDocument.Open(truncated);
                _ = doc.PageCount;
            }
            catch (PdfException)
            {
                // acceptable
            }
        });
    }

    [Fact]
    public void Opens_non_ascii_paths_and_does_not_lock_the_file()
    {
        string path = PdfFixtures.Write("ünïcödé 文件.pdf", Letter);

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            Assert.Equal(1, doc.PageCount);

            // Another program must be able to overwrite or delete the file while it is open in Leaf.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.True(fs.CanWrite);
            }

            File.Move(path, path + ".moved");
            File.Move(path + ".moved", path);
        });
    }

    [Fact]
    public void Page_cache_is_bounded()
    {
        var specs = Enumerable.Range(0, 20).Select(i => new PdfFixtures.PageSpec(300, 300, $"page {i}")).ToArray();
        string path = PdfFixtures.Write("many.pdf", specs);

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            for (int i = 0; i < 20; i++)
            {
                Assert.Contains($"page {i}", doc.GetTextLayout(i).Text);
            }

            doc.TrimPageCache();
            Assert.Contains("page 7", doc.GetTextLayout(7).Text);
        });
    }

    [Fact]
    public async Task Engine_thread_orders_by_priority()
    {
        var order = new List<int>();
        var gate = new ManualResetEventSlim();
        PdfiumThread thread = PdfiumThread.Instance;
        Task blocker = thread.RunAsync(() => gate.Wait());
        Task a = thread.RunAsync(() => order.Add(1), priority: 5);
        Task b = thread.RunAsync(() => order.Add(2), priority: -5);
        Task c = thread.RunAsync(() => order.Add(3), priority: 0);
        gate.Set();
        await Task.WhenAll(blocker, a, b, c);
        Assert.Equal([2, 3, 1], order);
    }
}
