using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Doc;

using static Egap.Documents.Tests.DocBuilder;

namespace Egap.Documents.Tests;

/// <summary>The binary Word reader on documents built for each feature: text, formatting, styles, tables, notes and more.</summary>
public sealed class DocReaderTests
{
    private const ushort Bold = 0x0835;
    private const ushort Italic = 0x0836;
    private const ushort Size = 0x4A43;
    private const ushort Font = 0x4A4F;
    private const ushort Justification = 0x2403;
    private const ushort LeftIndent = 0x840F;
    private const ushort InTable = 0x2416;
    private const ushort RowEnd = 0x2417;

    private static Document Read(DocBuilder builder)
    {
        return Read(builder.Build(), out _);
    }

    private static Document Read(byte[] bytes, out IReadOnlyList<ConversionWarning> warnings)
    {
        return DocReader.Read(bytes, new DocumentLimits(), out warnings);
    }

    private static List<Paragraph> Paragraphs(Document document)
    {
        return [.. document.Sections[0].Body.Descendants<Paragraph>()];
    }

    [Fact]
    public void ReadsParagraphsOfUnicodeText()
    {
        DocBuilder builder = new();
        builder.Run("Hello, w\u00F6rld").Paragraph().Run("\u03A9mega").Paragraph();

        Document document = Read(builder);

        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Equal(["Hello, w\u00F6rld", "\u03A9mega"], paragraphs.Select(p => p.GetText()));
        Assert.Equal("Normal", paragraphs[0].Style?.Name);
    }

    [Fact]
    public void ReadsCompressedTextAsWindows1252()
    {
        DocBuilder builder = new() { Compressed = true };
        builder.Run("Caf\u00E9 \u20AC5 \u201Cquoted\u201D").Paragraph();

        Document document = Read(builder);

        Assert.Equal("Caf\u00E9 \u20AC5 \u201Cquoted\u201D", Paragraphs(document)[0].GetText());
    }

    [Fact]
    public void SpecialCharactersBecomeInlines()
    {
        DocBuilder builder = new();
        builder.Run("a\tb\vc\u001Ed\u001Fe\u000Ef").Paragraph();

        Paragraph paragraph = Paragraphs(Read(builder))[0];

        Assert.Contains(paragraph.Inlines, i => i is Tab);
        Assert.Contains(paragraph.Inlines, i => i is Break { Kind: BreakKind.Line });
        Assert.Contains(paragraph.Inlines, i => i is Break { Kind: BreakKind.Column });
        string text = string.Concat(paragraph.Inlines.OfType<Run>().Select(r => r.Text));
        Assert.Equal("abc\u2011d\u00ADef", text);
    }

    [Fact]
    public void CharacterSprmsFormatRuns()
    {
        DocBuilder builder = new();
        builder.Run("plain ")
            .Run("bold", Sprm(Bold, 1))
            .Run(" big italic", Sprm16(Size, 36), Sprm(Italic, 1), Sprm16(Font, 2))
            .Paragraph();

        List<Run> runs = [.. Paragraphs(Read(builder))[0].Inlines.OfType<Run>()];

        Assert.Equal(["plain ", "bold", " big italic"], runs.Select(r => r.Text));
        Assert.Null(runs[0].Format.Bold);
        Assert.True(runs[1].Format.Bold);
        Assert.Equal(18, runs[2].Format.Size);
        Assert.True(runs[2].Format.Italic);
        Assert.Equal("Arial", runs[2].Format.FontAscii);
    }

    [Fact]
    public void ParagraphSprmsAndStylesFormatParagraphs()
    {
        DocBuilder builder = new();
        builder.Styles[1] = new DocStyleDef("heading 1", Sti: 1, Base: 0, Next: 0)
        {
            Papx = Sprm(Justification, 1),
            Chpx = [.. Sprm(Bold, 1), .. Sprm16(Size, 32)],
        };
        builder.Run("Title").Paragraph(1)
            .Run("Body").Paragraph(0, Sprm(Justification, 2), Sprm16(LeftIndent, 720));

        Document document = Read(builder);

        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Equal("heading 1", paragraphs[0].Style?.Name);
        Assert.Equal(ParagraphAlignment.Center, paragraphs[0].GetEffectiveFormat().Alignment);
        Run title = paragraphs[0].Inlines.OfType<Run>().Single();
        Assert.True(title.GetEffectiveFormat().Bold);
        Assert.Equal(16, title.GetEffectiveFormat().Size);
        Assert.Equal(ParagraphAlignment.Right, paragraphs[1].Format.Alignment);
        Assert.Equal(36, paragraphs[1].Format.LeftIndent);
        Assert.Equal("Normal", paragraphs[1].Style?.Name);
    }

    [Fact]
    public void ToggleOperandsFlipTheStylesValue()
    {
        // 0x81 sets a toggle property to the opposite of what the style gives; 0x80 to what it gives.
        DocBuilder builder = new();
        builder.Styles[1] = new DocStyleDef("Strong", Kind: 1, Base: 0) { Chpx = Sprm(Bold, 1) };
        builder.Run("not bold", Sprm(Bold, 0x81)).Run(" as style", Sprm(Bold, 0x80)).Paragraph(1);

        List<Run> runs = [.. Paragraphs(Read(builder))[0].Inlines.OfType<Run>()];

        Assert.False(runs[0].GetEffectiveFormat().Bold);
        Assert.True(runs[1].GetEffectiveFormat().Bold);
    }

    [Fact]
    public void FastSavedPiecesApplyTheirPrms()
    {
        // A Prm0 is one sprm by isprm (0x55, sprmCFBold) with its operand; a Prm1 names a Prc.
        DocBuilder builder = new();
        builder.Run("plain").Run("bold").Run("italic").Paragraph();
        builder.Prcs.Add(Sprm(Italic, 1));
        builder.Pieces.Clear();
        builder.Pieces.AddRange([(0, 0), (5, (ushort)((0x55 << 1) | (1 << 8))), (9, 1)]);

        List<Run> runs = [.. Paragraphs(Read(builder))[0].Inlines.OfType<Run>()];

        Assert.Equal("plain", runs[0].Text);
        Assert.Null(runs[0].Format.Bold);
        Assert.Equal("bold", runs[1].Text);
        Assert.True(runs[1].Format.Bold);
        Assert.StartsWith("italic", runs[2].Text);
        Assert.True(runs[2].Format.Italic);
    }

    private static byte[] DefineTable(params int[] edges)
    {
        int count = edges.Length - 1;
        List<byte> operand = [(byte)count];
        foreach (int edge in edges)
        {
            operand.AddRange(BitConverter.GetBytes((short)edge));
        }

        operand.AddRange(new byte[20 * count]);
        return [.. Sprm16(0xD608, operand.Count + 1), .. operand];
    }

    [Fact]
    public void CellAndRowMarksMakeTables()
    {
        DocBuilder builder = new();
        builder.Run("Before").Paragraph();
        foreach (string[] row in new[] { new[] { "A1", "B1" }, ["A2", "B2"] })
        {
            foreach (string cell in row)
            {
                builder.Run(cell).End('\a', 0, Sprm(InTable, 1));
            }

            builder.End('\a', 0, Sprm(InTable, 1), Sprm(RowEnd, 1), DefineTable(0, 1440, 4320));
        }

        builder.Run("After").Paragraph();

        Document document = Read(builder);

        List<Block> blocks = [.. document.Sections[0].Body.Blocks];
        Assert.Equal(3, blocks.Count);
        Table table = Assert.IsType<Table>(blocks[1]);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["A1", "B1"], table.Rows[0].Cells.Select(c => c.GetText().Trim()));
        Assert.Equal(["A2", "B2"], table.Rows[1].Cells.Select(c => c.GetText().Trim()));
        Assert.Equal([72.0, 144.0], table.Grid);
        Assert.Equal("After", ((Paragraph)blocks[2]).GetText());
    }

    [Fact]
    public void NestedTablesFollowTheirDepth()
    {
        const ushort itap = 0x6649;
        const ushort innerCell = 0x244B;
        const ushort innerRowEnd = 0x244C;
        DocBuilder builder = new();
        builder.Run("outer").End('\r', 0, Sprm(InTable, 1), Sprm32(itap, 1));
        builder.Run("inner").End('\r', 0, Sprm(InTable, 1), Sprm32(itap, 2), Sprm(innerCell, 1));
        builder.End('\r', 0, Sprm(InTable, 1), Sprm32(itap, 2), Sprm(innerCell, 1), Sprm(innerRowEnd, 1), DefineTable(0, 1000));
        builder.End('\a', 0, Sprm(InTable, 1), Sprm32(itap, 1));
        builder.End('\a', 0, Sprm(InTable, 1), Sprm(RowEnd, 1), DefineTable(0, 2000));

        Table outer = Assert.IsType<Table>(Read(builder).Sections[0].Body.Blocks[0]);

        Cell cell = outer.Rows.Single().Cells.Single();
        Table inner = cell.Blocks.OfType<Table>().Single();
        Assert.Equal("inner", inner.Rows.Single().Cells.Single().GetText().Trim());
        Assert.Equal("outer", cell.Blocks.OfType<Paragraph>().First().GetText());
    }

    [Fact]
    public void ImpossiblyDeepTablesStopAtTheDepthKept()
    {
        // A fuzzed sprmPItap of a billion levels once made a billion table levels; deeper levels now go to the deepest kept.
        const ushort itap = 0x6649;
        const ushort innerRowEnd = 0x244C;
        DocBuilder builder = new();
        builder.Run("deep").End('\r', 0, Sprm(InTable, 1), Sprm32(itap, 1_000_000_000));
        builder.End('\r', 0, Sprm(InTable, 1), Sprm32(itap, 1_000_000_000), Sprm(innerRowEnd, 1), DefineTable(0, 1000));

        Document document = Read(builder);

        Assert.Contains("deep", document.Sections[0].Body.GetText(), StringComparison.Ordinal);
    }

    [Fact]
    public void FootnotesAndEndnotesCarryTheirStories()
    {
        DocBuilder builder = new();
        builder.Run("Text").Run("\u0002").Run(" more").Run("*").Paragraph();
        builder.Story = DocStory.Footnotes;
        builder.Run("\u0002 The note.").Paragraph();
        builder.Story = DocStory.Endnotes;
        builder.Run("* Custom.").Paragraph();
        builder.Table(2, Plc([4, 99], [1, 0])).Table(3, Plc([0, 12]));
        builder.Table(46, Plc([10, 99], [0, 0])).Table(47, Plc([0, 10]));

        Paragraph paragraph = Paragraphs(Read(builder))[0];

        List<Footnote> notes = [.. paragraph.Inlines.OfType<Footnote>()];
        Assert.Equal(2, notes.Count);
        Assert.Equal(NoteKind.Footnote, notes[0].Kind);
        Assert.Null(notes[0].CustomMark);
        Assert.Equal(" The note.", notes[0].Story.GetText().TrimEnd());
        Assert.Equal(NoteKind.Endnote, notes[1].Kind);
        Assert.Equal("*", notes[1].CustomMark);
        // A custom mark stays in the note's text, as in DOCX, where it is a run of its own.
        Assert.Equal("* Custom.", notes[1].Story.GetText().TrimEnd());
        Assert.Equal("Text more", string.Concat(paragraph.Inlines.OfType<Run>().Select(r => r.Text)));
    }

    [Fact]
    public void CommentsHaveAuthorsInitialsRangesAndText()
    {
        DocBuilder builder = new();
        builder.Run("Look here").Run("\u0005", Sprm(0x0855, 1)).Paragraph();
        builder.Story = DocStory.Comments;
        builder.Run("\u0005Check this.").Paragraph();
        byte[] atrd = new byte[30];
        BinaryPrimitives.WriteUInt16LittleEndian(atrd, 2);
        Encoding.Unicode.GetBytes("AB").CopyTo(atrd, 2);
        BinaryPrimitives.WriteInt16LittleEndian(atrd.AsSpan(20), 0);
        BinaryPrimitives.WriteInt32LittleEndian(atrd.AsSpan(26), 7);
        byte[] owner = [.. BitConverter.GetBytes((ushort)8), .. Encoding.Unicode.GetBytes("Reviewer")];
        builder.Table(4, Plc([9, 99], atrd)).Table(5, Plc([0, 13])).Table(36, owner);
        // The range: an ATNBE (bmc, lTag, lTagOld) per range, its start (with the index of its end) and end.
        builder.Table(37, builder.Sttb([""], extra: 10, i => [0, 0, .. BitConverter.GetBytes(7), 0, 0, 0, 0]));
        builder.Table(42, Plc([5, 99], [0, 0, 0, 0])).Table(43, Plc([9, 99]));

        Paragraph paragraph = Paragraphs(Read(builder))[0];

        Comment comment = paragraph.Inlines.OfType<Comment>().Single();
        Assert.Equal("Reviewer", comment.Author);
        Assert.Equal("AB", comment.Initials);
        Assert.Equal("Check this.", comment.Story.GetText().TrimEnd());
        List<Inline> inlines = [.. paragraph.Inlines];
        int start = inlines.FindIndex(i => i is CommentRangeStart);
        int end = inlines.FindIndex(i => i is CommentRangeEnd);
        Assert.Equal("here", string.Concat(inlines[start..end].OfType<Run>().Select(r => r.Text)));
    }

    [Fact]
    public void ACommentReferencedFromItsOwnTextIsLeftOut()
    {
        // A damaged file's only reference to the comment is inside the comment's text, which once put the comment inside itself.
        DocBuilder builder = new();
        builder.Run("Look here").Paragraph();
        builder.Story = DocStory.Comments;
        builder.Run("\u0005Check \u0005 this.").Paragraph();
        builder.Table(4, Plc([17, 99], new byte[30])).Table(5, Plc([0, 16]));

        Document document = Read(builder);

        Assert.Empty(document.Descendants<Comment>());
        Assert.Equal("Look here", Paragraphs(document)[0].GetText().TrimEnd());
    }

    [Fact]
    public void BookmarksAndFieldsMarkTheText()
    {
        DocBuilder builder = new();
        builder.Run("Page \u0013 PAGE \u00141\u0015 end").Paragraph();
        builder.Table(21, builder.Sttb(["_Mark", "empty"])).Table(22, Plc([0, 5, 99], [0, 0, 0, 0], [1, 0, 0, 0])).Table(23, Plc([4, 5, 99]));
        // A field's characters, each with an FLD: the end's second byte has fLocked (0x10).
        builder.Table(16, Plc([5, 12, 14, 99], [0x13, 33], [0x14, 0xFF], [0x15, 0x10]));

        Paragraph paragraph = Paragraphs(Read(builder))[0];

        List<Inline> inlines = [.. paragraph.Inlines];
        Assert.IsType<BookmarkStart>(inlines[0]);
        Assert.Equal("_Mark", ((BookmarkStart)inlines[0]).Name);
        int end = inlines.FindIndex(i => i is BookmarkEnd { Name: "_Mark" });
        Assert.Equal("Page", string.Concat(inlines[..end].OfType<Run>().Select(r => r.Text)));
        int emptyStart = inlines.FindIndex(i => i is BookmarkStart { Name: "empty" });
        Assert.IsType<BookmarkEnd>(inlines[emptyStart + 1]);
        FieldStart field = inlines.OfType<FieldStart>().Single();
        Assert.True(field.IsLocked);
        Assert.Equal(" PAGE ", field.GetField()!.Code);
        Assert.Single(inlines.OfType<FieldEnd>());
    }

    [Fact]
    public void BookmarkNamesKeepOnlyWhatXmlCanHold()
    {
        DocBuilder builder = new();
        builder.Run("Some text").Paragraph();
        builder.Table(21, builder.Sttb(["A\u0001B\uFFFF", "\uFFFF\u0000"])).Table(22, Plc([0, 5, 99], [0, 0, 0, 0], [1, 0, 0, 0])).Table(23, Plc([4, 9, 99]));

        Document document = Read(builder);

        // A name of nothing XML can hold is left out, as the DOCX writer would leave it empty.
        Assert.Equal("AB", document.Descendants<BookmarkStart>().Single().Name);
    }

    [Fact]
    public void SectionsHavePageSetupAndHeaders()
    {
        const ushort pageWidth = 0xB01F;
        const ushort pageHeight = 0xB020;
        const ushort orientation = 0x301D;
        const ushort breakKind = 0x3009;
        DocBuilder builder = new();
        builder.Run("First").Paragraph().Run("Second").Paragraph();
        builder.Sections.Add((6, [.. Sprm16(pageWidth, 12240), .. Sprm16(pageHeight, 15840)]));
        builder.Sections.Add((13, [.. Sprm16(pageWidth, 15840), .. Sprm16(pageHeight, 12240), .. Sprm(orientation, 2), .. Sprm(breakKind, 2)]));
        builder.Story = DocStory.Headers;
        builder.Run("Odd header").Paragraph().Paragraph();
        // Six note separator stories (empty), then six stories per section: the first section's odd header only.
        builder.Table(11, Plc([0, 0, 0, 0, 0, 0, 0, 0, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12, 12]));

        Document document = Read(builder);

        Assert.Equal(2, document.Sections.Count);
        Assert.Equal(612, document.Sections[0].PageSetup.PageWidth);
        Assert.Equal(792, document.Sections[1].PageSetup.PageWidth);
        Assert.Equal(PageOrientation.Landscape, document.Sections[1].PageSetup.Orientation);
        HeaderFooter header = document.Sections[0].HeadersFooters.Single();
        Assert.Equal(HeaderFooterKind.HeaderPrimary, header.Kind);
        Assert.Equal("Odd header", header.GetText().Trim());
        Assert.Empty(document.Sections[1].HeadersFooters);
        Assert.Equal("Second", document.Sections[1].Body.GetText().Trim());
    }

    [Fact]
    public void ListsComeFromDefinitionsAndOverrides()
    {
        const ushort ilfo = 0x460B;
        const ushort ilvl = 0x260A;
        DocBuilder builder = new();
        builder.Run("One").Paragraph(0, Sprm16(ilfo, 1), Sprm(ilvl, 0));
        builder.Run("Two").Paragraph(0, Sprm16(ilfo, 1), Sprm(ilvl, 0));
        builder.Run("Not listed").Paragraph(0, Sprm16(ilfo, 0));

        byte[] lstf = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(lstf, 1234);
        lstf[26] = 0x01;
        byte[] lvl = new byte[28];
        BinaryPrimitives.WriteInt32LittleEndian(lvl, 3);
        lvl[4] = 0;
        lvl[6] = 1;
        byte[] lvlText = [.. BitConverter.GetBytes((ushort)2), 0, 0, (byte)'.', 0];
        builder.Table(73, [1, 0, .. lstf], [.. lvl, .. lvlText]);
        byte[] lfo = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(lfo, 1234);
        builder.Table(74, [1, 0, 0, 0, .. lfo, 0, 0, 0, 0]);

        List<Paragraph> paragraphs = Paragraphs(Read(builder));

        List list = paragraphs[0].Format.List!;
        Assert.Same(list, paragraphs[1].Format.List);
        Assert.Equal("%1.", list.GetLevel(0).LevelText);
        Assert.Equal(3, list.GetLevel(0).StartAt);
        Assert.Equal(NumberStyle.Decimal, list.GetLevel(0).NumberStyle);
        Assert.Same(List.None, paragraphs[2].Format.List);
    }

    [Fact]
    public void RevisionMarksBecomeRevisions()
    {
        const ushort inserted = 0x0801;
        const ushort author = 0x4804;
        DocBuilder builder = new();
        builder.Run("old ").Run("new", Sprm(inserted, 1), Sprm16(author, 1)).Paragraph();
        builder.Table(51, builder.Sttb(["Unknown", "Editor"]));

        Run run = Paragraphs(Read(builder))[0].Inlines.OfType<Run>().Last();

        Assert.Equal("new", run.Text);
        Assert.Equal(RevisionKind.Insertion, run.Revision?.Kind);
        Assert.Equal("Editor", run.Revision?.Author);
    }

    [Fact]
    public void SettingsComeFromTheDop()
    {
        DocBuilder builder = new() { Dop = new byte[16] };
        builder.Dop[0] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(builder.Dop.AsSpan(2), 5 << 2);
        BinaryPrimitives.WriteUInt16LittleEndian(builder.Dop.AsSpan(10), 360);
        builder.Run("x").Paragraph();

        Document document = Read(builder);

        Assert.True(document.Settings.EvenAndOddHeaders);
        Assert.Equal(5, document.Settings.FootnoteStartAt);
        Assert.Equal(18, document.Settings.DefaultTabStop);
    }

    [Fact]
    public void ReadsWord6Documents()
    {
        // Word 6: 8-bit text from fcMin, one-byte sprms (85 bold, 5 justification), its own style sheet and font table.
        DocBuilder builder = new() { Word6 = true };
        builder.Styles[1] = new DocStyleDef("Heading", Sti: 1, Base: 0) { Chpx = [85, 1] };
        builder.Run("Heading").Paragraph(1);
        builder.Run("Plain ").Run("bold", [85, 1]).Paragraph(0, [5, 1]);

        Document document = Read(builder);

        List<Paragraph> paragraphs = Paragraphs(document);
        Assert.Equal(["Heading", "Plain bold"], paragraphs.Select(p => p.GetText()));
        Assert.Equal("Heading", paragraphs[0].Style?.Name);
        Assert.True(paragraphs[0].Inlines.OfType<Run>().Single().GetEffectiveFormat().Bold);
        Assert.True(paragraphs[1].Inlines.OfType<Run>().Last().Format.Bold);
        Assert.Equal(ParagraphAlignment.Center, paragraphs[1].Format.Alignment);
    }

    [Fact]
    public void PasswordProtectedWord6AndDamagedEncryptionHeadersAreRefused()
    {
        DocBuilder word6 = new() { Word6 = true, Flags = 0x0100 };
        word6.Run("secret").Paragraph();
        DocBuilder noHeader = new() { Flags = 0x0100 };
        noHeader.Run("secret").Paragraph();

        DocumentException e = Assert.Throws<DocumentException>(() => Read(word6.Build(), out _));
        DocumentException damaged = Assert.Throws<DocumentException>(() => Read(noHeader.Build(), out _));

        Assert.Contains("password-protected", e.Message, StringComparison.Ordinal);
        Assert.IsNotType<DocumentPasswordException>(damaged);
    }

    [Fact]
    public void OtherFilesAreRefused()
    {
        using MemoryStream output = new();
        Office.CompoundFileWriter.Write(output, [("Workbook", new byte[600])]);

        Assert.Throws<DocumentException>(() => Read(output.ToArray(), out _));
        Assert.Throws<DocumentException>(() => Read(Encoding.ASCII.GetBytes("{\\rtf1 not a compound file}"), out _));
    }

    [Fact]
    public void DamagedDocumentsFailOnlyWithDocumentExceptions()
    {
        DocBuilder builder = new();
        builder.Styles[1] = new DocStyleDef("heading 1", Sti: 1) { Chpx = Sprm(Bold, 1) };
        builder.Run("Text with ").Run("bold", Sprm(Bold, 1)).Run("\u0002").Paragraph(1)
            .Run("A1").End('\a', 0, Sprm(InTable, 1)).End('\a', 0, Sprm(InTable, 1), Sprm(RowEnd, 1), DefineTable(0, 1000));
        builder.Story = DocStory.Footnotes;
        builder.Run("\u0002 note").Paragraph();
        builder.Table(2, Plc([10, 99], [1, 0])).Table(3, Plc([0, 8]));
        byte[] file = builder.Build();
        Random random = new(17);
        for (int trial = 0; trial < 300; trial++)
        {
            byte[] damaged = (byte[])file.Clone();
            for (int i = 0; i < 8; i++)
            {
                damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
            }

            try
            {
                Read(damaged, out _);
            }
            catch (DocumentException)
            {
            }
        }
    }

    [Fact]
    public void HyphenationSettingsComeFromTheDop()
    {
        // As Word 16 saves a document with automatic hyphenation, capitals not hyphenated, a 0.5 inch zone and at most 3 lines:
        // fAutoHyphen (bit 12) set and fHyphCapitals (bit 11) clear in the word at 4, dxaHotZ 720, cConsecHypLim 3.
        DocBuilder builder = new() { Dop = new byte[84] };
        BinaryPrimitives.WriteUInt16LittleEndian(builder.Dop.AsSpan(4), 0x1031);
        BinaryPrimitives.WriteUInt16LittleEndian(builder.Dop.AsSpan(14), 720);
        BinaryPrimitives.WriteUInt16LittleEndian(builder.Dop.AsSpan(16), 3);
        builder.Run("x").Paragraph();

        Document document = Read(builder);

        Assert.True(document.Settings.AutoHyphenation);
        Assert.False(document.Settings.HyphenateCapitals);
        Assert.Equal(36, document.Settings.HyphenationZone);
        Assert.Equal(3, document.Settings.ConsecutiveHyphenLimit);

        // Word's defaults: off, capitals hyphenated, 360 twips, no limit.
        builder = new() { Dop = new byte[84] };
        BinaryPrimitives.WriteUInt16LittleEndian(builder.Dop.AsSpan(4), 0x0831);
        BinaryPrimitives.WriteUInt16LittleEndian(builder.Dop.AsSpan(14), 360);
        builder.Run("x").Paragraph();
        document = Read(builder);
        Assert.False(document.Settings.AutoHyphenation);
        Assert.True(document.Settings.HyphenateCapitals);
        Assert.Equal(18, document.Settings.HyphenationZone);
        Assert.Equal(0, document.Settings.ConsecutiveHyphenLimit);
    }
}
