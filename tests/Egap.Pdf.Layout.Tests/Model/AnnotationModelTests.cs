using System.Numerics;

using Egami;
using Egami.Drawing;
using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Pdf.Layout.Tests.Synthetic;

namespace Egap.Pdf.Layout.Tests.Model;

/// <summary>Links, comments, forms and scans (Build Plan task 13, design Q9).</summary>
public sealed class AnnotationModelTests
{
    private static readonly SolidPaint Ink = new(new RgbaF32(0, 0, 0, 1));

    [Fact]
    public void LinksBecomeHyperlinksAndBookmarks()
    {
        using PdfDocument pdf = PdfDocument.Open(Make(canvas =>
        {
            canvas.DrawText("Read the full report on the web for details.", new Vector2(72, 100), SyntheticDocument.Body, Ink);
            canvas.DrawAnnotation(new RectF(130, 90, 60, 14), Annotation.Url("https://example.com/report"));
            canvas.DrawText("See the appendix on the next page.", new Vector2(72, 140), SyntheticDocument.Body, Ink);
            canvas.DrawAnnotation(new RectF(110, 130, 60, 14), Annotation.LinkToDestination("appendix"));
        }, canvas =>
        {
            canvas.DrawAnnotation(new RectF(72, 90, 1, 1), Annotation.NamedDestination("appendix"));
            canvas.DrawText("Appendix with the numbers behind the report.", new Vector2(72, 100), SyntheticDocument.Body, Ink);
        }));

        Document model = PdfToDocument.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        List<Hyperlink> links = [.. model.Descendants<Hyperlink>()];
        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/report", links[0].Address);
        Assert.Contains("report", links[0].GetText(), StringComparison.Ordinal);
        string anchor = Assert.IsType<string>(links[1].Anchor);
        BookmarkStart mark = Assert.Single(model.Descendants<BookmarkStart>(), b => b.Name == anchor);
        Assert.Contains("Appendix with", mark.GetAncestor<Paragraph>()!.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void NotesBecomeCommentsAndFormValuesText()
    {
        byte[] bytes = Make(canvas =>
        {
            canvas.DrawText("The budget for next year is under review.", new Vector2(72, 100), SyntheticDocument.Body, Ink);
            canvas.DrawText("Name:", new Vector2(72, 200), SyntheticDocument.Body, Ink);
        });
        using (PdfDocument edit = PdfDocument.Open(bytes))
        {
            PdfPage page = edit.Pages[0];
            double height = page.MediaBox.Height;
            PdfDictionary note = new();
            note.Set("Type", (PdfName)"Annot");
            note.Set("Subtype", (PdfName)"Text");
            note.Set("Rect", new PdfRectangle(200, height - 105, 220, height - 85).ToArray());
            note.Set("Contents", PdfString.FromText("Check the totals"));
            note.Set("T", PdfString.FromText("Ada"));
            PdfDictionary field = new();
            field.Set("Type", (PdfName)"Annot");
            field.Set("Subtype", (PdfName)"Widget");
            field.Set("FT", (PdfName)"Tx");
            field.Set("T", PdfString.FromText("name"));
            field.Set("V", PdfString.FromText("Ada Lovelace"));
            field.Set("Rect", new PdfRectangle(120, height - 205, 300, height - 190).ToArray());
            PdfReference noteRef = edit.Objects.Add(note), fieldRef = edit.Objects.Add(field);
            page.Dictionary.Set("Annots", new PdfArray([noteRef, fieldRef]));
            PdfDictionary form = new();
            form.Set("Fields", new PdfArray([fieldRef]));
            edit.Catalog.Set("AcroForm", form);
            using MemoryStream saved = new();
            edit.Save(saved);
            bytes = saved.ToArray();
        }

        using PdfDocument pdf = PdfDocument.Open(bytes);
        Document model = PdfToDocument.Convert(pdf, cancellationToken: TestContext.Current.CancellationToken);

        Comment comment = Assert.Single(model.Descendants<Comment>());
        Assert.Equal("Ada", comment.Author);
        Assert.Equal("Check the totals", comment.Story.GetText().Trim());
        Assert.Contains("The budget", comment.GetAncestor<Paragraph>()!.GetText(), StringComparison.Ordinal);
        Assert.Contains(model.Descendants<Shape>(), s => s.TextBox!.GetText().Contains("Ada Lovelace", StringComparison.Ordinal));
        DocxFormat.Save(model, new MemoryStream());
    }

    [Theory]
    [InlineData(PdfToDocumentMode.Flow)]
    [InlineData(PdfToDocumentMode.Exact)]
    public void AScannedPageIsAPictureWithAWarning(PdfToDocumentMode mode)
    {
        using PdfDocument pdf = PdfDocument.Open(Make(canvas =>
        {
            using Raster<Rgba8> scan = Raster.Create<Rgba8>(60, 80, new Rgba8(240, 240, 230, 255));
            canvas.DrawImage(scan, new RectF(0, 0, 595, 842));
        }));
        List<PdfToDocumentWarning> warnings = [];

        Document model = PdfToDocument.Convert(pdf, new PdfToDocumentOptions { Mode = mode, OnWarning = warnings.Add }, TestContext.Current.CancellationToken);

        Assert.Equal(PdfToDocumentWarningKind.ScannedPage, Assert.Single(warnings).Kind);
        Assert.Single(model.Descendants<Documents.Picture>());
    }

    private static byte[] Make(params Action<PdfCanvas>[] pages)
    {
        using PdfDocument created = PdfDocument.Create();
        foreach (Action<PdfCanvas> draw in pages)
        {
            PdfPage page = created.Pages.AddBlank(new PdfRectangle(0, 0, 595, 842));
            using PdfCanvas canvas = new(page);
            draw(canvas);
        }

        using MemoryStream saved = new();
        created.Save(saved);
        return saved.ToArray();
    }
}
