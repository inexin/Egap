using Egap.Documents.Pdf;
using Egap.Documents.Tests.Pdf.Synthetic;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf.Tables;

/// <summary>Borderless tables, partly ruled tables, explicit areas and tables across pages (Build Plan task 8).</summary>
public sealed class BorderlessTableTests
{
    public static TheoryData<string> Untabled => [.. SyntheticScenes.All.Keys.Where(k => k is not ("tables" or "table-across-pages"))];

    [Fact]
    public void PartlyRuledAndBorderlessTablesComeWithTheirCells()
    {
        SyntheticDocument doc = SyntheticScenes.Tables();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        PageContent content = PageReader.Read(pdf, 0);

        List<TableDraft> tables = Detect(content);

        Assert.Equal(3, tables.Count);
        foreach ((TruthTable truth, TableDraft table) in doc.Tables.Zip(tables.OrderBy(t => t.Box.Top)))
        {
            Assert.Equal((truth.Rows, truth.Columns), (table.Rows, table.Columns));
            Assert.Equal(truth.Cells.Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text, c.Header)),
                table.Cells.Select(c => (c.Row, c.Column, c.RowSpan, c.ColumnSpan, c.Text, c.Header)));
        }
    }

    [Theory]
    [MemberData(nameof(Untabled))]
    public void TextIsNeverTakenForATable(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        for (int page = 0; page < doc.PageCount; page++)
        {
            Assert.Empty(Detect(PageReader.Read(pdf, page)));
        }
    }

    [Fact]
    public void AnExplicitAreaIsATable()
    {
        SyntheticDocument doc = SyntheticScenes.Tables();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        PageContent content = PageReader.Read(pdf, 0);
        TruthTable truth = doc.Tables[2];
        PageBox area = new(truth.Box.Left - 2, truth.Box.Top - 2, truth.Box.Right + 2, truth.Box.Bottom + 2);

        TableDraft table = Assert.Single(BorderlessTables.Find(content.Glyphs, [], [area], LayoutThresholds.Default), t => t.Box == area);

        Assert.Equal(truth.Cells.Select(c => c.Text), table.Cells.Select(c => c.Text));
    }

    [Fact]
    public void ATableCarriesOnAcrossAPageWithItsHeaderRepeated()
    {
        SyntheticDocument doc = SyntheticScenes.TableAcrossPages();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());
        List<PageDraft> pages = [];
        for (int page = 0; page < doc.PageCount; page++)
        {
            PageDraft draft = new(PageReader.Read(pdf, page));
            draft.Tables.AddRange(Detect(draft.Content));
            pages.Add(draft);
        }

        TableJoins.AcrossPages(pages);

        Assert.False(pages[0].Tables[0].Continues);
        Assert.All(pages.Skip(1), p => Assert.True(p.Tables[0].Continues && p.Tables[0].RepeatsHeader));
    }

    /// <summary>Ruled tables first, then partly ruled and borderless ones among the glyphs left.</summary>
    internal static List<TableDraft> Detect(PageContent content)
    {
        (List<TableDraft> ruled, List<TableHint> hints) = RuledTables.Find(content.Paths, content.Glyphs, content.Images);
        HashSet<PageGlyph> used = [.. ruled.SelectMany(t => t.Cells).SelectMany(c => c.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs)];
        List<PageGlyph> rest = [.. content.Glyphs.Where(g => !used.Contains(g) && !ruled.Any(t => t.Box.Contains(g.Box.CenterX, g.Box.CenterY)))];
        return [.. ruled, .. BorderlessTables.Find(rest, hints, [], LayoutThresholds.Default)];
    }
}
