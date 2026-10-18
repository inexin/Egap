using System.Security.Cryptography;

using Egami;
using Egami.Drawing;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egap.Documents.Markdown;

/// <summary>
/// The Markdown writer's pictures (Phase 6 design Q20): only browser-ready formats leave it, each image is written once
/// however often it appears, and names follow reading order. Plain JPEGs, PNG, GIF, WebP and SVG keep their bytes; Word's
/// EMF and WMF figures become PNG (or SVG); every other image is decoded through Egami and written as PNG.
/// </summary>
internal sealed class MarkdownImages(MarkdownSaveOptions options, string? folder, string? folderUrl)
{
    /// <summary>What reading-order names were given, by content hash, so a repeated image is written once.</summary>
    private readonly Dictionary<string, string> urls = [];

    private int count;

    /// <summary>
    /// The address to write for a picture without one of its own, or null when it is left out (the reason added to
    /// <paramref name="warn"/>).
    /// </summary>
    public string? UrlFor(Picture picture, Action<ConversionWarningKind, string> warn)
    {
        MarkdownImageMode mode = options.EmbedImages ? MarkdownImageMode.DataUris : options.Images;
        if (mode == MarkdownImageMode.Omit)
        {
            warn(ConversionWarningKind.PictureDropped, "pictures, left out as the options ask");
            return null;
        }

        if (picture.Image.Bytes.IsEmpty || Prepare(picture) is not { } image)
        {
            warn(ConversionWarningKind.PictureDropped, "pictures in a format that could not be decoded");
            return null;
        }

        if (mode == MarkdownImageMode.DataUris)
        {
            return $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Bytes)}";
        }

        string hash = Convert.ToHexString(SHA256.HashData(image.Bytes));
        if (urls.TryGetValue(hash, out string? known))
        {
            return known;
        }

        string name = $"image-{++count:D3}{image.Extension}";
        string? url;
        if (options.ImageResolver is { } resolver)
        {
            url = resolver(new MarkdownImage(image.Bytes, image.ContentType, name, picture));
        }
        else if (folder is not null)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(System.IO.Path.Combine(folder, name), image.Bytes);
            url = folderUrl + "/" + name;
        }
        else
        {
            warn(ConversionWarningKind.PictureDropped, "pictures with nowhere to go (give MarkdownSaveOptions.ImageResolver or save to a file)");
            count--;
            return null;
        }

        if (url is null)
        {
            warn(ConversionWarningKind.PictureDropped, "pictures the image resolver left out");
            return null;
        }

        urls[hash] = url;
        return url;
    }

    /// <summary>Whether a picture is skipped as a page background or for being too small to carry content.</summary>
    public bool Skips(Picture picture)
    {
        if (picture.Floating is { Wrap: WrapMode.BehindText } && !options.KeepBackgroundImages)
        {
            return true;
        }

        return picture.Width > 0 && picture.Height > 0 && (picture.Width < options.MinImageSize || picture.Height < options.MinImageSize);
    }

    /// <summary>The picture in a browser-ready format, or null when it cannot be decoded.</summary>
    private Prepared? Prepare(Picture picture)
    {
        ImageData data = picture.Image;
        byte[] bytes = data.Bytes.ToArray();
        string type = data.ContentType == "application/octet-stream" ? ImageData.GuessContentType(bytes) : data.ContentType;
        try
        {
            switch (type)
            {
                case "image/jpeg" when IsPlainJpeg(bytes):
                    return new Prepared(bytes, "image/jpeg", ".jpg");
                case "image/png":
                    return new Prepared(bytes, type, ".png");
                case "image/gif":
                    return new Prepared(bytes, type, ".gif");
                case "image/webp":
                    return new Prepared(bytes, type, ".webp");
                case "image/svg+xml":
                    return new Prepared(bytes, type, ".svg");
                case "image/x-emf" or "image/emf" or "image/x-wmf" or "image/wmf":
                    return Metafile(bytes, picture);
                default:
                    using (Raster raster = Raster.Load(bytes))
                    {
                        return Png(raster);
                    }
            }
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or ArgumentException or EgamiException)
        {
            return null;
        }
    }

    /// <summary>A Word figure played back as PNG at <see cref="MarkdownSaveOptions.FigureResolution"/>, or as SVG.</summary>
    private Prepared Metafile(byte[] bytes, Picture picture)
    {
        Egami.Metafiles.Metafile metafile = Egami.Metafiles.Metafile.Load(bytes);
        float width = (float)(picture.Width > 0 ? picture.Width : Math.Max(1, metafile.Size.X));
        float height = (float)(picture.Height > 0 ? picture.Height : Math.Max(1, metafile.Size.Y));
        if (options.VectorFiguresAsSvg)
        {
            using MemoryStream svg = new();
            using (Egami.Svg.SvgCanvas canvas = new(svg, width, height))
            {
                metafile.Draw(canvas, new RectF(0, 0, width, height));
            }

            return new Prepared(svg.ToArray(), "image/svg+xml", ".svg");
        }

        float scale = (float)(options.FigureResolution / 72);
        using Raster<Rgba8> raster = Raster.Create(Math.Max(1, (int)Math.Ceiling(width * scale)), Math.Max(1, (int)Math.Ceiling(height * scale)), new Rgba8(0, 0, 0, 0));
        using (RasterCanvas<Rgba8> canvas = new(raster))
        {
            canvas.Scale(scale, scale);
            metafile.Draw(canvas, new RectF(0, 0, width, height));
        }

        return Png(raster);
    }

    private static Prepared Png(Raster raster)
    {
        using MemoryStream png = new();
        raster.Save(png, PngFormat.Info);
        return new Prepared(png.ToArray(), "image/png", ".png");
    }

    /// <summary>
    /// Whether a JPEG is one every browser shows as it is: baseline or progressive with one (grey) or three (YCbCr) components.
    /// CMYK and YCCK JPEGs, and anything not read as a JPEG, are decoded and written as PNG instead.
    /// </summary>
    private static bool IsPlainJpeg(ReadOnlySpan<byte> bytes)
    {
        int i = 2;
        while (i + 4 <= bytes.Length && bytes[0] == 0xFF && bytes[1] == 0xD8)
        {
            if (bytes[i] != 0xFF)
            {
                return false;
            }

            byte marker = bytes[i + 1];
            int length = (bytes[i + 2] << 8) | bytes[i + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2)
            {
                return i + 9 < bytes.Length && bytes[i + 9] is 1 or 3;
            }

            if (marker is 0xD9 or 0xDA || length < 2)
            {
                return false;
            }

            i += 2 + length;
        }

        return false;
    }

    private sealed record Prepared(byte[] Bytes, string ContentType, string Extension);
}
