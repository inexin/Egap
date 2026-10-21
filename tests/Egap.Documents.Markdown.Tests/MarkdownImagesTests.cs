using Egami.Pixels;

namespace Egap.Documents.Markdown.Tests;

/// <summary>Pictures in Markdown output (Phase 6 Build Plan task 8b, design Q20).</summary>
public sealed class MarkdownImagesTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("egap-md-images-").FullName;

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
    }

    private static byte[] Encoded(string format, Rgba8 color)
    {
        using MemoryStream stream = new();
        using Egami.Raster<Rgba8> raster = Egami.Raster.Create(8, 6, color);
        raster.Save(stream, format);
        return stream.ToArray();
    }

    /// <summary>The smallest EMF: a header for a 10 by 10 point frame and the end record.</summary>
    private static byte[] Emf()
    {
        using MemoryStream stream = new();
        using BinaryWriter w = new(stream);
        int[] header = [1, 108, 0, 0, 13, 13, 0, 0, 353, 353, 0x464D4520, 0x10000, 128, 2];
        foreach (int value in header)
        {
            w.Write(value);
        }

        w.Write((short)1);
        w.Write((short)0);
        foreach (int value in (int[])[0, 0, 0, 1920, 1080, 508, 286, 0, 0, 0, 508000, 286000])
        {
            w.Write(value);
        }

        foreach (int value in (int[])[14, 20, 0, 16, 20])
        {
            w.Write(value);
        }

        return stream.ToArray();
    }

    private static Document WithPictures(params (byte[] Bytes, string Type)[] images)
    {
        Document document = new();
        Body body = document.AddSection().Body;
        foreach ((byte[] bytes, string type) in images)
        {
            body.AddParagraph().Inlines.Add(new Picture(document, new ImageData(bytes, type), 40, 30) { Description = "alt" });
        }

        return document;
    }

    [Fact]
    public void FilesGoBesideTheMarkdownInReadingOrderOnceEach()
    {
        byte[] png = Encoded("png", new Rgba8(255, 0, 0, 255));
        byte[] jpeg = Encoded("jpg", new Rgba8(0, 255, 0, 255));
        byte[] bmp = Encoded("bmp", new Rgba8(0, 0, 255, 255));
        Document document = WithPictures((png, "image/png"), (jpeg, "image/jpeg"), (png, "image/png"), (bmp, "image/bmp"), (Emf(), "image/x-emf"));
        string path = Path.Combine(folder, "report.md");

        IReadOnlyList<ConversionWarning> warnings = MarkdownFormat.Save(document, path);
        string markdown = File.ReadAllText(path);

        Assert.Empty(warnings);
        Assert.Equal(
            ["![alt](report.images/image-001.png)", "![alt](report.images/image-002.jpg)", "![alt](report.images/image-001.png)", "![alt](report.images/image-003.png)", "![alt](report.images/image-004.png)"],
            markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal(png, File.ReadAllBytes(Path.Combine(folder, "report.images", "image-001.png")));
        Assert.Equal(jpeg, File.ReadAllBytes(Path.Combine(folder, "report.images", "image-002.jpg")));
        Assert.Equal(4, Directory.GetFiles(Path.Combine(folder, "report.images")).Length);
    }

    [Fact]
    public void TheResolverStoresEachImageAndNamesItsAddress()
    {
        Document document = WithPictures((Encoded("bmp", new Rgba8(1, 2, 3, 255)), "image/bmp"), (Emf(), "image/x-emf"));
        List<MarkdownImage> seen = [];

        string markdown = MarkdownFormat.ToMarkdown(document, new MarkdownSaveOptions
        {
            VectorFiguresAsSvg = true,
            ImageResolver = image =>
            {
                seen.Add(image);
                return "https://cdn.example/" + image.SuggestedName;
            },
        });

        Assert.Equal(["image-001.png", "image-002.svg"], seen.Select(i => i.SuggestedName));
        Assert.Equal(["image/png", "image/svg+xml"], seen.Select(i => i.ContentType));
        Assert.Contains("![alt](https://cdn.example/image-002.svg)", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void DataUrisPlaceholdersAndOmitting()
    {
        Document document = WithPictures((Encoded("png", new Rgba8(9, 9, 9, 255)), "image/png"));

        string embedded = MarkdownFormat.ToMarkdown(document, new MarkdownSaveOptions { Images = MarkdownImageMode.DataUris });
        string placeholder = MarkdownFormat.ToMarkdown(document, new MarkdownSaveOptions { Images = MarkdownImageMode.Placeholders });
        string omitted = MarkdownFormat.ToMarkdown(document, out IReadOnlyList<ConversionWarning> omitWarnings, new MarkdownSaveOptions { Images = MarkdownImageMode.Omit });
        string nowhere = MarkdownFormat.ToMarkdown(document, out IReadOnlyList<ConversionWarning> nowhereWarnings);

        Assert.StartsWith("![alt](data:image/png;base64,", embedded, StringComparison.Ordinal);
        Assert.Equal("<!-- image -->\n", placeholder);
        Assert.Equal("", omitted.Trim());
        Assert.Contains(omitWarnings, w => w.Kind == ConversionWarningKind.PictureDropped);
        Assert.Equal("", nowhere.Trim());
        Assert.Contains(nowhereWarnings, w => w.Kind == ConversionWarningKind.PictureDropped);
    }

    [Fact]
    public void TinyAndBackgroundPicturesAreLeftOut()
    {
        Document document = new();
        byte[] png = Encoded("png", new Rgba8(5, 5, 5, 255));
        Paragraph paragraph = document.AddSection().Body.AddParagraph("Text");
        paragraph.Inlines.Add(new Picture(document, new ImageData(png, "image/png"), 3, 3));
        paragraph.Inlines.Add(new Picture(document, new ImageData(png, "image/png"), 500, 700) { Floating = new FloatingPlacement { Wrap = WrapMode.BehindText } });

        string markdown = MarkdownFormat.ToMarkdown(document, new MarkdownSaveOptions { Images = MarkdownImageMode.DataUris });
        string kept = MarkdownFormat.ToMarkdown(document, new MarkdownSaveOptions { Images = MarkdownImageMode.DataUris, MinImageSize = 0, KeepBackgroundImages = true });

        Assert.Equal("Text\n", markdown);
        Assert.Equal(2, kept.Split("data:image/png").Length - 1);
    }

    [Fact]
    public void CaptionsAreItalicOrFigures()
    {
        Document document = WithPictures((Encoded("png", new Rgba8(7, 7, 7, 255)), "image/png"));
        Paragraph caption = document.Sections[0].Body.AddParagraph("Figure 1: A & B");
        caption.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Caption);
        MarkdownSaveOptions resolve = new() { ImageResolver = image => image.SuggestedName };

        string italic = MarkdownFormat.ToMarkdown(document, resolve);
        string figure = MarkdownFormat.ToMarkdown(document, new MarkdownSaveOptions { ImageResolver = image => image.SuggestedName, CaptionsAsFigures = true });

        Assert.Equal("![alt](image-001.png)\n\n_Figure 1: A & B_\n", italic);
        Assert.Equal("<figure>\n<img src=\"image-001.png\" alt=\"alt\">\n<figcaption>Figure 1: A &amp; B</figcaption>\n</figure>\n", figure);
    }
}
