using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Xls;

/// <summary>
/// The drawing layer of an Excel 97-2003 workbook ([MS-ODRAW], [MS-XLS] 2.4.168 and 2.4.169): the pictures of the
/// workbook's blip store (MSODRAWINGGROUP), and each sheet's shapes (MSODRAWING), matched in order with the sheet's OBJ
/// records, which say what each shape is. Pictures become picture drawings, and rectangles, ovals, lines, arcs, text boxes
/// and other AutoShapes become shapes with their text, fill and line, anchored to the cells as the client anchor says.
/// </summary>
internal static class XlsDrawing
{
    // OBJ kinds ([MS-XLS] 2.5.9, FtCmo.ot).
    private const int Group = 0x00, Line = 0x01, Rectangle = 0x02, Oval = 0x03, Arc = 0x04, Chart = 0x05, TextBox = 0x06, Picture = 0x08,
        Polygon = 0x09, Note = 0x19, OfficeArt = 0x1E;

    private const long EmuPerPixel = 9525;

    private static readonly XNamespace Xdr = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>A picture of the blip store, with its file type; null where the store's entry has none.</summary>
    internal sealed record Blip(byte[] Data, string ContentType);

    /// <summary>A shape of a sheet's drawing: its type, flags, properties, anchor and whether it has client data.</summary>
    internal sealed class Shape
    {
        public int Type;
        public int Id;
        public uint Flags;
        public Dictionary<int, (uint Value, byte[]? Complex)> Properties = [];
        public ushort[]? Anchor;
        public bool ClientData;
        public bool TopLevel;
        public bool GroupShape;
    }

    /// <summary>The blips of the globals' MSODRAWINGGROUP records: the BStore container's FBSE records, each maybe with its blip.</summary>
    public static List<Blip?> ReadBlips(byte[] group)
    {
        List<Blip?> blips = [];
        foreach ((int type, int instance, int start, int length) in Records(group, 0, group.Length))
        {
            if (type != 0xF000)
            {
                continue;
            }

            foreach ((int storeType, _, int storeStart, int storeLength) in Records(group, start, length))
            {
                if (storeType != 0xF001)
                {
                    continue;
                }

                foreach ((int entryType, _, int entryStart, int entryLength) in Records(group, storeStart, storeLength))
                {
                    if (entryType != 0xF007)
                    {
                        continue;
                    }

                    // FBSE: 36 bytes, the name, then the blip.
                    Blip? blip = null;
                    if (entryLength >= 36)
                    {
                        int nameLength = group[entryStart + 33];
                        int at = entryStart + 36 + nameLength;
                        int end = entryStart + entryLength;
                        if (at + 8 <= end)
                        {
                            foreach ((int blipType, int blipInstance, int blipStart, int blipLength) in Records(group, at, end - at))
                            {
                                blip = ReadBlip(group.AsSpan(blipStart, blipLength), blipType, blipInstance);
                                break;
                            }
                        }
                    }

                    blips.Add(blip);
                }
            }

            _ = instance;
        }

        return blips;
    }

    /// <summary>A blip record's picture ([MS-ODRAW] 2.2.23): its ids, then the file, deflated for a metafile.</summary>
    private static Blip? ReadBlip(ReadOnlySpan<byte> data, int type, int instance)
    {
        // Each kind has an instance for one id and one for two.
        (int single, string contentType, bool metafile) = type switch
        {
            0xF01A => (0x3D4, "image/x-emf", true),
            0xF01B => (0x216, "image/x-wmf", true),
            0xF01C => (0x542, "image/pict", true),
            0xF01D or 0xF02A => (0x46A, "image/jpeg", false),
            0xF01E => (0x6E0, "image/png", false),
            0xF01F => (0x7A8, "image/bmp", false),
            0xF029 => (0x6E4, "image/tiff", false),
            _ => (-1, "", false),
        };
        if (single < 0)
        {
            return null;
        }

        int header = (instance == single ? 16 : 32) + (metafile ? 34 : 1);
        if (type == 0xF02A)
        {
            header = (instance == 0x6E2 ? 16 : 32) + 1;
        }

        if (data.Length <= header)
        {
            return null;
        }

        ReadOnlySpan<byte> body = data[header..];
        if (metafile)
        {
            ReadOnlySpan<byte> metaHeader = data.Slice(header - 34, 34);
            int size = BinaryPrimitives.ReadInt32LittleEndian(metaHeader);
            if (metaHeader[32] == 0)
            {
                try
                {
                    using ZLibStream inflate = new(new MemoryStream(body.ToArray(), writable: false), CompressionMode.Decompress);
                    using MemoryStream plain = new();
                    inflate.CopyTo(plain);
                    body = plain.ToArray();
                }
                catch (InvalidDataException)
                {
                    return null;
                }
            }

            if (size > 0 && size < body.Length)
            {
                body = body[..size];
            }

            if (type == 0xF01B)
            {
                return new Blip(Placeable(body, metaHeader), contentType);
            }
        }

        if (type == 0xF01F)
        {
            return new Blip(Bitmap(body), contentType);
        }

        if (type == 0xF01E && OriginalGif(body) is { } gif)
        {
            return new Blip(gif, "image/gif");
        }

        return new Blip(body.ToArray(), contentType);
    }

    /// <summary>
    /// The GIF a PNG blip was made from: Office keeps the inserted file in a private "msOG" chunk, after "MSOFFICE9.0", and
    /// puts that file back in an XLSX.
    /// </summary>
    private static byte[]? OriginalGif(ReadOnlySpan<byte> png)
    {
        ReadOnlySpan<byte> marker = "MSOFFICE9.0"u8;
        int position = 8;
        while (position + 12 <= png.Length)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png[position..]);
            if (length > (uint)(png.Length - position - 12))
            {
                return null;
            }

            ReadOnlySpan<byte> data = png.Slice(position + 8, (int)length);
            if (png.Slice(position + 4, 4).SequenceEqual("msOG"u8) && data.StartsWith(marker) && data[marker.Length..].StartsWith("GIF8"u8))
            {
                return data[marker.Length..].ToArray();
            }

            if (png.Slice(position + 4, 4).SequenceEqual("IEND"u8))
            {
                return null;
            }

            position += 12 + (int)length;
        }

        return null;
    }

    /// <summary>A WMF with the placeable header Excel puts before it in an XLSX file, from the blip's bounds and size.</summary>
    private static byte[] Placeable(ReadOnlySpan<byte> wmf, ReadOnlySpan<byte> metaHeader)
    {
        int left = BinaryPrimitives.ReadInt32LittleEndian(metaHeader[4..]);
        int top = BinaryPrimitives.ReadInt32LittleEndian(metaHeader[8..]);
        int right = BinaryPrimitives.ReadInt32LittleEndian(metaHeader[12..]);
        int bottom = BinaryPrimitives.ReadInt32LittleEndian(metaHeader[16..]);
        byte[] result = new byte[22 + wmf.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x9AC6CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(6), (short)left);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(8), (short)top);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(10), (short)right);
        BinaryPrimitives.WriteInt16LittleEndian(result.AsSpan(12), (short)bottom);
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(14), 1440);
        ushort check = 0;
        for (int i = 0; i < 20; i += 2)
        {
            check ^= BinaryPrimitives.ReadUInt16LittleEndian(result.AsSpan(i));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20), check);
        wmf.CopyTo(result.AsSpan(22));
        return result;
    }

    /// <summary>A DIB as a BMP file: the 14-byte file header before it.</summary>
    private static byte[] Bitmap(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < 40)
        {
            return dib.ToArray();
        }

        int headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        int colors = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
        int compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        int palette = (colors == 0 && bits <= 8 ? 1 << bits : colors) * 4 + (compression == 3 && headerSize == 40 ? 12 : 0);
        byte[] result = new byte[14 + dib.Length];
        result[0] = (byte)'B';
        result[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(2), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(10), 14 + headerSize + palette);
        dib.CopyTo(result.AsSpan(14));
        return result;
    }

    /// <summary>Puts a sheet's pictures and shapes on it.</summary>
    public static void Add(Worksheet sheet, XlsObjects objects, IReadOnlyList<Blip?> blips, int digit)
    {
        byte[] drawing = objects.Drawing.ToArray();
        if (drawing.Length < 8)
        {
            return;
        }

        List<Shape> shapes = [];
        foreach ((int type, _, int start, int length) in Records(drawing, 0, drawing.Length))
        {
            if (type == 0xF002)
            {
                Walk(drawing, start, length, shapes, 0);
            }
        }

        // The n-th shape with client data is the n-th OBJ record.
        int index = 0;
        int named = 0;
        foreach (Shape shape in shapes)
        {
            if (!shape.ClientData)
            {
                continue;
            }

            (int id, int kind, bool embedded) = index < objects.Objects.Count ? objects.Objects[index] : (-1, -1, false);
            index++;
            // An embedded or linked OLE object's picture is its icon or preview, which Excel keeps with the object, not
            // as a drawing.
            if (!shape.TopLevel || shape.Anchor is not { } anchor || embedded)
            {
                continue;
            }

            SheetDrawing? made = kind switch
            {
                Picture => MakePicture(shape, blips),
                // WordArt keeps its text in the shape's own gtextUNICODE property, with no TXO record.
                Line or Rectangle or Oval or Arc or TextBox or Polygon or OfficeArt => MakeShape(shape, kind, objects.Texts.GetValueOrDefault(id) ?? Text(shape, 0x00C0)),
                _ => null,
            };
            if (made is null)
            {
                continue;
            }

            named++;
            made.AnchorKind = DrawingAnchorKind.TwoCell;
            made.From = Position(sheet, anchor[1], anchor[2], anchor[3], anchor[4], digit);
            made.To = Position(sheet, anchor[5], anchor[6], anchor[7], anchor[8], digit);
            sheet.Drawings.AddRead(made);
            _ = named;
        }

        _ = Chart;
        _ = Group;
        _ = Note;
    }

    /// <summary>A place given as a cell and its fraction (1/1024 of the column's width, 1/256 of the row's height), in EMUs.</summary>
    private static DrawingPosition Position(Worksheet sheet, int column, int dx, int row, int dy, int digit)
    {
        column = Math.Min(column, CellAddress.MaxColumns - 1);
        row = Math.Min(row, CellAddress.MaxRows - 1);
        long columnPixels = ColumnPixels(sheet, column, digit);
        long rowPixels = RowPixels(sheet, row);
        long x = (long)Math.Round(Math.Min(dx, 1024) * columnPixels / 1024.0, MidpointRounding.AwayFromZero) * EmuPerPixel;
        long y = (long)Math.Round(Math.Min(dy, 256) * rowPixels / 256.0, MidpointRounding.AwayFromZero) * EmuPerPixel;
        return new DrawingPosition(new CellAddress(row, column), x, y);
    }

    private static long ColumnPixels(Worksheet sheet, int column, int digit)
    {
        ColumnSpan? span = sheet.ColumnSpans.Find(column);
        if (span is { Hidden: true })
        {
            return 0;
        }

        double width = span is { } found && !double.IsNaN(found.Width) ? found.Width : sheet.DefaultColumnWidth ?? double.NaN;
        return double.IsNaN(width)
            ? ((8 * digit) + 5 + 7) / 8 * 8
            : (long)Math.Truncate(((256 * width) + Math.Truncate(128.0 / digit)) / 256 * digit);
    }

    private static long RowPixels(Worksheet sheet, int row)
    {
        // A row's height as the file gives it, fitted to its text or set: Excel places drawings by it.
        if (!sheet.RowRecords.TryGetValue(row, out RowRecord? record))
        {
            record = null;
        }
        else if (record.Hidden)
        {
            return 0;
        }

        double points = record is not null && !double.IsNaN(record.Height) ? record.Height
            : record is not null && !double.IsNaN(record.FittedHeight) ? record.FittedHeight
            : sheet.DefaultRowHeight ?? 15;
        return (long)Math.Round(points * 96 / 72, MidpointRounding.AwayFromZero);
    }

    private static SheetDrawing? MakePicture(Shape shape, IReadOnlyList<Blip?> blips)
    {
        if (!shape.Properties.TryGetValue(0x0104, out var pib) || pib.Value == 0 || pib.Value > blips.Count || blips[(int)pib.Value - 1] is not { } blip)
        {
            return null;
        }

        SheetDrawing picture = new(SheetDrawingKind.Picture)
        {
            Name = Name(shape) ?? "Picture " + (shape.Id & 0x3FF).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Description = Text(shape, 0x0381),
        };
        picture.SetImageRead(blip.Data, blip.ContentType);
        return picture;
    }

    private static SheetDrawing MakeShape(Shape shape, int kind, string? text)
    {
        string geometry = kind switch
        {
            Line => "line",
            Oval => "ellipse",
            Arc => "arc",
            _ => Geometry(shape.Type),
        };
        string name = Name(shape) ?? (kind switch
        {
            Line => "Line ",
            Oval => "Oval ",
            Arc => "Arc ",
            TextBox => "TextBox ",
            Rectangle => "Rectangle ",
            _ => "AutoShape ",
        }) + (shape.Id & 0x3FF).ToString(System.Globalization.CultureInfo.InvariantCulture);
        SheetDrawing made = new(SheetDrawingKind.Shape) { Name = name, Description = Text(shape, 0x0381), Text = text };

        // The shape as DrawingML, so a save writes it: geometry, fill, line and text.
        XElement properties = new(Xdr + "spPr", new XElement(A + "prstGeom", new XAttribute("prst", geometry), new XElement(A + "avLst")));
        uint fillStyle = shape.Properties.TryGetValue(0x01BF, out var fs) ? fs.Value : 0x00100010;
        bool filled = kind is not (Line or Arc) && Flag(fillStyle, 4);
        properties.Add(filled ? new XElement(A + "solidFill", Color(shape.Properties.TryGetValue(0x0181, out var fill) ? fill.Value : 0xFFFFFF)) : new XElement(A + "noFill"));
        uint lineStyle = shape.Properties.TryGetValue(0x01FF, out var ls) ? ls.Value : 0x00080008;
        if (Flag(lineStyle, 3))
        {
            long width = shape.Properties.TryGetValue(0x01CB, out var w) ? w.Value : 9525;
            properties.Add(new XElement(A + "ln", new XAttribute("w", width), new XElement(A + "solidFill", Color(shape.Properties.TryGetValue(0x01C0, out var line) ? line.Value : 0))));
        }
        else
        {
            properties.Add(new XElement(A + "ln", new XElement(A + "noFill")));
        }

        XElement nonVisual = new(Xdr + "nvSpPr",
            new XElement(Xdr + "cNvPr", new XAttribute("id", shape.Id & 0xFFFF), new XAttribute("name", name), made.Description is null ? null : new XAttribute("descr", made.Description)),
            new XElement(Xdr + "cNvSpPr", kind == TextBox ? new XAttribute("txBox", "1") : null));
        XElement element = new(Xdr + "sp", new XAttribute("macro", ""), new XAttribute("textlink", ""), nonVisual, properties);
        if (!string.IsNullOrEmpty(text))
        {
            XElement body = new(Xdr + "txBody", new XElement(A + "bodyPr", new XAttribute("vertOverflow", "clip"), new XAttribute("wrap", "square"), new XAttribute("rtlCol", "0"), new XAttribute("anchor", "t")), new XElement(A + "lstStyle"));
            foreach (string paragraph in text.Split('\n'))
            {
                body.Add(new XElement(A + "p", paragraph.Length == 0 ? null : new XElement(A + "r", new XElement(A + "t", paragraph))));
            }

            element.Add(body);
        }

        made.Source = new XElement(Xdr + "twoCellAnchor", element, new XElement(Xdr + "clientData"));
        return made;
    }

    // A boolean property of a style set ([MS-ODRAW] 2.3): the bit counts when its "use" bit, 16 places higher, is set; a
    // shape's fill and line are on by default.
    private static bool Flag(uint style, int bit) => ((style >> 16) & (1u << bit)) == 0 || (style & (1u << bit)) != 0;

    /// <summary>An OfficeArtCOLORREF as DrawingML: an RGB colour, or the indexed colours' defaults as black.</summary>
    private static XElement Color(uint color)
    {
        byte flags = (byte)(color >> 24);
        if (flags != 0)
        {
            // Palette, scheme and system colours: Excel's defaults, the window's fill and the text's line.
            return new XElement(A + "sysClr", new XAttribute("val", "windowText"), new XAttribute("lastClr", "000000"));
        }

        return new XElement(A + "srgbClr", new XAttribute("val", $"{color & 0xFF:X2}{(color >> 8) & 0xFF:X2}{(color >> 16) & 0xFF:X2}"));
    }

    /// <summary>The DrawingML preset geometry of an AutoShape type ([MS-ODRAW] 2.4.24), a rectangle for the rest.</summary>
    private static string Geometry(int type) => type switch
    {
        2 => "roundRect",
        3 => "ellipse",
        4 => "diamond",
        5 => "triangle",
        6 => "rtTriangle",
        7 => "parallelogram",
        8 => "trapezoid",
        9 => "hexagon",
        10 => "octagon",
        11 => "plus",
        12 => "star5",
        13 => "rightArrow",
        20 => "line",
        56 => "pentagon",
        66 => "leftArrow",
        67 => "downArrow",
        68 => "upArrow",
        _ => "rect",
    };

    private static string? Name(Shape shape) => Text(shape, 0x0380);

    private static string? Text(Shape shape, int id)
    {
        if (!shape.Properties.TryGetValue(id, out var property) || property.Complex is not { Length: > 0 } bytes)
        {
            return null;
        }

        string text = Encoding.Unicode.GetString(bytes, 0, bytes.Length / 2 * 2);
        int end = text.IndexOf('\0', StringComparison.Ordinal);
        text = end >= 0 ? text[..end] : text;
        return text.Length == 0 ? null : text;
    }

    /// <summary>The shapes of a group container, in order, the group's own shape first.</summary>
    private static void Walk(byte[] data, int start, int length, List<Shape> shapes, int depth)
    {
        if (depth > 32)
        {
            return;
        }

        bool first = true;
        foreach ((int type, int instance, int at, int size) in Records(data, start, length))
        {
            switch (type)
            {
                case 0xF003:
                    Walk(data, at, size, shapes, depth + 1);
                    break;
                case 0xF004:
                    {
                        Shape shape = ReadShape(data, at, size);
                        shape.TopLevel = depth == 1;
                        shape.GroupShape = first && depth > 0;
                        if (depth > 1 && first)
                        {
                            // A nested group's own shape anchors the group; its children are not top level.
                            shape.TopLevel = false;
                        }

                        shapes.Add(shape);
                        first = false;
                        break;
                    }
            }

            _ = instance;
        }
    }

    private static Shape ReadShape(byte[] data, int start, int length)
    {
        Shape shape = new();
        foreach ((int type, int instance, int at, int size) in Records(data, start, length))
        {
            switch (type)
            {
                case 0xF00A when size >= 8:
                    shape.Type = instance;
                    shape.Id = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at));
                    shape.Flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
                    break;
                case 0xF00B or 0xF121 or 0xF122:
                    Properties(data.AsSpan(at, size), instance, shape.Properties);
                    break;
                case 0xF010 when size >= 18:
                    shape.Anchor = new ushort[9];
                    for (int i = 0; i < 9; i++)
                    {
                        shape.Anchor[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + (2 * i)));
                    }

                    break;
                case 0xF011:
                    shape.ClientData = true;
                    break;
            }
        }

        return shape;
    }

    /// <summary>An FOPT record's properties: 6 bytes each, then the complex ones' data in their order.</summary>
    private static void Properties(ReadOnlySpan<byte> data, int count, Dictionary<int, (uint Value, byte[]? Complex)> properties)
    {
        count = Math.Min(count, data.Length / 6);
        int complexAt = count * 6;
        for (int i = 0; i < count; i++)
        {
            int id = BinaryPrimitives.ReadUInt16LittleEndian(data[(6 * i)..]);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[((6 * i) + 2)..]);
            byte[]? complex = null;
            if ((id & 0x8000) != 0)
            {
                int size = (int)Math.Min(value, (uint)Math.Max(0, data.Length - complexAt));
                complex = data.Slice(complexAt, size).ToArray();
                complexAt += size;
            }

            properties.TryAdd(id & 0x3FFF, (value, complex));
        }
    }

    /// <summary>The records of a span of OfficeArt data ([MS-ODRAW] 2.1.1): an 8-byte header each, cut to the span.</summary>
    internal static IEnumerable<(int Type, int Instance, int Start, int Length)> Records(byte[] data, int start, int length)
    {
        int end = Math.Min(data.Length, start + length);
        int at = start;
        while (at + 8 <= end)
        {
            int versionInstance = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at));
            int type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at + 2));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
            int bodyStart = at + 8;
            int bodyLength = (int)Math.Min(size, end - bodyStart);
            yield return (type, versionInstance >> 4, bodyStart, bodyLength);
            at = bodyStart + bodyLength;
        }
    }
}
