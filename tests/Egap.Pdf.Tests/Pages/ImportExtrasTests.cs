using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Pages;

/// <summary>What imported pages bring (Q5, Q6): fields, named destinations, layers, labels, attachments, outlines; Phase 2 task 19.</summary>
public sealed class ImportExtrasTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>
    /// Two pages labelled iv and v; page 1 has a text field "name" (widget 20, value from the label) and a link to the named
    /// destination "second"; page 2 uses layer 40 in its resources; an attachment "notes.txt"; an outline with an item to each
    /// page and a parent "Group" holding an item to page 2.
    /// </summary>
    private static byte[] Source(string value)
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R] /DR << /Font << /Helv 30 0 R >> >> /DA (/Helv 0 Tf 0 g) >>"
                + " /Names << /Dests << /Names [(second) [4 0 R /Fit]] >> /EmbeddedFiles << /Names [(notes.txt) 50 0 R] >> >>"
                + " /PageLabels << /Nums [0 << /S /r /St 4 >>] >> /OCProperties << /OCGs [40 0 R 41 0 R] /D << /OFF [40 0 R] >> >> /Outlines 60 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [20 0 R 21 0 R] >>")
            .Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Properties << /L 40 0 R >> >> >>")
            .Object(10, $"<< /T (name) /FT /Tx /V ({value}) /Kids [20 0 R] >>")
            .Object(20, "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /Rect [0 0 50 20] /P 3 0 R >>")
            .Object(21, "<< /Type /Annot /Subtype /Link /Rect [0 30 50 40] /Dest (second) >>")
            .Object(30, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")
            .Object(40, "<< /Type /OCG /Name (Used) >>")
            .Object(41, "<< /Type /OCG /Name (Unused) >>")
            .Object(50, "<< /Type /Filespec /F (notes.txt) /EF << /F 51 0 R >> >>")
            .Stream(51, "/Type /EmbeddedFile", "hello"u8.ToArray())
            .Object(60, "<< /Type /Outlines /First 61 0 R /Last 63 0 R /Count 3 >>")
            .Object(61, "<< /Title (One) /Parent 60 0 R /Next 62 0 R /Dest [3 0 R /Fit] >>")
            .Object(62, "<< /Title (Two) /Parent 60 0 R /Prev 61 0 R /Next 63 0 R /Dest [4 0 R /Fit] >>")
            .Object(63, "<< /Title (Group) /Parent 60 0 R /Prev 62 0 R /First 64 0 R /Last 64 0 R /Count 1 >>")
            .Object(64, "<< /Title (Inside) /Parent 63 0 R /Dest [4 0 R /Fit] >>");
        return file.StartXref(file.XrefTable("/Size 65 /Root 1 0 R")).ToArray();
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
        return output.ToArray();
    }

    [Fact]
    public void MergingFilledFormsRenamesTheSecondField()
    {
        using PdfDocument a = PdfDocument.Open(Source("Ada"));
        using PdfDocument b = PdfDocument.Open(Source("Grace"));

        using PdfDocument merged = PdfDocument.Merge(a, b);

        Assert.Equal(["name", "name_2"], merged.Form.Select(f => f.FullName));
        Assert.Equal(["Ada", "Grace"], merged.Form.Select(f => ((PdfTextField)f).Value));
        Assert.Contains(merged.Warnings, w => w.Code == PdfWarningCode.FieldRenamed);
        Assert.Equal(merged.Pages[2], merged.Form[1].Widgets[0].Page);
        Assert.Equal("/Helv 0 Tf 0 g", merged.Form.DefaultAppearance);
        Assert.NotNull(merged.Form.DefaultResources!.Get<PdfDictionary>("Font")!.Get("Helv"));

        using PdfDocument reopened = PdfDocument.Open(Save(merged), Strict);
        Assert.Equal(["name", "name_2"], reopened.Form.Select(f => f.FullName));
    }

    [Fact]
    public void JoiningSharesOneField()
    {
        using PdfDocument a = PdfDocument.Open(Source("Ada"));
        using PdfDocument b = PdfDocument.Open(Source("Grace"));
        using PdfDocument target = PdfDocument.Create();

        target.Pages.Import(a);
        target.Pages.Import(b, options: new PdfImportOptions { JoinFields = true });

        PdfTextField name = Assert.IsType<PdfTextField>(Assert.Single(target.Form));
        Assert.Equal(2, name.Widgets.Count);
        Assert.Equal("Ada", name.Value);
        Assert.Equal(name, ((PdfWidgetAnnotation)target.Pages[2].Annotations[0]).Field);
    }

    [Fact]
    public void LinksKeepTheirNamedDestinations()
    {
        using PdfDocument a = PdfDocument.Open(Source("Ada"));
        using PdfDocument b = PdfDocument.Open(Source("Grace"));

        using PdfDocument merged = PdfDocument.Merge(a, b);

        Assert.Equal(["second", "second_2"], merged.NamedDestinations.GetNames());
        Assert.Equal(merged.Pages[1].Reference, ((PdfLinkAnnotation)merged.Pages[0].Annotations[1]).Destination!.PageReference);
        Assert.Equal(merged.Pages[3].Reference, ((PdfLinkAnnotation)merged.Pages[2].Annotations[1]).Destination!.PageReference);
    }

    [Fact]
    public void BringsUsedLayersWithTheirVisibility()
    {
        using PdfDocument source = PdfDocument.Open(Source("Ada"));
        using PdfDocument target = PdfDocument.Create();

        target.Pages.Import(source, [1]);

        PdfLayer layer = Assert.Single(target.Layers);
        Assert.Equal(("Used", false), (layer.Name, layer.IsVisible));
    }

    [Fact]
    public void LabelsFollowTheirPages()
    {
        using PdfDocument source = PdfDocument.Open(Source("Ada"));
        using PdfDocument target = PdfDocument.Create();
        target.Pages.AddBlank();
        target.Pages.AddBlank();

        target.Pages.Import(source, at: 1);

        Assert.Equal(["1", "iv", "v", "2"], Enumerable.Range(0, 4).Select(target.Pages.GetLabel));
        using PdfDocument reopened = PdfDocument.Open(Save(target), Strict);
        Assert.Equal(["1", "iv", "v", "2"], Enumerable.Range(0, 4).Select(reopened.Pages.GetLabel));
    }

    [Fact]
    public void BringsAttachmentsAndTheOutlineOnce()
    {
        using PdfDocument a = PdfDocument.Open(Source("Ada"));
        using PdfDocument b = PdfDocument.Open(Source("Grace"));
        using PdfDocument target = PdfDocument.Create();

        target.Pages.Import(a, [0]);
        target.Pages.Import(a, [1]);
        target.Pages.Import(b, [0]);

        Assert.Equal(["notes.txt", "notes_2.txt"], target.Attachments.Select(x => x.Name));
        Assert.Equal("hello"u8.ToArray(), target.Attachments[1].GetBytes());

        // From a, the first import had only page 1: "One" came along, "Two" and the group did not. From b, only "One".
        Assert.Equal(["One", "One"], target.Outline.Children.Select(i => i.Title));
        Assert.Equal(target.Pages[0].Reference, target.Outline.Children[0].Destination!.PageReference);
        Assert.Equal(target.Pages[2].Reference, target.Outline.Children[1].Destination!.PageReference);
    }

    [Fact]
    public void AWholeImportBringsTheWholeOutline()
    {
        using PdfDocument source = PdfDocument.Open(Source("Ada"));
        using PdfDocument target = PdfDocument.Create();

        target.Pages.Import(source);

        Assert.Equal(["One", "Two", "Group"], target.Outline.Children.Select(i => i.Title));
        Assert.Equal(["Inside"], target.Outline.Children[2].Children.Select(i => i.Title));
        using PdfDocument reopened = PdfDocument.Open(Save(target), Strict);
        Assert.Equal(4, reopened.Outline.GetAllItems().Count);
    }

    [Fact]
    public void DuplicatingPagesWithinADocumentBringsNothingMore()
    {
        using PdfDocument pdf = PdfDocument.Open(Source("Ada"));

        pdf.Pages.Import(pdf, [0]);

        Assert.Single(pdf.Form);
        Assert.Single(pdf.Attachments);
    }
}
