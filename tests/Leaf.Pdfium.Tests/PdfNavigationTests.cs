using Xunit;

namespace Leaf.Pdfium.Tests;

/// <summary>Outline and link extraction, which drive the bookmarks panel and clickable links.</summary>
public sealed class PdfNavigationTests
{
    private static T OnEngine<T>(Func<T> work) => PdfiumThread.Instance.Run(work);

    [Fact]
    public void Reads_the_outline_as_a_tree_with_destinations()
    {
        string path = PdfNavigationFixture.Write("outline.pdf");

        IReadOnlyList<PdfOutlineNode> outline = OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            return doc.GetOutline();
        });

        Assert.Equal(2, outline.Count);

        Assert.Equal("Chapter one", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
        Assert.Empty(outline[0].Children);

        Assert.Equal("Chapter two", outline[1].Title);
        Assert.Equal(1, outline[1].PageIndex);
        Assert.Equal(2, outline[1].Children.Count);
        Assert.Equal("Section 2.1", outline[1].Children[0].Title);
        Assert.Equal(1, outline[1].Children[0].PageIndex);
        Assert.Equal("Section 2.2", outline[1].Children[1].Title);
        Assert.Equal(2, outline[1].Children[1].PageIndex);
    }

    [Fact]
    public void A_document_with_no_outline_reports_an_empty_one()
    {
        string path = PdfFixtures.Write("no-outline.pdf", new PdfFixtures.PageSpec(612, 792, "plain"));

        IReadOnlyList<PdfOutlineNode> outline = OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            return doc.GetOutline();
        });

        Assert.Empty(outline);
    }

    [Fact]
    public void Reads_external_and_internal_links_with_their_rectangles()
    {
        string path = PdfNavigationFixture.Write("links.pdf");

        List<PdfLink> page1 = OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            return doc.GetLinks(0);
        });

        Assert.Equal(2, page1.Count);

        PdfLink uri = Assert.Single(page1, l => l.IsExternal);
        Assert.Equal(PdfNavigationFixture.Uri, uri.Uri);
        Assert.False(uri.IsInternal);
        Assert.Equal(PdfNavigationFixture.UriLinkRect.Left, uri.Bounds.Left, 0.5);
        Assert.Equal(PdfNavigationFixture.UriLinkRect.Bottom, uri.Bounds.Bottom, 0.5);
        Assert.Equal(PdfNavigationFixture.UriLinkRect.Right, uri.Bounds.Right, 0.5);
        Assert.Equal(PdfNavigationFixture.UriLinkRect.Top, uri.Bounds.Top, 0.5);

        PdfLink jump = Assert.Single(page1, l => l.IsInternal);
        Assert.Equal(2, jump.TargetPage); // third page
        Assert.Null(jump.Uri);
    }

    [Fact]
    public void A_GoTo_action_link_resolves_to_its_page()
    {
        string path = PdfNavigationFixture.Write("links-goto.pdf");

        List<PdfLink> page2 = OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            return doc.GetLinks(1);
        });

        PdfLink link = Assert.Single(page2);
        Assert.Equal(0, link.TargetPage); // back to the first page
    }

    [Fact]
    public void A_page_with_no_links_and_an_out_of_range_page_both_come_back_empty()
    {
        string path = PdfNavigationFixture.Write("links-empty.pdf");

        (List<PdfLink> third, List<PdfLink> beyond) = OnEngine(() =>
        {
            using PdfDocument doc = PdfDocument.Open(path);
            return (doc.GetLinks(2), doc.GetLinks(99));
        });

        Assert.Empty(third);
        Assert.Empty(beyond);
    }
}
