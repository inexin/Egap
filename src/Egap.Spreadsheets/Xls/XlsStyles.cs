using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets.Xls;

/// <summary>
/// The FONT, FORMAT, XF and PALETTE records of a BIFF8 workbook ([MS-XLS] 2.4.122, 2.4.126, 2.4.353 and 2.4.188), turned
/// into the workbook's cell styles.
/// </summary>
internal sealed class XlsStyles
{
    /// <summary>The XF of a cell that names no other: the Normal style's cell XF ([MS-XLS] 2.4.353).</summary>
    public const int DefaultXf = 15;

    private readonly List<CellFont> fonts = [];
    private readonly Dictionary<int, string> formats = [];
    private readonly List<Xf> xfs = [];
    private readonly Dictionary<int, int> indexes = [];
    private uint[]? palette;

    private readonly record struct Xf(int Font, int Format, ushort Flags, uint Alignment, uint Border1, uint Border2, ushort Fill);

    public void ReadFont(RecordReader record)
    {
        double size = record.UInt16() / 20.0;
        ushort options = record.UInt16();
        int color = record.UInt16();
        int weight = record.UInt16();
        int script = record.UInt16();
        int underline = record.Byte();
        int family = record.Byte();
        int charset = record.Byte();
        record.Byte();
        string name = record.ShortUnicodeString();
        fonts.Add(new CellFont
        {
            Name = string.IsNullOrEmpty(name) ? "Arial" : name.Length > 31 ? name[..31] : name,
            Size = Math.Clamp(size, 1, 409),
            Bold = weight >= 700,
            Italic = (options & 0x02) != 0,
            Strikethrough = (options & 0x08) != 0,
            Underline = underline switch
            {
                1 => CellUnderline.Single,
                2 => CellUnderline.Double,
                0x21 => CellUnderline.SingleAccounting,
                0x22 => CellUnderline.DoubleAccounting,
                _ => CellUnderline.None,
            },
            VerticalPosition = script switch
            {
                1 => CellVerticalTextPosition.Superscript,
                2 => CellVerticalTextPosition.Subscript,
                _ => CellVerticalTextPosition.Baseline,
            },
            Color = color is 0x7FFF or 64 ? CellColor.Auto : Color(color),
            Family = family,
            CharacterSet = charset == 0 ? null : charset,
            Scheme = CellFontScheme.None,
        });
    }

    public void ReadFormat(RecordReader record)
    {
        int id = record.UInt16();
        string code = record.UnicodeString();
        if (code.Length is > 0 and <= 255)
        {
            formats[id] = code;
        }
    }

    public void ReadXf(RecordReader record)
    {
        int font = record.UInt16();
        int format = record.UInt16();
        ushort flags = record.UInt16();
        uint alignment = record.Byte() | ((uint)record.Byte() << 8) | ((uint)record.Byte() << 16) | ((uint)record.Byte() << 24);
        uint border1 = record.UInt32();
        uint border2 = record.UInt32();
        ushort fill = record.UInt16();
        xfs.Add(new Xf(font, format, flags, alignment, border1, border2, fill));
    }

    public void ReadPalette(RecordReader record)
    {
        int count = Math.Min((int)record.UInt16(), 56);
        palette = new uint[56];
        for (int i = 0; i < count; i++)
        {
            byte red = record.Byte(), green = record.Byte(), blue = record.Byte();
            record.Byte();
            palette[i] = ((uint)red << 16) | ((uint)green << 8) | blue;
        }

        for (int i = count; i < 56; i++)
        {
            palette[i] = Printing.SheetColors.DefaultPalette[i + 8];
        }
    }

    /// <summary>The number format code of a format id, as the file defines it or as Excel builds it in.</summary>
    public string FormatCode(int id) => formats.GetValueOrDefault(id) ?? NumberFormatTable.BuiltIn(id) ?? BuiltInCurrency(id) ?? "General";

    /// <summary>Whether an XF is a cell XF of the file.</summary>
    public bool IsCellXf(int xf) => (uint)xf < (uint)xfs.Count && (xfs[xf].Flags & 0x04) == 0;

    /// <summary>
    /// The index in the workbook's style table of an XF's style; the default style for an XF the file lacks. The first call
    /// adds the default XF's style, so that it is style 0.
    /// </summary>
    public int Index(Workbook book, int xf)
    {
        if (book.Styles.Count == 0)
        {
            book.Styles.Add((uint)DefaultXf < (uint)xfs.Count ? Style(DefaultXf) : CellStyle.Default);
        }

        if ((uint)xf >= (uint)xfs.Count)
        {
            return 0;
        }

        if (!indexes.TryGetValue(xf, out int index))
        {
            index = book.Styles.Add(Style(xf));
            indexes[xf] = index;
        }

        return index;
    }

    /// <summary>The style of an XF.</summary>
    public CellStyle Style(int index)
    {
        Xf xf = xfs[index];
        int font = xf.Font >= 4 ? xf.Font - 1 : xf.Font;
        uint a = xf.Alignment;
        int rotation = (int)((a >> 8) & 0xFF);
        return new CellStyle
        {
            Font = (uint)font < (uint)fonts.Count ? fonts[font] : CellFont.Default,
            NumberFormat = FormatCode(xf.Format),
            Protection = new CellProtection { Locked = (xf.Flags & 0x01) != 0, Hidden = (xf.Flags & 0x02) != 0 },
            Alignment = new CellAlignment
            {
                Horizontal = (CellHorizontalAlignment)(a & 0x07),
                WrapText = (a & 0x08) != 0,
                Vertical = ((a >> 4) & 0x07) switch
                {
                    0 => CellVerticalAlignment.Top,
                    1 => CellVerticalAlignment.Center,
                    3 => CellVerticalAlignment.Justify,
                    4 => CellVerticalAlignment.Distributed,
                    _ => CellVerticalAlignment.Bottom,
                },
                TextRotation = rotation is <= 180 or 255 ? rotation : 0,
                Indent = (int)((a >> 16) & 0x0F),
                ShrinkToFit = (a & 0x100000) != 0,
                ReadingOrder = (CellReadingOrder)Math.Min((a >> 22) & 0x03, 2),
            },
            Borders = Borders(xf.Border1, xf.Border2),
            Fill = Fill((int)(xf.Border2 >> 26) & 0x3F, xf.Fill & 0x7F, (xf.Fill >> 7) & 0x7F),
        };
    }

    private CellBorders Borders(uint first, uint second)
    {
        CellBorder Side(uint style, uint color) =>
            style == 0 || style > 13 ? CellBorder.None : new CellBorder((CellBorderStyle)style, Color((int)color));

        uint diagonalStyle = (second >> 21) & 0x0F;
        return new CellBorders
        {
            Left = Side(first & 0x0F, (first >> 16) & 0x7F),
            Right = Side((first >> 4) & 0x0F, (first >> 23) & 0x7F),
            Top = Side((first >> 8) & 0x0F, second & 0x7F),
            Bottom = Side((first >> 12) & 0x0F, (second >> 7) & 0x7F),
            Diagonal = Side(diagonalStyle, (second >> 14) & 0x7F),
            DiagonalDown = diagonalStyle != 0 && (first & 0x40000000) != 0,
            DiagonalUp = diagonalStyle != 0 && (first & 0x80000000) != 0,
        };
    }

    private CellFill Fill(int pattern, int foreground, int background) => pattern switch
    {
        0 or > 18 => CellFill.None,
        _ => new CellFill { Pattern = (CellFillPattern)pattern, Foreground = Color(foreground), Background = Color(background) },
    };

    /// <summary>
    /// A colour by its index ([MS-XLS] 2.5.161 Icv): 0 to 7 and the palette's 8 to 63 by index, or by value when the file
    /// changes the palette; 64 and up are the system colours, automatic here.
    /// </summary>
    private CellColor Color(int index)
    {
        if (index >= 64 || index < 0)
        {
            return CellColor.Auto;
        }

        if (index >= 8 && palette is not null && palette[index - 8] != Printing.SheetColors.DefaultPalette[index])
        {
            uint rgb = palette[index - 8];
            return CellColor.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }

        return CellColor.FromIndex(index);
    }

    /// <summary>The currency and accounting formats Excel builds in with the system's currency, as an en-US Excel shows them.</summary>
    private static string? BuiltInCurrency(int id) => id switch
    {
        5 => "\"$\"#,##0_);\\(\"$\"#,##0\\)",
        6 => "\"$\"#,##0_);[Red]\\(\"$\"#,##0\\)",
        7 => "\"$\"#,##0.00_);\\(\"$\"#,##0.00\\)",
        8 => "\"$\"#,##0.00_);[Red]\\(\"$\"#,##0.00\\)",
        41 => "_(* #,##0_);_(* \\(#,##0\\);_(* \"-\"_);_(@_)",
        42 => "_(\"$\"* #,##0_);_(\"$\"* \\(#,##0\\);_(\"$\"* \"-\"_);_(@_)",
        43 => "_(* #,##0.00_);_(* \\(#,##0.00\\);_(* \"-\"??_);_(@_)",
        44 => "_(\"$\"* #,##0.00_);_(\"$\"* \\(#,##0.00\\);_(\"$\"* \"-\"??_);_(@_)",
        _ => null,
    };
}
