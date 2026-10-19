using System.Numerics;
using System.Text;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Path = System.IO.Path;

namespace Egap.Pdf.Tests.Attachments;

/// <summary>Factur-X and ZUGFeRD e-invoices, and attachment relationships.</summary>
public sealed class InvoiceTests
{
    private static readonly string Fonts = Path.Combine(AppContext.BaseDirectory, "files", "fonts");

    [Theory]
    [InlineData("urn:cen.eu:en16931:2017", PdfInvoiceProfile.EN16931, "factur-x.xml", "Alternative", "EN 16931")]
    [InlineData("urn:factur-x.eu:1p0:minimum", PdfInvoiceProfile.Minimum, "factur-x.xml", "Data", "MINIMUM")]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic", PdfInvoiceProfile.Basic, "factur-x.xml", "Alternative", "BASIC")]
    [InlineData("urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0", PdfInvoiceProfile.XRechnung, "xrechnung.xml", "Alternative", "XRECHNUNG")]
    public void EmbedsAnInvoiceAsPdfA3WithTheFacturXSchema(string guideline, PdfInvoiceProfile profile, string fileName, string relationship, string level)
    {
        using PdfDocument pdf = Page();
        byte[] xml = Invoice(guideline);

        PdfAReport report = PdfInvoice.Embed(pdf, xml);

        Assert.True(report.Succeeded, report.ToString());
        using PdfDocument saved = Reopen(pdf);
        Assert.Equal(PdfAConformance.PdfA3b, saved.Conformance);
        Assert.True(PdfAConverter.Check(saved, PdfAConformance.PdfA3b).Succeeded, PdfAConverter.Check(saved, PdfAConformance.PdfA3b).ToString());
        PdfAttachment attachment = Assert.Single(saved.Attachments);
        Assert.Equal(fileName, attachment.Name);
        Assert.Equal(relationship, attachment.Relationship);
        Assert.Equal(xml, attachment.GetBytes());
        Assert.Contains(saved.Catalog.Get<PdfArray>("AF")!, a => a is PdfReference r && r.ObjectNumber == attachment.Reference!.ObjectNumber);

        XDocument xmp = saved.Metadata.GetXmp()!;
        XNamespace fx = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";
        Assert.Equal(level, xmp.Descendants(fx + "ConformanceLevel").Single().Value);
        Assert.Equal(fileName, xmp.Descendants(fx + "DocumentFileName").Single().Value);
        Assert.Equal("fx", xmp.Descendants(fx + "DocumentType").Single().GetPrefixOfNamespace(fx));
        Assert.Contains(xmp.Descendants(), e => e.Name.LocalName == "namespaceURI" && e.Value == fx.NamespaceName);

        PdfInvoice back = PdfInvoice.Read(saved)!;
        Assert.Equal(profile, back.Profile);
        Assert.Equal(PdfInvoiceStandard.FacturX, back.Standard);
        Assert.Equal(xml, back.Xml);
    }

    [Fact]
    public void EmbeddingAgainReplacesTheInvoice()
    {
        using PdfDocument pdf = Page();
        PdfInvoice.Embed(pdf, Invoice("urn:factur-x.eu:1p0:minimum"));
        PdfInvoice.Embed(pdf, Invoice("urn:cen.eu:en16931:2017"), new PdfInvoiceOptions { Profile = PdfInvoiceProfile.Extended });

        using PdfDocument saved = Reopen(pdf);
        Assert.Single(saved.Attachments);
        Assert.Equal(PdfInvoiceProfile.Extended, PdfInvoice.Read(saved)!.Profile);
    }

    [Fact]
    public void ReadsLegacyZugferdByItsFileName()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank();
        pdf.Attachments.Add("ZUGFeRD-invoice.xml", Invoice("urn:ferd:CrossIndustryDocument:invoice:1p0:comfort"), "text/xml");

        PdfInvoice invoice = PdfInvoice.Read(Reopen(pdf))!;

        Assert.Equal(PdfInvoiceStandard.Zugferd10, invoice.Standard);
        Assert.Equal(PdfInvoiceProfile.EN16931, invoice.Profile);
        Assert.Null(PdfInvoice.Read(Page()));
    }

    [Fact]
    public void RefusesWhatIsNotAnInvoice()
    {
        using PdfDocument pdf = Page();

        Assert.Throws<ArgumentException>(() => PdfInvoice.Embed(pdf, "<not xml"u8));
        Assert.Throws<ArgumentException>(() => PdfInvoice.Embed(pdf, "<Invoice/>"u8));
        Assert.Throws<ArgumentException>(() => PdfInvoice.Embed(pdf, Invoice("urn:cen.eu:en16931:2017"), new PdfInvoiceOptions { Conformance = PdfAConformance.PdfA2b }));
        Assert.Empty(pdf.Attachments);
    }

    [Fact]
    public void AttachmentsTakeARelationship()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank();

        PdfAttachment data = pdf.Attachments.Add("data.csv", "a,b\n1,2"u8, PdfAttachmentRelationship.Supplement, "text/csv");

        using PdfDocument saved = Reopen(pdf);
        Assert.Equal("Supplement", saved.Attachments[0].Relationship);
        Assert.Single(saved.Catalog.Get<PdfArray>("AF")!);
        Assert.NotNull(data.Reference);
    }

    private static byte[] Invoice(string guideline)
    {
        return Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" encoding="UTF-8"?>
            <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100" xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100">
              <rsm:ExchangedDocumentContext>
                <ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>{guideline}</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter>
              </rsm:ExchangedDocumentContext>
              <rsm:ExchangedDocument><ram:ID>2026-118</ram:ID><ram:TypeCode>380</ram:TypeCode></rsm:ExchangedDocument>
            </rsm:CrossIndustryInvoice>
            """);
    }

    private static PdfDocument Page()
    {
        Typeface noto = Typeface.FromFile(Path.Combine(Fonts, "NotoSans-Latin.ttf"));
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();
        using (PdfCanvas canvas = new(page))
        {
            canvas.DrawText("Invoice 2026-118", new Vector2(72, 100), new Font(noto, 14), new SolidPaint(new RgbaF32(0, 0, 0, 1)));
        }

        pdf.Metadata.Title = "Invoice 2026-118";
        return Reopen(pdf);
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }
}
