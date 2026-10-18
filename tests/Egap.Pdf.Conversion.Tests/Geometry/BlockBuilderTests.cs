using Egap.Pdf.Conversion.Tests.Synthetic;

namespace Egap.Pdf.Conversion.Tests.Geometry;

/// <summary>Docstrum-style blocks, XY-cut columns and reading order (Build Plan task 4) on the synthetic pages.</summary>
public sealed class BlockBuilderTests
{
    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void BlocksKeepParagraphsWholeAndColumnsApart(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        for (int page = 0; page < doc.PageCount; page++)
        {
            List<BlockDraft> blocks = Analyze(pdf, page);
            List<TruthBlock> truth = [.. doc.Blocks.Where(b => b.Page == page)];
            Dictionary<TruthBlock, HashSet<int>> homes = truth.ToDictionary(t => t, t => t.Lines.Select(l => Home(blocks, l)).ToHashSet());

            foreach ((TruthBlock block, HashSet<int> found) in homes)
            {
                Assert.True(found.Count == 1 && !found.Contains(-1), $"{scene} page {page + 1}: \"{block.Text[..Math.Min(40, block.Text.Length)]}\" lands in blocks {string.Join(", ", found)}");
            }

            foreach (IGrouping<int, TruthBlock> together in homes.GroupBy(h => h.Value.Single(), h => h.Key))
            {
                List<TruthBlock> members = [.. together];
                Assert.True(members.All(a => members.All(b => Overlaps(a.Box, b.Box))), $"{scene} page {page + 1}: block {together.Key} mixes columns");
                Assert.True(members.All(m => m.Order < 0) || members.All(m => m.Order >= 0), $"{scene} page {page + 1}: block {together.Key} mixes furniture and body");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Scenes))]
    public void ReadingOrderFollowsTheTruth(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        for (int page = 0; page < doc.PageCount; page++)
        {
            List<BlockDraft> blocks = Analyze(pdf, page);
            List<int> orders = [];
            foreach (PageLine line in blocks.SelectMany(b => b.Lines))
            {
                TruthBlock? owner = doc.Blocks.FirstOrDefault(t => t.Page == page && t.Order >= 0 && t.Lines.Any(l => Holds(line, l)));
                if (owner is not null && (orders.Count == 0 || orders[^1] != owner.Order))
                {
                    orders.Add(owner.Order);
                }
            }

            Assert.True(orders.SequenceEqual(orders.Order()), $"{scene} page {page + 1}: {string.Join(", ", orders)}");
        }
    }

    [Fact]
    public void AlignedParagraphBreaksDoNotSliceColumns()
    {
        // Two columns of two blocks each whose breaks line up: read the left column, then the right.
        List<PageBox> boxes = [new(50, 100, 280, 200), new(300, 100, 530, 200), new(50, 206, 280, 300), new(300, 206, 530, 300)];

        List<int> order = ReadingOrder.Order(boxes, [0, 1, 2, 3], 10, LayoutThresholds.Default);

        Assert.Equal([0, 2, 1, 3], order);
    }

    [Fact]
    public void AWideBandAboveColumnsIsReadFirst()
    {
        List<PageBox> boxes = [new(300, 120, 530, 300), new(50, 60, 530, 80), new(50, 120, 280, 300)];

        Assert.Equal([1, 2, 0], ReadingOrder.Order(boxes, [2, 0, 1], 10, LayoutThresholds.Default));
    }

    [Fact]
    public void ContentOrderWinsWhenGeometryDisagreesBadly()
    {
        // Content reads the right column first, which still reads down the page: geometry would read left first.
        List<PageBox> boxes = [new(50, 100, 280, 140), new(50, 150, 280, 190), new(50, 200, 280, 240), new(300, 100, 530, 140), new(300, 150, 530, 190), new(300, 200, 530, 240)];
        LayoutThresholds strict = LayoutThresholds.Default with { OrderAgreement = 0.9 };

        Assert.Equal([0, 1, 2, 3, 4, 5], ReadingOrder.Order(boxes, [0, 1, 2, 3, 4, 5], 10, strict));
        Assert.Equal([3, 4, 5, 0, 1, 2], ReadingOrder.Order(boxes, [3, 4, 5, 0, 1, 2], 10, strict));
    }

    [Theory]
    [InlineData("\u2022", true)]
    [InlineData("1.", true)]
    [InlineData("a)", true)]
    [InlineData("(iv)", true)]
    [InlineData("2.3.1", true)]
    [InlineData("word", false)]
    [InlineData("1999", false)]
    [InlineData("Dr.", false)]
    public void MarkersLookLikeMarkers(string word, bool marker)
    {
        Assert.Equal(marker, BlockBuilder.IsMarker(word));
    }

    internal static List<BlockDraft> Analyze(PdfDocument pdf, int page)
    {
        PageContent content = PageReader.Read(pdf, page);
        List<(BlockDraft Block, int Content)> built = BlockBuilder.Build(content.Glyphs, content.Paths, LayoutThresholds.Default);
        double size = content.Lines.Count == 0 ? 10 : content.Lines.Select(l => l.Size).Order().ElementAt(content.Lines.Count / 2);
        List<int> order = ReadingOrder.Order([.. built.Select(b => b.Block.Box)], [.. built.Select(b => b.Content)], size, LayoutThresholds.Default);
        return [.. order.Select(i => built[i].Block)];
    }

    /// <summary>The block holding a truth line, or -1.</summary>
    private static int Home(List<BlockDraft> blocks, TruthLine line)
    {
        return blocks.FindIndex(b => b.Lines.Any(l => Holds(l, line)));
    }

    private static bool Holds(PageLine line, TruthLine truth)
    {
        double x = (truth.Box.Left + truth.Box.Right) / 2, y = (truth.Box.Top + truth.Box.Bottom) / 2;
        return line.Box.Contains(x, y, 1) || (line.Box.Contains(truth.Box.Left + 2, y, 1) && line.Text.Contains(truth.Text.Split(' ')[0], StringComparison.Ordinal));
    }

    private static bool Overlaps(TruthBox a, TruthBox b)
    {
        return Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left);
    }
}
