using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Egap.Documents.Docx;

namespace Egap.Documents.Tests;

/// <summary>The OPC package layer under DOCX (Phase 6 Build Plan task 9, design Q9 and Q10).</summary>
public class PackageTests
{
    private const string Main = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    [Fact]
    public void WrittenPackageReadsBackWithTypesAndRelationships()
    {
        OpcPackageWriter writer = new();
        writer.AddXmlPart("/word/document.xml", Main, XDocument.Parse("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body/></w:document>"));
        writer.AddPart("/word/media/image1.png", "image/png", [0x89, 0x50, 0x4E, 0x47]);
        writer.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rId5", OpcRelationshipTypes.Image, "media/image1.png");
        writer.AddRelationship("/word/document.xml", "rId6", OpcRelationshipTypes.Hyperlink, "https://example.com", external: true);
        using MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;

        OpcPackage package = OpcPackage.Read(stream, new DocumentLimits());

        OpcPart document = package.GetRelatedPart(package.Relationships, OpcRelationshipTypes.OfficeDocument)!;
        Assert.Equal("/word/document.xml", document.Name);
        Assert.Equal(Main, document.ContentType);
        Assert.Equal("image/png", package.GetPart("word/media/IMAGE1.png")!.ContentType);
        Assert.Equal("/word/media/image1.png", document.GetRelationship("rId5")!.TargetName);
        Assert.True(document.GetRelationship("rId6")!.External);
        Assert.Equal("body", document.LoadXml().Root!.Elements().Single().Name.LocalName);
    }

    [Theory]
    [InlineData("/word/document.xml", "media/a.png", "/word/media/a.png")]
    [InlineData("/word/document.xml", "../customXml/item1.xml", "/customXml/item1.xml")]
    [InlineData("/word/document.xml", "/word/theme/theme1.xml", "/word/theme/theme1.xml")]
    [InlineData("/", "word/document.xml", "/word/document.xml")]
    [InlineData("/word/document.xml", "media/my%20image.png#frag", "/word/media/my image.png")]
    public void TargetsResolveAgainstTheirSource(string source, string target, string expected)
    {
        Assert.Equal(expected, OpcPackage.ResolveTarget(source, target));
    }

    [Fact]
    public void StrictNamespacesReadAsTransitional()
    {
        OpcPackage package = Package(("word/document.xml", "<w:document xmlns:w=\"http://purl.oclc.org/ooxml/wordprocessingml/main\" w:conformance=\"strict\"><w:body/></w:document>"));

        XDocument xml = package.GetPart("/word/document.xml")!.LoadXml();

        Assert.Equal("http://schemas.openxmlformats.org/wordprocessingml/2006/main", xml.Root!.Name.NamespaceName);
        Assert.Equal("strict", (string?)xml.Root.Attribute(XName.Get("conformance", "http://schemas.openxmlformats.org/wordprocessingml/2006/main")));
        Assert.True(OpcRelationshipTypes.Same("http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument", OpcRelationshipTypes.OfficeDocument));
        Assert.Equal("http://schemas.openxmlformats.org/officeDocument/2006/extended-properties", StrictNamespaces.ToTransitional("http://purl.oclc.org/ooxml/officeDocument/extendedProperties"));
    }

    [Fact]
    public void DtdsAndExternalEntitiesAreRefused()
    {
        OpcPackage package = Package(("word/document.xml", "<!DOCTYPE d [<!ENTITY x SYSTEM \"file:///c:/windows/win.ini\">]><d>&x;</d>"));

        Assert.Throws<DocumentException>(() => package.GetPart("/word/document.xml")!.LoadXml());
    }

    [Fact]
    public void LimitsStopBombsAndFloods()
    {
        using MemoryStream bomb = Zip(("[Content_Types].xml", "<Types/>"), ("big.xml", new string(' ', 4 * 1024 * 1024)));
        Assert.Throws<DocumentLimitException>(() => OpcPackage.Read(bomb, new DocumentLimits { MaxCompressionRatio = 50 }));

        using MemoryStream many = Zip([.. Enumerable.Range(0, 20).Select(i => ($"p{i}.xml", "<a/>"))]);
        Assert.Throws<DocumentLimitException>(() => OpcPackage.Read(many, new DocumentLimits { MaxEntries = 10 }));

        using MemoryStream large = Zip(("[Content_Types].xml", "<Types/>"), ("a.xml", new string('x', 5000)));
        Assert.Throws<DocumentLimitException>(() => OpcPackage.Read(large, new DocumentLimits { MaxPartSize = 1000 }));
    }

    [Fact]
    public void NotAPackage()
    {
        Assert.Throws<DocumentException>(() => OpcPackage.Read(new MemoryStream(Encoding.ASCII.GetBytes("hello, not a zip")), new DocumentLimits()));
        DocumentException doc = Assert.Throws<DocumentException>(() => OpcPackage.Read(new MemoryStream([0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0]), new DocumentLimits()));
        Assert.Contains(".doc", doc.Message, StringComparison.Ordinal);
        using MemoryStream noTypes = Zip(("word/document.xml", "<a/>"));
        Assert.Throws<DocumentException>(() => OpcPackage.Read(noTypes, new DocumentLimits()));
    }

    [Fact]
    public void AlternateContentPicksTheFirstUnderstoodChoice()
    {
        XElement content = XElement.Parse("""
            <mc:AlternateContent xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006" xmlns:wps="urn:wps" xmlns:v="urn:v">
              <mc:Choice Requires="wps"><wps:shape/></mc:Choice>
              <mc:Fallback><v:shape/></mc:Fallback>
            </mc:AlternateContent>
            """);

        Assert.Equal("{urn:wps}shape", MarkupCompatibility.Choose(content, ns => ns == "urn:wps").Single().Name.ToString());
        Assert.Equal("{urn:v}shape", MarkupCompatibility.Choose(content, ns => ns == "urn:v").Single().Name.ToString());
    }

    private static OpcPackage Package(params (string Name, string Xml)[] parts)
    {
        string types = "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>";
        using MemoryStream stream = Zip([("[Content_Types].xml", types), .. parts]);
        return OpcPackage.Read(stream, new DocumentLimits());
    }

    private static MemoryStream Zip(params (string Name, string Text)[] entries)
    {
        MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string text) in entries)
            {
                using Stream entry = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
                entry.Write(Encoding.UTF8.GetBytes(text));
            }
        }

        stream.Position = 0;
        return stream;
    }
}
