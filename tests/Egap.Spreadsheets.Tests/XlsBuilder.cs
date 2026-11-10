using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Office;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Builds small Excel 97-2003 (BIFF8) workbooks for the XLS reader's tests, record by record as [MS-XLS] lays them out: the
/// globals with a BOUNDSHEET per sheet, then each sheet's substream, in a compound file's "Workbook" stream.
/// </summary>
internal sealed class XlsBuilder
{
    private readonly List<byte[]> globals = [];
    private readonly List<(string Name, byte Type, byte State, List<byte[]> Records)> sheets = [];

    /// <summary>Adds a record to the globals, after the BOF and before the BOUNDSHEETs.</summary>
    public XlsBuilder Global(int type, params byte[][] parts)
    {
        globals.Add(Record(type, parts));
        return this;
    }

    /// <summary>Adds a sheet; <paramref name="type"/> 0 is a worksheet and 1 an Excel 4.0 macro sheet.</summary>
    public XlsBuilder Sheet(string name, byte type = 0, byte state = 0)
    {
        sheets.Add((name, type, state, []));
        return this;
    }

    /// <summary>Adds a record to the last sheet.</summary>
    public XlsBuilder Cell(int type, params byte[][] parts)
    {
        sheets[^1].Records.Add(Record(type, parts));
        return this;
    }

    /// <summary>The workbook stream alone.</summary>
    public byte[] Stream()
    {
        using var output = new MemoryStream();
        output.Write(Record(0x0809, Bof(0x0005)));
        foreach (byte[] record in globals)
        {
            output.Write(record);
        }

        var offsets = new List<long>();
        foreach ((string name, byte type, byte state, _) in sheets)
        {
            offsets.Add(output.Position + 4);
            output.Write(Record(0x0085, U32(0), [state, type], ShortString(name)));
        }

        output.Write(Record(0x000A));
        byte[] data;
        for (int i = 0; i < sheets.Count; i++)
        {
            long start = output.Position;
            data = output.GetBuffer();
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)offsets[i]), (uint)start);
            output.Write(Record(0x0809, Bof(sheets[i].Type == 1 ? 0x0040 : 0x0010)));
            foreach (byte[] record in sheets[i].Records)
            {
                output.Write(record);
            }

            output.Write(Record(0x000A));
        }

        return output.ToArray();
    }

    /// <summary>The workbook as an .xls file.</summary>
    public byte[] Build(string streamName = "Workbook")
    {
        using var file = new MemoryStream();
        CompoundFileWriter.Write(file, [(streamName, Stream())]);
        return file.ToArray();
    }

    /// <summary>Loads the built workbook.</summary>
    public Workbook Load() => Workbook.Load(new MemoryStream(Build()));

    public static byte[] Record(int type, params byte[][] parts)
    {
        byte[] body = [.. parts.SelectMany(p => p)];
        byte[] record = new byte[4 + body.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)type);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), (ushort)body.Length);
        body.CopyTo(record, 4);
        return record;
    }

    public static byte[] Bof(int kind) => [.. U16(0x0600), .. U16(kind), .. U16(0x0DBB), .. U16(0x07CC), .. U32(0x41), .. U32(0x06)];

    public static byte[] U16(int value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value);
        return bytes;
    }

    public static byte[] U32(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    public static byte[] Double(double value)
    {
        byte[] bytes = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(bytes, value);
        return bytes;
    }

    /// <summary>An XLUnicodeString: a 2-byte count, the option byte, then 8-bit or UTF-16 characters.</summary>
    public static byte[] String(string text) => [.. U16(text.Length), .. Characters(text)];

    /// <summary>A ShortXLUnicodeString: a 1-byte count, the option byte, then the characters.</summary>
    public static byte[] ShortString(string text) => [(byte)text.Length, .. Characters(text)];

    /// <summary>The option byte and the characters, 8-bit when they all fit.</summary>
    public static byte[] Characters(string text) => text.All(c => c < 256)
        ? [0, .. text.Select(c => (byte)c)]
        : [1, .. Encoding.Unicode.GetBytes(text)];

    /// <summary>The row, column and XF that start every cell record.</summary>
    public static byte[] At(int row, int column, int xf = 15) => [.. U16(row), .. U16(column), .. U16(xf)];

    /// <summary>A FORMULA record's body with a cached number.</summary>
    public static byte[] Formula(int row, int column, double value, byte[] tokens, int xf = 15) =>
        [.. At(row, column, xf), .. Double(value), .. U16(0), .. U32(0), .. U16(tokens.Length), .. tokens];

    /// <summary>A FORMULA record's body with a cached non-number: 0 text (a STRING follows), 1 Boolean, 2 error, 3 empty text.</summary>
    public static byte[] Formula(int row, int column, byte kind, byte value, byte[] tokens, int xf = 15) =>
        [.. At(row, column, xf), kind, 0, value, 0, 0, 0, 0xFF, 0xFF, .. U16(0), .. U32(0), .. U16(tokens.Length), .. tokens];

    /// <summary>A PtgRef or PtgRefN: a 2-byte row and a 2-byte column with the relative flags in its top bits.</summary>
    public static byte[] Ref(byte ptg, int row, int column, bool rowRelative = true, bool columnRelative = true) =>
        [ptg, .. U16(row), .. U16((column & 0x3FFF) | (rowRelative ? 0x8000 : 0) | (columnRelative ? 0x4000 : 0))];

    /// <summary>A PtgArea or PtgAreaN of relative references.</summary>
    public static byte[] Area(byte ptg, int firstRow, int lastRow, int firstColumn, int lastColumn, bool relative = true)
    {
        int flags = relative ? 0xC000 : 0;
        return [ptg, .. U16(firstRow), .. U16(lastRow), .. U16(firstColumn | flags), .. U16(lastColumn | flags)];
    }

    /// <summary>
    /// Fonts 0 to 3 in Arial 10, then <paramref name="extra"/> (whose first is font index 5, since BIFF8 skips 4), 15 style XFs,
    /// the Normal cell XF 15, then <paramref name="cellXfs"/> from XF 16.
    /// </summary>
    public XlsBuilder Styles(byte[][] extra, params byte[][] cellXfs)
    {
        for (int i = 0; i < 4; i++)
        {
            Global(0x0031, Font("Arial", 10));
        }

        foreach (byte[] font in extra)
        {
            Global(0x0031, font);
        }

        for (int i = 0; i < 15; i++)
        {
            Global(0x00E0, Xf(0, 0, style: true));
        }

        Global(0x00E0, Xf(0, 0));
        foreach (byte[] xf in cellXfs)
        {
            Global(0x00E0, xf);
        }

        return this;
    }

    public static byte[] Font(string name, double size, bool bold = false, bool italic = false, int color = 0x7FFF) =>
        [.. U16((int)(size * 20)), .. U16(italic ? 0x02 : 0), .. U16(color), .. U16(bold ? 700 : 400), .. U16(0), 0, 0, 0, 0, .. ShortString(name)];

    /// <summary>An XF: font, format, locked, alignment (horizontal, wrap and vertical in the first byte), no borders, a fill.</summary>
    public static byte[] Xf(int font, int format, bool style = false, byte alignment = 0x20, int pattern = 0, int foreground = 64, int background = 65) =>
        [.. U16(font), .. U16(format), .. U16(0x01 | (style ? 0xFFF4 : 0)), alignment, 0, 0, 0, .. U32(0), .. U32((uint)pattern << 26), .. U16(foreground | (background << 7))];
}
