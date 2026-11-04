using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace Egap.Documents.Tests;

public class RtfWriterTests
{
    public static TheoryData<string> RtfSeeds => [.. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "rtf"), "own-*.rtf").Select(f => Path.GetFileName(f))];

    public static TheoryData<string> DocxSeeds =>
    [
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "docx-xml"), "*.xml").Select(f => Path.GetFileName(f)),
        .. Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "files", "docx-package"), "*.docx").Select(f => Path.GetFileName(f)),
    ];

    private static byte[] Save(Document document, out IReadOnlyList<ConversionWarning> warnings, RtfSaveOptions? options = null)
    {
        using MemoryStream stream = new();
        warnings = RtfFormat.Save(document, stream, options);
        byte[] bytes = stream.ToArray();
        AssertBalanced(bytes);
        if (Environment.GetEnvironmentVariable("EGAP_RTF_OUTPUT") is { Length: > 0 } folder)
        {
            // For opening every file the tests write in other applications.
            File.WriteAllBytes(Path.Combine(folder, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..16] + ".rtf"), bytes);
        }

        return bytes;
    }

    /// <summary>Every group the file opens it closes, and none closes before the file's own: other readers give up on less.</summary>
    private static void AssertBalanced(byte[] rtf)
    {
        int depth = 0;
        for (int i = 0; i < rtf.Length; i++)
        {
            switch (rtf[i])
            {
                case (byte)'\\':
                    i++;
                    break;
                case (byte)'{':
                    depth++;
                    break;
                case (byte)'}':
                    depth--;
                    Assert.True(depth > 0 || i == rtf.Length - 1 || rtf.AsSpan(i + 1).IndexOfAnyExcept("\r\n"u8) < 0, $"The file's group closes at {i} of {rtf.Length}.");
                    break;
            }
        }

        Assert.Equal(0, depth);
    }

    private static byte[] Save(Document document, RtfSaveOptions? options = null) => Save(document, out _, options);

    private static Document Reread(Document document, out IReadOnlyList<ConversionWarning> warnings)
    {
        using MemoryStream stream = new(Save(document));
        return RtfFormat.Load(stream, out warnings);
    }

    private static Document Reread(Document document) => Reread(document, out _);

    [Theory]
    [MemberData(nameof(RtfSeeds))]
    public void SeedsRoundTrip(string name)
    {
        Document first = RtfFormat.Load(Path.Combine(AppContext.BaseDirectory, "files", "rtf", name), out IReadOnlyList<ConversionWarning> readWarnings);
        Document second = Reread(first, out IReadOnlyList<ConversionWarning> warnings);
        Same(Dump(first), Dump(second), name);

        // Reading the written file warns about nothing new: what the first read kept as islands warns again, nothing else.
        Assert.All(warnings, w => Assert.Contains(readWarnings, r => r.Kind == w.Kind && r.Message == w.Message));

        // Writing what was read back gives the same file.
        Assert.Equal(Encoding.Latin1.GetString(Save(first)), Encoding.Latin1.GetString(Save(second)));
    }

    [Theory]
    [MemberData(nameof(DocxSeeds))]
    public void DocxSavedAsRtfReadsTheSame(string name)
    {
        Document docx = LoadDocx(name);
        Document rtf = Reread(docx);
        AsRtfWrites(docx);
        Same(Effective(docx, docx), Effective(rtf, docx), "x-" + name);
    }

    [Theory]
    [MemberData(nameof(DocxSeeds))]
    public void DocxSavedAsRtfRoundTrips(string name)
    {
        Document first = Reread(LoadDocx(name));
        Document second = Reread(first, out IReadOnlyList<ConversionWarning> warnings);
        Same(Dump(first), Dump(second), "r-" + name);
        Assert.DoesNotContain(warnings, w => w.Kind != ConversionWarningKind.DrawingDropped);
    }

    [Fact]
    public void TextIsSevenBitWithUnicodeEscapes()
    {
        Document document = new();
        string text = "Plain {braces} back\\slash, café, Ωmega, 中文字, emoji 😀🎉, nbsp\u00A0here, soft\u00ADhyphen, nb\u2011hyphen, “quotes” – €";
        Body(document).AddParagraph(text);
        byte[] bytes = Save(document, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Empty(warnings);
        Assert.DoesNotContain(bytes, b => b >= 0x80);
        string rtf = Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("{\\rtf1\\ansi\\ansicpg1252\\uc1", rtf, StringComparison.Ordinal);
        Assert.Contains("caf\\u233\\'e9", rtf, StringComparison.Ordinal);
        Assert.Contains("\\u-10179?\\u-8704?", rtf, StringComparison.Ordinal);
        Assert.Contains("\\u8364\\'80", rtf, StringComparison.Ordinal);

        Document read = RtfFormat.Parse(rtf, out IReadOnlyList<ConversionWarning> readWarnings);
        Assert.Empty(readWarnings);
        Assert.Equal(text, read.Sections[0].Body.Blocks.OfType<Paragraph>().Single().GetText());
    }

    [Fact]
    public void HostileTextIsEscaped()
    {
        Document document = new();
        string text = "\\par \\b {\\rtf1 }} {{ \\\\ \\'41 \\u65 tab\there \u0001\u001F\u007F end";
        Body(document).AddParagraph(text);
        Paragraph paragraph = Body(document).AddParagraph();
        paragraph.AddRun("lone \uD800 high and \uDC00 low");
        Document read = Reread(document);
        List<Paragraph> paragraphs = [.. read.Sections[0].Body.Blocks.OfType<Paragraph>()];
        Assert.Equal(2, paragraphs.Count);
        Assert.Equal(text, paragraphs[0].GetText());
        Assert.Equal("lone \uFFFD high and \uFFFD low", paragraphs[1].GetText());
    }

    [Fact]
    public void HostileNamesAreEscaped()
    {
        Document document = new();
        ParagraphStyle style = document.Styles.AddParagraphStyle("Odd", "Odd;name {with} \\braces");
        Paragraph paragraph = Body(document).AddParagraph();
        paragraph.Style = style;
        Run run = paragraph.AddRun("text");
        run.Format.FontAscii = "Font;with {braces} \\ and;more";
        run.Revision = new Revision(RevisionKind.Insertion, "Author {\\} ;");
        paragraph.Inlines.Add(new BookmarkStart(document, "mark{\\}"));
        paragraph.Inlines.Add(new BookmarkEnd(document, "mark{\\}"));
        document.Properties.Title = "Title {\\} with ;";

        Document read = Reread(document, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Empty(warnings);
        Paragraph back = read.Sections[0].Body.Blocks.OfType<Paragraph>().Single();
        Assert.Equal("Oddname {with} \\braces", back.Style!.Name);
        Assert.Equal("Fontwith {braces} \\ andmore", back.Runs.Single().GetEffectiveFormat().FontAscii);
        // A semicolon ends a name in RTF's tables, so names lose theirs.
        Assert.Equal("Author {\\}", back.Runs.Single().Revision!.Author);
        Assert.Equal("mark{\\}", back.Inlines.OfType<BookmarkStart>().Single().Name);
        Assert.Equal("Title {\\} with ;", read.Properties.Title);
        Assert.Equal(Dump(read), Dump(Reread(read)));
    }

    /// <summary>Inputs the fuzzer found whose RTF, read and written again, came out different.</summary>
    [Theory]
    [InlineData(@"{\rtf1 A{\*\~n\pnlvlbody x}B\par}")]
    [InlineData(@"{\rtf1 A{\*{x}\pard y}B\par}")]
    [InlineData(@"{\rtf1 A{\fo.\p\*\ts11 x}B\par}")]
    [InlineData(@"{\rtf1 A\par{\*\unknownword {x\")]
    [InlineData(@"{\rtf1{\fonttbl{\f0 Times\tab New;}}{\stylesheet{\s1 Head\tab ing;}}\f0 A\par}")]
    [InlineData(@"{\rtf\field\sect")]
    [InlineData(@"{\rtf{\revtbl{Unknown}}t\revdttm0")]
    [InlineData(@"{\rtf\intbl\par")]
    [InlineData(@"{\rtf1\trowd\cellx2000\pard\intbl A\par\itap2 B\nestcell{\*\nesttableprops\trowd\cellx1000\nestrow}\pard\intbl\cell\row}")]
    [InlineData(@"{\rtf\field{\qr{\par\fldrslt}}\par")]
    public void OddInputsRewriteTheSame(string rtf)
    {
        byte[] first = Save(RtfFormat.Parse(rtf));
        byte[] second = Save(RtfFormat.Parse(Encoding.Latin1.GetString(first)));
        Assert.Equal(Encoding.Latin1.GetString(first), Encoding.Latin1.GetString(second));
    }

    [Fact]
    public void EmptyDocumentRoundTrips()
    {
        Document document = new();
        Document read = Reread(document, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Empty(warnings);
        Assert.Single(read.Sections);
        Assert.Equal(Dump(read), Dump(Reread(read)));
    }

    [Fact]
    public void StylesListsAndFormattingRoundTrip()
    {
        Document document = new();
        ParagraphStyle heading = document.Styles.AddParagraphStyle("Heading1", "heading 1");
        heading.RunFormat.Bold = true;
        heading.RunFormat.Size = 16;
        heading.ParagraphFormat.SpaceBefore = 12;
        heading.ParagraphFormat.KeepWithNext = true;
        ParagraphStyle sub = document.Styles.AddParagraphStyle("Heading2", "heading 2");
        sub.BasedOn = heading;
        sub.RunFormat.Bold = false;
        sub.RunFormat.Italic = true;
        heading.Next = document.Styles.DefaultParagraphStyle;
        CharacterStyle strong = document.Styles.AddCharacterStyle("Strong", "Strong");
        strong.RunFormat.Bold = true;
        strong.RunFormat.Color = DocumentColor.FromRgb(new Egami.Pixels.Rgba8(200, 0, 0, 255));

        Body(document).AddParagraph("Title").Style = heading;
        Body(document).AddParagraph("Subtitle").Style = sub;
        Paragraph body = Body(document).AddParagraph();
        body.AddRun("plain ");
        body.AddRun("strong").Style = strong;
        Run bold = body.AddRun(" and bold in a bold style");
        bold.Style = strong;
        bold.Format.Bold = false;
        body.Format.Alignment = ParagraphAlignment.Justify;
        body.Format.LeftIndent = 36;
        body.Format.FirstLineIndent = -18;
        body.Format.LineSpacing = 1.5;
        body.Format.LineSpacingRule = LineSpacingRule.Multiple;

        List numbered = document.Lists.AddNumbered();
        List bullets = document.Lists.AddBulleted();
        for (int i = 0; i < 3; i++)
        {
            Body(document).AddParagraph("item " + i).SetList(numbered, i % 2);
            Body(document).AddParagraph("bullet " + i).SetList(bullets);
        }

        Document first = Reread(document);
        Document second = Reread(first);
        Assert.Equal(Dump(first), Dump(second));

        List<Paragraph> paragraphs = [.. first.Sections[0].Body.Blocks.OfType<Paragraph>()];
        Assert.Equal("heading 1", paragraphs[0].Style!.Name);
        Assert.True(paragraphs[0].Runs.Single().GetEffectiveFormat().Bold);
        Assert.Equal("heading 1", paragraphs[1].Style!.BasedOn!.Name);
        Assert.True(paragraphs[1].Format.IsEmpty);
        Run[] runs = [.. paragraphs[2].Runs];
        Assert.Equal("Strong", runs[1].Style!.Name);
        Assert.True(runs[1].Format.IsEmpty);
        Assert.False(runs[2].Format.Bold);
        Assert.Equal(36, paragraphs[2].Format.LeftIndent);
        Assert.Equal(numbered.Definition.Levels[0].NumberStyle, paragraphs[3].Format.List!.Definition.Levels[0].NumberStyle);
        Assert.Equal(1, paragraphs[5].Format.ListLevel);
        Assert.Equal("1.", paragraphs[3].ListLabel!.Text);
    }

    [Fact]
    public void TablesWithMergesAndNestingRoundTrip()
    {
        Document document = new();
        Table table = Body(document).AddTable(3, 3);
        table.Format.BorderTop = table.Format.BorderBottom = table.Format.BorderLeft = table.Format.BorderRight = new Border(BorderStyle.Single, 0.5);
        table.Format.BorderInsideHorizontal = table.Format.BorderInsideVertical = new Border(BorderStyle.Single, 0.5);
        table.Rows[0].Cells[0].Format.GridSpan = 2;
        table.Rows[0].Cells.RemoveAt(1);
        table.Rows[1].Cells[2].Format.VerticalMerge = VerticalMerge.Restart;
        table.Rows[2].Cells[2].Format.VerticalMerge = VerticalMerge.Continue;
        table.Rows[1].Cells[0].Format.Shading = new Shading(ShadingPattern.Clear, DocumentColor.FromRgb(new Egami.Pixels.Rgba8(220, 230, 240, 255)));
        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < table.Rows[r].Cells.Count; c++)
            {
                table.Rows[r].Cells[c].AddParagraph($"r{r}c{c}");
            }
        }

        Table inner = new(document);
        table.Rows[1].Cells[1].Blocks.Add(inner);
        Row row = inner.AddRow(2);
        row.Cells[0].AddParagraph("inner a");
        row.Cells[1].AddParagraph("inner b");
        Table innermost = new(document);
        row.Cells[1].Blocks.Add(innermost);
        innermost.AddRow(1).Cells[0].AddParagraph("deepest");
        Body(document).AddParagraph("after");

        Document first = Reread(document);
        Assert.Equal(Dump(first), Dump(Reread(first)));
        Table read = first.Sections[0].Body.Blocks.OfType<Table>().Single();
        Assert.Equal(3, read.Rows.Count);
        Table readInner = read.Rows[1].Cells[1].Blocks.OfType<Table>().Single();
        Assert.Equal("deepest", readInner.Rows[0].Cells[1].Blocks.OfType<Table>().Single().Rows[0].Cells[0].GetText().Trim());
        Assert.Equal(VerticalMerge.Restart, read.Rows[1].Cells[2].Format.VerticalMerge);
    }

    [Fact]
    public void FieldsNotesCommentsAndRevisionsRoundTrip()
    {
        Document document = new();
        Paragraph paragraph = Body(document).AddParagraph("Page ");
        paragraph.AddField(FieldKind.Page);
        paragraph.AddRun(" see ");
        paragraph.AddHyperlink("the site", "https://example.com/a?b=\"c\"");
        paragraph.AddRun(" and ");
        paragraph.Inlines.Add(new BookmarkStart(document, "mark"));
        paragraph.AddRun("marked");
        paragraph.Inlines.Add(new BookmarkEnd(document, "mark"));
        paragraph.AddFootnote("A footnote.");
        paragraph.AddFootnote("An endnote.", NoteKind.Endnote);
        Footnote custom = paragraph.AddFootnote("Starred.");
        custom.CustomMark = "*";

        Comment comment = new(document, "Ann", new DateTimeOffset(2026, 3, 4, 5, 6, 0, TimeSpan.Zero)) { Initials = "A" };
        comment.AddParagraph("A comment.");
        Paragraph commented = Body(document).AddParagraph();
        commented.Inlines.Add(new CommentRangeStart(document, comment));
        commented.AddRun("commented");
        commented.Inlines.Add(new CommentRangeEnd(document, comment));
        commented.Inlines.Add(comment);
        _ = comment.AddReply("Bob", "A reply.");

        Paragraph tracked = Body(document).AddParagraph();
        tracked.AddRun("kept ");
        tracked.AddRun("inserted").Revision = new Revision(RevisionKind.Insertion, "Ann", new DateTimeOffset(2026, 1, 2, 3, 4, 0, TimeSpan.Zero));
        tracked.AddRun(" deleted").Revision = new Revision(RevisionKind.Deletion, "Bob");
        Run changed = tracked.AddRun(" bolded");
        changed.Format.Bold = true;
        changed.FormatRevision = new FormatRevision("Ann", null, new RunFormat { Italic = true });
        tracked.MarkRevision = new Revision(RevisionKind.Insertion, "Ann");

        Document first = Reread(document, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Empty(warnings);
        Assert.Equal(Dump(first), Dump(Reread(first)));

        List<Paragraph> paragraphs = [.. first.Sections[0].Body.Blocks.OfType<Paragraph>()];
        Field[] fields = [.. paragraphs[0].Inlines.OfType<FieldStart>().Select(s => s.GetField()!)];
        Assert.Equal(FieldKind.Page, fields[0].Kind);
        Assert.Equal(" HYPERLINK \"https://example.com/a?b=\\\"c\\\"\" ", fields[1].Code);
        Assert.Equal("the site", fields[1].Result);
        Footnote[] notes = [.. paragraphs[0].Inlines.OfType<Footnote>()];
        Assert.Equal([NoteKind.Footnote, NoteKind.Endnote, NoteKind.Footnote], notes.Select(n => n.Kind));
        Assert.Equal("*", notes[2].CustomMark);
        Assert.Equal("A footnote.", notes[0].Story.GetText().Trim());
        Comment[] comments = [.. paragraphs[1].Inlines.OfType<Comment>()];
        Assert.Equal("Ann", comments[0].Author);
        Assert.Equal("A", comments[0].Initials);
        Assert.Equal(new DateTimeOffset(2026, 3, 4, 5, 6, 0, TimeSpan.Zero), comments[0].Date);
        Assert.Equal(comments[0].Id, comments[1].ParentId);
        Run[] runs = [.. paragraphs[2].Runs];
        Assert.Equal(RevisionKind.Insertion, runs[1].Revision!.Kind);
        Assert.Equal(RevisionKind.Deletion, runs[2].Revision!.Kind);
        Assert.Equal("Bob", runs[2].Revision!.Author);
        Assert.True(((RunFormat)runs[3].FormatRevision!.OldFormat).Italic);
        Assert.NotNull(paragraphs[2].MarkRevision);
    }

    [Fact]
    public void TrackedBookmarksAndNotesKeepTheirChange()
    {
        Document document = new();
        Paragraph paragraph = Body(document).AddParagraph("Text");
        paragraph.Inlines.Add(new BookmarkStart(document, "moved") { Revision = new Revision(RevisionKind.Deletion, "Ann") });
        paragraph.AddRun("gone").Revision = new Revision(RevisionKind.Deletion, "Ann");
        paragraph.Inlines.Add(new BookmarkEnd(document, "moved") { Revision = new Revision(RevisionKind.Deletion, "Ann") });
        paragraph.AddFootnote("The note.").Revision = new Revision(RevisionKind.Insertion, "Bob");

        Document back = Reread(document);
        Assert.Equal(RevisionKind.Deletion, back.Descendants<BookmarkStart>().Single().Revision!.Kind);
        Assert.Equal(RevisionKind.Deletion, back.Descendants<BookmarkEnd>().Single().Revision!.Kind);
        Footnote read = back.Descendants<Footnote>().Single();
        Assert.Equal(RevisionKind.Insertion, read.Revision!.Kind);
        Assert.Equal("Bob", read.Revision.Author);
        Assert.All(read.Story.Blocks.OfType<Paragraph>().SelectMany(p => p.Runs), r => Assert.Null(r.Revision));
    }

    [Fact]
    public void FieldAcrossParagraphsKeepsEachParagraphsFormatting()
    {
        Document document = new();
        Paragraph start = Body(document).AddParagraph();
        start.Format.Alignment = ParagraphAlignment.Center;
        start.Inlines.Add(new FieldStart(document));
        start.AddRun(" TOC ");
        start.Inlines.Add(new FieldSeparator(document));
        start.AddRun("entry one");
        Paragraph middle = Body(document).AddParagraph("entry two");
        middle.Format.LeftIndent = 24;
        middle.Inlines.Add(new FieldEnd(document));
        middle.AddRun(" after");
        Paragraph last = Body(document).AddParagraph("last");
        last.Format.Alignment = ParagraphAlignment.Right;

        Document read = Reread(document);
        List<Paragraph> paragraphs = [.. read.Sections[0].Body.Blocks.OfType<Paragraph>()];
        Assert.Equal(3, paragraphs.Count);
        Assert.Equal(ParagraphAlignment.Center, paragraphs[0].Format.Alignment);
        Assert.Equal(24, paragraphs[1].Format.LeftIndent);
        Assert.Null(paragraphs[1].Format.Alignment);
        Assert.Equal(ParagraphAlignment.Right, paragraphs[2].Format.Alignment);
        Assert.Equal(" TOC ", paragraphs[0].Inlines.OfType<FieldStart>().Single().GetField()!.Code);
        Assert.Equal(Dump(read), Dump(Reread(read)));
    }

    [Fact]
    public void PicturesAndShapesRoundTrip()
    {
        Document document = new();
        Paragraph paragraph = Body(document).AddParagraph("picture: ");
        Picture inline = paragraph.AddPicture(new ImageData(Png(4, 2), "image/png"), 72, 36);
        inline.Description = "four by two";
        inline.Crop = (0.25, 0, 0, 0);
        Picture floating = paragraph.AddPicture(new ImageData(Png(2, 2), "image/png"), 50, 50);
        floating.Floating = new FloatingPlacement { HorizontalAnchor = HorizontalAnchor.Page, HorizontalOffset = 100, VerticalOffset = -20, Wrap = WrapMode.Square };
        Shape box = new(document, ShapeKind.Rectangle, 144, 72)
        {
            Fill = DocumentColor.FromRgb(new Egami.Pixels.Rgba8(255, 255, 0, 255)),
            Outline = new Border(BorderStyle.Single, 1.5, DocumentColor.FromRgb(new Egami.Pixels.Rgba8(0, 0, 255, 255))),
            Floating = new FloatingPlacement { HorizontalOffset = 10, VerticalOffset = 20, Wrap = WrapMode.TopAndBottom },
        };
        box.AddTextBox().Blocks.Add(new Paragraph(document));
        ((Paragraph)box.TextBox!.Blocks[0]).AddRun("in the box");
        paragraph.Inlines.Add(box);
        Shape ellipse = new(document, ShapeKind.Ellipse, 30, 30) { Floating = new FloatingPlacement() };
        paragraph.Inlines.Add(ellipse);

        // A GIF has no RTF blip: it goes in as a PNG.
        paragraph.AddPicture(ImageData.FromBytes(Gif()), 10, 10);

        Document first = Reread(document, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Empty(warnings);
        Assert.Equal(Dump(first), Dump(Reread(first)));
        Picture[] pictures = [.. first.Descendants<Picture>()];
        Assert.Equal(3, pictures.Length);
        Assert.Equal(72, pictures[0].Width, 1);
        Assert.Equal(36, pictures[0].Height, 1);
        Assert.Equal(0.25, pictures[0].Crop.Left, 2);
        Assert.Equal("four by two", pictures[0].Description);
        Assert.Equal(inline.Image.Bytes.ToArray(), pictures[0].Image.Bytes.ToArray());
        Assert.Equal(HorizontalAnchor.Page, pictures[1].Floating!.HorizontalAnchor);
        Assert.Equal(-20, pictures[1].Floating!.VerticalOffset);
        Assert.Equal("image/png", pictures[2].Image.ContentType);
        Shape[] shapes = [.. first.Descendants<Shape>()];
        Assert.Equal(2, shapes.Length);
        Assert.Equal("in the box", shapes[0].TextBox!.GetText().Trim());
        Assert.Equal(1.5, shapes[0].Outline!.Width);
        Assert.Equal(WrapMode.TopAndBottom, shapes[0].Floating!.Wrap);
        Assert.Equal(ShapeKind.Ellipse, shapes[1].Kind);
        Assert.Null(shapes[1].Fill);
    }

    [Fact]
    public void SectionsHeadersAndColumnsRoundTrip()
    {
        Document document = new();
        Section first = document.AddSection();
        first.PageSetup.Orientation = PageOrientation.Landscape;
        first.PageSetup.PageWidth = 842;
        first.PageSetup.PageHeight = 595;
        first.PageSetup.ColumnCount = 2;
        first.HeadersFooters.GetOrAdd(HeaderFooterKind.HeaderPrimary).AddParagraph("header");
        first.HeadersFooters.GetOrAdd(HeaderFooterKind.FooterFirst).AddParagraph("first footer");
        first.PageSetup.DifferentFirstPage = true;
        Body(document).AddParagraph("section one");
        Section second = document.AddSection();
        second.Body.AddParagraph("section two");

        Document read = Reread(document);
        Assert.Equal(2, read.Sections.Count);
        Assert.Equal(2, read.Sections[0].PageSetup.ColumnCount);
        Assert.Equal("header", read.Sections[0].HeadersFooters[HeaderFooterKind.HeaderPrimary]!.GetText().Trim());
        Assert.Equal("section two", read.Sections[1].Body.GetText().Trim());
        Assert.Equal(Dump(read), Dump(Reread(read)));
    }

    [Fact]
    public void UnknownGroupsAreKeptOrLeftOut()
    {
        const string rtf = @"{\rtf1\ansi{\*\themedata 0102}{\*\xmlnstbl {\xmlns1 http://x}}\pard Text{\*\myword data} more\par}";
        Document document = RtfFormat.Parse(rtf);
        string kept = Encoding.Latin1.GetString(Save(document));
        Assert.Contains(@"{\*\themedata 0102}", kept, StringComparison.Ordinal);
        Assert.Contains(@"Text}{\*\myword data}", kept, StringComparison.Ordinal);
        string dropped = Encoding.Latin1.GetString(Save(document, new RtfSaveOptions { KeepUnknownGroups = false }));
        Assert.DoesNotContain("themedata", dropped, StringComparison.Ordinal);
        Assert.DoesNotContain("myword", dropped, StringComparison.Ordinal);
        Assert.Equal(Dump(document), Dump(RtfFormat.Parse(kept)));
    }

    [Fact]
    public void DocumentSaveWritesRtfByExtension()
    {
        string path = Path.Combine(Path.GetTempPath(), "egap-rtf-" + Guid.NewGuid().ToString("N") + ".rtf");
        try
        {
            Document document = new();
            Body(document).AddParagraph("saved by extension");
            document.Save(path);
            Assert.Equal(DocumentFormatKind.Rtf, Document.Identify(path));
            Assert.Equal("saved by extension", Document.Load(path).Sections[0].Body.GetText().Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LargeAndDeepModelsSave()
    {
        Document document = new();
        Table big = Body(document).AddTable(300, 70);
        foreach (Cell cell in big.Descendants<Cell>())
        {
            cell.AddParagraph("x");
        }

        Body(document).AddParagraph("between");
        Table outer = Body(document).AddTable(1, 1);
        Cell at = outer.Rows[0].Cells[0];
        for (int i = 0; i < 30; i++)
        {
            Table nested = new(document);
            at.Blocks.Add(nested);
            at = nested.AddRow(1).Cells[0];
        }

        at.AddParagraph("bottom");
        Paragraph fields = Body(document).AddParagraph();
        for (int i = 0; i < 40; i++)
        {
            fields.Inlines.Add(new FieldStart(document));
            fields.AddRun(" QUOTE ");
            fields.Inlines.Add(new FieldSeparator(document));
        }

        fields.AddRun("deep");
        for (int i = 0; i < 40; i++)
        {
            fields.Inlines.Add(new FieldEnd(document));
        }

        // Unbalanced field characters are written as balanced groups.
        Paragraph odd = Body(document).AddParagraph();
        odd.Inlines.Add(new FieldEnd(document));
        odd.Inlines.Add(new FieldSeparator(document));
        odd.Inlines.Add(new FieldStart(document));
        odd.AddRun("never closed");

        byte[] bytes = Save(document, out IReadOnlyList<ConversionWarning> warnings);
        Assert.Contains(warnings, w => w.Kind == ConversionWarningKind.TableSimplified);
        Document read = RtfFormat.Load(new MemoryStream(bytes));
        Assert.Equal(300, read.Sections[0].Body.Blocks.OfType<Table>().First().Rows.Count);
        Assert.Contains("bottom", read.Sections[0].Body.GetText(), StringComparison.Ordinal);
        Assert.Contains("deep", read.Sections[0].Body.GetText(), StringComparison.Ordinal);
        Assert.Equal(Dump(read), Dump(Reread(read)));
    }

    // ---- Helpers ----

    private static void Same(string expected, string actual, string name)
    {
        if (expected != actual && Environment.GetEnvironmentVariable("EGAP_RTF_DUMPS") is { Length: > 0 } folder)
        {
            File.WriteAllText(Path.Combine(folder, name + ".a.txt"), expected);
            File.WriteAllText(Path.Combine(folder, name + ".b.txt"), actual);
        }

        Assert.Equal(expected, actual);
    }

    private static Body Body(Document document) => (document.Sections.Count == 0 ? document.AddSection() : document.Sections[0]).Body;

    private static Document LoadDocx(string name)
    {
        string xml = Path.Combine(AppContext.BaseDirectory, "files", "docx-xml", name);
        if (File.Exists(xml))
        {
            return DocxFormat.Load(DocxSeedPackage.Wrap(File.ReadAllBytes(xml)));
        }

        return DocxFormat.Load(Path.Combine(AppContext.BaseDirectory, "files", "docx-package", name));
    }

    /// <summary>
    /// Makes a model read from another format what writing it as RTF makes of it, where RTF has no place for something: an
    /// island from another format becomes its picture or its text, a picture with no image goes, and an empty last section
    /// gets a paragraph.
    /// </summary>
    private static void AsRtfWrites(Document document)
    {
        foreach (Island island in document.Descendants<Island>().Where(i => i.Format != "rtf").ToList())
        {
            NodeCollection<Inline> siblings = island.Parent is CompositeInline composite ? composite.Inlines : ((Paragraph)island.Parent!).Inlines;
            int at = siblings.IndexOf(island);
            island.Remove();
            if (island.Fallback is { } fallback)
            {
                siblings.Insert(at, new Picture(document, fallback, island.Width, island.Height) { Floating = island.Floating });
            }
            else if (island.Text.Length > 0)
            {
                siblings.Insert(at, new Run(document, island.Text));
            }
        }

        foreach (Picture picture in document.Descendants<Picture>().Where(p => p.Image.Bytes.Length == 0).ToList())
        {
            picture.Remove();
        }

        if (document.Sections is [_, .., { Body.Blocks.Count: 0 } last])
        {
            last.Body.AddParagraph();
        }
    }

    /// <summary>A PNG of <paramref name="width"/> by <paramref name="height"/> grey pixels.</summary>
    private static byte[] Png(int width, int height)
    {
        using Egami.Raster<Egami.Pixels.Rgba8> raster = Egami.Raster.Create(width, height, new Egami.Pixels.Rgba8(128, 128, 128, 255));
        using MemoryStream stream = new();
        raster.Save(stream, Egami.Formats.Png.PngFormat.Info);
        return stream.ToArray();
    }

    /// <summary>A 1 by 1 GIF.</summary>
    private static byte[] Gif() =>
    [
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0x21, 0xF9, 4, 1, 0, 0, 0, 0,
        0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 2, 2, 0x44, 1, 0, 0x3B,
    ];

    /// <summary>The model as text: styles, lists, sections and every node with its own formatting, to compare two reads.</summary>
    internal static string Dump(Document document)
    {
        StringBuilder text = new();
        List<List> lists = [.. document.Lists];
        text.Append("defaults ").Append(Format(document.Styles.DefaultRunFormat, lists)).Append(' ').AppendLine(Format(document.Styles.DefaultParagraphFormat, lists));
        foreach (Style style in document.Styles)
        {
            text.Append("style ").Append(style.Type).Append(' ').Append(style.Name).Append(" base=").Append(style.BasedOn?.Name)
                .Append(" default=").Append(style.IsDefault).Append(" hidden=").Append(style.Hidden).Append(" priority=").Append(style.Priority)
                .Append(" primary=").Append(style.IsPrimary);
            switch (style)
            {
                case ParagraphStyle paragraph:
                    text.Append(" next=").Append(paragraph.Next?.Name).Append(" linked=").Append(paragraph.Linked?.Name)
                        .Append(' ').Append(Format(paragraph.ParagraphFormat, lists)).Append(' ').Append(Format(paragraph.RunFormat, lists));
                    break;
                case CharacterStyle character:
                    text.Append(" linked=").Append(character.Linked?.Name).Append(' ').Append(Format(character.RunFormat, lists));
                    break;
                case TableStyle table:
                    text.Append(' ').Append(Format(table.TableFormat, lists)).Append(' ').Append(Format(table.RowFormat, lists))
                        .Append(' ').Append(Format(table.CellFormat, lists)).Append(' ').Append(Format(table.ParagraphFormat, lists))
                        .Append(' ').Append(Format(table.RunFormat, lists));
                    break;
            }

            text.AppendLine();
        }

        foreach (List list in lists)
        {
            text.Append("list ").Append(list.Definition.Kind).Append(' ').AppendLine(list.Definition.Name);
            foreach (ListLevel level in list.Definition.Levels)
            {
                text.Append("  level ").Append(level.Index).Append(' ').Append(level.StartAt).Append(' ').Append(level.NumberStyle).Append(" '")
                    .Append(level.LevelText).Append("' ").Append(level.Alignment).Append(' ').Append(level.Suffix).Append(' ')
                    .Append(level.RestartAfterLevel).Append(' ').Append(level.IsLegal).Append(' ').Append(level.LinkedStyle?.Name).Append(' ')
                    .Append(Format(level.ParagraphFormat, lists)).Append(' ').AppendLine(Format(level.RunFormat, lists));
            }

            foreach (ListLevelOverride levelOverride in list.Overrides)
            {
                text.Append("  override ").Append(levelOverride.Index).Append(' ').Append(levelOverride.StartAt).Append(' ')
                    .AppendLine(levelOverride.Level is { } level ? level.NumberStyle + " " + level.LevelText : "");
            }
        }

        foreach (Section section in document.Sections)
        {
            text.Append("section ").AppendLine(Format(section.PageSetup, lists));
            foreach (HeaderFooter headerFooter in section.HeadersFooters)
            {
                text.Append(' ').AppendLine(headerFooter.Kind.ToString());
                Blocks(headerFooter.Blocks, 2);
            }

            Blocks(section.Body.Blocks, 1);
        }

        return text.ToString();

        void Blocks(IEnumerable<Block> blocks, int depth)
        {
            foreach (Block block in blocks)
            {
                text.Append(' ', depth);
                switch (block)
                {
                    case Paragraph paragraph:
                        text.Append("p ").Append(paragraph.Style?.Name).Append(' ').Append(Format(paragraph.Format, lists)).Append(" mark ")
                            .Append(Format(paragraph.MarkFormat, lists)).Append(' ').Append(paragraph.MarkRevision).Append(' ')
                            .AppendLine(paragraph.FormatRevision is { } change ? change.Author + " " + change.Date + " " + Format(change.OldFormat, lists) : "");
                        foreach (Inline inline in paragraph.Inlines)
                        {
                            Inline(inline, depth + 1);
                        }

                        if (paragraph.Extensions.Get<Rtf.RtfExtension>(Rtf.RtfExtension.Format)?.Inline is { } kept)
                        {
                            foreach ((int index, string markup) in kept)
                            {
                                text.Append(' ', depth + 1).Append("kept ").Append(index).Append(' ').AppendLine(markup);
                            }
                        }

                        break;
                    case Table table:
                        text.Append("table ").Append(table.Style?.Name).Append(' ').Append(Format(table.Format, lists)).Append(" grid ")
                            .AppendLine(table.Grid is { } grid ? string.Join(",", grid.Select(g => g.ToString("0.##", CultureInfo.InvariantCulture))) : "");
                        foreach (Row row in table.Rows)
                        {
                            text.Append(' ', depth + 1).Append("row ").Append(Format(row.Format, lists)).Append(' ').AppendLine(row.Revision?.ToString());
                            foreach (Cell cell in row.Cells)
                            {
                                text.Append(' ', depth + 2).Append("cell ").AppendLine(Format(cell.Format, lists));
                                Blocks(cell.Blocks, depth + 3);
                            }
                        }

                        break;
                    default:
                        text.AppendLine(block.GetType().Name);
                        break;
                }
            }
        }

        void Inline(Inline inline, int depth)
        {
            text.Append(' ', depth).Append(inline.GetType().Name).Append(' ');
            if (inline.Revision is { } revision)
            {
                text.Append(revision).Append(' ');
            }

            switch (inline)
            {
                case Run run:
                    text.Append('"').Append(run.Text).Append("\" ").Append(run.Style?.Name).Append(' ').Append(Format(run.Format, lists));
                    if (run.FormatRevision is { } change)
                    {
                        text.Append(" was ").Append(change.Author).Append(' ').Append(change.Date).Append(' ').Append(Format(change.OldFormat, lists));
                    }

                    text.AppendLine();
                    break;
                case Break @break:
                    text.AppendLine(@break.Kind.ToString());
                    break;
                case FieldStart start:
                    text.Append(start.IsLocked).Append(' ').AppendLine(start.IsDirty.ToString());
                    break;
                case BookmarkStart start:
                    text.AppendLine(start.Name);
                    break;
                case BookmarkEnd end:
                    text.AppendLine(end.Name);
                    break;
                case CommentRangeStart start:
                    text.AppendLine(start.CommentId.ToString(CultureInfo.InvariantCulture));
                    break;
                case CommentRangeEnd end:
                    text.AppendLine(end.CommentId.ToString(CultureInfo.InvariantCulture));
                    break;
                case Comment comment:
                    text.Append(comment.Id).Append(' ').Append(comment.Author).Append(' ').Append(comment.Initials).Append(' ')
                        .Append(comment.Date).Append(' ').AppendLine(comment.ParentId?.ToString(CultureInfo.InvariantCulture));
                    Blocks(comment.Story.Blocks, depth + 1);
                    break;
                case Footnote note:
                    text.Append(note.Kind).Append(' ').AppendLine(note.CustomMark);
                    Blocks(note.Story.Blocks, depth + 1);
                    break;
                case Picture picture:
                    text.Append(picture.Image.ContentType).Append(' ').Append(picture.Image.Bytes.Length).Append(' ').Append(Number(picture.Width))
                        .Append('x').Append(Number(picture.Height)).Append(" crop ").Append(Number(picture.Crop.Left)).Append(',')
                        .Append(Number(picture.Crop.Top)).Append(',').Append(Number(picture.Crop.Right)).Append(',').Append(Number(picture.Crop.Bottom))
                        .Append(' ').Append(picture.Title).Append(' ').Append(picture.Description).Append(' ').AppendLine(picture.Floating?.ToString());
                    break;
                case Shape shape:
                    text.Append(shape.Kind).Append(' ').Append(Number(shape.Width)).Append('x').Append(Number(shape.Height)).Append(' ')
                        .Append(shape.Fill).Append(' ').Append(shape.Outline).Append(' ').Append(shape.Title).Append(' ').Append(shape.Description)
                        .Append(' ').Append(shape.TextInsets).Append(' ').Append(shape.FitToText).Append(' ').AppendLine(shape.Floating?.ToString());
                    if (shape.TextBox is { } box)
                    {
                        Blocks(box.Blocks, depth + 1);
                    }

                    break;
                case Island island:
                    text.Append(island.Kind).Append(' ').Append(island.Format).Append(' ').Append(island.Markup).Append(' ')
                        .Append(island.Fallback?.Bytes.Length).Append(' ').AppendLine(island.Text);
                    break;
                case CompositeInline composite:
                    text.AppendLine();
                    foreach (Inline child in composite.Inlines)
                    {
                        Inline(child, depth + 1);
                    }

                    break;
                default:
                    text.AppendLine();
                    break;
            }
        }
    }

    /// <summary>
    /// The effective formatting of every paragraph and the text of every run outside field codes, to compare two formats' reads;
    /// of the page setup, what <paramref name="original"/> says.
    /// </summary>
    internal static string Effective(Document document, Document original)
    {
        StringBuilder text = new();
        for (int i = 0; i < document.Sections.Count; i++)
        {
            // RTF always says the page size and margins; only what the original said is compared.
            Section section = document.Sections[i];
            PageSetup said = i < original.Sections.Count ? original.Sections[i].PageSetup : new PageSetup();
            text.Append("section ");
            for (int id = 0; id < said.PropertyCount; id++)
            {
                if (said.GetRaw(id) is not null)
                {
                    text.Append(said.PropertyName(id)).Append('=').Append(Value(section.PageSetup.GetRaw(id), [])).Append(';');
                }
            }

            text.AppendLine();
            Blocks(section.Body.Blocks);
        }

        return text.ToString();

        void Blocks(IEnumerable<Block> blocks)
        {
            foreach (Block block in blocks)
            {
                switch (block)
                {
                    case Paragraph paragraph:
                        text.Append("p ").AppendLine(Properties(paragraph.GetEffectiveFormat(), document.Theme));
                        int fieldDepth = 0;
                        bool inCode = false;
                        Stack<bool> codes = [];
                        foreach (Inline inline in Flat(paragraph.Inlines))
                        {
                            switch (inline)
                            {
                                case FieldStart:
                                    codes.Push(inCode);
                                    inCode = true;
                                    fieldDepth++;
                                    break;
                                case FieldSeparator:
                                    inCode = false;
                                    break;
                                case FieldEnd when fieldDepth > 0:
                                    inCode = codes.Pop();
                                    fieldDepth--;
                                    break;
                                case Run run when !inCode:
                                    text.Append("  \"").Append(run.Text).Append("\" ").AppendLine(Properties(run.GetEffectiveFormat(), document.Theme));
                                    break;
                                case Footnote note:
                                    text.Append("  note ").AppendLine(note.Kind.ToString());
                                    Blocks(note.Story.Blocks);
                                    break;
                                case Comment comment:
                                    text.Append("  comment ").AppendLine(comment.Author);
                                    Blocks(comment.Story.Blocks);
                                    break;
                                case Picture picture:
                                    text.Append("  picture ").Append(Number(picture.Width)).Append('x').AppendLine(Number(picture.Height));
                                    break;
                                case Tab or Break or BookmarkStart or BookmarkEnd:
                                    text.Append("  ").AppendLine(inline.GetType().Name);
                                    break;
                            }
                        }

                        break;
                    case ContentControl control:
                        Blocks(control.Blocks);
                        break;
                    case Table table:
                        text.AppendLine("table");
                        foreach (Row row in table.Rows)
                        {
                            text.AppendLine(" row");
                            foreach (Cell cell in row.Cells)
                            {
                                // RTF always says how wide a cell is.
                                CellFormat format = cell.GetEffectiveFormat();
                                format.Width = null;
                                text.Append("  cell ").AppendLine(Properties(format, document.Theme));
                                Blocks(cell.Blocks);
                            }
                        }

                        break;
                }
            }
        }

        // A hyperlink is a field in RTF: its text is read as the field's result.
        static IEnumerable<Inline> Flat(IEnumerable<Inline> inlines)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is CompositeInline composite)
                {
                    foreach (Inline child in Flat(composite.Inlines))
                    {
                        yield return child;
                    }
                }
                else
                {
                    yield return inline;
                }
            }
        }
    }

    /// <summary>The public properties of a resolved format, colours as the theme makes them.</summary>
    private static string Properties(object value, Theme theme)
    {
        if (value is FormatBase format)
        {
            return Format(format, []);
        }

        StringBuilder text = new();
        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0)
            {
                object? item = property.GetValue(value);
                text.Append(property.Name).Append('=').Append(item is DocumentColor color ? color.Resolve(theme, default).ToString() : Value(item, [])).Append(';');
            }
        }

        return text.ToString();
    }

    private static string Format(FormatBase format, List<List> lists)
    {
        StringBuilder text = new("{");
        for (int id = 0; id < format.PropertyCount; id++)
        {
            if (format.GetRaw(id) is { } value)
            {
                text.Append(format.PropertyName(id)).Append('=').Append(Value(value, lists)).Append(';');
            }
        }

        return text.Append('}').ToString();
    }

    private static string Value(object? value, List<List> lists)
    {
        return value switch
        {
            null => "",
            string s => "'" + s + "'",
            double d => Number(d),
            List list => list == List.None ? "none" : "list" + lists.IndexOf(list),
            Style style => style.Name,
            FormatBase format => Format(format, lists),
            IEnumerable items => "[" + string.Join(",", items.Cast<object?>().Select(i => Value(i, lists))) + "]",
            _ => value.ToString() ?? "",
        };
    }

    private static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
