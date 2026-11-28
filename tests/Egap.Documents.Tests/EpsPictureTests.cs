using System.Buffers.Binary;
using System.Text;

using Egami;
using Egami.Formats.Tiff;
using Egami.Pixels;

using Egap.Documents.Layout;
using Egap.Documents.Pdf;

namespace Egap.Documents.Tests;

/// <summary>EPS pictures drawn by their previews, each made here: red on the left half, blue (or white) on the right.</summary>
public sealed class EpsPictureTests
{
    private const string Header = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 40 20\n%%Creator: (Egap tests)\n%%EndComments\n";

    private const string Body = "1 0 0 setrgbcolor 0 0 20 20 rectfill 0 0 1 setrgbcolor 20 0 20 20 rectfill\n%%EOF\n";

    private static readonly Rgba8 Red = new(255, 0, 0, 255), Blue = new(0, 0, 255, 255);

    /// <summary>An EPS file with a DOS binary header pointing at a TIFF or WMF preview.</summary>
    private static byte[] WithBinaryHeader(byte[]? tiff, byte[]? wmf)
    {
        byte[] postScript = Encoding.ASCII.GetBytes(Header + Body);
        using MemoryStream file = new();
        byte[] header = new byte[30];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0xC5D0D3C6);
        int at = 30;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), at);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), postScript.Length);
        at += postScript.Length;
        if (wmf is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), at);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), wmf.Length);
            at += wmf.Length;
        }

        if (tiff is not null)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), at);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), tiff.Length);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), 0xFFFF);
        file.Write(header);
        file.Write(postScript);
        file.Write(wmf ?? []);
        file.Write(tiff ?? []);
        return file.ToArray();
    }

    private static byte[] Tiff()
    {
        using Raster<Rgba8> raster = Raster.Create(40, 20, Red);
        for (int y = 0; y < 20; y++)
        {
            raster.Row(y)[20..].Fill(Blue);
        }

        using MemoryStream stream = new();
        raster.Save(stream, TiffFormat.Info);
        return stream.ToArray();
    }

    /// <summary>A Windows metafile: a 400 by 200 window, its left half a red rectangle and its right half a blue one.</summary>
    private static byte[] Wmf()
    {
        List<ushort[]> records =
        [
            [0x020B, 0, 0], // SetWindowOrg(y, x)
            [0x020C, 200, 400], // SetWindowExt(height, width)
            [0x02FC, 0, 0x00FF, 0x0000, 0], // CreateBrushIndirect: solid, red (COLORREF 0x000000FF)
            [0x012D, 0], // SelectObject
            [0x041B, 200, 200, 0, 0], // Rectangle(bottom, right, top, left)
            [0x01F0, 0], // DeleteObject
            [0x02FC, 0, 0x0000, 0x00FF, 0], // CreateBrushIndirect: solid, blue (COLORREF 0x00FF0000)
            [0x012D, 0],
            [0x041B, 200, 400, 0, 200],
            [0x01F0, 0],
            [0x0000], // EOF
        ];
        using MemoryStream stream = new();
        byte[] word = new byte[2];
        int size = 9 + records.Sum(r => r.Length + 2), largest = records.Max(r => r.Length + 2);
        foreach (ushort value in new ushort[] { 1, 9, 0x0300, (ushort)size, (ushort)(size >> 16), 1, (ushort)largest, (ushort)(largest >> 16), 0 })
        {
            Write(value);
        }

        foreach (ushort[] record in records)
        {
            int words = record.Length + 2;
            Write((ushort)words);
            Write((ushort)(words >> 16));
            foreach (ushort value in record)
            {
                Write(value);
            }
        }

        return stream.ToArray();

        void Write(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(word, value);
            stream.Write(word);
        }
    }

    /// <summary>Photoshop's EPS: the full image as <c>%ImageData</c> RGB hex.</summary>
    private static byte[] Photoshop()
    {
        StringBuilder text = new(Header.Replace("%%EndComments\n", "", StringComparison.Ordinal));
        text.Append("%%EndComments\n%ImageData: 40 20 8 3 0 40 2 \"beginimage\"\nbeginimage\n");
        for (int y = 0; y < 20; y++)
        {
            // A row holds each channel's 40 samples in turn: the block size is a row's samples.
            foreach ((string Left, string Right) channel in new[] { ("FF", "00"), ("00", "00"), ("00", "FF") })
            {
                for (int x = 0; x < 40; x++)
                {
                    text.Append(x < 20 ? channel.Left : channel.Right);
                }
            }

            text.Append('\n');
        }

        text.Append("%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    /// <summary>An EPSI bitmap preview: 8-bit, where 0 is white, black on the left and white on the right.</summary>
    private static byte[] Epsi()
    {
        StringBuilder text = new(Header);
        text.Append("%%BeginPreview: 40 20 8 20\n");
        for (int y = 0; y < 20; y++)
        {
            text.Append('%');
            for (int x = 0; x < 40; x++)
            {
                text.Append(x < 20 ? "FF" : "00");
            }

            text.Append('\n');
        }

        text.Append("%%EndPreview\n").Append(Body);
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    public static TheoryData<string> Kinds => ["tiff", "wmf", "photoshop", "epsi"];

    private static byte[] Eps(string kind) => kind switch
    {
        "tiff" => WithBinaryHeader(Tiff(), null),
        "wmf" => WithBinaryHeader(null, Wmf()),
        "photoshop" => Photoshop(),
        "epsi" => Epsi(),
        _ => Encoding.ASCII.GetBytes(Header + Body),
    };

    private static Document WithPicture(byte[] eps, out Picture picture)
    {
        Document document = new();
        Section section = document.AddSection();
        section.PageSetup.MarginLeft = section.PageSetup.MarginRight = section.PageSetup.MarginTop = section.PageSetup.MarginBottom = 72;
        picture = section.Body.AddParagraph("").Inlines.Add(new Picture(document, ImageData.FromBytes(eps), 80, 40));
        return document;
    }

    /// <summary>Whether a column of the page has a pixel close to <paramref name="color"/>.</summary>
    private static bool ColumnHas(Raster<Rgba8> page, int x, Rgba8 color)
    {
        for (int y = 0; y < page.Height; y++)
        {
            Rgba8 pixel = page.Row(y)[x];
            if (Math.Abs(pixel.R - color.R) < 40 && Math.Abs(pixel.G - color.G) < 40 && Math.Abs(pixel.B - color.B) < 40)
            {
                return true;
            }
        }

        return false;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void EachPreviewIsDrawn(string kind)
    {
        Document document = WithPicture(Eps(kind), out Picture picture);
        Assert.Equal("application/postscript", picture.Image.ContentType);
        List<LayoutWarning> warnings = [];
        using Raster<Rgba8> page = document.Layout(new LayoutOptions { Fonts = FontSettings.Portable, OnWarning = warnings.Add }).Pages[0].Render(dpi: 72);
        Assert.DoesNotContain(warnings, w => w.Kind == LayoutWarningKind.PictureNotDrawn);

        // The picture is 80 points wide from the left margin: its left and right quarters.
        (Rgba8 left, Rgba8 right) = kind == "epsi" ? (new Rgba8(0, 0, 0, 255), new Rgba8(255, 255, 255, 255)) : (Red, Blue);
        Assert.True(ColumnHas(page, 72 + 20, left), "left half");
        Assert.True(ColumnHas(page, 72 + 60, right) || kind == "epsi", "right half");
    }

    [Fact]
    public void AnEpsWithoutAPreviewIsAnEmptyBoxWithAWarning()
    {
        Document document = WithPicture(Eps("none"), out Picture picture);
        List<LayoutWarning> warnings = [];
        using Raster<Rgba8> page = document.Layout(new LayoutOptions { Fonts = FontSettings.Portable, OnWarning = warnings.Add }).Pages[0].Render(dpi: 72);
        LayoutWarning warning = Assert.Single(warnings, w => w.Kind == LayoutWarningKind.PictureNotDrawn);
        Assert.Same(picture, warning.Node);
        Assert.False(ColumnHas(page, 72 + 20, Red));
    }

    [Theory]
    [InlineData("application/postscript")]
    [InlineData("image/x-eps")]
    [InlineData("application/eps")]
    public void EveryEpsTypeIsDrawn(string type)
    {
        Document document = WithPicture(Eps("tiff"), out Picture picture);
        picture.Image = new ImageData(picture.Image.Bytes, type);
        using Raster<Rgba8> page = document.Layout(new LayoutOptions { Fonts = FontSettings.Portable }).Pages[0].Render(dpi: 72);
        Assert.True(ColumnHas(page, 72 + 20, Red));
    }

    [Fact]
    public void FormatsKeepTheEpsAndPdfDrawsItsPreview()
    {
        byte[] eps = Eps("wmf");
        Document document = WithPicture(eps, out _);

        using MemoryStream docx = new();
        DocxFormat.Save(document, docx);
        Docx.OpcPackage package = Docx.OpcPackage.Read(new MemoryStream(docx.ToArray()), new DocumentLimits());
        Docx.OpcPart part = Assert.Single(package.Parts, p => p.Name.EndsWith(".eps", StringComparison.Ordinal));
        Assert.Equal("application/postscript", part.ContentType);
        Assert.Equal(eps, part.Data);
        docx.Position = 0;
        Picture fromDocx = DocxFormat.Load(docx).Descendants<Picture>().Single();
        Assert.Equal("application/postscript", fromDocx.Image.ContentType);
        Assert.Equal(eps, fromDocx.Image.Bytes.ToArray());

        using MemoryStream odt = new();
        OdtFormat.Save(document, odt);
        odt.Position = 0;
        Assert.Equal(eps, OdtFormat.Load(odt).Descendants<Picture>().Single().Image.Bytes.ToArray());

        using MemoryStream pdf = new();
        document.SaveAsPdf(pdf, new PdfExportOptions { Layout = new LayoutOptions { Fonts = FontSettings.Portable } });
        using Egap.Pdf.PdfDocument opened = Egap.Pdf.PdfDocument.Open(pdf.ToArray());
        using Raster<Rgba8> page = opened.Pages[0].Render(new Egap.Pdf.PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        Assert.True(ColumnHas(page, 72 + 20, Red));
        Assert.True(ColumnHas(page, 72 + 60, Blue));
    }

    [Fact]
    public void RtfWritesTheWmfPreviewAsAPicture()
    {
        // RTF has no EPS: the preview is written as a PNG, a metafile preview loaded through Egami.Metafiles.
        Document document = WithPicture(Eps("wmf"), out _);
        using MemoryStream rtf = new();
        IReadOnlyList<ConversionWarning> warnings = RtfFormat.Save(document, rtf);
        Assert.DoesNotContain(warnings, w => w.Kind == ConversionWarningKind.PictureDropped);
        Assert.Contains(@"\pngblip", Encoding.ASCII.GetString(rtf.ToArray()), StringComparison.Ordinal);

        // A picture placed without a size takes its preview's: 40 by 20 pixels at 96 an inch.
        Assert.Equal((30.0, 15.0), ImageSize.Intrinsic(Eps("tiff")));
    }

    [Fact]
    public void EpsIsRecognisedByItsBytes()
    {
        Assert.Equal("application/postscript", ImageData.GuessContentType(Eps("tiff")));
        Assert.Equal("application/postscript", ImageData.GuessContentType(Eps("none")));
        Assert.Equal("application/postscript", ImageData.GuessContentType(Encoding.ASCII.GetBytes("  \n%!PS-Adobe-3.0 EPSF-3.0\n")));
        Assert.Equal("application/octet-stream", ImageData.GuessContentType("%PDF-1.7"u8));
    }
}
