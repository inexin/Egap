using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Office;
using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>
/// Reads binary Word documents (Word 97 to 2003, and Word 6 and Word 95) into the model ([MS-DOC]): the compound file's
/// WordDocument stream and its FIB, the table stream's piece table, formatted disk pages, style sheet, fonts and lists,
/// then the text of each part (the main text with its sections, the footnotes, headers, comments and endnotes) paragraph
/// by paragraph. Properties come as sprms and go on the model through the shared Word property stream, as the RTF and
/// DOCX readers' properties do.
/// </summary>
/// <remarks>Ported from ClassicMac's Word reader (the maintainer's own MIT code) and extended to the whole model.</remarks>
internal sealed partial class DocReader
{
    private readonly Document document = new(blank: true);
    private readonly WarningCollector warnings = new();
    private readonly DocumentLimits limits;
    private readonly byte[] word;
    private readonly byte[] table;
    private readonly byte[]? data;
    private readonly DocFib fib;
    private readonly DocContext context;
    private DocText text = null!;
    private DocFkp chpx = null!;
    private DocFkp papx = null!;
    private DocStyles styles = null!;
    private DocLists lists = null!;
    private readonly Dictionary<(int Run, ushort Prm, int Style), DocChp> chpCache = [];
    private int nesting;

    private DocReader(byte[] word, byte[] table, byte[]? data, DocFib fib, DocumentLimits limits)
    {
        this.word = word;
        this.table = table;
        this.data = data;
        this.fib = fib;
        this.limits = limits;
        context = new DocContext(fib.IsWord6, [], warnings);
    }

    /// <summary>Reads a DOC file's bytes.</summary>
    /// <exception cref="DocumentException">The file is not a Word document, is password-protected or is malformed.</exception>
    public static Document Read(byte[] bytes, DocumentLimits limits, out IReadOnlyList<ConversionWarning> warnings)
    {
        try
        {
            CompoundFile file = CompoundFile.Read(bytes, limits);
            byte[] word = file.ReadStream("WordDocument") ?? throw new DocumentException("The compound file has no WordDocument stream: it is not a Word document.");
            DocFib fib = DocFib.Read(word);
            if (fib.IsEncrypted)
            {
                throw new DocumentException(fib.IsObfuscated
                    ? "The Word document is password-protected (XOR obfuscation), which Egap does not read yet."
                    : "The Word document is password-protected (RC4 encryption), which Egap does not read yet.");
            }

            byte[] table = fib.IsWord6 ? word : file.ReadStream(fib.TableStreamName)
                ?? throw new DocumentException($"The Word document has no {fib.TableStreamName} stream, which its FIB names.");
            byte[]? data = fib.IsWord6 ? null : file.ReadStream("Data");
            DocReader reader = new(word, table, data, fib, limits);
            reader.ReadDocument();
            warnings = reader.warnings.ToList();
            return reader.document;
        }
        catch (Exception e) when (e is not (DocumentException or OutOfMemoryException))
        {
            // Malformed input must not escape as an arbitrary exception: callers catch DocumentException for a bad file.
            throw new DocumentException("The Word document is malformed: " + e.Message, e);
        }
    }

    private void ReadDocument()
    {
        document.Styles.DefaultRunFormat.Size = 10;
        int ccpMain = fib.CcpText;
        long all = (long)ccpMain + fib.CcpFtn + fib.CcpHdd + (fib.IsWord6 ? fib.CcpMcr : 0) + fib.CcpAtn + fib.CcpEdn + fib.CcpTxbx + fib.CcpHdrTxbx + 1;
        if (all > limits.MaxPartSize || all > int.MaxValue / 2)
        {
            throw new DocumentLimitException($"The Word document's text is longer than {limits.MaxPartSize} characters.");
        }

        text = DocText.Read(word, fib, Table(DocTable.Clx), (int)all, warnings);
        context.Fonts = DocFonts.Read(Table(DocTable.SttbfFfn), fib.IsWord6, limits.MaxEntries);
        chpx = DocFkp.Read(word, Table(DocTable.PlcfBteChpx), paragraphs: false, fib.IsWord6, warnings);
        papx = DocFkp.Read(word, Table(DocTable.PlcfBtePapx), paragraphs: true, fib.IsWord6, warnings);
        styles = DocStyles.Read(Table(DocTable.Stshf), fib.IsWord6, warnings);
        lists = fib.IsWord6
            ? DocLists.Read(document, context, [], (0, 0), (0, 0), limits.MaxEntries)
            : DocLists.Read(document, context, table, fib.Pair(DocTable.PlfLst), fib.Pair(DocTable.PlfLfo), limits.MaxEntries);
        if (DefaultFont(styles.DefaultFonts[0]) is { } ascii)
        {
            document.Styles.DefaultRunFormat.FontAscii = ascii;
            document.Styles.DefaultRunFormat.FontHighAnsi = DefaultFont(styles.DefaultFonts[2]) ?? ascii;
        }

        if (DefaultFont(styles.DefaultFonts[1]) is { } eastAsia && !fib.IsWord6)
        {
            document.Styles.DefaultRunFormat.FontEastAsia = eastAsia;
        }

        styles.Make(document, context, ilfo => lists[ilfo]);
        ReadSettings();
        ReadAuthors();
        ReadBookmarks();
        ReadFields();
        ReadNotes();
        ReadComments();
        SortMarkers();
        ReadCommentStories();
        ReadMain(ccpMain);
    }

    private string? DefaultFont(int index)
    {
        return index >= 0 && index < context.Fonts.Count && context.Fonts[index].Length > 0 ? context.Fonts[index] : null;
    }

    /// <summary>A table the FIB points at, from the table stream (Word 6's from the WordDocument stream), or empty.</summary>
    private ReadOnlySpan<byte> Table(DocTable which)
    {
        (long fc, long lcb) = fib.Pair(which);
        if (lcb == 0)
        {
            return [];
        }

        if (fc < 0 || fc + lcb > table.Length)
        {
            warnings.Add(ConversionWarningKind.FormattingDropped, "A table of the Word document lies past the end of its stream; it is left out.");
            return [];
        }

        return table.AsSpan((int)fc, (int)lcb);
    }

    // ---- The main text and its sections ----

    private void ReadMain(int ccpMain)
    {
        // PlcfSed ([MS-DOC] 2.8.26): each section's end CP, then a 12-byte SED (fn, fcSepx, fnMpr, fcMpr) each.
        ReadOnlySpan<byte> plc = Table(DocTable.PlcfSed);
        int count = plc.Length >= 16 ? (plc.Length - 4) / 16 : 0;
        List<(int End, DocSep Sep)> sections = [];
        for (int i = 0; i < count && sections.Count < limits.MaxEntries; i++)
        {
            int end = Math.Clamp(BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * (i + 1))..]), 0, ccpMain);
            long fcSepx = BinaryPrimitives.ReadUInt32LittleEndian(plc[((4 * (count + 1)) + (12 * i) + 2)..]);
            DocSep sep = DocProperties.DefaultSep();
            if (fcSepx != 0xFFFFFFFF && fcSepx + 2 <= word.Length)
            {
                int cb = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan((int)fcSepx));
                DocProperties.ApplySep(sep, word.AsSpan((int)fcSepx + 2, (int)Math.Min(cb, word.Length - fcSepx - 2)), context);
            }

            sections.Add((end, sep));
        }

        if (sections.Count == 0 || sections[^1].End < ccpMain)
        {
            sections.Add((ccpMain, sections.Count > 0 ? sections[^1].Sep : DocProperties.DefaultSep()));
        }

        HeaderStories headers = ReadHeaderStories();
        int start = 0;
        for (int i = 0; i < sections.Count; i++)
        {
            (int end, DocSep sep) = sections[i];
            if (end <= start && i < sections.Count - 1)
            {
                continue;
            }

            Section section = document.Sections.AddTrusted(new Section(document));
            section.PageSetup.CopyRaw(sep.Setup);
            if (sep.PageRestart)
            {
                section.PageSetup.PageNumberStart = sep.PageStart;
            }

            ReadHeaders(section, headers, i);
            StoryBuilder story = new(this, section.Body.Blocks, StoryKind.Main);
            story.Read(start, Math.Max(start, end));
            story.Finish();
            start = Math.Max(start, end);
        }

        if (document.Sections[^1].Body.Blocks.Count == 0)
        {
            document.Sections[^1].Body.Blocks.AddTrusted(new Paragraph(document));
        }
    }

    // ---- Properties of the text ----

    /// <summary>
    /// Where the paragraph holding <paramref name="cp"/> ends ([MS-DOC] 2.4.2): the CP of its mark, found from the PAPX run
    /// holding its bytes, piece after piece; when the formatting pages do not say, the next paragraph mark in the text.
    /// </summary>
    private int ParagraphEnd(int cp, int limit)
    {
        int at = cp;
        for (int guard = 0; at < limit && guard < 1 << 16; guard++)
        {
            int index = text.PieceAt(at);
            if (index < 0)
            {
                break;
            }

            DocPiece piece = text.Pieces[index];
            long fc = piece.Fc + ((long)(at - piece.Cp) * piece.Width);
            int run = papx.Find(fc);
            if (run < 0)
            {
                break;
            }

            long fcEnd = papx[run].To;
            if (fcEnd <= piece.FcEnd)
            {
                int end = piece.Cp + (int)((fcEnd - piece.Fc + piece.Width - 1) / piece.Width) - 1;
                return Math.Clamp(end, cp, limit - 1);
            }

            at = piece.CpEnd;
        }

        // The formatting pages do not cover the text: the next paragraph or cell mark.
        char[] chars = text.Chars;
        for (int i = cp; i < limit; i++)
        {
            if (chars[i] is '\r' or '\a')
            {
                return i;
            }
        }

        return limit - 1;
    }

    /// <summary>The properties of the paragraph whose mark is at <paramref name="mark"/>, and its row's table sprms.</summary>
    private (DocPap Pap, DocRow Row) ParagraphProperties(int mark)
    {
        DocPap pap = new();
        DocRow row = new(fib.IsWord6);
        long fc = text.FcAt(mark);
        int run = fc < 0 ? -1 : papx.Find(fc);
        if (run >= 0)
        {
            byte[] block = papx[run].Block;
            if (block.Length >= 2)
            {
                pap.Style = BinaryPrimitives.ReadUInt16LittleEndian(block);
                DocProperties.ApplyPap(pap, block.AsSpan(2), context, row);
            }
        }

        if (pap.HugePapx >= 0 && data is not null && pap.HugePapx + 2 <= data.Length)
        {
            // sprmPHugePapx: the grpprl is too big for a page, so it lies in the Data stream, after its size.
            int cb = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)pap.HugePapx));
            DocProperties.ApplyPap(pap, data.AsSpan((int)pap.HugePapx + 2, (int)Math.Min(cb, data.Length - pap.HugePapx - 2)), context, row);
        }

        int piece = text.PieceAt(mark);
        if (piece >= 0 && text.Pieces[piece].Prm != 0)
        {
            foreach ((ushort sprm, byte[] operand) in PrmSprms(text.Pieces[piece].Prm))
            {
                if ((sprm & 0x1C00) == 0x1400)
                {
                    row.Add(sprm, operand);
                }
                else
                {
                    DocProperties.ApplyPap(pap, sprm, operand, context);
                }
            }
        }

        return (pap, row);
    }

    /// <summary>The character properties at <paramref name="cp"/> in a paragraph of style <paramref name="paragraphStyle"/>.</summary>
    private DocChp CharacterProperties(int cp, int paragraphStyle)
    {
        long fc = text.FcAt(cp);
        int run = fc < 0 ? -1 : chpx.Find(fc);
        int piece = text.PieceAt(cp);
        ushort prm = piece >= 0 ? text.Pieces[piece].Prm : (ushort)0;
        if (chpCache.TryGetValue((run, prm, paragraphStyle), out DocChp? cached))
        {
            return cached;
        }

        DocChp chp = new();
        RunFormat paragraph = styles.Resolved(paragraphStyle);
        bool Style(WordRunProperty property)
        {
            RunFormat format = paragraph;
            if (chp.Style >= 0)
            {
                format = new RunFormat();
                format.Apply(paragraph);
                format.Apply(styles.Resolved(chp.Style));
            }

            return DocProperties.ToggleOf(format, property);
        }

        if (run >= 0)
        {
            DocProperties.ApplyChp(chp, chpx[run].Block, context, Style);
        }

        if (prm != 0)
        {
            foreach ((ushort sprm, byte[] operand) in PrmSprms(prm))
            {
                DocProperties.ApplyChp(chp, sprm, operand, context, Style);
            }
        }

        if (chpCache.Count < 100_000)
        {
            chpCache[(run, prm, paragraphStyle)] = chp;
        }

        return chp;
    }

    /// <summary>
    /// A piece's Prm ([MS-DOC] 2.9.213 to 2.9.215): a Prm1 (bit 0 set) names one of the Clx's Prcs by index (bits 1-15); a
    /// Prm0 is one sprm, by isprm (bits 1-7), with its operand byte (bits 8-15). A Word 6 file's isprm is its own sprm.
    /// </summary>
    private List<(ushort Sprm, byte[] Operand)> PrmSprms(ushort prm)
    {
        if ((prm & 1) != 0)
        {
            int index = prm >> 1;
            if (index >= text.Prcs.Count)
            {
                warnings.Add(ConversionWarningKind.FormattingDropped, "A fast-saved piece names property changes the document does not have; they are left out.");
                return [];
            }

            List<(ushort Sprm, byte[] Operand)> found = [];
            DocSprms sprms = new(text.Prcs[index], fib.IsWord6);
            foreach (DocSprms prl in sprms)
            {
                found.Add((prl.Sprm, prl.Operand.ToArray()));
            }

            return found;
        }

        byte isprm = (byte)((prm >> 1) & 0x7F);
        byte value = (byte)(prm >> 8);
        if (fib.IsWord6)
        {
            return DocSprms.Word6(isprm) is (1, var same and not 0) ? [(same, [value])] : [];
        }

        ushort? sprm = isprm switch
        {
            0x04 => 0x2602,
            0x05 => 0x2403,
            0x07 => 0x2405,
            0x08 => 0x2406,
            0x09 => 0x2407,
            0x0C => 0x260A,
            0x0D => 0x2470,
            0x0E => 0x240C,
            0x18 => 0x2416,
            0x19 => 0x2417,
            0x1D => 0x261B,
            0x25 => 0x2423,
            0x2C => 0x242A,
            0x32 => 0x2430,
            0x33 => 0x2431,
            0x41 => 0x0800,
            0x42 => 0x0801,
            0x43 => 0x0802,
            0x47 => 0x0806,
            0x4B => 0x080A,
            0x4D => 0x2A0C,
            0x4E => 0x0858,
            0x51 => 0x0818,
            0x53 => 0x2A33,
            0x55 => 0x0835,
            0x56 => 0x0836,
            0x57 => 0x0837,
            0x58 => 0x0838,
            0x59 => 0x0839,
            0x5A => 0x083A,
            0x5B => 0x083B,
            0x5C => 0x083C,
            0x5E => 0x2A3E,
            0x62 => 0x2A42,
            0x68 => 0x2A48,
            0x73 => 0x2A53,
            0x74 => 0x0854,
            0x75 => 0x0855,
            0x76 => 0x0856,
            0x78 => 0x2640,
            _ => null,
        };
        return sprm is { } code ? [(code, [value])] : [];
    }

    private void Enter()
    {
        if (++nesting > limits.MaxDepth || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            nesting--;
            throw new DocumentLimitException($"The Word document's parts nest deeper than {limits.MaxDepth}.");
        }
    }

    private void Leave()
    {
        nesting--;
    }

    /// <summary>A revision from a run's revision marks, or null.</summary>
    private Revision? RevisionOf(DocChp chp)
    {
        if (chp.Deleted)
        {
            return new Revision(RevisionKind.Deletion, Author(chp.DeleteAuthor), WordProperties.Date(chp.DeleteDate));
        }

        return chp.Inserted ? new Revision(RevisionKind.Insertion, Author(chp.InsertAuthor), WordProperties.Date(chp.InsertDate)) : null;
    }

    // ---- STTBs ----

    /// <summary>
    /// An STTB ([MS-DOC] 2.2.4): Word 97's starts with $FFFF for UTF-16 strings, then the count and the size of each
    /// string's extra data; each string is its length then its characters. Word 6's is its size in bytes, then each string
    /// as a length byte and 8-bit characters.
    /// </summary>
    private List<(string Text, byte[] Extra)> Sttb(ReadOnlySpan<byte> sttb)
    {
        List<(string, byte[])> found = [];
        if (sttb.Length < 2)
        {
            return found;
        }

        if (fib.IsWord6)
        {
            int end = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(sttb), sttb.Length);
            for (int at = 2; at < end && found.Count < limits.MaxEntries;)
            {
                int length = Math.Min(sttb[at], end - at - 1);
                found.Add((Latin(sttb.Slice(at + 1, length)), []));
                at += 1 + sttb[at];
            }

            return found;
        }

        bool extended = BinaryPrimitives.ReadUInt16LittleEndian(sttb) == 0xFFFF;
        int offset = extended ? 2 : 0;
        if (sttb.Length < offset + 4)
        {
            return found;
        }

        int count = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(sttb[offset..]), limits.MaxEntries);
        int cbExtra = BinaryPrimitives.ReadUInt16LittleEndian(sttb[(offset + 2)..]);
        int pos = offset + 4;
        for (int i = 0; i < count && pos < sttb.Length; i++)
        {
            string value;
            if (extended)
            {
                if (pos + 2 > sttb.Length)
                {
                    break;
                }

                int cch = BinaryPrimitives.ReadUInt16LittleEndian(sttb[pos..]);
                int length = Math.Min(cch, (sttb.Length - pos - 2) / 2);
                StringBuilder builder = new(length);
                for (int c = 0; c < length; c++)
                {
                    builder.Append((char)BinaryPrimitives.ReadUInt16LittleEndian(sttb[(pos + 2 + (2 * c))..]));
                }

                value = builder.ToString();
                pos += 2 + (2 * cch);
            }
            else
            {
                int length = Math.Min(sttb[pos], sttb.Length - pos - 1);
                value = Latin(sttb.Slice(pos + 1, length));
                pos += 1 + sttb[pos];
            }

            byte[] extra = pos + cbExtra <= sttb.Length ? sttb.Slice(pos, cbExtra).ToArray() : [];
            pos += cbExtra;
            found.Add((value, extra));
        }

        return found;
    }

    private static string Latin(ReadOnlySpan<byte> bytes)
    {
        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>A PLC's CPs ([MS-DOC] 2.2.2): n + 1 of them, then n data elements of <paramref name="size"/> bytes.</summary>
    private static int PlcCount(ReadOnlySpan<byte> plc, int size)
    {
        return plc.Length >= 4 + 4 + size ? (plc.Length - 4) / (4 + size) : 0;
    }

    private static int PlcCp(ReadOnlySpan<byte> plc, int index)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * index)..]);
    }

    private static ReadOnlySpan<byte> PlcData(ReadOnlySpan<byte> plc, int count, int size, int index)
    {
        return plc.Slice((4 * (count + 1)) + (size * index), size);
    }
}
