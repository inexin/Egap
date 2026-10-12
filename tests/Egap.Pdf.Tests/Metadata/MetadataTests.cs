using System.Text;
using System.Xml.Linq;

using Egap.Pdf.Metadata;
using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Metadata;

/// <summary>Document information, XMP and the modification date on save (14.3, Phase 2 Q8 and Q9): Phase 2 task 8.</summary>
public sealed class MetadataTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    private const string Packet = """
        <?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
        <x:xmpmeta xmlns:x="adobe:ns:meta/">
         <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
          <rdf:Description rdf:about="" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:pdf="http://ns.adobe.com/pdf/1.3/" pdf:Producer="XMP producer">
           <dc:title><rdf:Alt><rdf:li xml:lang="de">Titel</rdf:li><rdf:li xml:lang="x-default">XMP title</rdf:li></rdf:Alt></dc:title>
           <dc:creator><rdf:Seq><rdf:li>Ada</rdf:li><rdf:li>Grace</rdf:li></rdf:Seq></dc:creator>
          </rdf:Description>
          <rdf:Description rdf:about="" xmlns:xmp="http://ns.adobe.com/xap/1.0/" xmlns:acme="http://example.com/acme/">
           <xmp:CreateDate>2020-05-06T07:08:09+02:00</xmp:CreateDate>
           <acme:Secret>kept</acme:Secret>
          </rdf:Description>
         </rdf:RDF>
        </x:xmpmeta>

        <?xpacket end="w"?>
        """;

    /// <summary>A one-page file with an Info dictionary (object 4) and, when asked, the XMP packet above (object 5).</summary>
    private static byte[] File(bool xmp, string info = "/Title (Info title) /Author (Info author) /Keywords (a, b) /CreationDate (D:19991231235959-05'00') /ModDate (D:2001)", string header = "%PDF-1.7\n")
    {
        FileBuilder file = new FileBuilder(header)
            .Object(1, $"<< /Type /Catalog /Pages 2 0 R {(xmp ? "/Metadata 5 0 R" : "")} >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] >>")
            .Object(4, $"<< {info} >>");
        if (xmp)
        {
            // Padding before the trailer, as XMP writers leave room for edits in place.
            file.Stream(5, "/Type /Metadata /Subtype /XML", Encoding.UTF8.GetBytes(Packet.Replace("<?xpacket end", new string(' ', 300) + "\n<?xpacket end", StringComparison.Ordinal)));
        }

        return file.StartXref(file.XrefTable($"/Size 6 /Root 1 0 R /Info 4 0 R")).ToArray();
    }

    private static byte[] Save(PdfDocument pdf, PdfSaveOptions? options = null)
    {
        using MemoryStream output = new();
        pdf.Save(output, options);
        return output.ToArray();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return now.ToUniversalTime();
        }

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test", now.Offset, "Test", "Test");
    }

    [Fact]
    public void ReadsTheInfoDictionary()
    {
        using PdfDocument pdf = PdfDocument.Open(File(xmp: false), Strict);
        PdfMetadata metadata = pdf.Metadata;

        Assert.Equal(("Info title", "Info author", "a, b", null, null), (metadata.Title, metadata.Author, metadata.Keywords, metadata.Subject, metadata.Producer));
        Assert.Equal(new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5)), metadata.CreationDate);
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), metadata.ModificationDate);
        Assert.False(metadata.HasXmp);
    }

    [Fact]
    public void PrefersXmpWhenItHasTheProperty()
    {
        using PdfDocument pdf = PdfDocument.Open(File(xmp: true), Strict);
        PdfMetadata metadata = pdf.Metadata;

        Assert.True(metadata.HasXmp);
        Assert.Equal(("XMP title", "Ada; Grace", "XMP producer", "a, b"), (metadata.Title, metadata.Author, metadata.Producer, metadata.Keywords));
        Assert.Equal(new DateTimeOffset(2020, 5, 6, 7, 8, 9, TimeSpan.FromHours(2)), metadata.CreationDate);
        Assert.Equal("Info title", metadata.Info!.GetText("Title"));
    }

    [Fact]
    public void SettingWritesBothStoresAndKeepsUnknownXmp()
    {
        using PdfDocument pdf = PdfDocument.Open(File(xmp: true));

        pdf.Metadata.Title = "New title";
        pdf.Metadata.Author = "Lars";
        pdf.Metadata.Subject = "About";
        pdf.Metadata.Producer = null;

        using PdfDocument reopened = PdfDocument.Open(Save(pdf, new PdfSaveOptions { UpdateModificationDate = false }), Strict);
        PdfMetadata metadata = reopened.Metadata;
        Assert.Equal(("New title", "Lars", "About", null), (metadata.Title, metadata.Author, metadata.Subject, metadata.Producer));
        Assert.Equal(("New title", "Lars"), (metadata.Info!.GetText("Title"), metadata.Info.GetText("Author")));
        XDocument xmp = metadata.GetXmp()!;
        Assert.Equal("kept", xmp.Descendants(XNamespace.Get("http://example.com/acme/") + "Secret").Single().Value);
        Assert.DoesNotContain(xmp.Descendants(XmpPacket.Dc + "title").Single().Descendants(), e => e.Value == "Titel");
        Assert.Null(reopened.Catalog.Get<PdfStream>("Metadata")!.Dictionary["Filter"]);
    }

    [Fact]
    public void AChangedPacketKeepsItsLengthWhenItCan()
    {
        using PdfDocument pdf = PdfDocument.Open(File(xmp: true));
        int before = pdf.Catalog.Get<PdfStream>("Metadata")!.GetDecodedBytes().Length;

        pdf.Metadata.Keywords = "x";

        byte[] after = pdf.Catalog.Get<PdfStream>("Metadata")!.GetDecodedBytes();
        Assert.Equal(before, after.Length);
        Assert.EndsWith("<?xpacket end=\"w\"?>", Encoding.UTF8.GetString(after), StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentBefore20WithoutXmpGetsNoPacket()
    {
        using PdfDocument pdf = PdfDocument.Open(File(xmp: false));

        pdf.Metadata.Title = "T";

        Assert.False(pdf.Metadata.HasXmp);
        Assert.Null(pdf.Catalog["Metadata"]);
    }

    [Fact]
    public void A20DocumentGetsAPacketAndCreateXmpCopiesInfo()
    {
        using (PdfDocument pdf = PdfDocument.Open(File(xmp: false, header: "%PDF-2.0\n")))
        {
            pdf.Metadata.Subject = "S";
            Assert.True(pdf.Metadata.HasXmp);
            Assert.Equal("S", pdf.Metadata.GetXmp()!.Descendants(XmpPacket.Dc + "description").Single().Value);
        }

        using PdfDocument older = PdfDocument.Open(File(xmp: false));
        older.Metadata.CreateXmp();
        Assert.Equal(("Info title", "Info author"), (older.Metadata.Title, older.Metadata.Author));
        Assert.Equal("Info title", older.Metadata.GetXmp()!.Descendants(XmpPacket.Dc + "title").Single().Value);
        Assert.Equal(new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5)), older.Metadata.CreationDate);
    }

    [Fact]
    public void ASaveWithChangesSetsTheModificationDate()
    {
        DateTimeOffset now = new(2026, 10, 12, 18, 30, 0, TimeSpan.FromHours(2));
        using PdfDocument pdf = PdfDocument.Open(File(xmp: true));
        pdf.Clock = new FixedClock(now);

        byte[] unchanged = Save(pdf);
        Assert.Equal(File(xmp: true), unchanged);

        pdf.Catalog["Edited"] = true;
        byte[] deterministic = Save(pdf, new PdfSaveOptions { Deterministic = true });
        using (PdfDocument reopened = PdfDocument.Open(deterministic))
        {
            Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero), reopened.Metadata.Info is { } info ? PdfDateOf(info) : null);
        }

        using PdfDocument saved = PdfDocument.Open(Save(pdf), Strict);
        Assert.Equal("D:20261012183000+02'00'", saved.Metadata.Info!.GetText("ModDate"));
        Assert.Equal(now, saved.Metadata.ModificationDate);
        Assert.Equal("2026-10-12T18:30:00+02:00", saved.Metadata.GetXmp()!.Descendants(XmpPacket.Xmp + "MetadataDate").Single().Value);
        Assert.Equal("XMP producer", saved.Metadata.Producer);
    }

    [Fact]
    public void ASaveDoesNotBringBackARemovedInfoDictionary()
    {
        using PdfDocument pdf = PdfDocument.Open(File(xmp: false));
        pdf.Trailer.Remove("Info");

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);

        Assert.Null(reopened.Metadata.Info);
    }

    [Theory]
    [InlineData("D:2026", 2026, 1, 1, 0, 0, 0, 0)]
    [InlineData("D:20261012", 2026, 10, 12, 0, 0, 0, 0)]
    [InlineData("D:20261012183005Z", 2026, 10, 12, 18, 30, 5, 0)]
    [InlineData("D:20261012183005+02'00'", 2026, 10, 12, 18, 30, 5, 120)]
    [InlineData("D:20261012183005-0530", 2026, 10, 12, 18, 30, 5, -330)]
    [InlineData("20261012183005+02'00", 2026, 10, 12, 18, 30, 5, 120)]
    public void ReadsPdfDatesLeniently(string text, int year, int month, int day, int hour, int minute, int second, int offsetMinutes)
    {
        Assert.Equal(new DateTimeOffset(year, month, day, hour, minute, second, TimeSpan.FromMinutes(offsetMinutes)), PdfDate.ParsePdf(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("D:")]
    [InlineData("D:20261399")]
    [InlineData("yesterday")]
    public void RejectsWhatIsNotADate(string text)
    {
        Assert.Null(PdfDate.ParsePdf(text));
    }

    [Fact]
    public void ABrokenPacketIsIgnored()
    {
        Assert.Null(XmpPacket.Parse("<x:xmpmeta"u8));
        Assert.Null(XmpPacket.Parse("<a/>"u8));
        Assert.Null(XmpPacket.Parse("<!DOCTYPE a [<!ENTITY b \"c\">]><a>&b;</a>"u8));
    }

    private static DateTimeOffset? PdfDateOf(PdfDictionary info)
    {
        return PdfDate.ParsePdf(info.GetText("ModDate"));
    }
}
