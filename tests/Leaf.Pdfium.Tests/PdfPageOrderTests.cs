using Xunit;

namespace Leaf.Pdfium.Tests;

/// <summary>
/// The delete-then-reorder index maths behind Organize Pages. Getting this wrong silently scrambles a
/// document, so it is tested against a model that simply applies the operations to a list.
/// </summary>
public sealed class PdfPageOrderTests
{
    private static T OnEngine<T>(Func<T> work) => PdfiumThread.Instance.Run(work);

    [Fact]
    public void Keeping_everything_in_place_plans_nothing()
    {
        (int[] removed, int[] order) = PdfPageOrder.Plan(4, [0, 1, 2, 3]);

        Assert.Empty(removed);
        Assert.Equal([0, 1, 2, 3], order);
    }

    [Fact]
    public void Deletions_renumber_the_pages_that_follow()
    {
        // Keep pages 0, 2 and 5, reversed. After deleting 1, 3 and 4 they sit at 0, 1 and 2.
        (int[] removed, int[] order) = PdfPageOrder.Plan(6, [5, 2, 0]);

        Assert.Equal([1, 3, 4], removed);
        Assert.Equal([2, 1, 0], order);
    }

    [Fact]
    public void Pure_reordering_never_deletes()
    {
        (int[] removed, int[] order) = PdfPageOrder.Plan(5, [4, 0, 3, 1, 2]);

        Assert.Empty(removed);
        Assert.Equal([4, 0, 3, 1, 2], order);
    }

    [Fact]
    public void A_plan_must_be_a_valid_selection()
    {
        Assert.Throws<ArgumentException>(() => PdfPageOrder.Plan(3, []));
        Assert.Throws<ArgumentException>(() => PdfPageOrder.Plan(3, [1, 1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageOrder.Plan(3, [0, 7]));
    }

    [Theory]
    // Every shape Organize Pages can produce: reverse, delete-and-reorder, keep one, move to the end.
    [InlineData(6, new[] { 5, 4, 3, 2, 1, 0 })]
    [InlineData(6, new[] { 0, 5 })]
    [InlineData(6, new[] { 3 })]
    [InlineData(6, new[] { 1, 2, 3, 4, 5, 0 })]
    [InlineData(6, new[] { 4, 1, 5 })]
    public void The_plan_applied_to_a_real_document_produces_exactly_those_pages(int pageCount, int[] keptInOrder)
    {
        var pages = new PdfFixtures.PageSpec[pageCount];
        for (int i = 0; i < pageCount; i++)
        {
            pages[i] = new PdfFixtures.PageSpec(612, 792, $"p{i}");
        }

        string path = PdfFixtures.Write($"plan-{pageCount}-{string.Join('_', keptInOrder)}.pdf", pages);
        string[] expected = [.. keptInOrder.Select(i => $"p{i}")];

        string[] actual = OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            (int[] removed, int[] order) = PdfPageOrder.Plan(doc.PageCount, keptInOrder);
            if (removed.Length > 0)
            {
                doc.DeletePages(removed);
            }

            doc.ReorderPages(order);

            var labels = new string[doc.PageCount];
            for (int i = 0; i < labels.Length; i++)
            {
                labels[i] = doc.GetTextLayout(i).Text.Trim().TrimEnd('\0');
            }

            return labels;
        });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Imported_pages_can_be_planned_alongside_the_originals()
    {
        // The shape Organize Pages produces when pages are inserted: imports land at the end, then the plan
        // interleaves them with the pages that were already there.
        string target = PdfFixtures.Write(
            "plan-import-target.pdf",
            new PdfFixtures.PageSpec(612, 792, "a0"),
            new PdfFixtures.PageSpec(612, 792, "a1"));
        string donor = PdfFixtures.Write(
            "plan-import-donor.pdf",
            new PdfFixtures.PageSpec(612, 792, "b0"),
            new PdfFixtures.PageSpec(612, 792, "b1"));

        string[] labels = OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(target);
            using PdfDocument source = PdfDocument.Open(donor);

            int appendAt = doc.PageCount;
            doc.ImportPages(source, [1], appendAt); // "b1" arrives at index 2

            // Wanted result: b1, a0, a1 -- and "a1" is deliberately kept so nothing is deleted.
            (int[] removed, int[] order) = PdfPageOrder.Plan(doc.PageCount, [appendAt, 0, 1]);
            if (removed.Length > 0)
            {
                doc.DeletePages(removed);
            }

            doc.ReorderPages(order);

            var result = new string[doc.PageCount];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = doc.GetTextLayout(i).Text.Trim().TrimEnd('\0');
            }

            return result;
        });

        Assert.Equal(["b1", "a0", "a1"], labels);
    }

    [Fact]
    public void Rotation_then_delete_then_reorder_keeps_the_rotation_with_its_page()
    {
        string path = PdfFixtures.Write(
            "plan-rotate.pdf",
            new PdfFixtures.PageSpec(612, 792, "p0"),
            new PdfFixtures.PageSpec(612, 792, "p1"),
            new PdfFixtures.PageSpec(612, 792, "p2"));

        (string Label, int Rotation)[] result = OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);

            // Rotate p2 while the original indices still hold, as the organizer does.
            doc.SetPageRotation(2, 1);

            (int[] removed, int[] order) = PdfPageOrder.Plan(doc.PageCount, [2, 0]);
            doc.DeletePages(removed);
            doc.ReorderPages(order);

            var pages = new (string, int)[doc.PageCount];
            for (int i = 0; i < pages.Length; i++)
            {
                pages[i] = (doc.GetTextLayout(i).Text.Trim().TrimEnd('\0'), doc.GetPageRotation(i));
            }

            return pages;
        });

        Assert.Equal(2, result.Length);
        Assert.Equal(("p2", 1), result[0]); // the rotation travelled with the page
        Assert.Equal(("p0", 0), result[1]);
    }
}
