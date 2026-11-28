using System.IO.Compression;
using System.Text;

using Egap.Documents;

namespace Egap.Presentations.Tests;

/// <summary>Files that are not presentations, or are damaged, throw <see cref="DocumentException"/> with a message that says why.</summary>
public sealed class MalformedTests
{
    [Fact]
    public void BytesThatAreNotAZipPackageThrow()
    {
        DocumentException e = Assert.Throws<DocumentException>(() => Presentation.Load(new MemoryStream(Encoding.ASCII.GetBytes("not a presentation at all"))));
        Assert.Contains("ZIP", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACompoundFileThrows()
    {
        byte[] head = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        DocumentException e = Assert.Throws<DocumentException>(() => Presentation.Load(new MemoryStream(head)));
        Assert.Contains("PowerPoint 97-2003", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageWithoutContentTypesThrows()
    {
        byte[] file = PptxBuilder.WithEntry(new PptxBuilder().Build(), "[Content_Types].xml", null);
        DocumentException e = Assert.Throws<DocumentException>(() => Presentation.Load(new MemoryStream(file)));
        Assert.Contains("[Content_Types].xml", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageWithoutAPresentationThrows()
    {
        byte[] file = PptxBuilder.WithEntry(new PptxBuilder().Build(), "ppt/presentation.xml", null);
        DocumentException e = Assert.Throws<DocumentException>(() => Presentation.Load(new MemoryStream(file)));
        Assert.Contains("not a PowerPoint file", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWordDocumentThrows()
    {
        byte[] file = PptxBuilder.WithEntry(new PptxBuilder().Build(), "ppt/presentation.xml",
            Encoding.UTF8.GetBytes("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"/>"));
        DocumentException e = Assert.Throws<DocumentException>(() => Presentation.Load(new MemoryStream(file)));
        Assert.Contains("document element", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASlideThatIsNotWellFormedThrowsNamingThePart()
    {
        byte[] file = PptxBuilder.WithEntry(new PptxBuilder().Build(), "ppt/slides/slide1.xml", Encoding.UTF8.GetBytes("<p:sld xmlns:p=\"x\"><unclosed></p:sld>"));
        DocumentException e = Assert.Throws<DocumentException>(() => Presentation.Load(new MemoryStream(file)));
        Assert.Contains("/ppt/slides/slide1.xml", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentTypeDeclarationIsRefused()
    {
        byte[] file = PptxBuilder.WithEntry(new PptxBuilder().Build(), "ppt/slideMasters/slideMaster1.xml",
            Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e \"boom\">]><p:sldMaster xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\">&e;</p:sldMaster>"));
        DocumentException e = Assert.Throws<DocumentException>(() => Presentation.Load(new MemoryStream(file)));
        Assert.Contains("/ppt/slideMasters/slideMaster1.xml", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LimitsStopReading()
    {
        byte[] file = new PptxBuilder().Build();
        PresentationLoadOptions options = new() { Limits = new DocumentLimits { MaxEntries = 3 } };

        Assert.Throws<DocumentLimitException>(() => Presentation.Load(new MemoryStream(file), options));
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentNullException>(() => Presentation.Load((Stream)null!));
        Assert.Throws<ArgumentException>(() => Presentation.Load(""));
        Presentation presentation = PresentationTests.Load(new PptxBuilder());
        Assert.Throws<ArgumentNullException>(() => presentation.Save((Stream)null!));
        Assert.Throws<ArgumentException>(() => presentation.Save(""));
        Assert.Throws<ArgumentNullException>(() => presentation.Slides[0].Shapes[0].Name = null!);
        Assert.Throws<ArgumentNullException>(() => ((AutoShape)presentation.Slides[0].Shapes[0]).TextBody!.Paragraphs[0].Runs[0].Text = null!);
    }

    [Fact]
    public void AnEmptyZipThrows()
    {
        using MemoryStream empty = new();
        using (new ZipArchive(empty, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        Assert.Throws<DocumentException>(() => Presentation.Load(new MemoryStream(empty.ToArray())));
    }
    [Fact]
    public void ASlidePartListedTwiceIsOneSlide()
    {
        PptxBuilder builder = new();
        builder.Slides.Add(PptxBuilder.Rectangle(2, "Box", 0, 0, 100, 100, "Box"));
        byte[] file = builder.Build();
        string main = PptxBuilder.Entry(file, "ppt/presentation.xml");
        int start = main.IndexOf("<p:sldId ", StringComparison.Ordinal);
        string entry = main[start..(main.IndexOf("/>", start, StringComparison.Ordinal) + 2)];
        string twice = main.Replace(entry, entry + entry.Replace("id=\"256\"", "id=\"300\"", StringComparison.Ordinal), StringComparison.Ordinal);
        Presentation presentation = Presentation.Load(new MemoryStream(PptxBuilder.WithEntry(file, "ppt/presentation.xml", Encoding.UTF8.GetBytes(twice))));

        Slide slide = Assert.Single(presentation.Slides);
        Assert.True(slide.Shapes.Remove(slide.Shapes[^1]));
        Assert.Empty(BuildingTests.Reload(presentation).Slides[0].Shapes);
    }

    [Fact]
    public void ANewSlideDoesNotTakeOverARelationshipToAMissingPart()
    {
        PptxBuilder builder = new();
        builder.Slides.Add("");
        builder.Slides.Add("");
        byte[] file = PptxBuilder.WithEntry(builder.Build(), "ppt/slides/slide2.xml", null);
        Presentation presentation = Presentation.Load(new MemoryStream(file));
        Assert.Single(presentation.Slides);

        Slide added = presentation.Slides.Add(presentation.SlideMasters[0].Layouts[0]);
        Assert.NotEqual("/ppt/slides/slide2.xml", added.PartName);
        Assert.Equal(2, BuildingTests.Reload(presentation).Slides.Count);
    }
}
