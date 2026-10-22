using System.CommandLine;
using System.Globalization;

using Egami;
using Egami.Pixels;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary><c>egap render</c>: pages to PNG, JPEG, WebP or TIFF at a resolution, one file per page.</summary>
internal static class RenderCommand
{
    /// <summary>The image formats render writes, by extension.</summary>
    private static readonly string[] Formats = ["png", "jpg", "webp", "tif"];

    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new(inPlace: false);
        PageOption pages = new("The pages to render");
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Option<double> dpi = new("--dpi") { Description = "Dots per inch; 150 by default.", HelpName = "dpi", DefaultValueFactory = _ => 150 };
        Option<string?> fit = new("--fit") { Description = "Scale each page to fit a box in pixels instead of --dpi: W, xH or WxH.", HelpName = "size" };
        Option<string?> format = new("--format", "-f") { Description = $"The image format when -o does not say: {string.Join(", ", Formats)}.", HelpName = "format" };
        Option<bool> transparent = new("--transparent") { Description = "Leave the page background transparent instead of white (PNG, WebP, TIFF)." };
        Option<string?> box = new("--box") { Description = "The page box to render: crop (the default), media, bleed, trim or art.", HelpName = "box" };
        Command command = new("render", "Render PDF pages to images, one file per page, named by a template such as out/{name}-{page}.png.");
        inputs.AddTo(command);
        output.AddTo(command);
        command.Options.Add(pages.Pages);
        command.Options.Add(dpi);
        command.Options.Add(fit);
        command.Options.Add(format);
        command.Options.Add(transparent);
        command.Options.Add(box);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate template = output.Template(session, result, files)!;
            if (template.IsStdout)
            {
                throw new UsageException("render writes one file per page; give a folder or a template such as out/{name}-{page}.png.");
            }

            string extension = (result.GetValue(format) ?? OutputOptions.ExtensionOf(template))?.TrimStart('.').ToLowerInvariant() switch
            {
                "jpeg" => "jpg",
                "tiff" => "tif",
                null => throw new UsageException($"Give --format ({string.Join(", ", Formats)}) when -o does not end in one."),
                string name when Formats.Contains(name) => name,
                string other => throw new UsageException($"'{other}' is not a format render writes; it writes {string.Join(", ", Formats)}."),
            };
            PdfRenderOptions options = Options(result.GetValue(dpi), result.GetValue(fit), result.GetValue(transparent), result.GetValue(box));
            PdfPageRange? range = pages.Range(result);
            string? secret = password.Read(session, result);
            return Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                List<string> written = [];
                foreach (int index in PageOption.Indices(document, range))
                {
                    session.Cancellation.ThrowIfCancellationRequested();
                    string path = PagePath(session, template, file, extension, index + 1);
                    using Raster<Rgba8> image = document.Pages[index].Render(options with { OnWarning = warning => session.Warn($"{file.Display} page {index + 1}: {warning.Message}") }, session.Cancellation);
                    Output.Write(session, path, stream => image.Save(stream, extension));
                    written.Add(path);
                }

                return written;
            });
        });
        return command;
    }

    /// <summary>The render options the flags describe.</summary>
    internal static PdfRenderOptions Options(double dpi, string? fit, bool transparent, string? box)
    {
        if (dpi is <= 0 or > 4800)
        {
            throw new UsageException("--dpi must be above 0 and at most 4800.");
        }

        PdfRenderOptions options = PdfRenderOptions.Default with
        {
            Dpi = dpi,
            Background = transparent ? new RgbaF32(0, 0, 0, 0) : PdfRenderOptions.Default.Background,
            Box = box switch
            {
                null or "crop" => PdfPageBox.CropBox,
                "media" => PdfPageBox.MediaBox,
                "bleed" => PdfPageBox.BleedBox,
                "trim" => PdfPageBox.TrimBox,
                "art" => PdfPageBox.ArtBox,
                _ => throw new UsageException($"--box '{box}' is not crop, media, bleed, trim or art."),
            },
        };
        if (fit is null)
        {
            return options;
        }

        int x = fit.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        string width = x < 0 ? fit : fit[..x], height = x < 0 ? string.Empty : fit[(x + 1)..];
        int? Side(string text) => text.Length == 0 ? null
            : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int pixels) && pixels > 0 ? pixels
            : throw new UsageException($"--fit '{fit}' is not W, xH or WxH in pixels, such as 1024 or 800x600.");
        (int? w, int? h) = (Side(width), Side(height));
        return w is null && h is null ? throw new UsageException($"--fit '{fit}' gives no size.") : options with { FitWidth = w, FitHeight = h };
    }

    /// <summary>
    /// The path for one page: a folder gets <c>{name}-{page}</c>; a template without <c>{page}</c> gets
    /// <c>-{page}</c> before its extension, as <see cref="PdfDocument.RenderPagesToFiles"/> does.
    /// </summary>
    private static string PagePath(EgapSession session, OutputTemplate template, InputFile file, string extension, int page)
    {
        string text = template.Text;
        if (text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text))))
        {
            text = Path.Combine(text, "{name}-{page}.{format}");
        }
        else if (!text.Contains("{page", StringComparison.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(text);
            text = text[..^ext.Length] + "-{page}" + (ext.Length > 0 ? ext : ".{format}");
        }

        return OutputOptions.PathFor(session, OutputTemplate.Parse(text, "page"), file, extension, page);
    }
}
