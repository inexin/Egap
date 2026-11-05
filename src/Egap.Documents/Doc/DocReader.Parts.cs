using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>
/// What the text's parts point at: the document's settings (the DOP), revision authors, bookmarks, fields' flags, notes,
/// comments and the header and footer stories. Each part's CPs come after the main text's, in the FIB's order: footnotes,
/// headers, (Word 6's macros,) comments, endnotes, text boxes, header text boxes.
/// </summary>
internal sealed partial class DocReader
{
    private List<string> authors = [];

    private int FootnoteBase => fib.CcpText;

    private int HeaderBase => FootnoteBase + fib.CcpFtn;

    private int CommentBase => HeaderBase + fib.CcpHdd + (fib.IsWord6 ? fib.CcpMcr : 0);

    private int EndnoteBase => CommentBase + fib.CcpAtn;

    private int TextBoxBase => EndnoteBase + fib.CcpEdn;

    private int HeaderTextBoxBase => TextBoxBase + fib.CcpTxbx;

    /// <summary>The header stories' CPs (PlcfHdd), from the start of the header part.</summary>
    private sealed record HeaderStories(int[] Cps);

    // DOP ([MS-DOC] 2.7.1, the same first bytes in Word 6): fFacingPages in bit 0, the footnote numbering's start (nFtn) in
    // bits 2-15 of the word at 2, dxaTab at 10.
    private void ReadSettings()
    {
        document.Settings.CompatibilityMode = 11;
        ReadOnlySpan<byte> dop = Table(DocTable.Dop);
        if (dop.Length < 12)
        {
            return;
        }

        document.Settings.EvenAndOddHeaders = (dop[0] & 1) != 0;
        int start = BinaryPrimitives.ReadUInt16LittleEndian(dop[2..]) >> 2;
        if (start > 0)
        {
            document.Settings.FootnoteStartAt = start;
        }

        int tab = BinaryPrimitives.ReadUInt16LittleEndian(dop[10..]);
        if (tab > 0)
        {
            document.Settings.DefaultTabStop = tab / 20.0;
        }
    }

    // SttbfRMark: the revision authors, which a revision's ibst names.
    private void ReadAuthors()
    {
        authors = [.. Sttb(Table(DocTable.SttbfRMark)).Select(s => s.Text)];
    }

    private string Author(int index)
    {
        return index >= 0 && index < authors.Count && authors[index].Length > 0 ? authors[index] : "Unknown";
    }

    // SttbfBkmk names the bookmarks; PlcfBkf has each start CP and the index (ibkl) of its end in PlcfBkl.
    private void ReadBookmarks()
    {
        List<(string Text, byte[] Extra)> names = Sttb(Table(DocTable.SttbfBkmk));
        ReadOnlySpan<byte> starts = Table(DocTable.PlcfBkf);
        ReadOnlySpan<byte> ends = Table(DocTable.PlcfBkl);
        int count = Math.Min(PlcCount(starts, 4), names.Count);
        int endCount = ends.Length >= 4 ? (ends.Length / 4) - 1 : 0;
        HashSet<string> seen = new(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string name = names[i].Text;
            int start = PlcCp(starts, i);
            int ibkl = BinaryPrimitives.ReadUInt16LittleEndian(PlcData(starts, PlcCount(starts, 4), 4, i));
            if (name.Length == 0 || ibkl >= endCount || !seen.Add(name))
            {
                continue;
            }

            int end = PlcCp(ends, ibkl);
            AddRange(start, end, () => new BookmarkStart(document, name), () => new BookmarkEnd(document, name));
        }
    }

    /// <summary>A range's start and end marks: an end goes before starts at its CP, unless the range is empty.</summary>
    private void AddRange(int start, int end, Func<Inline> makeStart, Func<Inline> makeEnd)
    {
        if (start < 0 || end < start || markers.Count >= limits.MaxEntries * 2)
        {
            return;
        }

        markers.Add(new Marker(start, 1, makeStart));
        markers.Add(new Marker(end, end == start ? 2 : 0, makeEnd));
    }

    // Each part's PlcFld: per field character its CP (from the start of the part) and an FLD, whose second byte for an end
    // has fResultDirty (0x04) and fLocked (0x10).
    private void ReadFields()
    {
        (DocTable Table, int Base)[] parts =
        [
            (DocTable.PlcfFldMom, 0),
            (DocTable.PlcfFldFtn, FootnoteBase),
            (DocTable.PlcfFldHdr, HeaderBase),
            (DocTable.PlcfFldAtn, CommentBase),
            (DocTable.PlcfFldEdn, EndnoteBase),
            (DocTable.PlcfFldTxbx, TextBoxBase),
            (DocTable.PlcfFldHdrTxbx, HeaderTextBoxBase),
        ];
        foreach ((DocTable which, int at) in parts)
        {
            ReadOnlySpan<byte> plc = Table(which);
            int count = PlcCount(plc, 2);
            for (int i = 0; i < count && fieldEnds.Count < limits.MaxEntries; i++)
            {
                ReadOnlySpan<byte> fld = PlcData(plc, count, 2, i);
                if ((fld[0] & 0x1F) == 0x15)
                {
                    fieldEnds[at + PlcCp(plc, i)] = ((fld[1] & 0x10) != 0, (fld[1] & 0x04) != 0);
                }
            }
        }
    }

    // Footnotes and endnotes: PlcffndRef's CPs are the references in the main text; PlcffndTxt's split the part into notes.
    private void ReadNotes()
    {
        ReadNotes(NoteKind.Footnote, Table(DocTable.PlcffndRef), Table(DocTable.PlcffndTxt), FootnoteBase, fib.CcpFtn);
        if (!fib.IsWord6 || (fib.Pair(DocTable.PlcfendRef).Lcb > 0))
        {
            ReadNotes(NoteKind.Endnote, Table(DocTable.PlcfendRef), Table(DocTable.PlcfendTxt), EndnoteBase, fib.CcpEdn);
        }
    }

    private void ReadNotes(NoteKind kind, ReadOnlySpan<byte> references, ReadOnlySpan<byte> texts, int at, int length)
    {
        int count = PlcCount(references, 2);
        int textCount = texts.Length >= 8 ? (texts.Length / 4) - 1 : 0;
        for (int i = 0; i < count && i < textCount && notes.Count < limits.MaxEntries; i++)
        {
            int start = Math.Clamp(PlcCp(texts, i), 0, length);
            int end = Math.Clamp(PlcCp(texts, i + 1), start, length);
            notes.TryAdd(PlcCp(references, i), (kind, at + start, at + end));
        }
    }

    // Comments: PlcfandRef's CPs are the references, each with an ATRD (Word 97's 30 bytes: the initials as an Xst of up to 9
    // characters in 20 bytes, ibst naming the author in GrpXstAtnOwners, then lTagBkmk at 26 naming its range in
    // SttbfAtnBkmk; Word 6's 20 bytes: the initials as a length byte and 8-bit characters in 10, ibst at 10, lTagBkmk at
    // 16). PlcfandTxt splits the part into comments; AtrdExtra (Word 2002) has each comment's date.
    private void ReadComments()
    {
        ReadOnlySpan<byte> references = Table(DocTable.PlcfandRef);
        int size = fib.IsWord6 ? 20 : 30;
        int count = PlcCount(references, size);
        if (count == 0)
        {
            return;
        }

        List<string> owners = Owners(Table(DocTable.GrpXstAtnOwners));
        ReadOnlySpan<byte> texts = Table(DocTable.PlcfandTxt);
        int textCount = texts.Length >= 8 ? (texts.Length / 4) - 1 : 0;
        Dictionary<int, (int Start, int End)> ranges = fib.IsWord6 ? [] : CommentRanges();
        ReadOnlySpan<byte> extra = Table(DocTable.AtrdExtra);
        if (extra.Length != 18 * count)
        {
            extra = [];
        }

        for (int i = 0; i < count && comments.Count < limits.MaxEntries; i++)
        {
            ReadOnlySpan<byte> atrd = PlcData(references, count, size, i);
            string initials;
            int ibst;
            int tag;
            if (fib.IsWord6)
            {
                initials = Latin(atrd.Slice(1, Math.Min((int)atrd[0], 9)));
                ibst = BinaryPrimitives.ReadInt16LittleEndian(atrd[10..]);
                tag = BinaryPrimitives.ReadInt32LittleEndian(atrd[16..]);
            }
            else
            {
                int cch = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(atrd), 9);
                StringBuilder builder = new(cch);
                for (int c = 0; c < cch; c++)
                {
                    builder.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(atrd[(2 + (2 * c))..]));
                }

                initials = builder.ToString();
                ibst = BinaryPrimitives.ReadInt16LittleEndian(atrd[20..]);
                tag = BinaryPrimitives.ReadInt32LittleEndian(atrd[26..]);
            }

            string author = ibst >= 0 && ibst < owners.Count && owners[ibst].Length > 0 ? owners[ibst] : "Unknown";
            DateTimeOffset? date = extra.Length > 0 ? WordProperties.Date(BinaryPrimitives.ReadInt32LittleEndian(extra[(18 * i)..])) : null;
            Comment comment = new(document, author, date);
            if (initials.Length > 0)
            {
                comment.Initials = initials;
            }

            int cp = PlcCp(references, i);
            if (!comments.TryAdd(cp, comment))
            {
                continue;
            }

            if (tag != -1 && ranges.TryGetValue(tag, out (int Start, int End) range))
            {
                AddRange(range.Start, range.End, () => new CommentRangeStart(document, comment), () => new CommentRangeEnd(document, comment));
            }

            if (i < textCount)
            {
                int start = Math.Clamp(PlcCp(texts, i), 0, fib.CcpAtn);
                int end = Math.Clamp(PlcCp(texts, i + 1), start, fib.CcpAtn);
                pendingComments.Add((comment, CommentBase + start, CommentBase + end));
            }
        }
    }

    private readonly List<(Comment Comment, int Start, int End)> pendingComments = [];

    /// <summary>Reads each comment's story, once the marks every story may hold are known.</summary>
    private void ReadCommentStories()
    {
        foreach ((Comment comment, int start, int end) in pendingComments)
        {
            Enter();
            StoryBuilder story = new(this, comment.Story.Blocks, StoryKind.Comment);
            story.Read(start, end);
            story.Finish();
            Leave();
        }
    }

    /// <summary>
    /// The comments' ranges by tag: SttbfAtnBkmk's extra data per range is an ATNBE (bmc, lTag, lTagOld); PlcfAtnBkf's
    /// CPs are the starts, with the index of each end in PlcfAtnBkl.
    /// </summary>
    private Dictionary<int, (int Start, int End)> CommentRanges()
    {
        Dictionary<int, (int, int)> ranges = [];
        List<(string Text, byte[] Extra)> names = Sttb(Table(DocTable.SttbfAtnBkmk));
        ReadOnlySpan<byte> starts = Table(DocTable.PlcfAtnBkf);
        ReadOnlySpan<byte> ends = Table(DocTable.PlcfAtnBkl);
        int count = Math.Min(PlcCount(starts, 4), names.Count);
        int endCount = ends.Length >= 4 ? (ends.Length / 4) - 1 : 0;
        for (int i = 0; i < count; i++)
        {
            if (names[i].Extra.Length < 6)
            {
                continue;
            }

            int tag = BinaryPrimitives.ReadInt32LittleEndian(names[i].Extra.AsSpan(2));
            int ibkl = BinaryPrimitives.ReadUInt16LittleEndian(PlcData(starts, PlcCount(starts, 4), 4, i));
            if (ibkl < endCount)
            {
                ranges.TryAdd(tag, (PlcCp(starts, i), PlcCp(ends, ibkl)));
            }
        }

        return ranges;
    }

    /// <summary>GrpXstAtnOwners: the comment authors, Xsts one after the other (Word 6's as a length byte and 8-bit text).</summary>
    private List<string> Owners(ReadOnlySpan<byte> group)
    {
        List<string> owners = [];
        int at = 0;
        while (at < group.Length && owners.Count < limits.MaxEntries)
        {
            if (fib.IsWord6)
            {
                int length = Math.Min(group[at], group.Length - at - 1);
                owners.Add(Latin(group.Slice(at + 1, length)));
                at += 1 + group[at];
                continue;
            }

            if (at + 2 > group.Length)
            {
                break;
            }

            int cch = BinaryPrimitives.ReadUInt16LittleEndian(group[at..]);
            int count = Math.Min(cch, (group.Length - at - 2) / 2);
            StringBuilder builder = new(count);
            for (int c = 0; c < count; c++)
            {
                builder.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(group[(at + 2 + (2 * c))..]));
            }

            owners.Add(builder.ToString());
            at += 2 + (2 * cch);
        }

        return owners;
    }

    // ---- Headers and footers ----

    // PlcfHdd: six stories for the note separators, then six per section (even header, odd header, even footer, odd footer,
    // first-page header, first-page footer); an empty story leaves the section with the one before it.
    private HeaderStories ReadHeaderStories()
    {
        ReadOnlySpan<byte> plc = Table(DocTable.PlcfHdd);
        int count = plc.Length / 4;
        int[] cps = new int[count];
        for (int i = 0; i < count; i++)
        {
            cps[i] = Math.Clamp(PlcCp(plc, i), 0, fib.CcpHdd);
        }

        if (fib.IsWord6 && count > 0)
        {
            // Word 6 stores only the headers a section's grpfIhdt names, which this reader does not follow yet.
            warnings.Add(ConversionWarningKind.FormattingDropped, "The headers and footers of a Word 6 or Word 95 document are left out.");
            return new HeaderStories([]);
        }

        return new HeaderStories(cps);
    }

    private static readonly HeaderFooterKind[] HeaderKinds =
    [
        HeaderFooterKind.HeaderEven,
        HeaderFooterKind.HeaderPrimary,
        HeaderFooterKind.FooterEven,
        HeaderFooterKind.FooterPrimary,
        HeaderFooterKind.HeaderFirst,
        HeaderFooterKind.FooterFirst,
    ];

    private void ReadHeaders(Section section, HeaderStories headers, int index)
    {
        // In the DOCX reader's order: the odd (primary) header before the even one, and so on.
        foreach (int k in (int[])[1, 0, 3, 2, 4, 5])
        {
            int i = 6 + (6 * index) + k;
            if (i + 1 >= headers.Cps.Length)
            {
                continue;
            }

            int start = headers.Cps[i];
            int end = headers.Cps[i + 1];
            if (end <= start)
            {
                continue;
            }

            HeaderFooter story = section.HeadersFooters.AddTrusted(new HeaderFooter(document, HeaderKinds[k]));
            Enter();
            StoryBuilder builder = new(this, story.Blocks, StoryKind.Header);
            // Each story ends with a paragraph mark of its own that is not part of its text.
            builder.Read(HeaderBase + start, HeaderBase + end - 1);
            builder.Finish();
            Leave();
        }
    }
}
