using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Egap.Documents.Odf;

namespace Egap.Documents.Tests;

/// <summary>The ODT writer: packages, kinds and versions, encryption, protection, text, fields, changes, notes, tables and kept markup.</summary>
public sealed class OdtWriterTests
{
    private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
    private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace Config = "urn:oasis:names:tc:opendocument:xmlns:config:1.0";
    private static readonly XNamespace Fo = "urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0";

    public static TheoryData<string> DocxSeeds =>
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "docx-xml"), "*.xml").Select(f => Path.GetFileName(f)),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "docx-package"), "*.docx").Select(f => Path.GetFileName(f)),
    ];

    public static TheoryData<string> OdtSeeds => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "odt")).Select(f => Path.GetFileName(f))];

    // ---- Helpers ----

    private static byte[] Save(Document document, out IReadOnlyList<ConversionWarning> warnings, OdtSaveOptions? options = null)
    {
        using MemoryStream stream = new();
        warnings = OdtFormat.Save(document, stream, options);
        byte[] bytes = stream.ToArray();
        if (Environment.GetEnvironmentVariable("EGAP_ODT_OUTPUT") is { Length: > 0 } folder)
        {
            // For opening every file the tests write in other applications.
            bool flat = options?.Kind is OdtKind.FlatDocument or OdtKind.FlatTemplate;
            File.WriteAllBytes(Path.Combine(folder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + (flat ? ".fodt" : ".odt")), bytes);
        }

        return bytes;
    }

    private static byte[] Save(Document document, OdtSaveOptions? options = null) => Save(document, out _, options);

    private static Document Reread(Document document, out IReadOnlyList<ConversionWarning> warnings, OdtSaveOptions? options = null, string? password = null)
    {
        using MemoryStream stream = new(Save(document, options));
        return OdtFormat.Load(stream, out warnings, new OdtLoadOptions { Password = password });
    }

    private static Document Reread(Document document, OdtSaveOptions? options = null, string? password = null) => Reread(document, out _, options, password);

    /// <summary>The XML of a part of a written package.</summary>
    private static XElement Part(byte[] package, string name)
    {
        using ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read);
        using Stream stream = zip.GetEntry(name)!.Open();
        return XElement.Load(stream);
    }

    private static XElement Content(Document document, OdtSaveOptions? options = null) => Part(Save(document, options), "content.xml");

    private static XElement Body(XElement content) => content.Element(Office + "body")!.Element(Office + "text")!;

    private static Document LoadDocx(string name)
    {
        string xml = Path.Combine(AppContext.BaseDirectory, "files", "docx-xml", name);
        return File.Exists(xml)
            ? DocxFormat.Load(DocxSeedPackage.Wrap(File.ReadAllBytes(xml)))
            : DocxFormat.Load(Path.Combine(AppContext.BaseDirectory, "files", "docx-package", name));
    }

    private static Document NewDocument(out Body body)
    {
        Document document = new();
        body = document.AddSection().Body;
        return document;
    }

    private static void Same(string expected, string actual, string name)
    {
        if (expected != actual && Environment.GetEnvironmentVariable("EGAP_ODT_DUMPS") is { Length: > 0 } folder)
        {
            File.WriteAllText(Path.Combine(folder, name + ".a.txt"), expected);
            File.WriteAllText(Path.Combine(folder, name + ".b.txt"), actual);
        }

        Assert.Equal(expected, actual);
    }

    // ---- Round trips ----

    [Theory]
    [MemberData(nameof(OdtSeeds))]
    public void SeedsRoundTrip(string name)
    {
        Document first = OdtFormat.Load(Path.Combine(AppContext.BaseDirectory, "files", "odt", name));
        Document second = Reread(first);
        Document third = Reread(second);
        Same(RtfWriterTests.Dump(second), RtfWriterTests.Dump(third), "s-" + name);
        Assert.Equal(first.GetText(), second.GetText());
    }

    [Theory]
    [MemberData(nameof(DocxSeeds))]
    public void DocxSavedAsOdtRoundTrips(string name)
    {
        Document first = Reread(LoadDocx(name));
        Document second = Reread(first);
        Same(RtfWriterTests.Dump(first), RtfWriterTests.Dump(second), "r-" + name);
    }
}
