using Egap.Documents.Pdf;
using Egap.Documents.Tests.Pdf.Synthetic;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Geometry;

/// <summary>Roles across a document (Build Plan task 6) on the synthetic pages: every paragraph's role and level as drawn.</summary>
public sealed class RolesTests
{
    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void EveryParagraphGetsItsRole(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        List<PageDraft> pages = Untagged(pdf);
        Roles.Assign(pages, PageReader.ReadOutline(pdf));

        List<string> wrong = [];
        foreach (PageDraft page in pages)
        {
            List<TruthTable> tables = [.. doc.Tables.Where(t => t.Page == page.Content.Index)];
            foreach (BlockDraft block in page.Blocks.Where(b => !tables.Any(t => t.Box.Contains(b.Box.CenterX, b.Box.CenterY, 1))))
            {
                TruthBlock? truth = doc.Blocks.FirstOrDefault(t => t.Page == page.Content.Index && t.Lines.Any(l => l.Box.Contains(block.Lines[0].Box.CenterX, block.Lines[0].Box.CenterY, 1)));
                if (truth is null)
                {
                    wrong.Add($"page {page.Content.Index + 1}: no truth for \"{block.Text}\"");
                    continue;
                }

                (LayoutRole role, int level) = Expected(truth);
                if (block.Role != role || (role is LayoutRole.Heading or LayoutRole.ListItem && block.Level != level))
                {
                    wrong.Add($"page {page.Content.Index + 1}: {block.Role} {block.Level} for {role} {level} \"{Short(block.Text)}\"");
                }
            }
        }

        Assert.True(wrong.Count == 0, $"{scene}: {string.Join("; ", wrong)}");
    }

    [Fact]
    public void FootnotesLinkToTheirMarkers()
    {
        SyntheticDocument doc = SyntheticScenes.Footnotes();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        List<PageDraft> pages = Untagged(pdf);

        List<Roles.NoteLink> links = Roles.Assign(pages, []);

        Assert.Equal(["1", "2"], links.Select(l => l.Note.Marker));
        Assert.All(links, l => Assert.Equal(l.Note.Marker, l.Marker.Text));
        Assert.All(links, l => Assert.True(l.Marker.Box.Bottom < l.Note.Box.Top));
    }

    [Fact]
    public void AnOutlineEntrySetsTheHeadingLevel()
    {
        SyntheticDocument doc = SyntheticScenes.OneColumn();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        List<PageDraft> pages = Untagged(pdf);

        Roles.Assign(pages, [new OutlineEntry("Method", 2, 0, null)]);

        Assert.Equal(3, pages[0].Blocks.Single(b => b.Text == "Method").Level);
    }

    /// <summary>The untagged path so far: blocks, reading order and paragraphs on every page (tasks 2, 4 and 5).</summary>
    internal static List<PageDraft> Untagged(PdfDocument pdf)
    {
        List<PageDraft> pages = [];
        for (int page = 0; page < pdf.Pages.Count; page++)
        {
            PageDraft draft = new(PageReader.Read(pdf, page));
            List<BlockDraft> paragraphs = Paragraphs.Split(BlockBuilderTests.Analyze(pdf, page));
            Paragraphs.JoinAcrossColumns(paragraphs);
            for (int i = 0; i < paragraphs.Count; i++)
            {
                paragraphs[i].Order = i;
            }

            draft.Blocks.AddRange(paragraphs);
            pages.Add(draft);
        }

        return pages;
    }

    private static (LayoutRole Role, int Level) Expected(TruthBlock truth)
    {
        return truth.Role switch
        {
            TruthRole.Title => (LayoutRole.Title, 0),
            TruthRole.Heading => (LayoutRole.Heading, truth.Level),
            TruthRole.ListItem => (LayoutRole.ListItem, truth.Level),
            TruthRole.Quote => (LayoutRole.Quote, 0),
            TruthRole.Code => (LayoutRole.Code, 0),
            TruthRole.Caption => (LayoutRole.Caption, 0),
            TruthRole.Footnote => (LayoutRole.Footnote, 0),
            TruthRole.PageHeader => (LayoutRole.PageHeader, 0),
            TruthRole.PageFooter => (LayoutRole.PageFooter, 0),
            TruthRole.PageNumber => (LayoutRole.PageNumber, 0),
            _ => (LayoutRole.Text, 0),
        };
    }

    private static string Short(string text)
    {
        return text.Length <= 40 ? text : text[..40];
    }
}
