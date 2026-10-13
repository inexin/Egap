using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Annotations;

/// <summary>Annotations (12.5): typed reading per Q10, add, edit and delete with /P, /Annots and popups kept in step; Phase 2 task 12.</summary>
public sealed class AnnotationTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>
    /// Page 3 lists: 10 a note with popup 11 and reply 12, 13 a link to a named destination, 14 a highlight, 15 an ink
    /// annotation, 16 an unknown subtype; then a number, 10 again and a direct square.
    /// </summary>
    private static byte[] File()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Names [(there) [3 0 R /Fit]] >> >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [10 0 R 11 0 R 12 0 R 13 0 R 14 0 R 15 0 R 16 0 R 7 10 0 R"
                + " << /Type /Annot /Subtype /Square /Rect [50 50 10 10] /IC [1] /BS << /W 2 /S /D /D [4 2] >> >>] >>")
            .Object(10, "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /Contents (Note) /T (Ann) /Subj (Re) /NM (n1) /F 28 /C [1 0 0]"
                + " /M (D:20260102030405Z) /CreationDate (D:20260101) /CA 0.5 /Open true /Name /Comment /Popup 11 0 R /P 3 0 R >>")
            .Object(11, "<< /Type /Annot /Subtype /Popup /Rect [40 40 140 100] /Parent 10 0 R /Open true >>")
            .Object(12, "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] /IRT 10 0 R /RT /Group /State (Accepted) /StateModel (Review) >>")
            .Object(13, "<< /Type /Annot /Subtype /Link /Rect [0 0 50 10] /Dest (there) /H /P /Border [0 0 3] >>")
            .Object(14, "<< /Type /Annot /Subtype /Highlight /Rect [0 0 100 20] /QuadPoints [0 20 100 20 0 0 100 0] /C [0 0 0 1] >>")
            .Object(15, "<< /Type /Annot /Subtype /Ink /Rect [0 0 100 100] /InkList [[0 0 10 10 20 0] 5 [1 2 3]] >>")
            .Object(16, "<< /Type /Annot /Subtype /Sound /Rect [0 0 10 10] >>");
        return file.StartXref(file.XrefTable("/Size 17 /Root 1 0 R")).ToArray();
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void ListsAnnotationsOnceTypedBySubtype()
    {
        using PdfDocument pdf = PdfDocument.Open(File(), Strict);
        PdfAnnotationCollection annotations = pdf.Pages[0].Annotations;

        Assert.Equal(8, annotations.Count);
        Assert.IsType<PdfTextAnnotation>(annotations[0]);
        Assert.IsType<PdfPopupAnnotation>(annotations[1]);
        Assert.IsType<PdfLinkAnnotation>(annotations[3]);
        Assert.IsType<PdfHighlightAnnotation>(annotations[4]);
        Assert.IsType<PdfInkAnnotation>(annotations[5]);
        Assert.Equal(typeof(PdfAnnotation), annotations[6].GetType());
        Assert.Equal("Sound", annotations[6].Subtype);
        Assert.Null(annotations[7].Reference);
        Assert.Equal(new PdfReference(10, 0), annotations[0].Reference);
        Assert.Equal(annotations[0], pdf.Pages[0].Annotations[0]);
        Assert.Equal(pdf.Pages[0], annotations[0].Page);
    }

    [Fact]
    public void ReadsCommonAndMarkupProperties()
    {
        using PdfDocument pdf = PdfDocument.Open(File(), Strict);
        PdfTextAnnotation note = (PdfTextAnnotation)pdf.Pages[0].Annotations[0];

        Assert.Equal(new PdfRectangle(10, 10, 30, 30), note.Rectangle);
        Assert.Equal(("Note", "n1", "Ann", "Re"), (note.Contents, note.Name, note.Author, note.Subject));
        Assert.Equal(PdfAnnotationFlags.Print | PdfAnnotationFlags.NoZoom | PdfAnnotationFlags.NoRotate, note.Flags);
        Assert.Equal(new PdfRgbColor(1, 0, 0), note.Color);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), note.ModificationDate);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), note.CreationDate);
        Assert.Equal(0.5, note.Opacity);
        Assert.True(note.IsOpen);
        Assert.Equal("Comment", note.Icon);
        Assert.Equal(new PdfRectangle(40, 40, 140, 100), note.Popup!.Rectangle);
        Assert.Equal(note, note.Popup.Parent);
        Assert.True(note.Popup.IsOpen);

        PdfTextAnnotation reply = (PdfTextAnnotation)pdf.Pages[0].Annotations[2];
        Assert.Equal(note, reply.InReplyTo);
        Assert.Equal(PdfReplyType.Group, reply.ReplyType);
        Assert.Equal(("Accepted", "Review"), (reply.State, reply.StateModel));
        Assert.Equal([reply], pdf.Pages[0].Annotations.GetReplies(note));
        Assert.Equal(1, reply.Opacity);
        Assert.Equal("Note", reply.Icon);
    }

    [Fact]
    public void ReadsSubtypeProperties()
    {
        using PdfDocument pdf = PdfDocument.Open(File(), Strict);
        PdfAnnotationCollection annotations = pdf.Pages[0].Annotations;

        PdfLinkAnnotation link = (PdfLinkAnnotation)annotations[3];
        Assert.Equal(PdfDestination.ToPage(pdf.Pages[0]), link.Destination);
        Assert.Equal(PdfHighlightMode.Push, link.HighlightMode);
        Assert.Equal(3, link.BorderWidth);
        Assert.Null(PdfAnnotation.FromDictionary(link.Dictionary) is PdfLinkAnnotation unattached ? unattached.Destination : null);

        PdfHighlightAnnotation highlight = (PdfHighlightAnnotation)annotations[4];
        Assert.Equal(PdfTextMarkupAnnotation.Quad(new PdfRectangle(0, 0, 100, 20)), highlight.QuadPoints);
        Assert.Equal(new PdfRgbColor(0, 0, 0), highlight.Color);

        PdfInkAnnotation ink = (PdfInkAnnotation)annotations[5];
        Assert.Equal(2, ink.Strokes.Count);
        Assert.Equal([new PdfPoint(0, 0), new PdfPoint(10, 10), new PdfPoint(20, 0)], ink.Strokes[0]);
        Assert.Equal([new PdfPoint(1, 2)], ink.Strokes[1]);

        PdfSquareAnnotation square = (PdfSquareAnnotation)annotations[7];
        Assert.Equal(new PdfRectangle(10, 10, 50, 50), square.Rectangle);
        Assert.Equal(new PdfRgbColor(1, 1, 1), square.InteriorColor);
        Assert.Equal((2.0, PdfBorderStyle.Dashed), (square.BorderWidth, square.BorderStyle));
        Assert.Equal([4.0, 2.0], square.BorderDash);
        Assert.Null(square.Color);
        Assert.Equal(1, annotations[6].BorderWidth);
    }

    [Fact]
    public void AddsAnnotationsWithPageAndPrintFlag()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfPage page = pdf.Pages[0];

        PdfSquareAnnotation square = page.Annotations.Add(new PdfSquareAnnotation(new PdfRectangle(5, 5, 25, 15)) { InteriorColor = new PdfRgbColor(0, 1, 0), Author = "Me" });
        PdfLineAnnotation line = page.Annotations.Add(new PdfLineAnnotation(new PdfPoint(0, 0), new PdfPoint(100, 50)) { EndEnding = PdfLineEnding.OpenArrow });
        PdfLinkAnnotation link = page.Annotations.Add(new PdfLinkAnnotation(new PdfRectangle(0, 0, 10, 10), new PdfUriAction("https://example.com")));
        page.Annotations.Add(new PdfStrikeOutAnnotation([.. PdfTextMarkupAnnotation.Quad(new PdfRectangle(0, 0, 10, 5)), .. PdfTextMarkupAnnotation.Quad(new PdfRectangle(0, 10, 10, 15))]));
        page.Annotations.Add(new PdfInkAnnotation([[new PdfPoint(1, 1), new PdfPoint(9, 9)]]));
        PdfPopupAnnotation popup = page.Annotations.AddPopup(square, new PdfRectangle(100, 100, 190, 160), open: true);

        Assert.NotNull(square.Reference);
        Assert.Equal(page, square.Page);
        Assert.Equal(PdfAnnotationFlags.Print, square.Flags);
        Assert.Equal(popup, square.Popup);
        // The drawn appearance grows the rectangle to hold the stroke and an arrow ending: half the width, the ending size of 4 and 1.
        Assert.Equal(new PdfRectangle(-5.5, -5.5, 105.5, 55.5), line.Rectangle);

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        PdfAnnotationCollection annotations = reopened.Pages[0].Annotations;
        Assert.Equal(14, annotations.Count);
        PdfSquareAnnotation readSquare = (PdfSquareAnnotation)annotations[8];
        Assert.Equal(("Me", new PdfRgbColor(0, 1, 0)), (readSquare.Author, readSquare.InteriorColor));
        Assert.Equal(readSquare, readSquare.Popup!.Parent);
        PdfLineAnnotation readLine = (PdfLineAnnotation)annotations[9];
        Assert.Equal((new PdfPoint(0, 0), new PdfPoint(100, 50), PdfLineEnding.None, PdfLineEnding.OpenArrow), (readLine.Start, readLine.End, readLine.StartEnding, readLine.EndEnding));
        Assert.Equal("https://example.com", Assert.IsType<PdfUriAction>(((PdfLinkAnnotation)annotations[10]).Action).Uri);
        Assert.Equal(8, ((PdfStrikeOutAnnotation)annotations[11]).QuadPoints.Count);
        Assert.Equal(new PdfRectangle(0, 0, 10, 15), annotations[11].Rectangle);
        Assert.IsType<PdfPopupAnnotation>(annotations[13]);
        Assert.All(annotations.Skip(8), a => Assert.Equal(reopened.Pages[0], a.Page));
        Assert.Equal(0, link.BorderWidth);
    }

    [Fact]
    public void AddsTheFirstAnnotationToAPageWithoutAnnots()
    {
        using PdfDocument pdf = PdfDocument.Create();
        PdfPage page = pdf.Pages.AddBlank();

        PdfTextAnnotation note = page.Annotations.Add(new PdfTextAnnotation(new PdfRectangle(0, 0, 20, 20), "Hi"));

        Assert.Equal([note], page.Annotations);
        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        Assert.Equal("Hi", reopened.Pages[0].Annotations.Single().Contents);
    }

    [Fact]
    public void RefusesToAddAnAnnotationTwice()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfCaretAnnotation caret = new(new PdfRectangle(0, 0, 5, 5));
        PdfCaretAnnotation added = pdf.Pages[0].Annotations.Add(caret);
        Assert.Same(caret, added);
        Assert.NotNull(caret.Reference);
        Assert.Equal(pdf.Pages[0], caret.Page);

        Assert.Throws<ArgumentException>(() => pdf.Pages[0].Annotations.Add(caret));
        Assert.Throws<ArgumentException>(() => pdf.Pages[0].Annotations.Add(added));
        Assert.Throws<ArgumentException>(() => pdf.Pages[0].Annotations.Add(pdf.Pages[0].Annotations[0]));
        Assert.Throws<ArgumentException>(() => new PdfHighlightAnnotation([new PdfPoint(0, 0)]));
    }

    [Fact]
    public void RemovingAnAnnotationTakesItsPopupAndRepliesToo()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfAnnotationCollection annotations = pdf.Pages[0].Annotations;

        Assert.True(annotations.Remove(annotations[0]));

        Assert.Equal(["Link", "Highlight", "Ink", "Sound", "Square"], annotations.Select(a => a.Subtype));
        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        Assert.Equal(5, reopened.Pages[0].Annotations.Count);
        PdfArray annots = reopened.Pages[0].Dictionary.Get<PdfArray>("Annots")!;
        Assert.Equal(6, annots.Count);
        Assert.Equal(new PdfReference(13, 0), annots[0]);
        Assert.Equal(7, annots.GetInt(4));
    }

    [Fact]
    public void RemovingAPopupTakesItOffItsAnnotation()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfAnnotationCollection annotations = pdf.Pages[0].Annotations;
        PdfTextAnnotation note = (PdfTextAnnotation)annotations[0];

        Assert.True(annotations.Remove(note.Popup!));

        Assert.Null(note.Popup);
        Assert.Equal(7, annotations.Count);
        Assert.False(annotations.Remove(new PdfTextAnnotation(default)));
    }

    [Fact]
    public void ReplacesAPopup()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfAnnotationCollection annotations = pdf.Pages[0].Annotations;
        PdfTextAnnotation note = (PdfTextAnnotation)annotations[0];

        PdfPopupAnnotation popup = annotations.AddPopup(note, new PdfRectangle(0, 0, 50, 50));

        Assert.Equal(popup, note.Popup);
        Assert.Equal(1, annotations.Count(a => a is PdfPopupAnnotation));
        Assert.Throws<ArgumentException>(() => annotations.AddPopup(new PdfTextAnnotation(default), default));
    }

    [Fact]
    public void EditsProperties()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfAnnotationCollection annotations = pdf.Pages[0].Annotations;
        PdfTextAnnotation reply = (PdfTextAnnotation)annotations[2];
        PdfLinkAnnotation link = (PdfLinkAnnotation)annotations[3];
        PdfSquareAnnotation square = (PdfSquareAnnotation)annotations[7];

        reply.InReplyTo = null;
        link.Destination = PdfDestination.ToPage(pdf.Pages[0], PdfDestinationFit.FitH) with { Top = 100 };
        link.HighlightMode = PdfHighlightMode.Invert;
        square.Rectangle = new PdfRectangle(20, 20, 0, 0);
        square.BorderWidth = 4;
        square.BorderStyle = PdfBorderStyle.Solid;
        square.Flags |= PdfAnnotationFlags.Hidden;
        square.Contents = "Box";
        square.Opacity = 1;
        square.ModificationDate = new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        Assert.Throws<ArgumentException>(() => reply.InReplyTo = square);
        Assert.Throws<ArgumentOutOfRangeException>(() => square.Opacity = 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => square.BorderWidth = -1);

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        PdfAnnotationCollection read = reopened.Pages[0].Annotations;
        Assert.Null(((PdfTextAnnotation)read[2]).InReplyTo);
        Assert.Equal(PdfReplyType.Reply, ((PdfTextAnnotation)read[2]).ReplyType);
        Assert.Equal(100, ((PdfLinkAnnotation)read[3]).Destination!.Top);
        Assert.False(read[3].Dictionary.ContainsKey("H"));
        PdfSquareAnnotation readSquare = (PdfSquareAnnotation)read[7];
        Assert.Equal((new PdfRectangle(0, 0, 20, 20), 4.0, PdfBorderStyle.Solid), (readSquare.Rectangle, readSquare.BorderWidth, readSquare.BorderStyle));
        Assert.True(readSquare.Flags.HasFlag(PdfAnnotationFlags.Hidden));
        Assert.Equal("Box", readSquare.Contents);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 0, 0, 0, TimeSpan.Zero), readSquare.ModificationDate);
    }

    [Fact]
    public void AddsAFileAttachmentAnnotation()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        PdfFileAttachmentAnnotation added = pdf.Pages[0].Annotations.AddFileAttachment(new PdfRectangle(0, 0, 10, 20), "data.csv", "a,b\n1,2\n"u8, "text/csv");

        Assert.Empty(pdf.Attachments);
        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        PdfFileAttachmentAnnotation read = Assert.IsType<PdfFileAttachmentAnnotation>(reopened.Pages[0].Annotations[^1]);
        Assert.Equal(("data.csv", "text/csv", "PushPin", "data.csv"), (read.Attachment!.FileName, read.Attachment.MimeType, read.Icon, read.Contents));
        Assert.Equal("a,b\n1,2\n"u8.ToArray(), read.Attachment.GetBytes());
        Assert.True(read.Attachment.VerifyChecksum());
        Assert.Equal(added, pdf.Pages[0].Annotations[^1]);
    }

    [Fact]
    public void PicksTheAppearanceForTheState()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [4 0 R] >>")
            .Object(4, "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AS /On /AP << /N << /On 5 0 R /Off 6 0 R >> /D 6 0 R >> /H /T >>")
            .Stream(5, "", "q Q"u8.ToArray())
            .Stream(6, "", "Q q"u8.ToArray());
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 7 /Root 1 0 R")).ToArray(), Strict);
        PdfWidgetAnnotation widget = Assert.IsType<PdfWidgetAnnotation>(pdf.Pages[0].Annotations[0]);

        Assert.Equal("q Q"u8.ToArray(), widget.GetAppearance()!.GetDecodedBytes());
        Assert.Equal("q Q"u8.ToArray(), widget.GetAppearance(PdfAppearanceKind.Rollover)!.GetDecodedBytes());
        Assert.Equal("Q q"u8.ToArray(), widget.GetAppearance(PdfAppearanceKind.Down)!.GetDecodedBytes());
        Assert.Equal(PdfHighlightMode.Toggle, widget.HighlightMode);
        widget.AppearanceState = "Off";
        Assert.Equal("Q q"u8.ToArray(), widget.GetAppearance()!.GetDecodedBytes());
        widget.AppearanceState = null;
        Assert.Null(widget.GetAppearance());
    }

    [Fact]
    public void APageWithBrokenAnnotsHasNone()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots 9 0 R >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 4 /Root 1 0 R")).ToArray());

        Assert.Empty(pdf.Pages[0].Annotations);
        pdf.Pages[0].Annotations.Add(new PdfCircleAnnotation(new PdfRectangle(0, 0, 5, 5)));
        Assert.Single(pdf.Pages[0].Annotations);
    }

    [Fact]
    public void AnAnnotationOnAPageFoundByScanningSurvivesARewrite()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog >>")
            .Object(3, "<< /Type /Page /MediaBox [0 0 10 10] >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 4 /Root 1 0 R")).ToArray());

        pdf.Pages[0].Annotations.Add(new PdfTextAnnotation(new PdfRectangle(0, 0, 5, 5), "Kept"));

        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
        using PdfDocument reopened = PdfDocument.Open(output.ToArray(), Strict);
        Assert.Equal("Kept", reopened.Pages.Single().Annotations.Single().Contents);
    }

    [Fact]
    public void AnAnnotationOnADirectPageOfABrokenTreeSurvivesARewrite()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Object(2, "<< /Kids [<< /Parent 2 0 R /MediaBox [0 0 10 10] >>] >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 3 /Root 1 0 R")).ToArray());
        PdfPage page = pdf.Pages[0];

        page.Annotations.Add(new PdfTextAnnotation(new PdfRectangle(0, 0, 5, 5), "Kept"));
        page.Annotations.Add(new PdfTextAnnotation(new PdfRectangle(0, 0, 5, 5), "Too"));

        Assert.Equal(page, pdf.Pages[0]);
        using MemoryStream output = new();
        pdf.Save(output, new PdfSaveOptions { Mode = PdfSaveMode.Rewrite });
        using PdfDocument reopened = PdfDocument.Open(output.ToArray(), Strict);
        Assert.Equal(["Kept", "Too"], reopened.Pages.Single().Annotations.Select(a => a.Contents));
        PdfAnnotation annotation = reopened.Pages[0].Annotations[0];
        Assert.Equal(reopened.Pages[0], annotation.Page);
    }

    [Fact]
    public void AShortLineEndingArrayReadsAsNone()
    {
        PdfLineAnnotation line = (PdfLineAnnotation)PdfAnnotation.FromDictionary(new PdfDictionary
        {
            ["Subtype"] = PdfName.Get("Line"),
            ["LE"] = new PdfArray([PdfName.Get("Circle")]),
        });

        Assert.Equal((PdfLineEnding.Circle, PdfLineEnding.None), (line.StartEnding, line.EndEnding));
        line.EndEnding = PdfLineEnding.Slash;
        Assert.Equal((PdfLineEnding.Circle, PdfLineEnding.Slash), (line.StartEnding, line.EndEnding));
    }
}
