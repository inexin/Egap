using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Office;

namespace Egap.Documents.Tests;

/// <summary>The parts of a binary Word document's text, in the order their CPs follow each other.</summary>
internal enum DocStory
{
    Main,
    Footnotes,
    Headers,
    Comments,
    Endnotes,
    TextBoxes,
}

/// <summary>
/// Builds small binary Word documents ([MS-DOC]) for the reader's tests: the text of each part with its character and
/// paragraph sprms, a style sheet, fonts, sections, notes, comments, bookmarks, fields and lists, written as a Word 97 file
/// (WordDocument and 1Table streams) or a Word 6 file (everything in the WordDocument stream, one-byte sprms).
/// </summary>
internal sealed class DocBuilder
{
    private const int TextStart = 0x800;

    private readonly StringBuilder[] stories = [new(), new(), new(), new(), new(), new()];
    private readonly List<(DocStory Story, int Start, int End, byte[] Grpprl)> runs = [];
    private readonly List<(DocStory Story, int Mark, int Istd, byte[] Grpprl)> paragraphs = [];
    private readonly Dictionary<int, (byte[] Data, byte[] Tail)> tables = [];

    /// <summary>A Word 6 file: 8-bit text, Word 6's FIB, style sheet, STTBs and one-byte sprms.</summary>
    public bool Word6 { get; init; }

    /// <summary>The text as 8-bit Windows-1252 rather than UTF-16.</summary>
    public bool Compressed { get; init; }

    /// <summary>The FIB's flags word (fEncrypted 0x100, fObfuscated 0x8000); fWhichTblStm is set for Word 97.</summary>
    public ushort Flags { get; init; }

    /// <summary>The part text and sprms go into.</summary>
    public DocStory Story { get; set; }

    /// <summary>The style sheet's styles; istd is the index. The default holds Normal and Default Paragraph Font.</summary>
    public List<DocStyleDef?> Styles { get; } =
    [
        new DocStyleDef("Normal", Sti: 0, Kind: 1, Base: 0xFFF),
        null, null, null, null, null, null, null, null, null,
        new DocStyleDef("Default Paragraph Font", Sti: 65, Kind: 2, Base: 0xFFF),
    ];

    /// <summary>The font table; a sprm's ftc indexes it.</summary>
    public List<string> Fonts { get; } = ["Times New Roman", "Symbol", "Arial"];

    /// <summary>Where the text is split into pieces, with each piece's Prm (the first piece starts at 0).</summary>
    public List<(int Cp, ushort Prm)> Pieces { get; } = [(0, 0)];

    /// <summary>The property changes (Prcs) a Prm1 names by index.</summary>
    public List<byte[]> Prcs { get; } = [];

    /// <summary>Each section's end CP and its SEPX grpprl; none gives one section with no properties.</summary>
    public List<(int End, byte[] Sepx)> Sections { get; } = [];

    /// <summary>The DOP's bytes; null leaves the document without one.</summary>
    public byte[]? Dop { get; set; }

    /// <summary>The Data stream (pictures), or null for none.</summary>
    public byte[]? Data { get; set; }

    /// <summary>Bytes at the start of the table stream before the tables (an encryption header), and the FIB's lKey.</summary>
    public byte[] TableHeader { get; init; } = [];

    /// <summary>The FIB's lKey: an XOR password verifier or the size of the encryption header.</summary>
    public uint LKey { get; init; }

    /// <summary>Changes the finished WordDocument, table and Data streams (encrypts them) before they go in the file.</summary>
    public Action<byte[], byte[], byte[]?>? Transform { get; init; }

    /// <summary>The current CP in <see cref="Story"/>, from that part's start.</summary>
    public int Cp => stories[(int)Story].Length;

    public static byte[] Sprm(ushort sprm, params byte[] operand)
    {
        return [(byte)sprm, (byte)(sprm >> 8), .. operand];
    }

    public static byte[] Sprm16(ushort sprm, int value)
    {
        return Sprm(sprm, (byte)value, (byte)(value >> 8));
    }

    public static byte[] Sprm32(ushort sprm, int value)
    {
        return Sprm(sprm, (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24));
    }

    /// <summary>Text with character sprms (Word 6: one-byte sprm codes given as the low byte).</summary>
    public DocBuilder Run(string text, params byte[][] sprms)
    {
        int start = Cp;
        stories[(int)Story].Append(text);
        byte[] grpprl = [.. sprms.SelectMany(s => s)];
        if (grpprl.Length > 0)
        {
            runs.Add((Story, start, Cp, grpprl));
        }

        return this;
    }

    /// <summary>Ends a paragraph with <paramref name="mark"/> (a paragraph mark, or a cell mark), its style and sprms.</summary>
    public DocBuilder Paragraph(int istd = 0, params byte[][] sprms)
    {
        return End('\r', istd, sprms);
    }

    public DocBuilder End(char mark, int istd = 0, params byte[][] sprms)
    {
        int at = Cp;
        stories[(int)Story].Append(mark);
        paragraphs.Add((Story, at, istd, [.. sprms.SelectMany(s => s)]));
        return this;
    }

    /// <summary>A table the FIB points at, by its FibRgFcLcb index, with bytes after it the FIB does not count (a PlfLst's LVLs).</summary>
    public DocBuilder Table(int index, byte[] data, byte[]? tail = null)
    {
        tables[index] = (data, tail ?? []);
        return this;
    }

    /// <summary>The CP in the whole text of <paramref name="cp"/> in <paramref name="story"/>.</summary>
    public int Global(DocStory story, int cp)
    {
        int at = 0;
        for (int i = 0; i < (int)story; i++)
        {
            at += stories[i].Length;
        }

        return at + cp;
    }

    /// <summary>A PLC: n + 1 CPs, then n data elements.</summary>
    public static byte[] Plc(int[] cps, params byte[][] data)
    {
        List<byte> bytes = [];
        foreach (int cp in cps)
        {
            bytes.AddRange(BitConverter.GetBytes(cp));
        }

        foreach (byte[] item in data)
        {
            bytes.AddRange(item);
        }

        return [.. bytes];
    }

    /// <summary>An STTB of UTF-16 strings (Word 6: a size word and 8-bit strings), each with its extra data.</summary>
    public byte[] Sttb(IReadOnlyList<string> strings, int extra = 0, Func<int, byte[]>? data = null)
    {
        List<byte> bytes = [];
        if (Word6)
        {
            foreach (string s in strings)
            {
                bytes.Add((byte)s.Length);
                bytes.AddRange(Encoding.Latin1.GetBytes(s));
            }

            return [.. BitConverter.GetBytes((ushort)(bytes.Count + 2)), .. bytes];
        }

        bytes.AddRange([0xFF, 0xFF]);
        bytes.AddRange(BitConverter.GetBytes((ushort)strings.Count));
        bytes.AddRange(BitConverter.GetBytes((ushort)extra));
        for (int i = 0; i < strings.Count; i++)
        {
            bytes.AddRange(BitConverter.GetBytes((ushort)strings[i].Length));
            bytes.AddRange(Encoding.Unicode.GetBytes(strings[i]));
            bytes.AddRange(data?.Invoke(i) ?? new byte[extra]);
        }

        return [.. bytes];
    }

    /// <summary>Writes the compound file.</summary>
    public byte[] Build()
    {
        string text = string.Concat(stories.Select(s => s.ToString()));
        int width = Compressed || Word6 ? 1 : 2;
        List<byte> word = [.. new byte[TextStart]];
        foreach (char c in text)
        {
            if (width == 1)
            {
                word.Add(c switch
                {
                    '\u20AC' => 0x80,
                    '\u201C' => 0x93,
                    '\u201D' => 0x94,
                    _ => Encoding.Latin1.GetBytes([c])[0],
                });
            }
            else
            {
                word.AddRange(BitConverter.GetBytes(c));
            }
        }

        long Fc(int cp) => TextStart + ((long)cp * width);

        // Formatted disk pages for the character runs and the paragraphs, each run its global byte range.
        List<(long From, long To, byte[] Grpprl)> characterRuns = [];
        int next = 0;
        foreach ((DocStory story, int start, int end, byte[] grpprl) in runs.Select(r => (r.Story, Global(r.Story, r.Start), Global(r.Story, r.End), r.Grpprl)).OrderBy(r => r.Item2))
        {
            if (start > next)
            {
                characterRuns.Add((Fc(next), Fc(start), []));
            }

            characterRuns.Add((Fc(start), Fc(end), grpprl));
            next = end;
        }

        if (next < text.Length)
        {
            characterRuns.Add((Fc(next), Fc(text.Length), []));
        }

        List<(long From, long To, byte[] Grpprl)> paragraphRuns = [];
        next = 0;
        foreach ((int mark, int istd, byte[] grpprl) in paragraphs.Select(p => (Global(p.Story, p.Mark), p.Istd, p.Grpprl)).OrderBy(p => p.Item1))
        {
            byte[] istdAndGrpprl = [(byte)istd, (byte)(istd >> 8), .. grpprl];
            paragraphRuns.Add((Fc(next), Fc(mark + 1), istdAndGrpprl));
            next = mark + 1;
        }

        Pad(word, 512);
        byte[] chpBte = Pages(word, characterRuns, paragraphs: false);
        byte[] papBte = Pages(word, paragraphRuns, paragraphs: true);

        // The tables: Word 97's in the table stream, Word 6's after the pages in the WordDocument stream.
        List<byte> table = Word6 ? word : [.. TableHeader];
        Dictionary<int, (long Fc, long Lcb)> pairs = [];
        void Put(int index, byte[] data)
        {
            if (data.Length > 0)
            {
                pairs[index] = (table.Count, data.Length);
                table.AddRange(data);
            }
        }

        Put(1, StyleSheet());
        Put(12, chpBte);
        Put(13, papBte);
        Put(15, FontTable());
        Put(6, SectionTable(word, text.Length));
        if (!Word6)
        {
            Put(33, Clx(width));
        }

        if (Dop is { } dop)
        {
            Put(31, dop);
        }

        foreach ((int index, (byte[] data, byte[] tail)) in tables)
        {
            Put(index, data);
            table.AddRange(tail);
        }

        byte[] fib = Word6 ? Fib6(pairs) : Fib97(pairs);
        for (int i = 0; i < fib.Length; i++)
        {
            word[i] = fib[i];
        }

        BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(word)[0x0E..], LKey);
        byte[] wordStream = [.. word];
        byte[] tableStream = [.. table];
        byte[]? dataStream = Data is { } dataBytes ? [.. dataBytes] : null;
        Transform?.Invoke(wordStream, tableStream, dataStream);
        List<(string, byte[])> streams = [("WordDocument", wordStream)];
        if (!Word6)
        {
            streams.Add(("1Table", tableStream));
            if (dataStream is not null)
            {
                streams.Add(("Data", dataStream));
            }
        }

        using MemoryStream output = new();
        CompoundFileWriter.Write(output, streams);
        return output.ToArray();
    }

    private static void Pad(List<byte> bytes, int size)
    {
        while (bytes.Count % size != 0)
        {
            bytes.Add(0);
        }
    }

    // FKPs ([MS-DOC] 2.9.62, 2.9.63): 512-byte pages of runs, written at the end of the WordDocument stream, and their
    // PlcBte: the runs' first FCs and the last FC, then each page's number.
    private byte[] Pages(List<byte> word, List<(long From, long To, byte[] Grpprl)> items, bool paragraphs)
    {
        List<long> fcs = [];
        List<int> pns = [];
        int entry = paragraphs ? (Word6 ? 7 : 13) : 1;
        int index = 0;
        while (index < items.Count)
        {
            byte[] page = new byte[512];
            int count = 0;
            int top = 511;
            List<(int Offset, int Size)> placed = [];
            while (index + count < items.Count)
            {
                byte[] grpprl = items[index + count].Grpprl;
                byte[] stored = Stored(grpprl, paragraphs);
                int newTop = (top - stored.Length) & ~1;
                int needed = (4 * (count + 2)) + (entry * (count + 1));
                if (newTop < needed || count >= 100)
                {
                    break;
                }

                top = newTop;
                stored.CopyTo(page, top);
                placed.Add((stored.Length == 0 && !paragraphs ? 0 : top / 2, stored.Length));
                count++;
            }

            for (int i = 0; i < count; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4 * i), (uint)items[index + i].From);
                page[(4 * (count + 1)) + (entry * i)] = (byte)placed[i].Offset;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(4 * count), (uint)items[index + count - 1].To);
            page[511] = (byte)count;
            fcs.Add(items[index].From);
            pns.Add(word.Count / 512);
            word.AddRange(page);
            index += count;
        }

        if (items.Count > 0)
        {
            fcs.Add(items[^1].To);
        }

        List<byte> plc = [];
        foreach (long fc in fcs)
        {
            plc.AddRange(BitConverter.GetBytes((uint)fc));
        }

        foreach (int pn in pns)
        {
            plc.AddRange(Word6 ? BitConverter.GetBytes((ushort)pn) : BitConverter.GetBytes((uint)pn));
        }

        return [.. plc];
    }

    // A CHPX: its size and the grpprl. A PapxInFkp: cb with 2 × cb − 1 bytes following, or 0, a second count and twice that;
    // Word 6's is a count of words and the words.
    private byte[] Stored(byte[] grpprl, bool paragraphs)
    {
        if (!paragraphs)
        {
            return grpprl.Length == 0 ? [] : [(byte)grpprl.Length, .. grpprl];
        }

        if (Word6)
        {
            byte[] even = grpprl.Length % 2 == 0 ? grpprl : [.. grpprl, 0];
            return [(byte)(even.Length / 2), .. even];
        }

        return grpprl.Length % 2 == 1
            ? [(byte)((grpprl.Length + 1) / 2), .. grpprl]
            : [0, (byte)(grpprl.Length / 2), .. grpprl];
    }

    // The Clx: the Prcs, then the Pcdt with the PlcPcd (CPs, then 8-byte PCDs: flags, fc, prm).
    private byte[] Clx(int width)
    {
        List<byte> clx = [];
        foreach (byte[] prc in Prcs)
        {
            clx.Add(0x01);
            clx.AddRange(BitConverter.GetBytes((ushort)prc.Length));
            clx.AddRange(prc);
        }

        int length = stories.Sum(s => s.Length);
        List<byte> plc = [];
        foreach ((int cp, _) in Pieces)
        {
            plc.AddRange(BitConverter.GetBytes(cp));
        }

        plc.AddRange(BitConverter.GetBytes(length));
        foreach ((int cp, ushort prm) in Pieces)
        {
            uint fc = (uint)(TextStart + (cp * width));
            if (width == 1)
            {
                fc = (fc * 2) | 0x40000000;
            }

            plc.AddRange([0, 0]);
            plc.AddRange(BitConverter.GetBytes(fc));
            plc.AddRange(BitConverter.GetBytes(prm));
        }

        clx.Add(0x02);
        clx.AddRange(BitConverter.GetBytes(plc.Count));
        clx.AddRange(plc);
        return [.. clx];
    }

    private byte[] SectionTable(List<byte> word, int length)
    {
        List<(int End, byte[] Sepx)> sections = Sections.Count > 0 ? Sections : [(stories[0].Length, [])];
        List<int> cps = [0, .. sections.Select(s => s.End)];
        List<byte[]> seds = [];
        foreach ((_, byte[] sepx) in sections)
        {
            uint fcSepx = 0xFFFFFFFF;
            if (sepx.Length > 0)
            {
                Pad(word, 2);
                fcSepx = (uint)word.Count;
                word.AddRange(BitConverter.GetBytes((ushort)sepx.Length));
                word.AddRange(sepx);
            }

            seds.Add([0, 0, .. BitConverter.GetBytes(fcSepx), 0, 0, 0, 0, 0, 0]);
        }

        return Plc([.. cps], [.. seds]);
    }

    // The STSH: the STSHI (cstd, the STD base size, flags, three ftcs), then each STD with its size.
    private byte[] StyleSheet()
    {
        List<byte> stsh = [];
        int baseSize = Word6 ? 8 : 10;
        byte[] stshi = Word6
            ? [.. BitConverter.GetBytes((ushort)Styles.Count), .. BitConverter.GetBytes((ushort)baseSize), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
            : [.. BitConverter.GetBytes((ushort)Styles.Count), .. BitConverter.GetBytes((ushort)baseSize), 1, 0, 0x5B, 0, 0x0F, 0, 0, 0, 0, 0, 0, 0, 2, 0];
        stsh.AddRange(BitConverter.GetBytes((ushort)stshi.Length));
        stsh.AddRange(stshi);
        foreach (DocStyleDef? style in Styles)
        {
            if (style is null)
            {
                stsh.AddRange([0, 0]);
                continue;
            }

            List<byte> std = [];
            std.AddRange(BitConverter.GetBytes((ushort)style.Sti));
            std.AddRange(BitConverter.GetBytes((ushort)(style.Kind | (style.Base << 4))));
            int upxCount = style.Kind == 1 ? 2 : 1;
            std.AddRange(BitConverter.GetBytes((ushort)(upxCount | (style.Next << 4))));
            std.AddRange([0, 0]);
            if (!Word6)
            {
                std.AddRange([0, 0]);
                std.AddRange(BitConverter.GetBytes((ushort)style.Name.Length));
                std.AddRange(Encoding.Unicode.GetBytes(style.Name));
                std.AddRange([0, 0]);
            }
            else
            {
                std.Add((byte)style.Name.Length);
                std.AddRange(Encoding.Latin1.GetBytes(style.Name));
                std.Add(0);
            }

            if (std.Count % 2 == 1)
            {
                std.Add(0);
            }

            if (style.Kind == 1)
            {
                Upx(std, [.. BitConverter.GetBytes((ushort)Styles.IndexOf(style)), .. style.Papx]);
            }

            Upx(std, style.Chpx);
            stsh.AddRange(BitConverter.GetBytes((ushort)std.Count));
            stsh.AddRange(std);
        }

        return [.. stsh];
    }

    private static void Upx(List<byte> std, byte[] upx)
    {
        std.AddRange(BitConverter.GetBytes((ushort)upx.Length));
        std.AddRange(upx);
        if (upx.Length % 2 == 1)
        {
            std.Add(0);
        }
    }

    // SttbfFfn: Word 97's count, then per font a size byte and an FFN with the name at 39; Word 6's size, then per font a
    // size byte, ffid, weight, charset, the alternate name's index and the 8-bit name.
    private byte[] FontTable()
    {
        List<byte> fonts = [];
        if (Word6)
        {
            foreach (string font in Fonts)
            {
                byte[] ffn = [0, 0x90, 0x01, 0, 0, .. Encoding.Latin1.GetBytes(font), 0];
                fonts.Add((byte)ffn.Length);
                fonts.AddRange(ffn);
            }

            return [.. BitConverter.GetBytes((ushort)(fonts.Count + 2)), .. fonts];
        }

        fonts.AddRange(BitConverter.GetBytes((ushort)Fonts.Count));
        fonts.AddRange([0, 0]);
        foreach (string font in Fonts)
        {
            byte[] ffn = [.. new byte[39], .. Encoding.Unicode.GetBytes(font), 0, 0];
            fonts.Add((byte)ffn.Length);
            fonts.AddRange(ffn);
        }

        return [.. fonts];
    }

    private byte[] Fib97(Dictionary<int, (long Fc, long Lcb)> pairs)
    {
        const int csw = 14;
        const int cslw = 22;
        const int cbRgFcLcb = 0x5D;
        byte[] fib = new byte[32 + 2 + (2 * csw) + 2 + (4 * cslw) + 2 + (8 * cbRgFcLcb) + 2];
        BinaryPrimitives.WriteUInt16LittleEndian(fib, 0xA5EC);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(2), 0x00C1);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(0x0A), (ushort)(Flags | 0x0200));
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(0x0C), 0x00BF);
        int at = 32;
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(at), csw);
        at += 2 + (2 * csw);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(at), cslw);
        int lw = at + 2;
        for (int i = 0; i < stories.Length; i++)
        {
            // ccpText, ccpFtn, ccpHdd, then (after ccpMcr) ccpAtn, ccpEdn and ccpTxbx.
            int index = i < 3 ? 3 + i : 4 + i;
            BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(lw + (4 * index)), stories[i].Length);
        }

        at = lw + (4 * cslw);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(at), cbRgFcLcb);
        at += 2;
        foreach ((int index, (long fc, long lcb)) in pairs)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fib.AsSpan(at + (8 * index)), (uint)fc);
            BinaryPrimitives.WriteUInt32LittleEndian(fib.AsSpan(at + (8 * index) + 4), (uint)lcb);
        }

        return fib;
    }

    private byte[] Fib6(Dictionary<int, (long Fc, long Lcb)> pairs)
    {
        byte[] fib = new byte[0x1E2];
        BinaryPrimitives.WriteUInt16LittleEndian(fib, 0xA5DC);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(2), 0x0065);
        BinaryPrimitives.WriteUInt16LittleEndian(fib.AsSpan(0x0A), Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(fib.AsSpan(0x18), TextStart);
        BinaryPrimitives.WriteUInt32LittleEndian(fib.AsSpan(0x1C), (uint)(TextStart + stories.Sum(s => s.Length)));
        for (int i = 0; i < stories.Length; i++)
        {
            int index = i < 3 ? i : i + 1;
            BinaryPrimitives.WriteInt32LittleEndian(fib.AsSpan(0x34 + (4 * index)), stories[i].Length);
        }

        foreach ((int index, (long fc, long lcb)) in pairs)
        {
            int at = index switch
            {
                46 => 0x1D2,
                47 => 0x1DA,
                _ => 0x58 + (8 * index),
            };
            BinaryPrimitives.WriteUInt32LittleEndian(fib.AsSpan(at), (uint)fc);
            BinaryPrimitives.WriteUInt32LittleEndian(fib.AsSpan(at + 4), (uint)lcb);
        }

        return fib;
    }
}

/// <summary>A style for <see cref="DocBuilder"/>: stk 1 paragraph, 2 character, 3 table; base and next are istds.</summary>
internal sealed record DocStyleDef(string Name, int Sti = 0xFFE, int Kind = 1, int Base = 0, int Next = 0)
{
    public byte[] Papx { get; init; } = [];

    public byte[] Chpx { get; init; } = [];
}
