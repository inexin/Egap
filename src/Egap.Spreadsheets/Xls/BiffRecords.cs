using System.Buffers.Binary;
using System.Text;

namespace Egap.Spreadsheets.Xls;

/// <summary>Record types of a BIFF8 workbook stream ([MS-XLS] 2.3).</summary>
internal static class BiffType
{
    public const int Formula = 0x0006;
    public const int Eof = 0x000A;
    public const int ExternSheet = 0x0017;
    public const int Name = 0x0018;
    public const int DateMode = 0x0022;
    public const int ExternName = 0x0023;
    public const int FilePass = 0x002F;
    public const int Font = 0x0031;
    public const int Continue = 0x003C;
    public const int DefColWidth = 0x0055;
    public const int Xct = 0x0059;
    public const int Crn = 0x005A;
    public const int ColInfo = 0x007D;
    public const int BoundSheet = 0x0085;
    public const int Palette = 0x0092;
    public const int StandardWidth = 0x0099;
    public const int MulRk = 0x00BD;
    public const int MulBlank = 0x00BE;
    public const int Xf = 0x00E0;
    public const int MergeCells = 0x00E5;
    public const int Sst = 0x00FC;
    public const int LabelSst = 0x00FD;
    public const int SupBook = 0x01AE;
    public const int Blank = 0x0201;
    public const int Number = 0x0203;
    public const int Label = 0x0204;
    public const int BoolErr = 0x0205;
    public const int String = 0x0207;
    public const int Row = 0x0208;
    public const int Array = 0x0221;
    public const int DefaultRowHeight = 0x0225;
    public const int Table = 0x0236;
    public const int Rk = 0x027E;
    public const int Format = 0x041E;
    public const int SharedFormula = 0x04BC;
    public const int Bof = 0x0809;
}

/// <summary>
/// Walks the records of a BIFF8 stream ([MS-XLS] 2.1.4): each a 2-byte type, a 2-byte size and that many bytes of data.
/// </summary>
internal sealed class BiffStream(byte[] data)
{
    private int next;

    /// <summary>The whole stream.</summary>
    public byte[] Data => data;

    /// <summary>The current record's type.</summary>
    public int Type { get; private set; }

    /// <summary>Where the current record's data starts.</summary>
    public int Start { get; private set; }

    /// <summary>The current record's size, cut to what the stream holds.</summary>
    public int Length { get; private set; }

    /// <summary>The current record's data.</summary>
    public ReadOnlySpan<byte> Body => data.AsSpan(Start, Length);

    /// <summary>Moves to the record at <paramref name="offset"/>; the next <see cref="Read"/> reads it.</summary>
    public void Seek(int offset) => next = offset;

    /// <summary>Reads the next record; false at the end of the stream.</summary>
    public bool Read()
    {
        if (next < 0 || next > data.Length - 4)
        {
            return false;
        }

        Type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(next));
        Start = next + 4;
        Length = Math.Min(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(next + 2)), data.Length - Start);
        next = Start + Length;
        return true;
    }

    /// <summary>The type of the record after the current one, or -1 at the end.</summary>
    public int PeekType() => next >= 0 && next <= data.Length - 4 ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(next)) : -1;

    /// <summary>The current record with the CONTINUE records that follow it, as one reader.</summary>
    public RecordReader Continued()
    {
        List<(int Start, int Length)> segments = [(Start, Length)];
        while (PeekType() == BiffType.Continue && Read())
        {
            segments.Add((Start, Length));
        }

        return new RecordReader(data, segments);
    }

    /// <summary>The current record alone, as a reader.</summary>
    public RecordReader Reader() => new(data, [(Start, Length)]);
}

/// <summary>
/// Reads a record's data, which may go on in CONTINUE records. A string that goes on in the next one starts that one with a
/// new option byte that says whether its characters are one byte or two ([MS-XLS] 2.5.293 and 2.1.4).
/// </summary>
internal sealed class RecordReader(byte[] data, List<(int Start, int Length)> segments)
{
    private int segment;
    private int position = segments[0].Start;
    private int end = segments[0].Start + segments[0].Length;

    /// <summary>The bytes left to read in all segments.</summary>
    public int Remaining
    {
        get
        {
            int left = end - position;
            for (int i = segment + 1; i < segments.Count; i++)
            {
                left += segments[i].Length;
            }

            return left;
        }
    }

    /// <summary>The bytes left in the current segment.</summary>
    public int SegmentRemaining => end - position;

    public byte Byte()
    {
        NextSegmentIfDone();
        if (position >= end)
        {
            throw new InvalidDataException("A record ends before its data does.");
        }

        return data[position++];
    }

    public ushort UInt16() => (ushort)(Byte() | (Byte() << 8));

    public short Int16() => (short)UInt16();

    public uint UInt32() => UInt16() | ((uint)UInt16() << 16);

    public double Double() => BitConverter.Int64BitsToDouble((long)UInt32() | ((long)UInt32() << 32));

    public void Skip(int count)
    {
        for (int i = 0; i < count; i++)
        {
            Byte();
        }
    }

    /// <summary>The next <paramref name="count"/> bytes, which must lie in one segment, as a span.</summary>
    public ReadOnlySpan<byte> Bytes(int count)
    {
        NextSegmentIfDone();
        if (count < 0 || count > end - position)
        {
            throw new InvalidDataException("A record ends before its data does.");
        }

        ReadOnlySpan<byte> span = data.AsSpan(position, count);
        position += count;
        return span;
    }

    /// <summary>The rest of the current segment.</summary>
    public ReadOnlySpan<byte> Rest() => Bytes(end - position);

    /// <summary>An XLUnicodeString: a 2-byte count, then the characters.</summary>
    public string UnicodeString() => Characters(UInt16());

    /// <summary>A ShortXLUnicodeString: a 1-byte count, then the characters.</summary>
    public string ShortUnicodeString() => Characters(Byte());

    /// <summary>
    /// <paramref name="count"/> characters after an option byte, with the rich-text runs and phonetic data an SST string may
    /// carry skipped ([MS-XLS] 2.5.293 XLUnicodeRichExtendedString).
    /// </summary>
    public string Characters(int count)
    {
        byte options = Byte();
        bool wide = (options & 0x01) != 0;
        int runs = (options & 0x08) != 0 ? UInt16() : 0;
        int extra = (options & 0x04) != 0 ? (int)Math.Min(UInt32(), int.MaxValue) : 0;
        StringBuilder text = new(Math.Min(count, 4096));
        for (int i = 0; i < count; i++)
        {
            if (position >= end)
            {
                if (segment + 1 >= segments.Count)
                {
                    throw new InvalidDataException("A string ends before its characters do.");
                }

                MoveToNextSegment();
                wide = (Byte() & 0x01) != 0;
            }

            text.Append(wide ? (char)UInt16() : (char)Byte());
        }

        SkipLong((long)runs * 4 + extra);
        return text.ToString();
    }

    private void SkipLong(long count)
    {
        while (count > 0)
        {
            NextSegmentIfDone();
            if (position >= end)
            {
                throw new InvalidDataException("A record ends before its data does.");
            }

            int step = (int)Math.Min(count, end - position);
            position += step;
            count -= step;
        }
    }

    private void NextSegmentIfDone()
    {
        while (position >= end && segment + 1 < segments.Count)
        {
            MoveToNextSegment();
        }
    }

    private void MoveToNextSegment()
    {
        segment++;
        position = segments[segment].Start;
        end = position + segments[segment].Length;
    }
}
