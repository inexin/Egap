using System.IO.Compression;
using System.Xml.Linq;

using Egami.Fonts.Layout;

using Egap.Documents.Comparing;
using Egap.Documents.Docx;
using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests;

/// <summary>Ruby, emphasis marks and tate-chu-yoko: the model, layout, tagged PDF, DOCX and ODT.</summary>
public sealed class RubyTests
{
    private const string WNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace W = WNs;
    private static readonly LayoutOptions Portable = new() { Fonts = FontSettings.Portable };

    /// <summary>"Tokyo" with "TO KYO" over it, between plain text, and the ruby.</summary>
    private static (Document Document, Ruby Ruby) Sample()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Visit ");
        Ruby ruby = new(document, "TO KYO") { FontSize = 6, Raise = 11 };
        ruby.Inlines.Add(new Run(document, "Tokyo"));
        paragraph.Inlines.Add(ruby);
        paragraph.AddRun(" soon.");
        return (document, ruby);
    }

    [Fact]
    public void TheBaseIsTheTextAndTheAnnotationIsNot()
    {
        (Document document, Ruby ruby) = Sample();

        Assert.Equal("Visit Tokyo soon.", document.Sections[0].Body.Paragraphs.First().GetText());
        Ruby copy = ruby.Clone();
        Assert.Equal("TO KYO", copy.Text);
        Assert.Equal(6, copy.FontSize);
        Assert.Equal("Tokyo", copy.GetText());
        Assert.Throws<ArgumentOutOfRangeException>(() => ruby.FontSize = 0);
        Assert.Throws<ArgumentException>(() => ruby.Inlines.Add(new Ruby(document, "x")));
    }

    [Fact]
    public void LayoutSetsTheAnnotationAndEmphasisMarksBesideTheLine()
    {
        (Document document, Ruby ruby) = Sample();
        Paragraph paragraph = document.Sections[0].Body.Paragraphs.First();
        Run marked = paragraph.AddRun(" Now");
        marked.Format.Emphasis = EmphasisMark.Dot;
        Paragraph plain = document.Sections[0].Body.AddParagraph("Visit Tokyo soon. Now");

        DocumentLayout layout = document.Layout(Portable);
        LineBox line = layout.FindBoxes(paragraph).Select(b => b.Box).OfType<LineBox>().Single();
        LineBox plainLine = layout.FindBoxes(plain).Select(b => b.Box).OfType<LineBox>().Single();

        Assert.Same(ruby, Assert.Single(line.Content.Rubies!));
        LayoutRun annotation = Assert.Single(line.Line.AnnotationRuns, r => r.Kind == LayoutRunKind.Ruby);
        Assert.Equal(0, annotation.Annotation);
        Assert.Equal("Visit ".Length, annotation.Start);
        Assert.Equal("Tokyo".Length, annotation.Length);
        Assert.Contains(line.Line.AnnotationRuns, r => r.Kind == LayoutRunKind.Emphasis);

        // The annotation takes room above the line: the line is taller than the same text without it.
        Assert.True(line.Bounds.Height > plainLine.Bounds.Height, $"{line.Bounds.Height} vs {plainLine.Bounds.Height}");
    }

    [Fact]
    public void TaggedPdfsPutTheBaseAndAnnotationInRubyElements()
    {
        (Document document, _) = Sample();
        using PdfDocument pdf = document.ToPdf(new PdfExportOptions { Tagged = true, Layout = Portable });

        PdfStructureElement ruby = Assert.Single(pdf.Structure.Root!.DescendantsAndSelf(), e => e.Type.Name == "Ruby");
        Assert.Equal(PdfStructureType.P, ruby.Parent!.Type);
        Assert.Equal(["RB", "RT"], ruby.Elements.Select(e => e.Type.Name));
        Assert.All(ruby.Elements, e => Assert.IsType<PdfMarkedContentReference>(Assert.Single(e.Children)));

        // The paragraph's own content goes on after the ruby, in content of its own.
        Assert.True(ruby.Parent.Children.OfType<PdfMarkedContentReference>().Count() >= 2);
        // Text extraction reads the base and the annotation.
        using MemoryStream stream = new();
        pdf.Save(stream);
        using PdfDocument saved = PdfDocument.Open(stream.ToArray());
        string text = saved.Pages[0].ExtractText();
        Assert.True(text.Contains("Tokyo", StringComparison.Ordinal) && text.Contains("KYO", StringComparison.Ordinal), text);
    }

    [Fact]
    public void DocxPhoneticGuidesAreRead()
    {
        Document document = OfficeMathTests.LoadDocx(
            """
            <w:p><w:r><w:t xml:space="preserve">See </w:t></w:r><w:r><w:ruby>
              <w:rubyPr><w:rubyAlign w:val="distributeSpace"/><w:hps w:val="10"/><w:hpsRaise w:val="18"/><w:hpsBaseText w:val="21"/><w:lid w:val="ja-JP"/><w:dirty/></w:rubyPr>
              <w:rt><w:r><w:rPr><w:rFonts w:ascii="MS Mincho"/><w:sz w:val="10"/></w:rPr><w:t>とうきょう</w:t></w:r></w:rt>
              <w:rubyBase><w:r><w:rPr><w:sz w:val="21"/></w:rPr><w:t>東京</w:t></w:r></w:rubyBase>
            </w:ruby></w:r><w:r><w:rPr><w:em w:val="comma"/><w:eastAsianLayout w:id="1" w:vert="1" w:vertCompress="1"/></w:rPr><w:t>12</w:t></w:r></w:p>
            """);

        Ruby ruby = Assert.Single(document.Descendants<Ruby>());
        Assert.Equal("とうきょう", ruby.Text);
        Assert.Equal("東京", ruby.GetText());
        Assert.Equal(RubyAlignment.DistributeSpace, ruby.Alignment);
        Assert.Equal(5, ruby.FontSize);
        Assert.Equal(9, ruby.Raise);
        Assert.Equal(10.5, ruby.BaseFontSize);
        Assert.Equal("ja-JP", ruby.Language);
        Assert.Equal("MS Mincho", ruby.TextFormat.FontAscii);
        Assert.Null(ruby.TextFormat.Size);
        Assert.Equal("See 東京12", document.Sections[0].Body.Paragraphs.First().GetText());
        Run digits = document.Descendants<Run>().Last();
        Assert.Equal(EmphasisMark.Comma, digits.Format.Emphasis);
        Assert.True(digits.Format.CombineUpright);
    }

    [Fact]
    public void DocxPhoneticGuidesRoundTrip()
    {
        (Document document, Ruby ruby) = Sample();
        ruby.Alignment = RubyAlignment.Left;
        ruby.TextFormat.Bold = true;
        Run run = document.Sections[0].Body.Paragraphs.First().AddRun("12");
        run.Format.Emphasis = EmphasisMark.UnderDot;
        run.Format.CombineUpright = true;

        byte[] saved = SaveDocx(document);
        XElement body = DocxPart(saved, "word/document.xml");
        XElement rubyElement = body.Descendants(W + "ruby").Single();
        Assert.Equal(W + "r", rubyElement.Parent!.Name);
        Assert.Equal(["rubyAlign", "hps", "hpsRaise", "hpsBaseText", "lid"], rubyElement.Element(W + "rubyPr")!.Elements().Select(e => e.Name.LocalName));
        Assert.Equal("12", (string?)rubyElement.Element(W + "rubyPr")!.Element(W + "hps")!.Attribute(W + "val"));
        Assert.Equal("22", (string?)rubyElement.Element(W + "rubyPr")!.Element(W + "hpsRaise")!.Attribute(W + "val"));
        Assert.Equal("12", (string?)rubyElement.Element(W + "rt")!.Descendants(W + "sz").Single().Attribute(W + "val"));
        Assert.Equal("underDot", (string?)body.Descendants(W + "em").Single().Attribute(W + "val"));
        Assert.Equal("1", (string?)body.Descendants(W + "eastAsianLayout").Single().Attribute(W + "vert"));

        Document again = DocxFormat.Load(new MemoryStream(saved));
        Ruby read = Assert.Single(again.Descendants<Ruby>());
        Assert.Equal("TO KYO", read.Text);
        Assert.Equal("Tokyo", read.GetText());
        Assert.Equal(RubyAlignment.Left, read.Alignment);
        Assert.Equal(6, read.FontSize);
        Assert.Equal(11, read.Raise);
        Assert.True(read.TextFormat.Bold);
        Run digits = again.Descendants<Run>().Last();
        Assert.Equal(EmphasisMark.UnderDot, digits.Format.Emphasis);
        Assert.True(digits.Format.CombineUpright);
    }

    [Fact]
    public void DocxRubyWithoutSettingsGetsWordsDefaults()
    {
        Document document = new();
        Paragraph paragraph = document.AddSection().Body.AddParagraph();
        Ruby ruby = new(document, "かん");
        Run base_ = new(document, "漢");
        base_.Format.Size = 12;
        ruby.Inlines.Add(base_);
        paragraph.Inlines.Add(ruby);

        XElement properties = DocxPart(SaveDocx(document), "word/document.xml").Descendants(W + "rubyPr").Single();

        Assert.Equal(["12", "21", "24", "ja-JP"], properties.Elements().Skip(1).Select(e => (string)e.Attribute(W + "val")!));
    }

    [Fact]
    public void OdtRubyAndEmphasisRoundTrip()
    {
        (Document document, Ruby ruby) = Sample();
        ruby.Position = RubyPosition.Under;
        ruby.Alignment = RubyAlignment.DistributeLetter;
        document.Sections[0].Body.Paragraphs.First().AddRun("!").Format.Emphasis = EmphasisMark.Circle;

        using MemoryStream stream = new();
        OdtFormat.Save(document, stream);
        XElement content;
        using (ZipArchive zip = new(new MemoryStream(stream.ToArray())))
        using (Stream part = zip.GetEntry("content.xml")!.Open())
        {
            content = XElement.Load(part);
        }

        XNamespace text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0", style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";
        XElement rubyElement = content.Descendants(text + "ruby").Single();
        Assert.Equal(["ruby-base", "ruby-text"], rubyElement.Elements().Select(e => e.Name.LocalName));
        Assert.Contains(content.Descendants(style + "ruby-properties"), p => (string?)p.Attribute(style + "ruby-position") == "below");
        Assert.Contains(content.Descendants(style + "text-properties"), p => (string?)p.Attribute(style + "text-emphasis") == "circle above");

        stream.Position = 0;
        Document again = OdtFormat.Load(stream);
        Ruby read = Assert.Single(again.Descendants<Ruby>());
        Assert.Equal("TO KYO", read.Text);
        Assert.Equal("Tokyo", read.GetText());
        Assert.Equal(RubyPosition.Under, read.Position);
        Assert.Equal(RubyAlignment.DistributeLetter, read.Alignment);
        Assert.Equal(6, read.FontSize);
        Assert.Equal(EmphasisMark.Circle, again.Descendants<Run>().Last().Format.Emphasis);
    }

    [Fact]
    public void FormatsWithoutRubyKeepTheBase()
    {
        (Document document, _) = Sample();

        using MemoryStream rtf = new();
        IReadOnlyList<ConversionWarning> warnings = RtfFormat.Save(document, rtf);
        rtf.Position = 0;

        Assert.Equal("Visit Tokyo soon.", RtfFormat.Load(rtf).Sections[0].Body.Paragraphs.First().GetText());
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.FormattingDropped);
    }

    [Fact]
    public void ComparisonSeesAChangedAnnotation()
    {
        (Document original, _) = Sample();
        (Document revised, Ruby ruby) = Sample();
        ruby.Text = "TOO KYO";

        TokenTable table = new(false, false, false);
        ParagraphTokens a = ParagraphTokens.Create(original.Sections[0].Body.Paragraphs.First(), table);
        ParagraphTokens b = ParagraphTokens.Create(revised.Sections[0].Body.Paragraphs.First(), table);
        ParagraphTokens same = ParagraphTokens.Create(Sample().Document.Sections[0].Body.Paragraphs.First(), table);

        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(a.Key, same.Key);
    }

    private static byte[] SaveDocx(Document document)
    {
        using MemoryStream stream = new();
        DocxFormat.Save(document, stream);
        return stream.ToArray();
    }

    private static XElement DocxPart(byte[] package, string name)
    {
        using ZipArchive zip = new(new MemoryStream(package));
        using Stream stream = zip.GetEntry(name)!.Open();
        return XElement.Load(stream);
    }
}
