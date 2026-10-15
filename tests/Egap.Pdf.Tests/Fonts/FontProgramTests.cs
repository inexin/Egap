using System.Text;

using Egami.Fonts;

namespace Egap.Pdf.Tests.Fonts;

/// <summary>Embedded font programs as Egami typefaces, Type 1 through CFF: Phase 3 task 19.</summary>
public sealed class FontProgramTests
{
    private static readonly PdfDocument Objects = PdfDocument.Create();

    [Fact]
    public void ConvertsAType1ProgramToCff()
    {
        // A box for A, an accent made of a line, and Aacute composed of both with seac.
        byte[] box = CharString(0, 600, [100, 0, 21], [300, 0, 5], [0, 700, 5], [-300, 0, 5], [9], [14]);
        byte[] acute = CharString(0, 300, [50, 750, 21], [100, 100, 5], [-20, 0, 5], [9], [14]);
        byte[] aacute = CharString(0, 600, [0, 150, 0, 65, 194, 1206]);
        PdfFont font = Type1Font(Program(("A", box), ("acute", acute), ("Aacute", aacute)));

        Typeface face = Assert.IsType<Typeface>(font.GetTypeface());
        Assert.Empty(font.Warnings);
        ushort a = font.GetGlyphId('A')!.Value;
        Assert.NotEqual(0, a);
        Assert.Equal(new GlyphExtents(100, 0, 400, 700), face.GetGlyphExtents(a));
        Assert.Equal(600, face.GetAdvance(a));

        // Aacute through the font's own encoding (code 0xC9 in the program), the accent 150 units right.
        ushort composed = font.GetGlyphId(0xC9)!.Value;
        Assert.Equal(new GlyphExtents(100, 0, 400, 850), face.GetGlyphExtents(composed));
    }

    [Fact]
    public void ReadsCharstringsWithoutTheirEndOperator()
    {
        // ttf2pt1 (pdf.js issue 14462) ends each charstring with a newline alone, no ND.
        byte[] box = CharString(0, 600, [100, 0, 21], [300, 0, 5], [0, 700, 5], [-300, 0, 5], [9], [14]);
        PdfFont font = Type1Font(Program("\n", ("A", box), ("acute", box), ("Aacute", box)));

        Assert.Empty(font.Warnings);
        Assert.NotEqual((ushort)0, font.GetGlyphId('A'));
        Assert.NotEqual((ushort)0, font.GetGlyphId(0xC2));
        Assert.NotEqual((ushort)0, font.GetGlyphId(0xC9));
    }

    [Theory]
    [InlineData(" NP\n")]
    [InlineData(" noaccess put\n")]
    public void ReadsEverySubroutine(string subrEnd)
    {
        // pdf.js issue 3207: subroutines ended by "noaccess put" rather than NP; the box is drawn by the last one.
        byte[] ret = [11];
        byte[] box = [.. CharString(0, 0, [100, 0, 21], [300, 0, 5], [0, 700, 5], [-300, 0, 5], [9])[3..], 11];
        byte[] glyph = CharString(0, 600, [4, 10], [14]);
        PdfFont font = Type1Font(Program(" ND\n", [ret, ret, ret, ret, box], subrEnd, ("A", glyph)));

        Typeface face = Assert.IsType<Typeface>(font.GetTypeface());
        Assert.Equal(new GlyphExtents(100, 0, 400, 700), face.GetGlyphExtents(font.GetGlyphId('A')!.Value));
    }

    [Fact]
    public void ReadsPlainCharstringsUnderLenIVMinusOne()
    {
        // pdf.js issue 7769: lenIV -1 stores the charstrings without their own encryption.
        byte[] box = CharString(0, 600, [100, 0, 21], [300, 0, 5], [0, 700, 5], [-300, 0, 5], [9], [14]);
        PdfFont font = Type1Font(Program(" ND ", [], "", encrypt: false, ("A", box)));

        Typeface face = Assert.IsType<Typeface>(font.GetTypeface());
        Assert.Equal(new GlyphExtents(100, 0, 400, 700), face.GetGlyphExtents(font.GetGlyphId('A')!.Value));
    }

    [Fact]
    public void ReportsABrokenProgramInsteadOfThrowing()
    {
        PdfDictionary font = new() { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("TrueType"), ["BaseFont"] = PdfName.Get("Broken") };
        PdfStream file = new();
        file.SetDecodedBytes("not a font at all"u8);
        font["FontDescriptor"] = new PdfDictionary { ["Type"] = PdfName.Get("FontDescriptor"), ["FontName"] = PdfName.Get("Broken"), ["FontFile2"] = Objects.Objects.Add(file) };
        PdfFont broken = PdfFont.FromDictionary(font);

        Assert.True(broken.IsEmbedded);
        Assert.Null(broken.GetTypeface());
        Assert.Null(broken.GetGlyphId(65));
        Assert.Equal(PdfWarningCode.FontProgramUnreadable, Assert.Single(broken.Warnings).Code);
    }

    [Fact]
    public void ReachesTrueTypeGlyphsThroughTheUnicodeCmap()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "files", "fonts", "NotoSans-Latin.ttf"));
        PdfDictionary font = new() { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("TrueType"), ["BaseFont"] = PdfName.Get("NotoSans"), ["Encoding"] = PdfName.Get("WinAnsiEncoding") };
        PdfStream file = new();
        file.SetDecodedBytes(data);
        font["FontDescriptor"] = new PdfDictionary { ["Flags"] = new PdfInteger(32), ["FontFile2"] = Objects.Objects.Add(file) };
        PdfFont pdfFont = PdfFont.FromDictionary(font);

        Typeface face = Assert.IsType<Typeface>(pdfFont.GetTypeface());
        Assert.Equal(face.GetGlyph('e'), pdfFont.GetGlyphId('e'));
        Assert.Equal(face.GetGlyph('\u00E9'), pdfFont.GetGlyphId(0xE9));
        Assert.Equal(face.GetGlyph('\u20AC'), pdfFont.GetGlyphId(0x80));
    }

    private static PdfFont Type1Font(byte[] program)
    {
        PdfStream file = new();
        file.SetDecodedBytes(program);
        PdfDictionary descriptor = new() { ["Type"] = PdfName.Get("FontDescriptor"), ["FontName"] = PdfName.Get("Test"), ["Flags"] = new PdfInteger(32), ["FontFile"] = Objects.Objects.Add(file) };
        return PdfFont.FromDictionary(new PdfDictionary { ["Type"] = PdfName.Get("Font"), ["Subtype"] = PdfName.Get("Type1"), ["BaseFont"] = PdfName.Get("Test"), ["FontDescriptor"] = descriptor });
    }

    /// <summary>A Type 1 program: clear text with a custom encoding, then the eexec part with lenIV 4 and the charstrings.</summary>
    private static byte[] Program(params (string Name, byte[] CharString)[] glyphs)
    {
        return Program(" ND\n", glyphs);
    }

    private static byte[] Program(string end, params (string Name, byte[] CharString)[] glyphs)
    {
        return Program(end, [], "", glyphs);
    }

    /// <summary>A Type 1 program with subroutines, each entry ended by <paramref name="subrEnd"/>.</summary>
    private static byte[] Program(string end, byte[][] subrs, string subrEnd, params (string Name, byte[] CharString)[] glyphs)
    {
        return Program(end, subrs, subrEnd, encrypt: true, glyphs);
    }

    /// <summary>As above; with <paramref name="encrypt"/> false the charstrings are stored plain under lenIV -1.</summary>
    private static byte[] Program(string end, byte[][] subrs, string subrEnd, bool encrypt, params (string Name, byte[] CharString)[] glyphs)
    {
        string clear = "%!PS-AdobeFont-1.0: Test\n/FontName /Test def\n/FontMatrix [0.001 0 0 0.001 0 0] readonly def\n/FontBBox {0 0 600 900} readonly def\n"
            + "/Encoding 256 array\n0 1 255 {1 index exch /.notdef put} for\ndup 65 /A put\ndup 194 /acute put\ndup 201 /Aacute put\nreadonly def\ncurrentdict end\ncurrentfile eexec\n";
        List<byte> priv = [.. Encoding.ASCII.GetBytes("dup /Private 8 dict dup begin\n/lenIV " + (encrypt ? 4 : -1) + " def\n/Subrs " + subrs.Length + " array\n")];
        for (int i = 0; i < subrs.Length; i++)
        {
            byte[] encrypted = encrypt ? Encrypt([0, 0, 0, 0, .. subrs[i]], 4330) : subrs[i];
            priv.AddRange(Encoding.ASCII.GetBytes($"dup {i} {encrypted.Length} RD "));
            priv.AddRange(encrypted);
            priv.AddRange(Encoding.ASCII.GetBytes(subrEnd));
        }

        priv.AddRange(Encoding.ASCII.GetBytes("ND\n2 index /CharStrings " + (glyphs.Length + 1) + " dict dup begin\n"));
        foreach ((string name, byte[] cs) in glyphs.Prepend((".notdef", CharString(0, 0, [14]))))
        {
            byte[] encrypted = encrypt ? Encrypt([0, 0, 0, 0, .. cs], 4330) : cs;
            priv.AddRange(Encoding.ASCII.GetBytes($"/{name} {encrypted.Length} RD "));
            priv.AddRange(encrypted);
            priv.AddRange(Encoding.ASCII.GetBytes(end));
        }

        priv.AddRange("end\nend\nmark currentfile closefile\n"u8.ToArray());
        return [.. Encoding.ASCII.GetBytes(clear), .. Encrypt([0, 0, 0, 0, .. priv], 55665), .. "\n0000000000000000\ncleartomark\n"u8.ToArray()];
    }

    /// <summary>A charstring: hsbw, then each command's numbers and operator (1206 is seac, as 12 6).</summary>
    private static byte[] CharString(int sbx, int width, params int[][] commands)
    {
        List<byte> code = [];
        Number(code, sbx);
        Number(code, width);
        code.Add(13);
        foreach (int[] command in commands)
        {
            for (int i = 0; i < command.Length - 1; i++)
            {
                Number(code, command[i]);
            }

            int op = command[^1];
            code.AddRange(op >= 1200 ? [12, (byte)(op - 1200)] : [(byte)op]);
        }

        return [.. code];
    }

    private static void Number(List<byte> code, int v)
    {
        if (v is >= -107 and <= 107)
        {
            code.Add((byte)(v + 139));
        }
        else
        {
            code.Add(255);
            code.AddRange([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);
        }
    }

    private static byte[] Encrypt(byte[] plain, ushort key)
    {
        ushort r = key;
        byte[] cipher = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            byte c = (byte)(plain[i] ^ (r >> 8));
            cipher[i] = c;
            r = (ushort)(((c + r) * 52845) + 22719);
        }

        return cipher;
    }
}
