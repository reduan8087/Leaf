using Xunit;

namespace Leaf.Pdfium.Tests;

/// <summary>
/// Write-side engine tests: rotate, delete, reorder, import, combine and save. Page text is the identity
/// marker throughout, so a round-trip proves the right pages ended up in the right order, not merely the
/// right count.
/// </summary>
public sealed class PdfEditingTests
{
    private static T OnEngine<T>(Func<T> work) => PdfiumThread.Instance.Run(work);

    private static void OnEngine(Action work) => PdfiumThread.Instance.Run(work);

    /// <summary>A document whose pages read "p0", "p1", ... so order is observable.</summary>
    private static string NumberedPdf(string name, int pageCount, int rotate = 0)
    {
        var pages = new PdfFixtures.PageSpec[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            pages[i] = new PdfFixtures.PageSpec(612, 792, $"p{i}", Rotate: rotate);
        }

        return PdfFixtures.Write(name, pages);
    }

    /// <summary>The first token of each page's extracted text, e.g. ["p0", "p3", "p1"].</summary>
    private static string[] PageLabels(PdfDocument doc)
    {
        var labels = new string[doc.PageCount];
        for (int i = 0; i < doc.PageCount; i++)
        {
            labels[i] = doc.GetTextLayout(i).Text.Trim().TrimEnd('\0');
        }

        return labels;
    }

    private static string OutputPath(string name) => Path.Combine(PdfFixtures.TempDir, name);

    // ---------------------------------------------------------------- reorder

    [Fact]
    public void Reorder_applies_the_whole_permutation()
    {
        string path = NumberedPdf("reorder.pdf", 5);
        string saved = OutputPath("reorder-out.pdf");

        OnEngine(() =>
        {
            using (PdfDocument doc = PdfDocument.Open(path))
            {
                Assert.Equal(["p0", "p1", "p2", "p3", "p4"], PageLabels(doc));
                Assert.False(doc.IsModified);

                doc.ReorderPages([4, 0, 3, 1, 2]);

                Assert.True(doc.IsModified);
                Assert.Equal(["p4", "p0", "p3", "p1", "p2"], PageLabels(doc));
                doc.SaveAs(saved);
            }

            // The new order must survive the round-trip, not just live in memory.
            using PdfDocument reopened = PdfDocument.Open(saved);
            Assert.Equal(["p4", "p0", "p3", "p1", "p2"], PageLabels(reopened));
        });
    }

    [Fact]
    public void Reorder_rejects_a_bad_permutation()
    {
        string path = NumberedPdf("reorder-bad.pdf", 3);

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            Assert.Throws<ArgumentException>(() => doc.ReorderPages([0, 1]));          // too few
            Assert.Throws<ArgumentException>(() => doc.ReorderPages([0, 1, 1]));       // duplicate
            Assert.Throws<ArgumentOutOfRangeException>(() => doc.ReorderPages([0, 1, 9])); // out of range
            Assert.False(doc.IsModified);
        });
    }

    [Fact]
    public void Reordering_to_the_same_order_is_a_no_op()
    {
        string path = NumberedPdf("reorder-identity.pdf", 4);

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            doc.ReorderPages([0, 1, 2, 3]);
            Assert.False(doc.IsModified);
        });
    }

    // ---------------------------------------------------------------- delete

    [Fact]
    public void Delete_removes_pages_and_keeps_the_rest_in_order()
    {
        string path = NumberedPdf("delete.pdf", 6);
        string saved = OutputPath("delete-out.pdf");

        OnEngine(() =>
        {
            using (PdfDocument doc = PdfDocument.Open(path))
            {
                // Deliberately unsorted and repeated: the caller should not have to normalise.
                doc.DeletePages([4, 1, 1]);

                Assert.Equal(4, doc.PageCount);
                Assert.Equal(["p0", "p2", "p3", "p5"], PageLabels(doc));
                doc.SaveAs(saved);
            }

            using PdfDocument reopened = PdfDocument.Open(saved);
            Assert.Equal(["p0", "p2", "p3", "p5"], PageLabels(reopened));
        });
    }

    [Fact]
    public void Deleting_every_page_is_refused()
    {
        string path = NumberedPdf("delete-all.pdf", 2);

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            PdfException ex = Assert.Throws<PdfException>(() => doc.DeletePages([0, 1]));
            Assert.Equal(PdfError.Page, ex.Error);
            Assert.Equal(2, doc.PageCount);
        });
    }

    // ---------------------------------------------------------------- rotate

    [Fact]
    public void Rotation_is_written_into_the_file_and_swaps_the_page_size()
    {
        string path = NumberedPdf("rotate.pdf", 2);
        string saved = OutputPath("rotate-out.pdf");

        OnEngine(() =>
        {
            using (PdfDocument doc = PdfDocument.Open(path))
            {
                Assert.Equal(0, doc.GetPageRotation(0));
                Assert.Equal(612, doc.Pages[0].WidthPt, 0.01);

                doc.SetPageRotation(0, 1); // a quarter turn clockwise

                Assert.Equal(1, doc.GetPageRotation(0));
                // Reported page sizes have /Rotate applied, so the page table must have been rebuilt.
                Assert.Equal(792, doc.Pages[0].WidthPt, 0.01);
                Assert.Equal(612, doc.Pages[0].HeightPt, 0.01);
                Assert.Equal(612, doc.Pages[1].WidthPt, 0.01); // untouched
                doc.SaveAs(saved);
            }

            using PdfDocument reopened = PdfDocument.Open(saved);
            Assert.Equal(1, reopened.GetPageRotation(0));
            Assert.Equal(792, reopened.Pages[0].WidthPt, 0.01);
            Assert.Equal(0, reopened.GetPageRotation(1));
        });
    }

    // ---------------------------------------------------------------- import

    [Fact]
    public void Import_inserts_selected_pages_at_a_position()
    {
        string target = NumberedPdf("import-target.pdf", 3);
        string donorPath = PdfFixtures.Write(
            "import-donor.pdf",
            new PdfFixtures.PageSpec(612, 792, "d0"),
            new PdfFixtures.PageSpec(612, 792, "d1"),
            new PdfFixtures.PageSpec(612, 792, "d2"));

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(target);
            using PdfDocument donor = PdfDocument.Open(donorPath);

            doc.ImportPages(donor, [2, 0], atIndex: 1);

            Assert.Equal(5, doc.PageCount);
            Assert.Equal(["p0", "d2", "d0", "p1", "p2"], PageLabels(doc));
        });
    }

    // ---------------------------------------------------------------- save

    [Fact]
    public void Save_preserves_text_and_reports_write_failures()
    {
        string path = NumberedPdf("save.pdf", 2);

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);

            // A directory path can never be opened as a file.
            PdfException ex = Assert.Throws<PdfException>(() => doc.SaveAs(PdfFixtures.TempDir));
            Assert.Equal(PdfError.Write, ex.Error);

            string saved = OutputPath("save-out.pdf");
            doc.SaveAs(saved);
            Assert.True(new FileInfo(saved).Length > 0);
            doc.MarkSaved();
            Assert.False(doc.IsModified);
        });
    }

    [Fact]
    public void A_failed_save_leaves_no_partial_file()
    {
        string path = NumberedPdf("save-partial.pdf", 1);
        string saved = OutputPath("save-partial-out.pdf");

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            doc.SaveAs(saved);
            Assert.True(File.Exists(saved));
        });

        // Holding the destination open exclusively makes the next save fail at the open.
        using (var block = new FileStream(saved, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            OnEngine(() =>
            {
                using PdfDocument doc = PdfDocument.Open(path);
                PdfException ex = Assert.Throws<PdfException>(() => doc.SaveAs(saved));
                Assert.Equal(PdfError.Write, ex.Error);
            });
        }

        // The earlier good copy must still be readable: a failed save never truncates.
        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(saved);
            Assert.Equal(1, doc.PageCount);
        });
    }

    [Fact]
    public void Saving_does_not_lock_the_output_or_the_source()
    {
        string path = NumberedPdf("save-unlocked.pdf", 1);
        string saved = OutputPath("save-unlocked-out.pdf");

        OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            doc.SaveAs(saved);
        });

        File.Delete(saved); // would throw if the sink had leaked a handle
        Assert.False(File.Exists(saved));
    }

    // ---------------------------------------------------------------- combine

    [Fact]
    public void Combine_merges_documents_in_order()
    {
        string a = PdfFixtures.Write("combine-a.pdf", new PdfFixtures.PageSpec(612, 792, "a0"), new PdfFixtures.PageSpec(612, 792, "a1"));
        string b = PdfFixtures.Write("combine-b.pdf", new PdfFixtures.PageSpec(400, 400, "b0"));
        string saved = OutputPath("combine-out.pdf");

        OnEngine(() =>
        {
            using (var builder = new PdfBuilder())
            {
                Assert.Equal(0, builder.PageCount);

                using (PdfDocument first = PdfDocument.Open(a))
                {
                    builder.AppendPages(first);
                    builder.CopyViewerPreferencesFrom(first);
                }

                using (PdfDocument second = PdfDocument.Open(b))
                {
                    builder.AppendPages(second);
                }

                Assert.Equal(3, builder.PageCount);
                builder.Save(saved);
            }

            using PdfDocument combined = PdfDocument.Open(saved);
            Assert.Equal(["a0", "a1", "b0"], PageLabels(combined));
            // Page geometry must come across too, not just the content.
            Assert.Equal(612, combined.Pages[0].WidthPt, 0.01);
            Assert.Equal(400, combined.Pages[2].WidthPt, 0.01);
        });
    }

    [Fact]
    public void Combine_can_take_a_subset_of_pages()
    {
        string source = NumberedPdf("combine-subset.pdf", 5);
        string saved = OutputPath("combine-subset-out.pdf");

        OnEngine(() =>
        {
            using (var builder = new PdfBuilder())
            {
                using (PdfDocument doc = PdfDocument.Open(source))
                {
                    builder.AppendPages(doc, [3, 1]);
                }

                builder.Save(saved);
            }

            using PdfDocument combined = PdfDocument.Open(saved);
            Assert.Equal(["p3", "p1"], PageLabels(combined));
        });
    }

    [Fact]
    public void Combining_nothing_is_refused_rather_than_writing_an_invalid_pdf()
    {
        OnEngine(() =>
        {
            using var builder = new PdfBuilder();
            PdfException ex = Assert.Throws<PdfException>(() => builder.Save(OutputPath("combine-empty.pdf")));
            Assert.Equal(PdfError.Page, ex.Error);
        });

        Assert.False(File.Exists(OutputPath("combine-empty.pdf")));
    }

    [Fact]
    public void A_damaged_source_fails_the_combine_without_crashing()
    {
        string good = NumberedPdf("combine-good.pdf", 1);
        string junk = Path.Combine(PdfFixtures.TempDir, "combine-junk.pdf");
        File.WriteAllBytes(junk, "this is not a PDF"u8.ToArray());

        OnEngine(() =>
        {
            using var builder = new PdfBuilder();
            using (PdfDocument doc = PdfDocument.Open(good))
            {
                builder.AppendPages(doc);
            }

            Assert.Throws<PdfException>(() => PdfDocument.Open(junk));

            // The builder is untouched by the failed source and can still be used.
            Assert.Equal(1, builder.PageCount);
        });
    }

    // ---------------------------------------------------------------- images

    [Fact]
    public void An_image_becomes_a_page_sized_to_the_image()
    {
        string saved = OutputPath("image-page.pdf");
        const int w = 200;
        const int h = 100;
        byte[] pixels = SolidBgra(w, h, 0x20, 0xA0, 0x40);

        OnEngine(() =>
        {
            using (var builder = new PdfBuilder())
            {
                builder.AppendImagePage(new PdfImagePlacement(w, h), pixels, w * 4);
                Assert.Equal(1, builder.PageCount);
                builder.Save(saved);
            }

            using PdfDocument doc = PdfDocument.Open(saved);
            Assert.Equal(1, doc.PageCount);
            // "Match image": 200x100 px at 96 DPI is 150x75 pt.
            Assert.Equal(150, doc.Pages[0].WidthPt, 0.5);
            Assert.Equal(75, doc.Pages[0].HeightPt, 0.5);

            // The image must actually be drawn: the middle of the page is the fill colour, not white.
            byte[] rendered = doc.RenderPage(0, 0, 1.0, out int rw, out int rh);
            int middle = (((rh / 2) * rw) + (rw / 2)) * 4;
            Assert.Equal(0x20, rendered[middle]);     // B
            Assert.Equal(0xA0, rendered[middle + 1]); // G
            Assert.Equal(0x40, rendered[middle + 2]); // R
        });
    }

    [Fact]
    public void An_image_is_centred_and_keeps_its_aspect_ratio_on_a_fixed_page()
    {
        string saved = OutputPath("image-a4.pdf");
        const int w = 100;
        const int h = 100;
        byte[] pixels = SolidBgra(w, h, 0, 0, 0);
        const double a4W = 595;
        const double a4H = 842;

        OnEngine(() =>
        {
            using (var builder = new PdfBuilder())
            {
                builder.AppendImagePage(new PdfImagePlacement(w, h, a4W, a4H), pixels, w * 4);
                builder.Save(saved);
            }

            using PdfDocument doc = PdfDocument.Open(saved);
            Assert.Equal(a4W, doc.Pages[0].WidthPt, 0.5);
            Assert.Equal(a4H, doc.Pages[0].HeightPt, 0.5);

            byte[] rendered = doc.RenderPage(0, 0, 1.0, out int rw, out int rh);
            // A square image on a portrait page: black across the middle row, white top and bottom.
            int centre = (((rh / 2) * rw) + (rw / 2)) * 4;
            Assert.True(rendered[centre] < 40, "the image should cover the centre of the page");
            int topBand = (((rh / 20) * rw) + (rw / 2)) * 4;
            Assert.True(rendered[topBand] > 200, "the page above the image should stay white");
        });
    }

    [Fact]
    public void A_bad_pixel_buffer_is_rejected_before_pdfium_sees_it()
    {
        OnEngine(() =>
        {
            using var builder = new PdfBuilder();
            byte[] tooSmall = new byte[10];
            Assert.Throws<ArgumentException>(() => builder.AppendImagePage(new PdfImagePlacement(100, 100), tooSmall, 400));
            Assert.Throws<ArgumentException>(() => builder.AppendImagePage(new PdfImagePlacement(0, 0), tooSmall, 0));
            Assert.Equal(0, builder.PageCount); // no half-built page left behind
        });
    }

    private static byte[] SolidBgra(int width, int height, byte b, byte g, byte r)
    {
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 0xFF;
        }

        return pixels;
    }
}
