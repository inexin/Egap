using System.IO.Compression;
using System.Text;

namespace Egap.Documents.Tests;

/// <summary>Embedded parts (<c>w:altChunk</c>) in DOCX: RTF ones are read in place, others stay islands.</summary>
public class DocxAltChunkTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string AltChunkType = R + "/aFChunk";

    private const string Chunk = @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0\fs28 {\b Bold} chunk text\par Second\par}";

    /// <summary>A package whose body is <paramref name="body"/>, with each chunk as a part related to the document as rIdC0, rIdC1...</summary>
    private static MemoryStream Package(string body, params (string Name, string ContentType, string Data)[] chunks)
    {
        Docx.OpcPackageWriter writer = new();
        writer.AddPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", Encoding.UTF8.GetBytes(
            $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body>{body}</w:body></w:document>"));
        writer.AddRelationship("/", "rId1", Docx.OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        for (int i = 0; i < chunks.Length; i++)
        {
            writer.AddPart(chunks[i].Name, chunks[i].ContentType, Encoding.Latin1.GetBytes(chunks[i].Data));
            writer.AddRelationship("/word/document.xml", $"rIdC{i}", AltChunkType, chunks[i].Name[("/word/".Length)..]);
        }

        MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        return stream;
    }

    private static string P(string text) => $"<w:p><w:r><w:t>{text}</w:t></w:r></w:p>";

    private static List<string> Texts(IEnumerable<Block> blocks) => [.. blocks.OfType<Paragraph>().Select(p => p.GetText())];

    [Theory]
    [InlineData("application/rtf")]
    [InlineData("text/rtf")]
    public void RtfChunkIsReadInPlace(string contentType)
    {
        using MemoryStream stream = Package(P("Before") + "<w:altChunk r:id=\"rIdC0\"/>" + P("After"), ("/word/chunk.rtf", contentType, Chunk));
        Document document = DocxFormat.Load(stream, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Empty(warnings);
        NodeCollection<Block> blocks = document.Sections[0].Body.Blocks;
        Assert.Equal(["Before", "Bold chunk text", "Second", "After"], Texts(blocks));
        Assert.DoesNotContain(blocks, b => b is BlockIsland);
        Run bold = blocks.OfType<Paragraph>().ElementAt(1).Inlines.OfType<Run>().First();
        Assert.Equal("Bold", bold.Text);
        Assert.True(bold.GetEffectiveFormat().Bold);
        Assert.Equal(14, bold.GetEffectiveFormat().Size);
        Assert.Equal("Arial", bold.GetEffectiveFormat().FontAscii);
    }

    [Fact]
    public void RtfChunkInACellAndTwoChunks()
    {
        string body = "<w:tbl><w:tr><w:tc><w:altChunk r:id=\"rIdC0\"/></w:tc></w:tr></w:tbl>" + "<w:altChunk r:id=\"rIdC1\"/>";
        using MemoryStream stream = Package(body, ("/word/a.rtf", "application/rtf", Chunk), ("/word/b.rtf", "application/rtf", @"{\rtf1 {\trowd\cellx2000 In table\cell\row}After\par}"));
        Document document = DocxFormat.Load(stream);
        NodeCollection<Block> blocks = document.Sections[0].Body.Blocks;
        Cell cell = Assert.IsType<Table>(blocks[0]).Rows[0].Cells[0];
        Assert.Equal(["Bold chunk text", "Second"], Texts(cell.Blocks));
        Assert.Equal("In table", Assert.IsType<Table>(blocks[1]).Rows[0].Cells[0].GetText().Trim());
        Assert.Equal(["After"], Texts(blocks.Skip(2)));
    }

    [Fact]
    public void BrokenRtfChunkStaysAnIslandWithAWarning()
    {
        using MemoryStream stream = Package(P("Before") + "<w:altChunk r:id=\"rIdC0\"/>" + P("After"), ("/word/chunk.rtf", "application/rtf", "not RTF at all"));
        Document document = DocxFormat.Load(stream, out IReadOnlyList<ConversionWarning> warnings);
        ConversionWarning warning = Assert.Single(warnings);
        Assert.Equal(ConversionWarningKind.ResourceNotLoaded, warning.Kind);
        NodeCollection<Block> blocks = document.Sections[0].Body.Blocks;
        Assert.Equal(["Before", "After"], Texts(blocks));
        Assert.Contains("altChunk", Assert.Single(blocks.OfType<BlockIsland>()).Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlChunkStaysAnIsland()
    {
        using MemoryStream stream = Package(P("Before") + "<w:altChunk r:id=\"rIdC0\"/>", ("/word/chunk.htm", "application/xhtml+xml", "<html><body><p>Hi</p></body></html>"));
        Document document = DocxFormat.Load(stream, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Empty(warnings);
        Assert.Single(document.Sections[0].Body.Blocks.OfType<BlockIsland>());
    }

    [Fact]
    public void ImportedChunkSavesAsOrdinaryContent()
    {
        using MemoryStream stream = Package(P("Before") + "<w:altChunk r:id=\"rIdC0\"/>" + P("After"),
            ("/word/chunk.rtf", "application/rtf", @"{\rtf1 Note{\super\chftn{\footnote\pard{\super\chftn} The note.\par}} here.\par}"));
        Document document = DocxFormat.Load(stream);
        using MemoryStream saved = new();
        DocxFormat.Save(document, saved);
        saved.Position = 0;
        using (ZipArchive zip = new(saved, ZipArchiveMode.Read, leaveOpen: true))
        {
            using StreamReader reader = new(zip.GetEntry("word/document.xml")!.Open());
            Assert.DoesNotContain("altChunk", reader.ReadToEnd(), StringComparison.Ordinal);
        }

        saved.Position = 0;
        Document read = DocxFormat.Load(saved);
        Assert.Equal(Texts(document.Sections[0].Body.Blocks), Texts(read.Sections[0].Body.Blocks));
        Assert.Equal(" The note.", Assert.Single(read.Descendants<Footnote>()).Story.GetText().TrimEnd());
    }

    [Fact]
    public void NotesInAChunkInAHeaderAreLeftOut()
    {
        Docx.OpcPackageWriter writer = new();
        writer.AddPart("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml", Encoding.UTF8.GetBytes(
            $"<w:document xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:body>{P("Body")}<w:sectPr><w:headerReference w:type=\"default\" r:id=\"rIdH\"/></w:sectPr></w:body></w:document>"));
        writer.AddPart("/word/header1.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.header+xml", Encoding.UTF8.GetBytes(
            $"<w:hdr xmlns:w=\"{W}\" xmlns:r=\"{R}\"><w:altChunk r:id=\"rIdC\"/></w:hdr>"));
        writer.AddPart("/word/chunk.rtf", "application/rtf", Encoding.Latin1.GetBytes(@"{\rtf1 Head{\super\chftn{\footnote\pard{\super\chftn} Gone.\par}} text\par}"));
        writer.AddRelationship("/", "rId1", Docx.OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        writer.AddRelationship("/word/document.xml", "rIdH", Docx.OpcRelationshipTypes.Header, "header1.xml");
        writer.AddRelationship("/word/header1.xml", "rIdC", AltChunkType, "chunk.rtf");
        using MemoryStream stream = new();
        writer.Save(stream);
        stream.Position = 0;
        Document document = DocxFormat.Load(stream, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Equal(ConversionWarningKind.CommentsDropped, Assert.Single(warnings).Kind);
        HeaderFooter header = Assert.Single(document.Sections[0].HeadersFooters);
        Assert.Equal("Head text", header.GetText().Trim());
        Assert.Empty(document.Descendants<Footnote>());
    }
}
