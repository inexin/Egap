using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>
/// An Office Art shape ([MS-ODRAW] OfficeArtSpContainer): its id, its MSOSPT type, the FSP flags and its properties (the
/// FOPT, secondary and tertiary FOPT tables merged), with a group's members.
/// </summary>
internal sealed class OfficeArtShape
{
    public int Spid { get; init; }

    public int Type { get; init; }

    public int Flags { get; init; }

    /// <summary>The simple properties by property id (opid without the fBid and fComplex bits).</summary>
    public Dictionary<int, uint> Properties { get; } = [];

    /// <summary>The complex properties' data by property id.</summary>
    public Dictionary<int, byte[]> Complex { get; } = [];

    /// <summary>A group's members in drawing order; empty for a shape.</summary>
    public List<OfficeArtShape> Children { get; } = [];

    /// <summary>A group's coordinate space (FSPGR): left, top, right, bottom.</summary>
    public (int Left, int Top, int Right, int Bottom)? GroupSpace { get; set; }

    /// <summary>A member's rectangle in its group's coordinate space (OfficeArtChildAnchor).</summary>
    public (int Left, int Top, int Right, int Bottom)? ChildAnchor { get; set; }

    public bool IsGroup => (Flags & 0x1) != 0;

    public bool IsDeleted => (Flags & 0x8) != 0;

    /// <summary>The property, or null when the shape does not set it.</summary>
    public uint? this[int id] => Properties.TryGetValue(id, out uint value) ? value : null;

    /// <summary>A complex property holding a null-terminated UTF-16 string, or null.</summary>
    public string? Text(int id)
    {
        if (!Complex.TryGetValue(id, out byte[]? data) || data.Length < 2)
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        text = end >= 0 ? text[..end] : text;
        return text.Length > 0 ? text : null;
    }
}

/// <summary>
/// The Office Art of a Word document ([MS-DOC] OfficeArtContent at fcDggInfo): the drawing group's pictures (the blip store,
/// whose blips may lie in the WordDocument stream, the delay stream) and the shapes of the main and header drawings by id.
/// </summary>
internal sealed class OfficeArtContent
{
    public List<ImageData?> Blips { get; } = [];

    public Dictionary<int, OfficeArtShape> Main { get; } = [];

    public Dictionary<int, OfficeArtShape> Header { get; } = [];
}

/// <summary>
/// Office Art records ([MS-ODRAW] 2.2): an 8-byte header (version in the low 4 bits and instance in the high 12 of the first
/// word, the record type, the body length), containers with version 0xF holding further records.
/// </summary>
internal static class OfficeArt
{
    private const int MaxDepth = 32;
    private const int MaxRecords = 1 << 20;

    private const int DggContainer = 0xF000;
    private const int BStoreContainer = 0xF001;
    private const int DgContainer = 0xF002;
    private const int SpgrContainer = 0xF003;
    private const int SpContainer = 0xF004;
    private const int Fbse = 0xF007;
    private const int Fspgr = 0xF009;
    private const int Fsp = 0xF00A;
    private const int ChildAnchor = 0xF00F;
    private const int Fopt = 0xF00B;
    private const int SecondaryFopt = 0xF121;
    private const int TertiaryFopt = 0xF122;

    private readonly record struct Record(int Instance, int Version, int Type, int Body, int Length)
    {
        public int End => Body + Length;
    }

    /// <summary>The records of <paramref name="data"/> from <paramref name="start"/> to <paramref name="end"/>; a record that runs past the end ends them.</summary>
    private static IEnumerable<Record> Records(byte[] data, int start, int end)
    {
        int at = start;
        for (int i = 0; i < MaxRecords && at + 8 <= end; i++)
        {
            int head = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            int type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            if (length > (uint)(end - at - 8))
            {
                yield break;
            }

            yield return new Record(head >> 4, head & 0xF, type, at + 8, (int)length);
            at += 8 + (int)length;
        }
    }

    /// <summary>Reads the OfficeArtContent in <paramref name="content"/>: the drawing group, then each drawing after its dgglbl byte (0 main, 1 header).</summary>
    public static OfficeArtContent Read(byte[] content, byte[] delay, long maxPicture)
    {
        OfficeArtContent art = new();
        int at = 0;
        if (Records(content, 0, content.Length).FirstOrDefault() is { Type: DggContainer } dgg)
        {
            foreach (Record record in Records(content, dgg.Body, dgg.End))
            {
                if (record.Type == BStoreContainer)
                {
                    foreach (Record fbse in Records(content, record.Body, record.End))
                    {
                        art.Blips.Add(fbse.Type == Fbse ? StoredBlip(content, fbse, delay, maxPicture) : null);
                    }
                }
            }

            at = dgg.End;
        }

        while (at + 9 <= content.Length)
        {
            byte label = content[at];
            Record? drawing = Records(content, at + 1, content.Length).Cast<Record?>().FirstOrDefault();
            if (drawing is not { Type: DgContainer } dg)
            {
                break;
            }

            Dictionary<int, OfficeArtShape> shapes = label == 1 ? art.Header : art.Main;
            foreach (Record record in Records(content, dg.Body, dg.End))
            {
                if (record.Type == SpgrContainer)
                {
                    foreach (OfficeArtShape shape in Group(content, record, 0))
                    {
                        shapes.TryAdd(shape.Spid, shape);
                    }
                }
            }

            at = dg.End;
        }

        return art;
    }

    /// <summary>The members of a group container: its first shape is the group itself, which the members leave out.</summary>
    private static List<OfficeArtShape> Group(byte[] data, Record group, int depth)
    {
        List<OfficeArtShape> members = [];
        bool first = true;
        foreach (Record record in Records(data, group.Body, group.End))
        {
            if (record.Type == SpContainer)
            {
                OfficeArtShape shape = Shape(data, record);
                if (!first)
                {
                    members.Add(shape);
                }
            }
            else if (record.Type == SpgrContainer && depth < MaxDepth)
            {
                // A nested group: its own shape, holding its members.
                OfficeArtShape? self = null;
                foreach (Record inner in Records(data, record.Body, record.End))
                {
                    if (inner.Type == SpContainer)
                    {
                        self = Shape(data, inner);
                    }

                    break;
                }

                if (self is not null)
                {
                    self.Children.AddRange(Group(data, record, depth + 1));
                    members.Add(self);
                }
            }

            first = false;
        }

        return members;
    }

    /// <summary>A shape container's FSP and property tables.</summary>
    public static OfficeArtShape Shape(byte[] data, int start, int end)
    {
        Record? container = Records(data, start, end).Cast<Record?>().FirstOrDefault();
        return container is { Type: SpContainer } found ? Shape(data, found) : new OfficeArtShape();
    }

    private static OfficeArtShape Shape(byte[] data, Record container)
    {
        int spid = 0;
        int type = 0;
        int flags = 0;
        (int, int, int, int)? space = null;
        (int, int, int, int)? anchor = null;
        List<Record> tables = [];
        foreach (Record record in Records(data, container.Body, container.End))
        {
            if (record.Type == Fsp && record.Length >= 8)
            {
                spid = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(record.Body));
                flags = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(record.Body + 4));
                type = record.Instance;
            }
            else if (record.Type is Fopt or SecondaryFopt or TertiaryFopt)
            {
                tables.Add(record);
            }
            else if (record.Type is Fspgr or ChildAnchor && record.Length >= 16)
            {
                ReadOnlySpan<byte> rect = data.AsSpan(record.Body, 16);
                (int, int, int, int) value = (BinaryPrimitives.ReadInt32LittleEndian(rect), BinaryPrimitives.ReadInt32LittleEndian(rect[4..]),
                    BinaryPrimitives.ReadInt32LittleEndian(rect[8..]), BinaryPrimitives.ReadInt32LittleEndian(rect[12..]));
                if (record.Type == Fspgr)
                {
                    space = value;
                }
                else
                {
                    anchor = value;
                }
            }
        }

        OfficeArtShape shape = new() { Spid = spid, Type = type, Flags = flags, GroupSpace = space, ChildAnchor = anchor };
        foreach (Record table in tables)
        {
            ReadProperties(data, table, shape);
        }

        return shape;
    }

    /// <summary>
    /// A property table ([MS-ODRAW] OfficeArtRGFOPTE): instance-count 6-byte entries (opid: the id in the low 14 bits, fBid
    /// 0x4000, fComplex 0x8000; then op), then each complex entry's data, op bytes long, in entry order.
    /// </summary>
    private static void ReadProperties(byte[] data, Record table, OfficeArtShape shape)
    {
        int count = Math.Min(table.Instance, table.Length / 6);
        int complex = table.Body + (count * 6);
        for (int i = 0; i < count; i++)
        {
            int at = table.Body + (i * 6);
            int opid = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            uint op = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 2));
            int id = opid & 0x3FFF;
            if ((opid & 0x8000) != 0)
            {
                int length = (int)Math.Min(op, (uint)Math.Max(0, table.End - complex));
                shape.Complex[id] = data.AsSpan(complex, length).ToArray();
                complex += length;
            }
            else
            {
                shape.Properties[id] = op;
            }
        }
    }

    /// <summary>
    /// An FBSE's picture ([MS-ODRAW] OfficeArtFBSE: btWin32, btMacOS, the UID, tag, size, cRef, foDelay, then cbName at 33 and
    /// the name after the 36 bytes): the blip that follows it, or the one at foDelay in the delay stream.
    /// </summary>
    private static ImageData? StoredBlip(byte[] data, Record fbse, byte[] delay, long maxPicture)
    {
        if (fbse.Length < 36)
        {
            return null;
        }

        int embedded = fbse.Body + 36 + data[fbse.Body + 33];
        if (embedded + 8 <= fbse.End)
        {
            return Blip(data, embedded, fbse.End, maxPicture);
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fbse.Body + 20));
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(fbse.Body + 28));
        if (size < 8 || offset >= delay.Length)
        {
            return null;
        }

        return Blip(delay, (int)offset, (int)Math.Min(delay.Length, offset + (long)size), maxPicture);
    }

    /// <summary>
    /// An inline picture's Office Art ([MS-DOC] OfficeArtInlineSpContainer): the shape, then the FBSEs of its pictures; the
    /// picture is the one its pib names, else the first. An FBSE without its blip points into <paramref name="data"/> for it.
    /// </summary>
    public static (OfficeArtShape Shape, ImageData? Image) Inline(byte[] data, int start, int end, long maxPicture)
    {
        OfficeArtShape shape = new();
        List<ImageData?> blips = [];
        foreach (Record record in Records(data, start, end))
        {
            if (record.Type == SpContainer)
            {
                shape = Shape(data, record);
            }
            else if (record.Type == Fbse)
            {
                blips.Add(StoredBlip(data, record, data, maxPicture));
            }
            else if (record.Type is >= 0xF018 and <= 0xF117)
            {
                blips.Add(Blip(data, record.Body - 8, record.End, maxPicture));
            }
        }

        ImageData? image = shape[0x104] is uint pib && pib >= 1 && pib <= blips.Count ? blips[(int)pib - 1] : null;
        return (shape, image ?? blips.FirstOrDefault(b => b is not null));
    }

    /// <summary>
    /// A blip record ([MS-ODRAW] 2.2.23): after one UID, or two when the instance is odd. A metafile (EMF, WMF, PICT) follows a
    /// 34-byte header (cbSize, rcBounds, ptSize in EMUs, cbSave, compression: 0 deflated, 0xFE stored); a bitmap (JPEG, PNG,
    /// DIB, TIFF) a tag byte.
    /// </summary>
    public static ImageData? Blip(byte[] data, int start, int end, long maxPicture)
    {
        if (Records(data, start, end).Cast<Record?>().FirstOrDefault() is not { } blip)
        {
            return null;
        }

        int uids = (blip.Instance & 1) == 1 ? 32 : 16;
        ReadOnlySpan<byte> body = data.AsSpan(blip.Body, blip.Length);
        switch (blip.Type)
        {
            case 0xF01A or 0xF01B or 0xF01C:
                {
                    if (body.Length < uids + 34)
                    {
                        return null;
                    }

                    ReadOnlySpan<byte> header = body[uids..];
                    uint size = BinaryPrimitives.ReadUInt32LittleEndian(header);
                    int widthEmu = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
                    int heightEmu = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
                    uint saved = BinaryPrimitives.ReadUInt32LittleEndian(header[28..]);
                    ReadOnlySpan<byte> stored = body[(uids + 34)..];
                    stored = stored[..(int)Math.Min(saved, (uint)stored.Length)];
                    byte[]? bytes = header[32] switch
                    {
                        0xFE => stored.ToArray(),
                        0 when size <= maxPicture => Inflate(stored, (int)size),
                        _ => null,
                    };
                    if (bytes is null || bytes.Length == 0)
                    {
                        return null;
                    }

                    return blip.Type switch
                    {
                        0xF01A => new ImageData(bytes, "image/x-emf"),
                        0xF01B => new ImageData(WordPictures.WithPlaceableHeader(bytes, Math.Max(0, widthEmu / 360), Math.Max(0, heightEmu / 360), 0, 0), "image/x-wmf"),
                        _ => new ImageData(bytes, "image/x-pict"),
                    };
                }

            case 0xF01D or 0xF01E or 0xF01F or 0xF029 or 0xF02A:
                {
                    if (body.Length <= uids + 1)
                    {
                        return null;
                    }

                    byte[] bytes = body[(uids + 1)..].ToArray();
                    return ImageData.FromBytes(blip.Type == 0xF01F ? WordPictures.WithBitmapFileHeader(bytes) : bytes);
                }

            default:
                return null;
        }
    }

    private static byte[]? Inflate(ReadOnlySpan<byte> stored, int size)
    {
        try
        {
            using ZLibStream inflater = new(new MemoryStream(stored.ToArray()), CompressionMode.Decompress);
            byte[] bytes = new byte[size];
            int read = inflater.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            return read == size ? bytes : bytes[..read];
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }
}
