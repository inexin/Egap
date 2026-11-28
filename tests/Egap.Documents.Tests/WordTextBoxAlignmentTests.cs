using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Egami.Fonts.Layout;

using Egap.Documents.Layout;

namespace Egap.Documents.Tests;

/// <summary>
/// Text boxes whose text sits centred or at the bottom (DrawingML's anchor, VML's v-text-anchor, Word's doNotVertAlignInTxbx),
/// and the tab after an underlined list label, which Word leaves plain unless its underlineTabInNumList is on.
/// </summary>
public class WordTextBoxAlignmentTests
{
    private static readonly LayoutOptions Isolated = new() { Fonts = new FontSettings { UseSystemFonts = false } };
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Wps = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";

    [Theory]
    [InlineData(VerticalAlignment.Top, false)]
    [InlineData(VerticalAlignment.Center, false)]
    [InlineData(VerticalAlignment.Bottom, false)]
    [InlineData(VerticalAlignment.Center, true)]
    [InlineData(VerticalAlignment.Bottom, true)]
    public void TextSitsWhereTheBoxSays(VerticalAlignment alignment, bool flag)
    {
        (Document document, Shape shape) = BoxDocument(alignment);
        document.Settings.WordCompatibility.DoNotVerticallyAlignInTextBox = flag;

        DocumentLayout layout = document.Layout(Isolated);
        LayoutRect box = layout.FindBoxes(shape).Single().Box.Bounds;
        LineBox[] lines = [.. layout.FindBoxes(shape).Single().Box.Descendants().OfType<LineBox>()];
        double above = lines[0].Bounds.Y - box.Y - 3.6;
        double below = box.Bottom - 3.6 - lines[^1].Bounds.Bottom;

        VerticalAlignment expected = flag ? VerticalAlignment.Top : alignment;
        switch (expected)
        {
            case VerticalAlignment.Top:
                Assert.Equal(0, above, 1);
                break;
            case VerticalAlignment.Center:
                Assert.True(above > 10);
                Assert.Equal(above, below, 1);
                break;
            default:
                Assert.True(above > 10);
                Assert.Equal(0, below, 1);
                break;
        }
    }

    [Fact]
    public void TextTallerThanTheBoxStartsAtTheTop()
    {
        (Document document, Shape shape) = BoxDocument(VerticalAlignment.Bottom, height: 20);
        DocumentLayout layout = document.Layout(Isolated);

        LayoutRect box = layout.FindBoxes(shape).Single().Box.Bounds;
        LineBox first = layout.FindBoxes(shape).Single().Box.Descendants().OfType<LineBox>().First();
        Assert.Equal(box.Y + 3.6, first.Bounds.Y, 1);
    }

    [Fact]
    public void JustifyIsTakenAsTop()
    {
        Shape shape = new(new Document(), ShapeKind.Rectangle, 10, 10) { TextVerticalAlignment = VerticalAlignment.Justify };

        Assert.Equal(VerticalAlignment.Top, shape.TextVerticalAlignment);
    }

    [Theory]
    [InlineData(VerticalAlignment.Top, null)]
    [InlineData(VerticalAlignment.Center, "ctr")]
    [InlineData(VerticalAlignment.Bottom, "b")]
    public void AlignmentGoesThroughDocx(VerticalAlignment alignment, string? anchor)
    {
        (Document document, _) = BoxDocument(alignment);
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);

        XElement bodyPr = Part(stream.ToArray(), "word/document.xml").Descendants(Wps + "bodyPr").Single();
        Assert.Equal(anchor, (string?)bodyPr.Attribute("anchor"));
        Assert.Equal(alignment, ShapeOf(DocxFormat.Load(new MemoryStream(stream.ToArray()))).TextVerticalAlignment);
    }

    [Fact]
    public void ReadsVmlTextAnchor()
    {
        (Document document, _) = BoxDocument(VerticalAlignment.Top);
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        string body = """
            <w:p><w:r><w:pict><v:shape style="position:absolute;margin-left:10pt;margin-top:10pt;width:100pt;height:80pt;v-text-anchor:middle" xmlns:v="urn:schemas-microsoft-com:vml">
            <v:textbox><w:txbxContent><w:p><w:r><w:t>vml</w:t></w:r></w:p></w:txbxContent></v:textbox></v:shape></w:pict></w:r></w:p>
            """;
        byte[] docx = ReplaceBody(stream.ToArray(), body);

        Assert.Equal(VerticalAlignment.Center, ShapeOf(DocxFormat.Load(new MemoryStream(docx))).TextVerticalAlignment);
    }

    [Theory]
    [InlineData(VerticalAlignment.Top)]
    [InlineData(VerticalAlignment.Center)]
    [InlineData(VerticalAlignment.Bottom)]
    public void AlignmentGoesThroughRtf(VerticalAlignment alignment)
    {
        (Document document, _) = BoxDocument(alignment);
        using MemoryStream stream = new();
        RtfFormat.Save(document, stream);
        string rtf = Encoding.ASCII.GetString(stream.ToArray());

        Assert.Equal(alignment != VerticalAlignment.Top, rtf.Contains("anchorText", StringComparison.Ordinal));
        Assert.Equal(alignment, ShapeOf(RtfFormat.Load(new MemoryStream(stream.ToArray()))).TextVerticalAlignment);
    }

    [Fact]
    public void CloneKeepsTheAlignment()
    {
        (_, Shape shape) = BoxDocument(VerticalAlignment.Bottom);

        Assert.Equal(VerticalAlignment.Bottom, ((Shape)shape.Clone()).TextVerticalAlignment);
    }

    [Theory]
    [InlineData("doNotVertAlignInTxbx")]
    [InlineData("underlineTabInNumList")]
    public void FlagsGoThroughDocx(string name)
    {
        Document document = new();
        WordCompatibility compatibility = document.Settings.WordCompatibility;
        Assert.False(name == "doNotVertAlignInTxbx" ? compatibility.DoNotVerticallyAlignInTextBox : compatibility.UnderlineTabInNumberedList);
        if (name == "doNotVertAlignInTxbx")
        {
            compatibility.DoNotVerticallyAlignInTextBox = true;
        }
        else
        {
            compatibility.UnderlineTabInNumberedList = true;
        }

        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);

        Assert.NotNull(Part(stream.ToArray(), "word/settings.xml").Element(W + "compat")!.Element(W + name));
        WordCompatibility read = DocxFormat.Load(new MemoryStream(stream.ToArray())).Settings.WordCompatibility;
        Assert.True(name == "doNotVertAlignInTxbx" ? read.DoNotVerticallyAlignInTextBox : read.UnderlineTabInNumberedList);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VerticalAlignFlagGoesThroughRtf(bool on)
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("text");
        document.Settings.WordCompatibility.DoNotVerticallyAlignInTextBox = on;
        using MemoryStream stream = new();
        RtfFormat.Save(document, stream);

        Assert.Equal(on, Encoding.ASCII.GetString(stream.ToArray()).Contains(@"\notvatxbx", StringComparison.Ordinal));
        Assert.Equal(on, RtfFormat.Load(new MemoryStream(stream.ToArray())).Settings.WordCompatibility.DoNotVerticallyAlignInTextBox);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TabAfterAnUnderlinedLabelIsPlainUnlessTheFlagSaysSo(bool flag)
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("text");
        List list = document.Lists.AddNumbered();
        list.Definition.Levels[0].RunFormat.Underline = UnderlineStyle.Double;
        list.Definition.Levels[0].RunFormat.Strikethrough = true;
        paragraph.SetList(list);
        document.Settings.WordCompatibility.UnderlineTabInNumberedList = flag;

        ParagraphContent content = new ParagraphBridge(new FontProvider(document, new FontSettings { UseSystemFonts = false }), document).Build(paragraph);

        Assert.Equal("1.\ttext", content.Text.Text);
        TextDecoration? label = DecorationAt(content, 0);
        TextDecoration? tab = DecorationAt(content, 2);
        Assert.Equal(TextDecorationLines.Underline | TextDecorationLines.LineThrough, label!.Lines);
        Assert.Equal(flag ? label.Lines : TextDecorationLines.LineThrough, tab!.Lines);
        Assert.Equal(label.Style, tab.Style);
    }

    private static TextDecoration? DecorationAt(ParagraphContent content, int index)
    {
        return content.Text.RichText.Spans.Single(s => s.Start <= index && index < s.End).Style.Decoration;
    }

    private static (Document Document, Shape Shape) BoxDocument(VerticalAlignment alignment, double height = 120)
    {
        Document document = new();
        Paragraph anchor = document.AddSection().Body.AddParagraph("anchor");
        Shape shape = new(document, ShapeKind.Rectangle, 150, height)
        {
            TextVerticalAlignment = alignment,
            Floating = new FloatingPlacement { HorizontalAnchor = HorizontalAnchor.Margin, VerticalAnchor = VerticalAnchor.Margin, HorizontalOffset = 20, VerticalOffset = 30, Wrap = WrapMode.InFrontOfText },
        };
        TextBoxStory story = shape.AddTextBox();
        foreach (string text in (ReadOnlySpan<string>)["one", "two"])
        {
            Paragraph line = story.AddParagraph(text);
            line.Format.SpaceAfter = 0;
            line.Format.SpaceBefore = 0;
        }

        anchor.Inlines.Add(shape);
        return (document, shape);
    }

    private static Shape ShapeOf(Document document)
    {
        return document.Sections[0].Body.Paragraphs.SelectMany(p => p.Inlines).OfType<Shape>().Single();
    }

    private static XElement Part(byte[] docx, string name)
    {
        using ZipArchive zip = new(new MemoryStream(docx));
        using Stream part = zip.GetEntry(name)!.Open();
        return XElement.Load(part);
    }

    private static byte[] ReplaceBody(byte[] docx, string paragraphs)
    {
        using MemoryStream output = new();
        output.Write(docx);
        using (ZipArchive zip = new(output, ZipArchiveMode.Update, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.GetEntry("word/document.xml")!;
            string xml;
            using (StreamReader reader = new(entry.Open()))
            {
                xml = reader.ReadToEnd();
            }

            int start = xml.IndexOf("<w:body>", StringComparison.Ordinal) + "<w:body>".Length;
            int end = xml.IndexOf("<w:sectPr", StringComparison.Ordinal);
            xml = xml[..start] + paragraphs + xml[end..];
            entry.Delete();
            using StreamWriter writer = new(zip.CreateEntry("word/document.xml").Open());
            writer.Write(xml);
        }

        return output.ToArray();
    }
}
