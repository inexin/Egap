using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Xlsb;

/// <summary>
/// Walks the records of an XLSB part ([MS-XLSB] 2.1.4): each a record type of one or two bytes and a size of one to four
/// bytes, seven bits to a byte with the top bit saying another byte follows, then the record's data.
/// </summary>
internal ref struct XlsbRecords
{
    private readonly ReadOnlySpan<byte> data;
    private int at;

    public XlsbRecords(ReadOnlySpan<byte> data)
    {
        this.data = data;
    }

    /// <summary>The record's type, as a number.</summary>
    public int Type { get; private set; }

    /// <summary>The record's data.</summary>
    public ReadOnlySpan<byte> Body { get; private set; }

    /// <summary>A reader over the record's data.</summary>
    public readonly XlsbReader Reader => new(Body);

    /// <summary>Moves to the next record; false at the end of the part.</summary>
    /// <exception cref="InvalidDataException">A record's header or data runs past the end of the part.</exception>
    public bool Next()
    {
        if (at >= data.Length)
        {
            return false;
        }

        int first = data[at++];
        int type = first & 0x7F;
        if ((first & 0x80) != 0)
        {
            type |= (Take() & 0x7F) << 7;
        }

        int size = 0;
        for (int i = 0; ; i++)
        {
            int part = Take();
            size |= (part & 0x7F) << (7 * i);
            if ((part & 0x80) == 0)
            {
                break;
            }

            if (i == 3)
            {
                throw new InvalidDataException("A record's size is longer than four bytes.");
            }
        }

        if (size > data.Length - at)
        {
            throw new InvalidDataException("A record runs past the end of its part.");
        }

        Type = type;
        Body = data.Slice(at, size);
        at += size;
        return true;
    }

    private int Take() => at < data.Length ? data[at++] : throw new InvalidDataException("A record's header is cut short.");
}

/// <summary>Reads the fields of one record's data in order; reading past its end throws <see cref="InvalidDataException"/>.</summary>
internal ref struct XlsbReader
{
    private readonly ReadOnlySpan<byte> data;
    private int at;

    public XlsbReader(ReadOnlySpan<byte> data)
    {
        this.data = data;
    }

    /// <summary>How many bytes are left.</summary>
    public readonly int Remaining => data.Length - at;

    public byte Byte() => Need(1)[0];

    public ushort UInt16() => BinaryPrimitives.ReadUInt16LittleEndian(Need(2));

    public short Int16() => BinaryPrimitives.ReadInt16LittleEndian(Need(2));

    public uint UInt32() => BinaryPrimitives.ReadUInt32LittleEndian(Need(4));

    public int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Need(4));

    public double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Need(8));

    public ReadOnlySpan<byte> Bytes(int count) => Need(count);

    public void Skip(int count) => Need(count);

    /// <summary>What is left of the data.</summary>
    public ReadOnlySpan<byte> Rest()
    {
        ReadOnlySpan<byte> rest = data[at..];
        at = data.Length;
        return rest;
    }

    /// <summary>An XLWideString ([MS-XLSB] 2.5.168): a count of UTF-16 characters, then the characters.</summary>
    public string WideString() => NullableWideString() ?? throw new InvalidDataException("A string that cannot be null is null.");

    /// <summary>An XLNullableWideString ([MS-XLSB] 2.5.167): a count of 0xFFFFFFFF is null.</summary>
    public string? NullableWideString()
    {
        uint count = UInt32();
        if (count == 0xFFFFFFFF)
        {
            return null;
        }

        if (count > (uint)Remaining / 2)
        {
            throw new InvalidDataException("A string runs past the end of its record.");
        }

        return Encoding.Unicode.GetString(Need((int)count * 2));
    }

    /// <summary>A formula as most records keep it: the parsed tokens (cce, rgce), then their extra data (cb, rgcb).</summary>
    public (byte[] Tokens, byte[] Extra) Formula()
    {
        byte[] tokens = Need(Count()).ToArray();
        byte[] extra = Need(Count()).ToArray();
        return (tokens, extra);
    }

    /// <summary>A range ([MS-XLSB] 2.5.153 RfX): first row, last row, first column, last column.</summary>
    public (int FirstRow, int LastRow, int FirstColumn, int LastColumn) Range() => (Int32(), Int32(), Int32(), Int32());

    private int Count()
    {
        uint count = UInt32();
        return count <= (uint)Remaining ? (int)count : throw new InvalidDataException("A formula runs past the end of its record.");
    }

    private ReadOnlySpan<byte> Need(int count)
    {
        if (count < 0 || count > data.Length - at)
        {
            throw new InvalidDataException("A record is shorter than its fields.");
        }

        ReadOnlySpan<byte> span = data.Slice(at, count);
        at += count;
        return span;
    }
}

/// <summary>The XML the XLSB reader makes for the XLSX code to read.</summary>
internal static class XlsbXml
{
    /// <summary>
    /// The element, with each attribute and text that holds characters XML cannot, as a BIFF12 string may, escaped as
    /// <c>_xHHHH_</c> the way an XLSX file holds them (ST_Xstring), so the workbook can be saved.
    /// </summary>
    public static T Safe<T>(T element)
        where T : XElement
    {
        foreach (XElement e in element.DescendantsAndSelf())
        {
            foreach (XAttribute attribute in e.Attributes())
            {
                if (!Valid(attribute.Value))
                {
                    attribute.Value = XlsxNames.Encode(attribute.Value);
                }
            }

            foreach (XText text in e.Nodes().OfType<XText>())
            {
                if (!Valid(text.Value))
                {
                    text.Value = XlsxNames.Encode(text.Value);
                }
            }
        }

        return element;
    }

    /// <summary>Whether XML can hold the text: no control characters but tab, line feed and carriage return, and surrogates only in pairs.</summary>
    private static bool Valid(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(c) || !XmlConvert.IsXmlChar(c))
            {
                return false;
            }
        }

        return true;
    }
}
