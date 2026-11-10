using System.Buffers.Binary;
using System.Text;

namespace Egap.Spreadsheets.Xls;

/// <summary>
/// The objects of a worksheet substream ([MS-XLS] 2.4): hyperlinks (HLINK and HLINKTOOLTIP), notes (NOTE, with their text in
/// the TXO record after the note's OBJ record), and the drawing layer (MSODRAWING), read as their records come and put on
/// the sheet at its end.
/// </summary>
internal sealed class XlsObjects(Worksheet sheet)
{
    // OBJ record kinds ([MS-XLS] 2.5.9, FtCmo.ot).
    public const int ObjectNote = 0x19;

    private readonly List<(int Row, int Column, int ObjectId, string Author, bool Visible)> notes = [];
    private readonly Dictionary<int, string> texts = [];
    private readonly List<(int Id, int Kind, bool Embedded)> objects = [];
    private readonly List<Hyperlink> links = [];

    /// <summary>The OfficeArt records of every MSODRAWING record, in order: one drawing container.</summary>
    public MemoryStream Drawing { get; } = new();

    /// <summary>
    /// The OBJ records in order, with their ids, kinds and whether they hold an embedded or linked object (an FtPictFmla
    /// subrecord); the n-th shape with client data is the n-th object.
    /// </summary>
    public IReadOnlyList<(int Id, int Kind, bool Embedded)> Objects => objects;

    /// <summary>The text of each object that has a TXO record, by object id.</summary>
    public IReadOnlyDictionary<int, string> Texts => texts;

    public void MsoDrawing(ReadOnlySpan<byte> body) => Drawing.Write(body);

    /// <summary>An OBJ record: its first subrecord is FtCmo, with the object's kind and id.</summary>
    public void Obj(ReadOnlySpan<byte> body)
    {
        if (body.Length >= 8 && BinaryPrimitives.ReadUInt16LittleEndian(body) == 0x15)
        {
            // The subrecords after FtCmo: an FtPictFmla (0x09) says the picture stands for an OLE object.
            bool embedded = false;
            int position = 0;
            while (position + 4 <= body.Length)
            {
                int kind = BinaryPrimitives.ReadUInt16LittleEndian(body[position..]);
                if (kind == 0x09)
                {
                    embedded = true;
                }

                if (kind is 0x00 or 0x13)
                {
                    // The end, or a list box's data (FtLbsData), whose size its header doesn't give.
                    break;
                }

                position += 4 + BinaryPrimitives.ReadUInt16LittleEndian(body[(position + 2)..]);
            }

            objects.Add((BinaryPrimitives.ReadUInt16LittleEndian(body[6..]), BinaryPrimitives.ReadUInt16LittleEndian(body[4..]), embedded));
        }
        else
        {
            objects.Add((-1, -1, false));
        }
    }

    /// <summary>
    /// A TXO record ([MS-XLS] 2.4.329) and the CONTINUE records after it: the text, each piece starting with a byte that says
    /// whether its characters take one byte or two, then the formatting runs.
    /// </summary>
    public void Txo(BiffStream biff)
    {
        ReadOnlySpan<byte> body = biff.Body;
        int length = body.Length >= 14 ? BinaryPrimitives.ReadUInt16LittleEndian(body[10..]) : 0;
        int runs = body.Length >= 14 ? BinaryPrimitives.ReadUInt16LittleEndian(body[12..]) : 0;
        StringBuilder text = new();
        while (text.Length < length && biff.PeekType() == BiffType.Continue && biff.Read())
        {
            ReadOnlySpan<byte> piece = biff.Body;
            if (piece.Length == 0)
            {
                continue;
            }

            bool wide = (piece[0] & 0x01) != 0;
            piece = piece[1..];
            int count = Math.Min(length - text.Length, wide ? piece.Length / 2 : piece.Length);
            for (int i = 0; i < count; i++)
            {
                text.Append(wide ? (char)BinaryPrimitives.ReadUInt16LittleEndian(piece[(2 * i)..]) : (char)piece[i]);
            }
        }

        // The formatting runs follow in CONTINUE records of their own.
        int skipped = 0;
        while (runs > 0 && skipped < runs && biff.PeekType() == BiffType.Continue && biff.Read())
        {
            skipped += biff.Length;
        }

        if (objects.Count > 0)
        {
            // Excel ends lines with a lone carriage return or line feed; XLSX keeps line feeds.
            texts[objects[^1].Id] = text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        }
    }

    /// <summary>A NOTE record ([MS-XLS] 2.4.179): the cell, whether the note shows, its object and its author.</summary>
    public void Note(RecordReader record)
    {
        if (record.Remaining < 8)
        {
            return;
        }

        int row = record.UInt16();
        int column = record.UInt16();
        int flags = record.UInt16();
        int id = record.UInt16();
        string author = record.Remaining >= 3 ? record.UnicodeString() : "";
        notes.Add((row, column, id, author.TrimEnd('\0'), (flags & 0x0002) != 0));
    }

    /// <summary>An HLINK record ([MS-XLS] 2.4.140): the cells, then a hyperlink object ([MS-OSHARED] 2.3.7.1).</summary>
    public void Hyperlink(ReadOnlySpan<byte> body)
    {
        if (body.Length < 32)
        {
            return;
        }

        int firstRow = BinaryPrimitives.ReadUInt16LittleEndian(body);
        int lastRow = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        int firstColumn = BinaryPrimitives.ReadUInt16LittleEndian(body[4..]);
        int lastColumn = BinaryPrimitives.ReadUInt16LittleEndian(body[6..]);
        if (lastRow < firstRow || lastColumn < firstColumn || lastRow >= CellAddress.MaxRows || lastColumn >= CellAddress.MaxColumns)
        {
            return;
        }

        if (HyperlinkObject.Read(body[24..].ToArray()) is not { } target || (target.Address is null && target.Location is null))
        {
            return;
        }

        // Excel writes a space in a link's address as %20 when it saves the workbook as XLSX.
        Hyperlink link = new(new RangeAddress(firstRow, firstColumn, lastRow, lastColumn), target.Address?.Replace(" ", "%20", StringComparison.Ordinal), target.Location)
        {
            Display = target.Display,
        };
        links.Add(link);
    }

    /// <summary>An HLINKTOOLTIP record ([MS-XLS] 2.4.141): the tip of the hyperlink on the same cells.</summary>
    public void HyperlinkTip(ReadOnlySpan<byte> body)
    {
        if (body.Length < 12 || links.Count == 0)
        {
            return;
        }

        ReadOnlySpan<byte> chars = body[12..];
        string tip = Encoding.Unicode.GetString(chars[..(chars.Length / 2 * 2)]);
        int end = tip.IndexOf('\0', StringComparison.Ordinal);
        links[^1].ToolTip = end >= 0 ? tip[..end] : tip;
    }

    /// <summary>Puts the hyperlinks and notes on the sheet.</summary>
    public void Finish()
    {
        foreach (Hyperlink link in links)
        {
            Hyperlink added = link.Address is { } address ? sheet.Hyperlinks.Add(link.Range, address) : sheet.Hyperlinks.AddInternal(link.Range, link.Location!);
            added.Location = link.Location;
            added.Display = link.Display;
            added.ToolTip = link.ToolTip;
        }

        foreach ((int row, int column, int id, string author, bool visible) in notes)
        {
            if (row >= CellAddress.MaxRows || column >= CellAddress.MaxColumns || sheet.Comments[new CellAddress(row, column)] is not null)
            {
                continue;
            }

            CellComment comment = sheet.Comments.Add(new CellAddress(row, column), texts.GetValueOrDefault(id, ""), author);
            comment.Visible = visible;
        }

        sheet.Comments.Changed = false;
    }
}

/// <summary>A hyperlink object ([MS-OSHARED] 2.3.7.1): a URL or file moniker, a place in the workbook, a display name.</summary>
internal readonly record struct HyperlinkObject(string? Address, string? Location, string? Display)
{
    private const uint HasMoniker = 0x01, IsAbsolute = 0x02, HasLocation = 0x08, HasDisplayName = 0x10, HasGuid = 0x20, HasCreationTime = 0x40, HasFrameName = 0x80, MonikerSavedAsString = 0x100;

    private static readonly Guid UrlMoniker = new("79EAC9E0-BAF9-11CE-8C82-00AA004BA90B");
    private static readonly Guid FileMoniker = new("00000303-0000-0000-C000-000000000046");

    public static HyperlinkObject? Read(byte[] data)
    {
        int position = 0;
        bool Need(int count) => position + count <= data.Length;
        uint UInt32()
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position));
            position += 4;
            return value;
        }

        string? String()
        {
            // HyperlinkString: a count of characters, the null at the end among them, then UTF-16.
            if (!Need(4))
            {
                return null;
            }

            int count = (int)Math.Min(UInt32(), int.MaxValue / 2);
            if (!Need(count * 2))
            {
                return null;
            }

            string text = Encoding.Unicode.GetString(data, position, count * 2);
            position += count * 2;
            int end = text.IndexOf('\0', StringComparison.Ordinal);
            return end >= 0 ? text[..end] : text;
        }

        if (!Need(8) || UInt32() != 2)
        {
            return null;
        }

        uint flags = UInt32();
        string? display = (flags & HasDisplayName) != 0 ? String() : null;
        if ((flags & HasFrameName) != 0)
        {
            _ = String();
        }

        string? address = null;
        if ((flags & HasMoniker) != 0)
        {
            if ((flags & MonikerSavedAsString) != 0)
            {
                address = String();
            }
            else
            {
                if (!Need(16))
                {
                    return null;
                }

                Guid kind = new(data.AsSpan(position, 16));
                position += 16;
                if (kind == UrlMoniker)
                {
                    if (!Need(4))
                    {
                        return null;
                    }

                    int size = (int)Math.Min(UInt32(), int.MaxValue);
                    if (!Need(size))
                    {
                        return null;
                    }

                    string url = Encoding.Unicode.GetString(data, position, size / 2 * 2);
                    position += size;
                    int end = url.IndexOf('\0', StringComparison.Ordinal);
                    address = end >= 0 ? url[..end] : url;
                }
                else if (kind == FileMoniker)
                {
                    address = File(data, ref position, (flags & IsAbsolute) != 0);
                    if (address is null)
                    {
                        return null;
                    }
                }
                else
                {
                    // A composite or item moniker: nothing Excel writes for a cell's link; the place, if any, still reads.
                    return null;
                }
            }
        }

        string? location = (flags & HasLocation) != 0 ? String() : null;
        _ = (flags & (HasGuid | HasCreationTime)) != 0;
        return new HyperlinkObject(string.IsNullOrEmpty(address) ? null : address, string.IsNullOrEmpty(location) ? null : location, display);
    }

    /// <summary>A file moniker ([MS-OSHARED] 2.3.7.8): the "..\" count, the ANSI path and maybe a Unicode one.</summary>
    private static string? File(ReadOnlySpan<byte> data, ref int position, bool absolute)
    {
        if (position + 6 > data.Length)
        {
            return null;
        }

        int up = BinaryPrimitives.ReadUInt16LittleEndian(data[position..]);
        int ansiLength = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[(position + 2)..]), int.MaxValue);
        position += 6;
        if (ansiLength > data.Length - position)
        {
            return null;
        }

        string path = Encoding.Latin1.GetString(data.Slice(position, ansiLength)).TrimEnd('\0');
        position += ansiLength;

        // endServer, versionNumber, 20 reserved bytes, then the size of what follows.
        if (position + 28 > data.Length)
        {
            return null;
        }

        position += 24;
        int unicodeSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[position..]), int.MaxValue);
        position += 4;
        if (unicodeSize > 0)
        {
            if (unicodeSize > data.Length - position || unicodeSize < 6)
            {
                return null;
            }

            int bytes = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[position..]), (uint)(unicodeSize - 6));
            path = Encoding.Unicode.GetString(data.Slice(position + 6, bytes / 2 * 2));
            position += unicodeSize;
        }

        string prefix = string.Concat(Enumerable.Repeat("..\\", up));
        _ = absolute;
        return prefix + path;
    }
}
