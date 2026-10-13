using System.Text;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Forms;

/// <summary>XFA packets kept, datasets read and filled with the field values, static XFA converted (hub Q7): Phase 2 task 17.</summary>
public sealed class XfaTests
{
    private const string Datasets = "<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form1><page1><name>Ada</name><agree>0</agree></page1></form1></xfa:data></xfa:datasets>";

    private const string Template = "<template xmlns=\"http://www.xfa.org/schema/xfa-template/3.3/\"><subform name=\"form1\"/></template>";

    /// <summary>XFA as named packets, fields form1[0].page1[0].name[0] (text) and agree[0] (check box), and an AcroForm value out of step with the data.</summary>
    private static byte[] File(bool whole = false, string? needsRendering = null)
    {
        string xdp = $"<xdp:xdp xmlns:xdp=\"http://ns.adobe.com/xdp/\">{Template}{Datasets}</xdp:xdp>";
        string xfa = whole ? "/XFA 30 0 R" : "/XFA [(template) 30 0 R (datasets) 31 0 R]";
        FileBuilder file = new FileBuilder()
            .Object(1, $"<< /Type /Catalog /Pages 2 0 R {needsRendering} /AcroForm << /Fields [10 0 R] {xfa} >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [12 0 R 13 0 R] >>")
            .Object(10, "<< /T (form1[0]) /Kids [11 0 R] >>")
            .Object(11, "<< /T (page1[0]) /Parent 10 0 R /Kids [12 0 R 13 0 R] >>")
            .Object(12, "<< /T (name[0]) /Parent 11 0 R /FT /Tx /V (Old) /Subtype /Widget /Rect [0 0 100 20] >>")
            .Object(13, "<< /T (agree[0]) /Parent 11 0 R /FT /Btn /V /Off /Subtype /Widget /Rect [0 30 10 40] /AP << /N << /1 32 0 R /Off 32 0 R >> >> >>")
            .Stream(30, "", Encoding.UTF8.GetBytes(whole ? xdp : Template))
            .Stream(31, "", Encoding.UTF8.GetBytes(Datasets))
            .Stream(32, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "q Q"u8.ToArray());
        return file.StartXref(file.XrefTable("/Size 33 /Root 1 0 R")).ToArray();
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsPacketsAndData(bool whole)
    {
        using PdfDocument pdf = PdfDocument.Open(File(whole));
        PdfXfa xfa = pdf.Form.Xfa!;

        Assert.Equal(["template", "datasets"], xfa.PacketNames);
        Assert.Contains("<form1><page1><name>Ada</name>", xfa.GetDatasets(), StringComparison.Ordinal);
        Assert.StartsWith("<template", xfa.GetPacket("template"), StringComparison.Ordinal);
        Assert.Null(xfa.GetPacket("config"));
        Assert.Equal("Ada", xfa.GetDataValue("form1[0].page1[0].name[0]"));
        Assert.Null(xfa.GetDataValue("form1[0].page1[0].missing[0]"));
        Assert.False(xfa.IsDynamic);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FillingAFieldFillsTheData(bool whole)
    {
        using PdfDocument pdf = PdfDocument.Open(File(whole));
        Assert.True(pdf.Form.TryGetField("form1[0].page1[0].name[0]", out PdfField name));
        Assert.True(pdf.Form.TryGetField("form1[0].page1[0].agree[0]", out PdfField agree));

        ((PdfTextField)name).Value = "Grace";
        ((PdfCheckBoxField)agree).IsChecked = true;
        Assert.True(pdf.Form.Xfa!.SetDataValue("form1[0].page1[0].extra[0]", "made"));

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), new PdfReadOptions { Strict = true });
        PdfXfa xfa = reopened.Form.Xfa!;
        Assert.Equal("Grace", xfa.GetDataValue("form1[0].page1[0].name[0]"));
        Assert.Equal("1", xfa.GetDataValue("form1[0].page1[0].agree[0]"));
        Assert.Equal("made", xfa.GetDataValue("form1[0].page1[0].extra[0]"));
        Assert.Equal(["template", "datasets"], xfa.PacketNames);
    }

    [Fact]
    public void ConvertingAStaticFormTakesTheDataAndDropsTheXfa()
    {
        using PdfDocument pdf = PdfDocument.Open(File(needsRendering: "/NeedsRendering false"));

        Assert.True(pdf.Form.Xfa!.ConvertToAcroForm());

        Assert.False(pdf.Form.HasXfa);
        Assert.Null(pdf.Form.Xfa);
        Assert.False(pdf.Catalog.ContainsKey("NeedsRendering"));
        Assert.True(pdf.Form.TryGetField("form1[0].page1[0].name[0]", out PdfField name));
        Assert.Equal("Ada", ((PdfTextField)name).Value);
        Assert.True(pdf.Form.TryGetField("form1[0].page1[0].agree[0]", out PdfField agree));
        Assert.False(((PdfCheckBoxField)agree).IsChecked);
    }

    [Fact]
    public void ADynamicFormIsReportedAndKept()
    {
        using PdfDocument pdf = PdfDocument.Open(File(needsRendering: "/NeedsRendering true"));

        Assert.True(pdf.Form.Xfa!.IsDynamic);
        Assert.False(pdf.Form.Xfa!.ConvertToAcroForm());
        Assert.True(pdf.Form.HasXfa);
    }

    [Fact]
    public void ADocumentWithoutXfaHasNone()
    {
        using PdfDocument pdf = PdfDocument.Create();

        Assert.Null(pdf.Form.Xfa);
    }

    [Fact]
    public void ADtdIsRefused()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA [(datasets) 31 0 R] >> >>")
            .Object(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Stream(31, "", "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><a>&e;</a></xfa:data></xfa:datasets>"u8.ToArray());
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 32 /Root 1 0 R")).ToArray());

        Assert.Null(pdf.Form.Xfa!.GetDataValue("a[0]"));
        Assert.False(pdf.Form.Xfa!.SetDataValue("a[0]", "x"));
    }

    [Fact]
    public void ASubformWithoutDataIsSkipped()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        // page1 has data here, but a part with no element of its own is passed over, as a page subform that binds none is.
        Assert.Equal("Ada", pdf.Form.Xfa!.GetDataValue("form1[0].Page9[0].page1[0].name[0]"));
        Assert.Null(pdf.Form.Xfa!.GetDataValue("form1[0].page1[0].Missing[0]"));
    }
}
