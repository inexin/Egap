using Egap.Pdf.Fonts;

namespace Egap.Pdf.Content;

/// <summary>
/// How a font dictionary splits shown strings into codes and how wide each glyph is (9.2.4, 9.6.2, 9.7.4.3), as much as the
/// content walker needs to advance the text matrix. Simple fonts take one byte per code with <c>/Widths</c>, falling back to
/// <c>/MissingWidth</c> and the standard 14 metrics; Type 3 widths are in glyph space and go through <c>/FontMatrix</c>;
/// composite fonts take codes as their CMap reads them and widths from <c>/W</c> and <c>/DW</c>.
/// </summary>
internal sealed class FontWidths
{
    private readonly double[] simple = [];
    private readonly int firstChar;
    private readonly double missing;
    private readonly Standard14Font? standard;
    private readonly Dictionary<int, double>? cidWidths;
    private readonly CodeSplitter? splitter;

    private FontWidths(PdfDictionary? font)
    {
        if (font is null)
        {
            return;
        }

        string? subtype = font.GetName("Subtype")?.Value;
        if (subtype == "Type0")
        {
            IsComposite = true;
            splitter = CodeSplitter.For(font.Get("Encoding")?.Resolve());
            PdfDictionary? descendant = font.Get<PdfArray>("DescendantFonts") is { Count: > 0 } descendants ? descendants.Get<PdfDictionary>(0) : null;
            missing = descendant?.GetNumber("DW") ?? 1000;
            cidWidths = ReadW(descendant?.Get<PdfArray>("W"));
            IsVertical = splitter.IsVertical;
            return;
        }

        firstChar = (int)Math.Clamp(font.GetInt("FirstChar") ?? 0, 0, 255);
        if (font.Get<PdfArray>("Widths") is PdfArray widths)
        {
            simple = new double[Math.Min(widths.Count, 256)];
            for (int i = 0; i < simple.Length; i++)
            {
                simple[i] = widths.GetNumber(i) ?? 0;
            }
        }

        missing = font.Get<PdfDictionary>("FontDescriptor")?.GetNumber("MissingWidth") ?? 0;
        if (subtype == "Type3")
        {
            // Type 3 widths are in glyph space: scale them by the font matrix into thousandths of text space.
            double scale = font.Get<PdfArray>("FontMatrix")?.GetNumber(0) ?? 0.001;
            for (int i = 0; i < simple.Length; i++)
            {
                simple[i] *= scale * 1000;
            }

            missing *= scale * 1000;
        }
        else if (simple.Length == 0)
        {
            standard = Standard14Font.Get(font.GetName("BaseFont")?.Value);
        }
    }

    /// <summary>Whether the font is composite (Type0), so codes may take more than one byte.</summary>
    public bool IsComposite { get; }

    /// <summary>Whether the font writes vertically (a CMap ending in <c>-V</c>).</summary>
    public bool IsVertical { get; }

    public static FontWidths For(PdfDictionary? font)
    {
        return new FontWidths(font);
    }

    /// <summary>The next code in <paramref name="bytes"/> from <paramref name="offset"/> and how many bytes it takes.</summary>
    public (int Code, int Length) NextCode(ReadOnlySpan<byte> bytes, int offset)
    {
        if (splitter is null)
        {
            return (bytes[offset], 1);
        }

        return CodeSplitter.Next(bytes, offset);
    }

    /// <summary>The glyph's width in thousandths of text space.</summary>
    public double Width(int code)
    {
        if (IsComposite)
        {
            int cid = CodeSplitter.ToCid(code);
            return cidWidths is not null && cidWidths.TryGetValue(cid, out double w) ? w : missing;
        }

        int index = code - firstChar;
        if ((uint)index < (uint)simple.Length)
        {
            return simple[index];
        }

        return standard is not null && code is >= 0 and <= 255 ? standard.Width((byte)code) : missing;
    }

    /// <summary>Reads a CIDFont's <c>/W</c> array: <c>c [w1 w2 ...]</c> and <c>cFirst cLast w</c> runs (9.7.4.3).</summary>
    private static Dictionary<int, double>? ReadW(PdfArray? w)
    {
        if (w is null)
        {
            return null;
        }

        Dictionary<int, double> widths = [];
        const int MaxEntries = 1 << 20;
        int i = 0;
        while (i < w.Count && widths.Count < MaxEntries)
        {
            if (w.GetInt(i) is not long first)
            {
                break;
            }

            if (i + 1 < w.Count && w.Get(i + 1).Resolve() is PdfArray list)
            {
                for (int k = 0; k < list.Count && widths.Count < MaxEntries; k++)
                {
                    widths[(int)Math.Clamp(first + k, 0, int.MaxValue)] = list.GetNumber(k) ?? 0;
                }

                i += 2;
            }
            else if (i + 2 < w.Count && w.GetInt(i + 1) is long last && w.GetNumber(i + 2) is double width)
            {
                for (long c = Math.Max(first, 0); c <= last && widths.Count < MaxEntries; c++)
                {
                    widths[(int)Math.Min(c, int.MaxValue)] = width;
                }

                i += 3;
            }
            else
            {
                break;
            }
        }

        return widths;
    }

    /// <summary>How a composite font's CMap splits strings into codes and maps codes to CIDs.</summary>
    private sealed class CodeSplitter
    {
        private CodeSplitter(bool vertical)
        {
            IsVertical = vertical;
        }

        public bool IsVertical { get; }

        public static CodeSplitter For(PdfObject? encoding)
        {
            string? name = (encoding as PdfName)?.Value ?? (encoding as PdfStream)?.Dictionary.GetName("CMapName")?.Value;
            return new CodeSplitter(name?.EndsWith("-V", StringComparison.Ordinal) == true);
        }

        /// <summary>Two-byte codes, as Identity-H and -V and most CJK CMaps use; a lone last byte is a code of its own.</summary>
        public static (int Code, int Length) Next(ReadOnlySpan<byte> bytes, int offset)
        {
            return offset + 1 < bytes.Length ? ((bytes[offset] << 8) | bytes[offset + 1], 2) : (bytes[offset], 1);
        }

        public static int ToCid(int code)
        {
            return code;
        }
    }
}
