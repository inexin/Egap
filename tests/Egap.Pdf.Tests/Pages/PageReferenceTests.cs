using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Pages;

/// <summary>Outline items, links and named destinations to a removed page (Q7): Phase 2 task 20.</summary>
public sealed class PageReferenceTests
{
    /// <summary>
    /// Pages 3 and 4; page 3 has a link to page 4 (by a GoTo action) and a link to the name "four"; the outline has an item
    /// to page 4, one to "four" and one to page 3; "four" and "three" are named destinations.
    /// </summary>
    private static byte[] File()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /Outlines 10 0 R /Names << /Dests << /Names [(four) [4 0 R /Fit] (three) [3 0 R /Fit]] >> >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [20 0 R 21 0 R 22 0 R] >>")
            .Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>")
            .Object(10, "<< /Type /Outlines /First 11 0 R /Last 13 0 R /Count 3 >>")
            .Object(11, "<< /Title (Four) /Parent 10 0 R /Next 12 0 R /Dest [4 0 R /Fit] >>")
            .Object(12, "<< /Title (Named four) /Parent 10 0 R /Prev 11 0 R /Next 13 0 R /Dest (four) >>")
            .Object(13, "<< /Title (Three) /Parent 10 0 R /Prev 12 0 R /Dest [3 0 R /Fit] >>")
            .Object(20, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /A << /S /GoTo /D [4 0 R /Fit] >> >>")
            .Object(21, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /Dest (four) >>")
            .Object(22, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /A << /S /URI /URI (https://example.com) >> >>");
        return file.StartXref(file.XrefTable("/Size 23 /Root 1 0 R")).ToArray();
    }

    [Fact]
    public void RemovingAPageCleansUpWhatPointsAtIt()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        pdf.Pages.RemoveAt(1);

        Assert.Equal(["Four", "Named four", "Three"], pdf.Outline.Children.Select(i => i.Title));
        Assert.Equal([false, false, true], pdf.Outline.Children.Select(i => i.Destination is not null));
        Assert.Equal(["URI"], pdf.Pages[0].Annotations.Select(a => ((PdfLinkAnnotation)a).Action!.ActionType));
        Assert.Equal(["three"], pdf.NamedDestinations.GetNames());
        PdfWarning warning = Assert.Single(pdf.Warnings, w => w.Code == PdfWarningCode.PageReferencesRemoved);
        Assert.Contains("2 outline items", warning.Message, StringComparison.Ordinal);
        Assert.Contains("2 links", warning.Message, StringComparison.Ordinal);
        Assert.Contains("1 named destinations", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReferencesCanBeKept()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        Assert.True(pdf.Pages.Remove(pdf.Pages[1], removeReferences: false));

        Assert.Equal(3, pdf.Pages[0].Annotations.Count);
        Assert.Equal(["four", "three"], pdf.NamedDestinations.GetNames());
        Assert.DoesNotContain(pdf.Warnings, w => w.Code == PdfWarningCode.PageReferencesRemoved);
    }

    [Fact]
    public void APageNothingPointsAtLeavesNoWarning()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank();
        pdf.Pages.AddBlank();

        pdf.Pages.RemoveAt(0);

        Assert.Empty(pdf.Warnings);
    }
}
