using System.Buffers.Binary;

namespace Egap.Documents.Doc;

/// <summary>The tables the FIB points at, by their index in FibRgFcLcb97 ([MS-DOC] 2.5.6).</summary>
internal enum DocTable
{
    Stshf = 1,
    PlcffndRef = 2,
    PlcffndTxt = 3,
    PlcfandRef = 4,
    PlcfandTxt = 5,
    PlcfSed = 6,
    PlcfHdd = 11,
    PlcfBteChpx = 12,
    PlcfBtePapx = 13,
    SttbfFfn = 15,
    PlcfFldMom = 16,
    PlcfFldHdr = 17,
    PlcfFldFtn = 18,
    PlcfFldAtn = 19,
    SttbfBkmk = 21,
    PlcfBkf = 22,
    PlcfBkl = 23,
    Dop = 31,
    Clx = 33,
    GrpXstAtnOwners = 36,
    SttbfAtnBkmk = 37,
    PlcfAtnBkf = 42,
    PlcfAtnBkl = 43,
    PlcfendRef = 46,
    PlcfendTxt = 47,
    PlcfFldEdn = 48,
    SttbfRMark = 51,
    PlcftxbxTxt = 56,
    PlcfFldTxbx = 57,
    PlcfHdrtxbxTxt = 58,
    PlcfFldHdrTxbx = 59,
    PlfLst = 73,
    PlfLfo = 74,
    SttbListNames = 92,
    AtrdExtra = 113,
}

/// <summary>
/// The file information block at the start of the WordDocument stream ([MS-DOC] 2.5.1): the version, the flags, the
/// character counts of each part of the text and where each table lies in the table stream. Word 6 and Word 95 files
/// (nFib $65 to $68) keep a shorter FIB of their own, with the counts at $34 and the same table pairs from $58 up to the
/// comment bookmarks.
/// </summary>
internal sealed class DocFib
{
    /// <summary>The wIdent of a Word 97 to 2003 document.</summary>
    public const ushort WordIdent = 0xA5EC;

    /// <summary>The wIdent of a Word 6 or Word 95 document.</summary>
    public const ushort Word6Ident = 0xA5DC;

    /// <summary>The first nFib of Word 97.</summary>
    public const ushort Word97 = 0x00C1;

    /// <summary>The first nFib of Word 6.</summary>
    public const ushort Word6 = 0x0065;

    private readonly byte[] stream;
    private readonly int lwBase;
    private readonly int pairBase;
    private readonly int pairCount;

    private DocFib(byte[] stream, ushort nFib, ushort flags)
    {
        this.stream = stream;
        NFib = nFib;
        Flags = flags;
        IsWord6 = nFib < Word97;
        if (IsWord6)
        {
            lwBase = 0x34 - 12;
            pairBase = 0x58;
            pairCount = 38;
            return;
        }

        // FibBase (32 bytes), then csw and FibRgW97, cslw and FibRgLw97, cbRgFcLcb and FibRgFcLcb.
        int at = 32;
        int csw = Word(at);
        at += 2 + (2 * csw);
        int cslw = Word(at);
        lwBase = at + 2;
        at = lwBase + (4 * cslw);
        pairCount = Word(at);
        pairBase = at + 2;
    }

    /// <summary>The FIB's version number.</summary>
    public ushort NFib { get; }

    /// <summary>The FIB's flags at $0A: fDot, fComplex, fEncrypted, fWhichTblStm, fObfuscated and others.</summary>
    public ushort Flags { get; }

    /// <summary>Whether the file is a Word 6 or Word 95 document.</summary>
    public bool IsWord6 { get; }

    /// <summary>Whether the file is password-protected (encrypted or obfuscated).</summary>
    public bool IsEncrypted => (Flags & 0x0100) != 0;

    /// <summary>Whether the protection is XOR obfuscation rather than RC4.</summary>
    public bool IsObfuscated => (Flags & 0x8000) != 0;

    /// <summary>Whether the file is a template (fDot).</summary>
    public bool IsTemplate => (Flags & 0x0001) != 0;

    /// <summary>The table stream's name: 1Table when fWhichTblStm is set, otherwise 0Table.</summary>
    public string TableStreamName => (Flags & 0x0200) != 0 ? "1Table" : "0Table";

    /// <summary>Whether a Word 6 document's 8-bit text is Mac OS Roman (chse 256) rather than Windows-1252.</summary>
    public bool IsMac => IsWord6 && stream.Length >= 0x16 && BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(0x14)) == 256;

    /// <summary>A Word 6 document's first byte of text, where the text starts when it has no piece table.</summary>
    public long FcMin => stream.Length >= 0x1C ? BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(0x18)) : 0;

    /// <summary>The main text's character count.</summary>
    public int CcpText => Ccp(3);

    /// <summary>The footnotes' character count.</summary>
    public int CcpFtn => Ccp(4);

    /// <summary>The headers' character count.</summary>
    public int CcpHdd => Ccp(5);

    /// <summary>The macros' character count (not used since Word 97, but counted in Word 6).</summary>
    public int CcpMcr => Ccp(6);

    /// <summary>The comments' character count.</summary>
    public int CcpAtn => Ccp(7);

    /// <summary>The endnotes' character count.</summary>
    public int CcpEdn => Ccp(8);

    /// <summary>The text boxes' character count.</summary>
    public int CcpTxbx => Ccp(9);

    /// <summary>The header text boxes' character count.</summary>
    public int CcpHdrTxbx => Ccp(10);

    /// <summary>Reads the FIB of a WordDocument stream.</summary>
    /// <exception cref="DocumentException">The stream does not start with a Word FIB, or one older than Word 6.</exception>
    public static DocFib Read(byte[] stream)
    {
        if (stream.Length < 0x60)
        {
            throw new DocumentException("The file is not a Word document: its WordDocument stream is shorter than a FIB.");
        }

        ushort ident = BinaryPrimitives.ReadUInt16LittleEndian(stream);
        ushort nFib = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(2));
        if (ident is not (WordIdent or Word6Ident))
        {
            throw new DocumentException("The file is not a Word document: its WordDocument stream does not start with a FIB.");
        }

        if (nFib < Word6)
        {
            throw new DocumentException($"The file is a Word document older than Word 6 (nFib {nFib}), which Egap does not read.");
        }

        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(0x0A));
        DocFib fib = new(stream, nFib, flags);
        if (!fib.IsWord6 && fib.pairBase + (8 * Math.Min(fib.pairCount, 34)) > stream.Length)
        {
            throw new DocumentException("The Word document's FIB is cut short.");
        }

        return fib;
    }

    /// <summary>
    /// Where a table lies in the table stream (or, for Word 6, the WordDocument stream): its offset and length, or (0, 0)
    /// when the FIB has no such pair or the pair is empty.
    /// </summary>
    public (long Fc, long Lcb) Pair(DocTable table)
    {
        int index = (int)table;
        if (IsWord6)
        {
            // Word 6's endnotes sit after its own fields ([MS-DOC] has no Word 6 FIB; found in Word 6 and 95 files).
            int at = table switch
            {
                DocTable.PlcfendRef => 0x1D2,
                DocTable.PlcfendTxt => 0x1DA,
                _ when index < pairCount => pairBase + (8 * index),
                _ => -1,
            };
            return at < 0 ? (0, 0) : PairAt(at);
        }

        return index < pairCount ? PairAt(pairBase + (8 * index)) : (0, 0);
    }

    private (long Fc, long Lcb) PairAt(int at)
    {
        if (at < 0 || at + 8 > stream.Length)
        {
            return (0, 0);
        }

        long fc = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at));
        long lcb = BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(at + 4));
        return lcb == 0 ? (0, 0) : (fc, lcb);
    }

    // The FibRgLw97 longs from cbMac (0): ccpText is the fourth. Word 6's counts start at $34.
    private int Ccp(int index)
    {
        int at = lwBase + (4 * index);
        return at >= 0 && at + 4 <= stream.Length ? Math.Max(0, BinaryPrimitives.ReadInt32LittleEndian(stream.AsSpan(at))) : 0;
    }

    private int Word(int at)
    {
        return at + 2 <= stream.Length ? BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(at)) : 0;
    }
}
