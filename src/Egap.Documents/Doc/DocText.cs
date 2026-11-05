using System.Buffers.Binary;
using System.Text;

namespace Egap.Documents.Doc;

/// <summary>
/// One piece of the text ([MS-DOC] 2.9.177, Pcd): the characters from <see cref="Cp"/> up to <see cref="CpEnd"/>, stored
/// from <see cref="Fc"/> in the WordDocument stream, one byte each when <see cref="Compressed"/>, otherwise two.
/// </summary>
/// <param name="Cp">The piece's first character position.</param>
/// <param name="CpEnd">The character position after its last.</param>
/// <param name="Fc">The stream offset of its first character.</param>
/// <param name="Compressed">Whether its characters are 8-bit.</param>
/// <param name="Prm">The fast save's property changes to its text (a Prm0 or Prm1).</param>
internal readonly record struct DocPiece(int Cp, int CpEnd, long Fc, bool Compressed, ushort Prm)
{
    /// <summary>The bytes each character takes.</summary>
    public int Width => Compressed ? 1 : 2;

    /// <summary>The stream offset after the piece's last character.</summary>
    public long FcEnd => Fc + ((long)(CpEnd - Cp) * Width);
}

/// <summary>
/// The document's text ([MS-DOC] 2.4.1): the piece table from the Clx, the property changes (Prcs) a fast save left, and
/// every character from CP 0 to the end of the last part, decoded once.
/// </summary>
internal sealed class DocText
{
    private static readonly char[] Windows1252 = Table(1252);
    private static char[]? macRoman;

    private DocText(List<DocPiece> pieces, List<byte[]> prcs, char[] chars)
    {
        Pieces = pieces;
        Prcs = prcs;
        Chars = chars;
    }

    /// <summary>The pieces, in CP order.</summary>
    public List<DocPiece> Pieces { get; }

    /// <summary>The Clx's Prcs, which a Prm1 names by index.</summary>
    public List<byte[]> Prcs { get; }

    /// <summary>Every character of the text, by CP.</summary>
    public char[] Chars { get; }

    /// <summary>The number of characters in the text.</summary>
    public int Length => Chars.Length;

    /// <summary>
    /// Reads the pieces from the Clx (or, for a Word 6 file that has none, one piece from fcMin) and decodes up to
    /// <paramref name="length"/> characters.
    /// </summary>
    public static DocText Read(byte[] document, DocFib fib, ReadOnlySpan<byte> clx, int length, WarningCollector warnings)
    {
        List<byte[]> prcs = [];
        List<DocPiece> pieces = ReadPieces(clx, fib.IsWord6, prcs);
        if (pieces.Count == 0 && fib.IsWord6)
        {
            // A Word 6 document that was not fast saved has no piece table: its text is one run of bytes from fcMin.
            pieces.Add(new DocPiece(0, length, fib.FcMin, true, 0));
        }

        if (pieces.Count == 0)
        {
            throw new DocumentException("The Word document has no piece table, so its text cannot be found.");
        }

        // Pieces in CP order without overlaps: a damaged table's out-of-order or overlapping pieces are cut.
        pieces.Sort((a, b) => a.Cp.CompareTo(b.Cp));
        List<DocPiece> clean = new(pieces.Count);
        int next = 0;
        foreach (DocPiece piece in pieces)
        {
            int cp = Math.Max(piece.Cp, next);
            int end = Math.Min(piece.CpEnd, length);
            if (end <= cp)
            {
                continue;
            }

            long fc = piece.Fc + ((long)(cp - piece.Cp) * piece.Width);
            long room = Math.Max(0, (document.Length - fc) / piece.Width);
            if (room < end - cp)
            {
                warnings.Add(ConversionWarningKind.FormattingDropped, "A piece of the text lies past the end of the WordDocument stream; it is cut there.");
                end = cp + (int)room;
                if (end <= cp)
                {
                    continue;
                }
            }

            clean.Add(piece with { Cp = cp, CpEnd = end, Fc = fc });
            next = end;
        }

        int total = clean.Count == 0 ? 0 : clean[^1].CpEnd;
        char[] chars = new char[total];
        char[] bytes = fib.IsMac ? MacRoman : Windows1252;
        foreach (DocPiece piece in clean)
        {
            ReadOnlySpan<byte> data = document.AsSpan((int)piece.Fc, (int)(piece.FcEnd - piece.Fc));
            Span<char> target = chars.AsSpan(piece.Cp, piece.CpEnd - piece.Cp);
            if (piece.Compressed)
            {
                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = bytes[data[i]];
                }
            }
            else
            {
                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(data[(2 * i)..]);
                }
            }
        }

        return new DocText(clean, prcs, chars);
    }

    /// <summary>The index of the piece holding <paramref name="cp"/>, or -1.</summary>
    public int PieceAt(int cp)
    {
        int low = 0;
        int high = Pieces.Count - 1;
        while (low <= high)
        {
            int middle = (low + high) >>> 1;
            DocPiece piece = Pieces[middle];
            if (piece.CpEnd <= cp)
            {
                low = middle + 1;
            }
            else if (piece.Cp > cp)
            {
                high = middle - 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }

    /// <summary>The stream offset of the character at <paramref name="cp"/>, or -1 when no piece holds it.</summary>
    public long FcAt(int cp)
    {
        int index = PieceAt(cp);
        if (index < 0)
        {
            return -1;
        }

        DocPiece piece = Pieces[index];
        return piece.Fc + ((long)(cp - piece.Cp) * piece.Width);
    }

    /// <summary>The text from <paramref name="cp"/> up to <paramref name="end"/>, cut to the text's length.</summary>
    public string Slice(int cp, int end)
    {
        cp = Math.Clamp(cp, 0, Chars.Length);
        end = Math.Clamp(end, cp, Chars.Length);
        return new string(Chars, cp, end - cp);
    }

    // The Clx ([MS-DOC] 2.9.38): Prcs (0x01, a size and a grpprl), then the Pcdt (0x02, a size, the PlcPcd).
    private static List<DocPiece> ReadPieces(ReadOnlySpan<byte> clx, bool word6, List<byte[]> prcs)
    {
        List<DocPiece> pieces = [];
        int at = 0;
        while (at + 3 <= clx.Length && clx[at] == 0x01)
        {
            int size = BinaryPrimitives.ReadInt16LittleEndian(clx[(at + 1)..]);
            if (size < 0)
            {
                return pieces;
            }

            prcs.Add(clx.Slice(at + 3, Math.Min(size, clx.Length - at - 3)).ToArray());
            at += 3 + size;
        }

        if (at + 5 > clx.Length || clx[at] != 0x02)
        {
            return pieces;
        }

        int lcb = BinaryPrimitives.ReadInt32LittleEndian(clx[(at + 1)..]);
        ReadOnlySpan<byte> plc = clx.Slice(at + 5, Math.Clamp(lcb, 0, clx.Length - at - 5));
        int count = (plc.Length - 4) / 12;
        for (int i = 0; i < count; i++)
        {
            int cp = BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * i)..]);
            int end = BinaryPrimitives.ReadInt32LittleEndian(plc[(4 * (i + 1))..]);
            ReadOnlySpan<byte> pcd = plc[((4 * (count + 1)) + (8 * i))..];
            uint fcCompressed = BinaryPrimitives.ReadUInt32LittleEndian(pcd[2..]);
            ushort prm = BinaryPrimitives.ReadUInt16LittleEndian(pcd[6..]);
            if (cp < 0 || end <= cp)
            {
                continue;
            }

            if (word6)
            {
                // Word 6's pieces are 8-bit text at a plain offset.
                pieces.Add(new DocPiece(cp, end, fcCompressed, true, prm));
                continue;
            }

            bool compressed = (fcCompressed & 0x40000000) != 0;
            long fc = fcCompressed & 0x3FFFFFFF;
            pieces.Add(new DocPiece(cp, end, compressed ? fc / 2 : fc, compressed, prm));
        }

        return pieces;
    }

    private static char[] MacRoman => macRoman ??= Table(10000);

    // An 8-bit code page as a table; bytes below $80 are ASCII, and a byte the code page leaves out stays itself.
    private static char[] Table(int codePage)
    {
        char[] table = new char[256];
        Encoding? encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage);
        byte[] one = new byte[1];
        for (int i = 0; i < 256; i++)
        {
            one[0] = (byte)i;
            string decoded = i < 0x80 || encoding is null ? ((char)i).ToString() : encoding.GetString(one);
            table[i] = decoded.Length == 1 && decoded[0] != '\uFFFD' ? decoded[0] : (char)i;
        }

        return table;
    }
}
