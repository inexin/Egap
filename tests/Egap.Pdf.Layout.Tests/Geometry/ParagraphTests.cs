using Egap.Pdf.Layout.Tests.Synthetic;

namespace Egap.Pdf.Layout.Tests.Geometry;

/// <summary>Paragraph splits and joins and de-hyphenation (Build Plan task 5) on the synthetic pages.</summary>
public sealed class ParagraphTests
{
    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void ParagraphsMatchTheTruth(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        List<string> found = [];
        BlockDraft? previous = null;
        for (int page = 0; page < doc.PageCount; page++)
        {
            List<TruthBlock> furniture = [.. doc.Blocks.Where(b => b.Page == page && b.Order < 0)];
            List<TruthTable> tables = [.. doc.Tables.Where(t => t.Page == page)];
            List<BlockDraft> paragraphs = [.. Paragraphs.Split(BlockBuilderTests.Analyze(pdf, page))
                .Where(p => !furniture.Any(f => f.Lines.Any(l => Inside(p.Box, l.Box))) && !tables.Any(t => Inside(t.Box, p.Box)))];
            Paragraphs.JoinAcrossColumns(paragraphs);
            if (previous is not null && paragraphs.Count > 0 && Paragraphs.Carries(previous, paragraphs[0], Paragraphs.IndentStyle(paragraphs)))
            {
                paragraphs[0].Continues = true;
            }

            foreach (BlockDraft paragraph in paragraphs)
            {
                if (paragraph.Continues && found.Count > 0)
                {
                    found[^1] = Paragraphs.Join(found[^1], paragraph.Text);
                }
                else
                {
                    found.Add(paragraph.Text);
                }
            }

            previous = paragraphs.LastOrDefault() ?? previous;
        }

        // The truth keeps list markers and footnote markers apart from the text; the page has them in the lines.
        List<TruthBlock> truth = [.. doc.Blocks.Where(b => b.Order >= 0 && !b.Continues).OrderBy(b => b.Order)];
        List<string> expected = [.. doc.ReadingOrderText().Select((t, i) => truth[i] switch
        {
            { Role: TruthRole.ListItem, Marker: string m } => m + " " + t,
            { Role: TruthRole.Paragraph, Marker: string m } => t + m,
            _ => t,
        })];
        Assert.Equal(expected.Select(Squash), found.Select(Squash));
    }

    [Theory]
    [InlineData("A paragraph that ends with", "and goes on in the next column.", true)]
    [InlineData("A paragraph that ends.", "A new one starts here.", false)]
    [InlineData("A word broken at the col-", "umn end goes on.", true)]
    [InlineData("A list follows:", "1. First item", false)]
    public void ParagraphsCarryOnAcrossColumnsOnlyWhenTheyHaveNotEnded(string last, string first, bool carries)
    {
        Assert.Equal(carries, Paragraphs.Carries(Block(last), Block(first)));
    }

    [Fact]
    public void BrokenWordsJoinWithoutTheirHyphen()
    {
        Assert.Equal("column end", Paragraphs.Join("col-", "umn end"));
        Assert.Equal("self- Contained", Paragraphs.Join("self-", "Contained"));
        Assert.Equal("one two", Paragraphs.Join("one", "two"));
    }

    private static BlockDraft Block(string text)
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 400, 200));
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawText(text, new System.Numerics.Vector2(20, 50), SyntheticDocument.Body, new Egami.Drawing.SolidPaint(new Egami.Pixels.RgbaF32(0, 0, 0, 1)));
        }

        using MemoryStream saved = new();
        created.Save(saved);
        using PdfDocument pdf = PdfDocument.Open(saved.ToArray());
        return new BlockDraft(LayoutRole.Text, [.. PageReader.Read(pdf, 0).Lines]);
    }

    private static bool Inside(PageBox outer, TruthBox inner)
    {
        return outer.Contains((inner.Left + inner.Right) / 2, (inner.Top + inner.Bottom) / 2, 1);
    }

    private static bool Inside(TruthBox outer, PageBox inner)
    {
        return outer.Contains(inner.CenterX, inner.CenterY, 1);
    }

    private static string Squash(string text)
    {
        return new([.. text.Where(c => !char.IsWhiteSpace(c) && c != '\u00AD')]);
    }
}
