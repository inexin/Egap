using System.Numerics;
using System.Text.RegularExpressions;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Pdf.Conversion.Tests.Synthetic;

namespace Egap.Pdf.Conversion.Tests.Model;

/// <summary>Output modes (Build Plan task 12): Flow with positioned frames for what cannot flow, Exact, the confidence threshold.</summary>
public sealed partial class OutputModeTests
{
    private static readonly SolidPaint Ink = new(new RgbaF32(0, 0, 0, 1));

    public static TheoryData<string> Scenes => [.. SyntheticScenes.All.Keys];

    [Theory]
    [MemberData(nameof(Scenes))]
    public void ExactModePutsEveryLineInAFrameWhereItWas(string scene)
    {
        SyntheticDocument doc = SyntheticScenes.All[scene]();
        using PdfDocument pdf = PdfDocument.Open(doc.Save());

        Document model = PdfToDocument.Convert(pdf, new PdfToDocumentOptions { Mode = PdfToDocumentMode.Exact }, TestContext.Current.CancellationToken);

        // One anchor paragraph a page, the later ones starting a page each.
        List<Paragraph> anchors = [.. model.Sections.SelectMany(s => s.Body.Paragraphs)];
        Assert.Equal(doc.PageCount, anchors.Count);
        Assert.All(anchors.Skip(1), a => Assert.True(a.Format.PageBreakBefore));
        Assert.All(model.Sections, s => Assert.Equal(0, s.PageSetup.MarginLeft));

        string text = Squash(string.Join(" ", model.Descendants<Paragraph>().Select(p => p.GetText())));
        List<string> missing = [.. doc.Blocks.SelectMany(b => b.Lines).Select(l => Squash(l.Text).TrimEnd('-', '\u00AD')).Where(t => t.Length > 0 && !text.Contains(t, StringComparison.Ordinal))];
        Assert.True(missing.Count == 0, $"{scene}: missing {string.Join(" | ", missing.Take(5))}");

        // Each line's frame sits over the line, Word's text box inset aside.
        TruthLine first = doc.Blocks.Where(b => b.Page == 0).SelectMany(b => b.Lines).MinBy(l => l.Box.Top)!;
        Shape frame = model.Descendants<Shape>().First(s => Squash(s.TextBox!.GetText()) == Squash(first.Text));
        Assert.Equal(first.Box.Left - 7.2, frame.Floating!.HorizontalOffset, 0);
        Assert.Equal(HorizontalAnchor.Page, frame.Floating.HorizontalAnchor);

        using MemoryStream docx = new();
        DocxFormat.Save(model, docx);
    }

    [Fact]
    public void ExactModeKeepsTablesAndFiguresInPlace()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.Tables().Save());

        Document model = PdfToDocument.Convert(pdf, new PdfToDocumentOptions { Mode = PdfToDocumentMode.Exact }, TestContext.Current.CancellationToken);

        Assert.Equal(3, model.Descendants<Table>().Count());
        Assert.All(model.Descendants<Table>(), t => Assert.IsType<TextBoxStory>(t.Parent));
    }

    [Fact]
    public void ABlockUnderTheConfidenceThresholdStaysWhereItWas()
    {
        using PdfDocument pdf = PdfDocument.Open(SyntheticScenes.OneColumn().Save());
        LayoutBlock target = PdfLayoutAnalyzer.Analyze(pdf, 0).Blocks.First(b => b.Role == LayoutRole.Text);
        PdfLayoutOptions unsure = new() { RegionDetector = new Fixed(new LayoutRegionHint(LayoutRole.Text, target.Bounds, 0.3)) };

        Document framed = PdfToDocument.Convert(pdf, new PdfToDocumentOptions { Layout = unsure }, TestContext.Current.CancellationToken);
        Document flowed = PdfToDocument.Convert(pdf, new PdfToDocumentOptions { Layout = unsure, ConfidenceThreshold = 0 }, TestContext.Current.CancellationToken);

        Shape frame = Assert.Single(framed.Descendants<Shape>());
        Assert.Equal(Squash(target.Text), Squash(frame.TextBox!.GetText().Replace("\v", " ", StringComparison.Ordinal)));
        Assert.Equal(target.Bounds.Y - 3.6, frame.Floating!.VerticalOffset, 0);
        Assert.Equal(WrapMode.Square, frame.Floating.Wrap);
        Assert.DoesNotContain(framed.Sections[0].Body.Paragraphs, p => p.GetText().Trim() == target.Text.Trim());
        Assert.Empty(flowed.Descendants<Shape>());
    }

    [Fact]
    public void APageOfScatteredLabelsKeepsTheLabelsInPlace()
    {
        using PdfDocument created = PdfDocument.Create();
        PdfPage blank = created.Pages.AddBlank(new PdfRectangle(0, 0, 595, 842));
        using (PdfCanvas canvas = new(blank))
        {
            for (int i = 0; i < 20; i++)
            {
                canvas.DrawText($"Node {i}", new Vector2(40 + (i * 97 % 460), 80 + (i * 37)), SyntheticDocument.Body, Ink);
            }
        }

        using MemoryStream saved = new();
        created.Save(saved);
        using PdfDocument pdf = PdfDocument.Open(saved.ToArray());

        Document model = PdfToDocument.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(20, model.Descendants<Shape>().Count());
    }

    private static string Squash(string text)
    {
        return Whitespace().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed class Fixed(params LayoutRegionHint[] hints) : ILayoutRegionDetector
    {
        public IReadOnlyList<LayoutRegionHint> Detect(PdfPage page)
        {
            return hints;
        }
    }
}
