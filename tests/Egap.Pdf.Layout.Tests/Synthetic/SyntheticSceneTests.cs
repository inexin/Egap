using System.Text.Json;

namespace Egap.Pdf.Layout.Tests.Synthetic;

/// <summary>
/// The synthetic page generator draws what its truth says (Build Plan task 1): Phase 3's text extraction finds every truth
/// line at its box, and each scene shows the feature it is for.
/// </summary>
public sealed class SyntheticSceneTests
{
    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void ExtractionFindsEveryTruthLineInItsBox(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        Assert.Equal(doc.PageCount, pdf.Pages.Count);
        for (int page = 0; page < doc.PageCount; page++)
        {
            List<(string Text, TruthBox Box)> found = [.. pdf.Pages[page].GetText().Lines.Select(l =>
                (l.Text, new TruthBox(l.Box.Left, doc.Height - l.Box.Top, l.Box.Right, doc.Height - l.Box.Bottom)))];
            IEnumerable<TruthLine> lines = doc.Blocks.Where(b => b.Page == page).SelectMany(b => b.Lines)
                .Concat(doc.Tables.Where(t => t.Page == page).SelectMany(t => t.Cells).Where(c => c.Text.Length > 0)
                    .Select(c => new TruthLine(c.Text, default, 0)));
            foreach (TruthLine line in lines)
            {
                Assert.True(found.Any(f => Squash(f.Text).Contains(Squash(line.Text), StringComparison.Ordinal)
                    && (line.Box == default || Overlaps(f.Box, line.Box))),
                    $"{scene} page {page + 1}: \"{line.Text}\" not found where drawn; lines: {string.Join(" | ", found.Select(f => f.Text))}");
            }
        }
    }

    [Fact]
    public void TwoColumnsBreakParagraphsAcrossColumnsAndPages()
    {
        SyntheticDocument doc = SyntheticScenes.TwoColumns();
        List<TruthBlock> paragraphs = [.. doc.Blocks.Where(b => b.Role == TruthRole.Paragraph)];

        Assert.True(doc.PageCount >= 2);
        Assert.Contains(paragraphs, p => p.Continues && p.Page == 0);
        Assert.Contains(paragraphs, p => p.Continues && p.Page == 1);
        Assert.True(paragraphs.Select(p => p.Box.Left).Distinct().Count() >= 2, "text should sit in two columns");
        Assert.Equal(12, doc.ReadingOrderText().Count(t => t.Length > 300));
    }

    [Fact]
    public void PullQuoteKeepsColumnTextAway()
    {
        SyntheticDocument doc = SyntheticScenes.PullQuote();
        TruthBox quote = doc.Blocks.Single(b => b.Role == TruthRole.Quote).Box;

        Assert.DoesNotContain(doc.Blocks.Where(b => b.Role == TruthRole.Paragraph).SelectMany(b => b.Lines), l => Overlaps(l.Box, quote));
        Assert.True(quote.Left > 595 / 2.0 - 150 && quote.Right < 595 / 2.0 + 150);
    }

    [Fact]
    public void ListsHangAtThreeLevels()
    {
        SyntheticDocument doc = SyntheticScenes.Lists();
        List<TruthBlock> items = [.. doc.Blocks.Where(b => b.Role == TruthRole.ListItem)];

        Assert.Equal([1, 2, 3], items.Select(i => i.Level).Distinct().Order());
        foreach (TruthBlock item in items.Where(i => i.Lines.Count > 1))
        {
            // A hanging indent: the wrapped lines start where the first line's text does, right of the marker.
            Assert.Equal(item.Lines[0].Box.Left, item.Lines[1].Box.Left, 0.01);
        }
    }

    [Fact]
    public void TablesCoverEveryGridPositionOnce()
    {
        SyntheticDocument doc = SyntheticScenes.Tables();

        Assert.Equal([TruthRules.All, TruthRules.Partial, TruthRules.None], doc.Tables.Select(t => t.Rules));
        foreach (TruthTable table in doc.Tables)
        {
            int covered = table.Cells.Sum(c => c.RowSpan * c.ColumnSpan);
            Assert.Equal(table.Rows * table.Columns, covered);
        }

        Assert.Contains(doc.Tables[0].Cells, c => c.ColumnSpan == 2 && c.Text == "n/a");
    }

    [Fact]
    public void ALongTableRepeatsItsHeaderOnTheNextPage()
    {
        SyntheticDocument doc = SyntheticScenes.TableAcrossPages();

        Assert.True(doc.Tables.Count >= 2);
        Assert.False(doc.Tables[0].Continues);
        Assert.All(doc.Tables.Skip(1), t => Assert.True(t.Continues && t.RepeatsHeader));
        Assert.Equal("Sample", doc.Tables[1].Cells.Single(c => c.Row == 0 && c.Column == 1).Text);
        Assert.Equal(61, doc.Tables.Sum(t => t.Rows) - (doc.Tables.Count - 1));
    }

    [Fact]
    public void FootnotesSitAtThePageFootBelowTheText()
    {
        SyntheticDocument doc = SyntheticScenes.Footnotes();
        List<TruthBlock> notes = [.. doc.Blocks.Where(b => b.Role == TruthRole.Footnote)];
        double lowestText = doc.Blocks.Where(b => b.Order >= 0).Max(b => b.Box.Bottom);

        Assert.Equal(["1", "2"], notes.Select(n => n.Marker).Order());
        Assert.All(notes, n => Assert.True(n.Box.Top > lowestText));
        Assert.Equal(["1", "2"], doc.Blocks.Where(b => b.Role == TruthRole.Paragraph && b.Marker is not null).Select(b => b.Marker!));
    }

    [Fact]
    public void HeadersAndPageNumbersRepeatOnEveryPage()
    {
        SyntheticDocument doc = SyntheticScenes.HeadersAndFooters();

        Assert.True(doc.PageCount >= 3);
        Assert.Equal(doc.PageCount, doc.Blocks.Count(b => b.Role == TruthRole.PageHeader && b.Text == "Annual Report 2025"));
        Assert.Equal(Enumerable.Range(1, doc.PageCount).Select(p => $"Page {p}"),
            doc.Blocks.Where(b => b.Role == TruthRole.PageNumber).Select(b => b.Text));
        Assert.All(doc.Blocks.Where(b => b.Order < 0), b => Assert.True(b.Role is TruthRole.PageHeader or TruthRole.PageNumber));
    }

    [Fact]
    public void HyphenatedLinesJoinWithoutTheHyphen()
    {
        SyntheticDocument doc = SyntheticScenes.Hyphenation();
        List<TruthLine> split = [.. doc.Blocks.SelectMany(b => b.Lines).Where(l => l.Text.EndsWith('-'))];

        Assert.NotEmpty(split);
        foreach (TruthBlock block in doc.Blocks.Where(b => b.Lines.Any(l => l.Text.EndsWith('-'))))
        {
            string head = block.Lines.First(l => l.Text.EndsWith('-')).Text.Split(' ')[^1].TrimEnd('-');
            Assert.DoesNotContain(head + "-", block.Text, StringComparison.Ordinal);
            Assert.Contains(head, block.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CodeKeepsItsLinesAndFontsAreRecorded()
    {
        SyntheticDocument doc = SyntheticScenes.QuoteCodeCaption();
        TruthBlock code = doc.Blocks.Single(b => b.Role == TruthRole.Code);

        Assert.Equal(4, code.Text.Split('\n').Length);
        Assert.Equal("Liberation Mono", code.FontName);
        Assert.True(doc.Blocks.Single(b => b.Role == TruthRole.Quote).Italic);
        Assert.True(doc.Blocks.Single(b => b.Role == TruthRole.Heading).Bold);
    }

    /// <summary>
    /// Writes every scene's PDF and truth (as JSON) into the folder EGAP_SYNTHETIC_OUT names, for the comparison repository's
    /// reference tools; skipped otherwise.
    /// </summary>
    [Fact]
    public void WritesScenesWhenAsked()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAP_SYNTHETIC_OUT");
        Assert.SkipWhen(string.IsNullOrEmpty(folder), "Set EGAP_SYNTHETIC_OUT to a folder to write the synthetic pages there.");
        Directory.CreateDirectory(folder!);
        foreach ((string name, Func<SyntheticDocument> make) in SyntheticScenes.All)
        {
            SyntheticDocument doc = make();
            File.WriteAllBytes(Path.Combine(folder!, name + ".pdf"), doc.Save());
            File.WriteAllText(Path.Combine(folder!, name + ".truth.json"), JsonSerializer.Serialize(new
            {
                doc.Width,
                doc.Height,
                Blocks = doc.Blocks.Select(b => new { b.Page, Role = b.Role.ToString(), b.Text, b.Order, b.Level, b.Continues, b.Marker, b.Box }),
                Tables = doc.Tables.Select(t => new { t.Page, t.Box, t.Rows, t.Columns, Rules = t.Rules.ToString(), t.Order, t.Continues, t.RepeatsHeader, t.Cells }),
            }));
        }
    }

    private static string Squash(string text)
    {
        return new([.. text.Where(c => !char.IsWhiteSpace(c))]);
    }

    private static bool Overlaps(TruthBox a, TruthBox b)
    {
        return Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left) && Math.Min(a.Bottom, b.Bottom) > Math.Max(a.Top, b.Top);
    }
}
