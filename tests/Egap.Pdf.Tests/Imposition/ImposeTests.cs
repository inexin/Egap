namespace Egap.Pdf.Tests.Imposition;

/// <summary>Imposition layouts: Phase 7 tasks 10 and 11.</summary>
public sealed class ImposeTests
{
    [Fact]
    public void NUpPlacesPagesInReadingOrderOnEnoughSheets()
    {
        using PdfDocument source = Numbered(10, 595, 842);

        using PdfDocument nup = PdfImpose.NUp(source, new NUpOptions { Columns = 2, Rows = 2, AutoRotate = false });

        Assert.Equal(3, nup.Pages.Count);
        Assert.Equal(PdfPageSize.A4.ToRectangle(), nup.Pages[0].MediaBox);
        Dictionary<string, PdfRectangle> boxes = Words(nup.Pages[0]);
        Assert.Equal(["P1", "P2", "P3", "P4"], boxes.Keys.Order(StringComparer.Ordinal));
        Assert.True(boxes["P1"].Left < boxes["P2"].Left && boxes["P1"].Bottom > boxes["P3"].Bottom, "P1 top left, P2 to its right, P3 below");
        Assert.Equal(["P10", "P9"], Words(nup.Pages[2]).Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(NUpOrder.RightToLeftThenDown, "P1", "P2")]
    [InlineData(NUpOrder.DownThenLeftToRight, "P3", "P1")]
    public void OtherOrdersFillOtherCells(NUpOrder order, string topRight, string topLeft)
    {
        using PdfDocument source = Numbered(4, 595, 842);
        using PdfDocument nup = PdfImpose.NUp(source, new NUpOptions { Order = order, AutoRotate = false });
        Dictionary<string, PdfRectangle> boxes = Words(nup.Pages[0]);
        string[] top = [.. boxes.OrderByDescending(b => b.Value.Bottom).Take(2).OrderBy(b => b.Value.Left).Select(b => b.Key)];
        Assert.Equal([topLeft, topRight], top);
    }

    [Fact]
    public void TurnsTheSheetWhenPagesFitLarger()
    {
        // Two landscape pages side by side fit larger on a landscape sheet.
        using PdfDocument source = Numbered(2, 842, 595);
        using PdfDocument nup = PdfImpose.NUp(source, new NUpOptions { Columns = 2, Rows = 1 });
        Assert.True(nup.Pages[0].MediaBox.Width > nup.Pages[0].MediaBox.Height);
    }

    [Fact]
    public void GridPutsEveryPageOnOneSheetAtItsSize()
    {
        using PdfDocument source = Numbered(5, 200, 100);

        using PdfDocument grid = PdfImpose.Grid(source, new GridOptions { Margin = 0, Gutter = 0, Border = true });

        PdfPage sheet = Assert.Single(grid.Pages);
        Assert.Equal(new PdfRectangle(0, 0, 600, 200), sheet.MediaBox);
        Assert.Equal(5, Words(sheet).Count);
    }

    [Fact]
    public void BookletOrdersSidesForSaddleStitching()
    {
        Assert.Equal([(7, 0), (1, 6), (5, 2), (3, 4)], PdfImpose.BookletSides(8, BookletBinding.Left));
        Assert.Equal([(0, 7), (6, 1), (2, 5), (4, 3)], PdfImpose.BookletSides(8, BookletBinding.Right));

        using PdfDocument source = Numbered(6, 420, 595);
        using PdfDocument booklet = PdfImpose.Booklet(source, new BookletOptions { Sheet = PdfPageSize.A4 });

        // Six pages pad to eight: two sheets, four sides, landscape A4 with two pages each.
        Assert.Equal(4, booklet.Pages.Count);
        Assert.True(booklet.Pages[0].MediaBox.Width > booklet.Pages[0].MediaBox.Height);
        Dictionary<string, PdfRectangle> first = Words(booklet.Pages[0]);
        Assert.Equal(["P1"], first.Keys);
        Assert.True(first["P1"].Left > booklet.Pages[0].MediaBox.Width / 2, "page 1 on the right of the cover side; page 8 is blank");
        Assert.Equal(["P2"], Words(booklet.Pages[1]).Keys);
        Assert.Equal(["P3", "P6"], Words(booklet.Pages[2]).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SignaturesFoldSheetsInGroups()
    {
        using PdfDocument source = Numbered(16, 420, 595);
        using PdfDocument booklet = PdfImpose.Booklet(source, new BookletOptions { SheetsPerSignature = 2 });
        Assert.Equal(8, booklet.Pages.Count);
        Assert.Equal(["P1", "P8"], Words(booklet.Pages[0]).Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["P16", "P9"], Words(booklet.Pages[4]).Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PosterCutsAPageIntoOverlappingTiles()
    {
        using PdfDocument source = Numbered(1, 842, 595);

        using PdfDocument poster = PdfImpose.Poster(source, new PosterOptions { Tile = PdfPageSize.A4, Scale = 2, Overlap = 20, CutMarks = true, Labels = true });

        // 1684 by 1190 points on 595 by 842 tiles that advance 575 by 822: 3 across, 2 down.
        Assert.Equal(6, poster.Pages.Count);
        Assert.Contains("1 A1", poster.Pages[0].ExtractText(), StringComparison.Ordinal);
        Assert.Contains("1 B3", poster.Pages[5].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void FitToSizeKeepsProportionsCentred()
    {
        using PdfDocument source = Numbered(2, 400, 400);
        using PdfDocument fitted = PdfImpose.FitToSize(source, PdfPageSize.Letter);
        Assert.All(fitted.Pages, p => Assert.Equal(PdfPageSize.Letter.ToRectangle(), p.MediaBox));
        PdfRectangle word = Words(fitted.Pages[0])["P1"];
        Assert.InRange(word.Left, 0, 612);
    }

    [Fact]
    public void RefusesLayoutsWithNoRoom()
    {
        using PdfDocument source = Numbered(1, 100, 100);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImpose.NUp(source, new NUpOptions { Columns = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImpose.NUp(source, new NUpOptions { Margin = 400 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImpose.Poster(source, new PosterOptions { Overlap = 1000 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImpose.FitToSize(source, new PdfPageSize(0, 10)));
    }

    [Fact]
    public void TheSourceIsUnchangedAndTheResultSaves()
    {
        using PdfDocument source = Numbered(3, 300, 300);
        using (PdfDocument nup = PdfImpose.NUp(source))
        {
            using MemoryStream saved = new();
            nup.Save(saved);
            using PdfDocument reopened = PdfDocument.Open(saved.ToArray());
            Assert.Equal(3, Words(reopened.Pages[0]).Count);
        }

        Assert.False(source.Objects.IsChanged);
    }

    /// <summary>Pages with "P1", "P2" ... in Helvetica in their middle, saved and reopened.</summary>
    private static PdfDocument Numbered(int count, double width, double height)
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfReference font = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Helvetica") });
        for (int i = 1; i <= count; i++)
        {
            PdfPage page = pdf.Pages.AddBlank(new PdfRectangle(0, 0, width, height));
            page.Resources = new PdfDictionary { ["Font"] = new PdfDictionary { ["F1"] = font } };
            page.SetContent(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"BT /F1 24 Tf {width / 2 - 20} {height / 2} Td (P{i}) Tj ET"))));
        }

        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }

    private static Dictionary<string, PdfRectangle> Words(PdfPage page)
    {
        return page.GetText().Lines.SelectMany(l => l.Words).Where(w => w.Text.StartsWith('P')).ToDictionary(w => w.Text, w => w.Box);
    }
}
