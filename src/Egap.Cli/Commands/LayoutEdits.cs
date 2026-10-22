using System.CommandLine;
using System.Globalization;

using Egami;
using Egami.Barcodes;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary><c>egap optimize</c> and the <c>optimize</c> step: a preset (lossless, print, ebook, screen) with its options as flags.</summary>
internal sealed class OptimizeEdit : Edit
{
    private readonly Option<string?> _preset = new("--preset") { Description = "lossless (the default), print, ebook or screen.", HelpName = "preset" };
    private readonly Option<double?> _resolution = new("--image-resolution") { Description = "Downsample images above this many pixels per inch.", HelpName = "ppi" };
    private readonly Option<double?> _threshold = new("--downsample-threshold") { Description = "Downsample only images more than this many times the target resolution, such as 1.5.", HelpName = "factor" };
    private readonly Option<int?> _quality = new("--jpeg-quality") { Description = "JPEG quality for recompressed images, 1 to 100.", HelpName = "quality" };
    private readonly Option<bool?> _lossy = new("--lossy-images") { Description = "Allow lossy recompression of images (true or false)." };
    private readonly Option<bool?> _images = new("--optimize-images") { Description = "Recompress and downsample images (true or false)." };
    private readonly Option<bool?> _fonts = new("--subset-fonts") { Description = "Keep only the glyphs used of embedded fonts (true or false)." };
    private readonly Option<bool?> _duplicates = new("--merge-duplicates") { Description = "Store identical images, fonts and other streams once (true or false)." };
    private readonly Option<bool?> _thumbnails = new("--remove-thumbnails") { Description = "Remove page thumbnails (true or false)." };
    private readonly Option<bool?> _private = new("--remove-private-data") { Description = "Remove private application data (true or false)." };
    private readonly Option<bool?> _empty = new("--remove-empty-content") { Description = "Remove content streams that draw nothing (true or false)." };

    /// <inheritdoc/>
    public override string Name => "optimize";

    /// <inheritdoc/>
    public override string Description => "Make PDFs smaller: a preset (lossless, print, ebook, screen) and its settings, such as --image-resolution 150.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_preset, _resolution, _threshold, _quality, _lossy, _images, _fonts, _duplicates, _thumbnails, _private, _empty])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        PdfOptimizeOptions options = result.GetValue(_preset) switch
        {
            null or "lossless" => PdfOptimizeOptions.Lossless,
            "print" => PdfOptimizeOptions.Print,
            "ebook" => PdfOptimizeOptions.Ebook,
            "screen" => PdfOptimizeOptions.Screen,
            string other => throw new UsageException($"--preset '{other}' is not lossless, print, ebook or screen."),
        };
        if (result.GetValue(_quality) is int quality && quality is < 1 or > 100)
        {
            throw new UsageException("--jpeg-quality runs from 1 to 100.");
        }

        options = options with
        {
            ImageResolution = result.GetValue(_resolution) ?? options.ImageResolution,
            DownsampleThreshold = result.GetValue(_threshold) ?? options.DownsampleThreshold,
            JpegQuality = result.GetValue(_quality) ?? options.JpegQuality,
            LossyImages = result.GetValue(_lossy) ?? options.LossyImages,
            OptimizeImages = result.GetValue(_images) ?? options.OptimizeImages,
            SubsetFonts = result.GetValue(_fonts) ?? options.SubsetFonts,
            MergeDuplicates = result.GetValue(_duplicates) ?? options.MergeDuplicates,
            RemoveThumbnails = result.GetValue(_thumbnails) ?? options.RemoveThumbnails,
            RemovePrivateData = result.GetValue(_private) ?? options.RemovePrivateData,
            RemoveEmptyContent = result.GetValue(_empty) ?? options.RemoveEmptyContent,
        };
        return (document, context) =>
        {
            PdfOptimizeReport report = document.Optimize(options with { AllowInvalidatingSignatures = context.AllowInvalidatingSignatures }, session.Cancellation);
            session.Info($"{context.File.Display}: {report.BytesBefore.ToString(CultureInfo.InvariantCulture)} to {report.BytesAfter.ToString(CultureInfo.InvariantCulture)} bytes, {report.Images.Count} images recompressed, {report.Savings.FontsSubset} fonts subset");
            foreach (string skipped in report.Skipped)
            {
                session.Info($"{context.File.Display}: {skipped}");
            }

            return document;
        };
    }
}

/// <summary><c>egap impose</c> and the <c>impose</c> step: n-up, booklet, poster, grid or fit to a size, as a new document.</summary>
internal sealed class ImposeEdit : Edit
{
    private readonly Option<string?> _nup = new("--nup") { Description = "Several pages on each sheet, CxR such as 2x1 or 2x2.", HelpName = "columns x rows" };
    private readonly Option<bool> _booklet = new("--booklet") { Description = "Fold-and-staple booklet order, two pages on each side of a sheet." };
    private readonly Option<string?> _poster = new("--poster") { Description = "Spread each page over tiles of this size, such as A4.", HelpName = "size" };
    private readonly Option<int?> _grid = new("--grid") { Description = "Pages in a grid of this many columns on one tall sheet.", HelpName = "columns" };
    private readonly Option<string?> _fit = new("--fit") { Description = "Scale every page to fit this size, such as A4 or Letter.", HelpName = "size" };
    private readonly Option<string?> _sheet = new("--sheet") { Description = "The sheet size for --nup and --booklet, such as A3; by default the pages' size.", HelpName = "size" };
    private readonly Option<string?> _margin = new("--margin") { Description = "The margin around the sheet, such as 10mm.", HelpName = "length" };
    private readonly Option<string?> _gutter = new("--gutter") { Description = "The space between pages, such as 5mm.", HelpName = "length" };
    private readonly Option<bool> _border = new("--border") { Description = "Draw a thin border around each page." };
    private readonly Option<string?> _order = new("--order") { Description = "The order on a sheet: across (the default), across-rtl, down or down-rtl.", HelpName = "order" };
    private readonly Option<bool> _autoRotate = new("--auto-rotate") { Description = "Turn pages to make the best use of the sheet." };
    private readonly Option<string?> _binding = new("--binding") { Description = "The booklet's binding edge: left (the default) or right.", HelpName = "edge" };
    private readonly Option<int?> _signature = new("--sheets-per-signature") { Description = "Sheets folded together per booklet signature; 0 for one signature.", HelpName = "count" };
    private readonly Option<string?> _overlap = new("--overlap") { Description = "How far poster tiles overlap, such as 10mm.", HelpName = "length" };
    private readonly Option<double?> _scale = new("--scale") { Description = "How much to enlarge a poster, such as 2.", HelpName = "factor" };
    private readonly Option<bool> _cutMarks = new("--cut-marks") { Description = "Draw cut marks on poster tiles." };
    private readonly Option<bool> _labels = new("--labels") { Description = "Label each poster tile with its row and column." };

    /// <inheritdoc/>
    public override string Name => "impose";

    /// <inheritdoc/>
    public override string Description => "Lay pages out on sheets: --nup 2x1, --booklet, --poster A4, --grid 3 or --fit A4.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_nup, _booklet, _poster, _grid, _fit, _sheet, _margin, _gutter, _border, _order, _autoRotate, _binding, _signature, _overlap, _scale, _cutMarks, _labels])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        int modes = (result.GetValue(_nup) is null ? 0 : 1) + (result.GetValue(_booklet) ? 1 : 0) + (result.GetValue(_poster) is null ? 0 : 1)
            + (result.GetValue(_grid) is null ? 0 : 1) + (result.GetValue(_fit) is null ? 0 : 1);
        if (modes != 1)
        {
            throw new UsageException("Give one of --nup, --booklet, --poster, --grid and --fit.");
        }

        double? margin = result.GetValue(_margin) is { } m ? Values.Points(m, "--margin") : null;
        double? gutter = result.GetValue(_gutter) is { } g ? Values.Points(g, "--gutter") : null;
        PdfPageSize? sheet = result.GetValue(_sheet) is { } s ? Values.Size(s, "--sheet") : null;
        Func<PdfDocument, PdfDocument> impose;
        if (result.GetValue(_nup) is { } nup)
        {
            (int columns, int rows) = Grid(nup);
            NUpOptions options = new NUpOptions() with
            {
                Columns = columns,
                Rows = rows,
                Border = result.GetValue(_border),
                AutoRotate = result.GetValue(_autoRotate),
                Order = result.GetValue(_order) switch
                {
                    null or "across" => NUpOrder.LeftToRightThenDown,
                    "across-rtl" => NUpOrder.RightToLeftThenDown,
                    "down" => NUpOrder.DownThenLeftToRight,
                    "down-rtl" => NUpOrder.DownThenRightToLeft,
                    string other => throw new UsageException($"--order '{other}' is not across, across-rtl, down or down-rtl."),
                },
            };
            options = options with { Margin = margin ?? options.Margin, Gutter = gutter ?? options.Gutter, Sheet = sheet ?? options.Sheet };
            impose = source => PdfImpose.NUp(source, options);
        }
        else if (result.GetValue(_booklet))
        {
            BookletOptions options = new BookletOptions() with
            {
                Binding = result.GetValue(_binding) switch
                {
                    null or "left" => BookletBinding.Left,
                    "right" => BookletBinding.Right,
                    string other => throw new UsageException($"--binding '{other}' is not left or right."),
                },
            };
            options = options with { Margin = margin ?? options.Margin, Sheet = sheet ?? options.Sheet, SheetsPerSignature = result.GetValue(_signature) ?? options.SheetsPerSignature };
            impose = source => PdfImpose.Booklet(source, options);
        }
        else if (result.GetValue(_poster) is { } tile)
        {
            PosterOptions options = new PosterOptions() with { Tile = Values.Size(tile, "--poster"), CutMarks = result.GetValue(_cutMarks), Labels = result.GetValue(_labels) };
            options = options with
            {
                Overlap = result.GetValue(_overlap) is { } o ? Values.Points(o, "--overlap") : options.Overlap,
                Scale = result.GetValue(_scale) ?? options.Scale,
            };
            impose = source => PdfImpose.Poster(source, options);
        }
        else if (result.GetValue(_grid) is int columns)
        {
            GridOptions options = new GridOptions() with { Columns = columns > 0 ? columns : throw new UsageException("--grid takes 1 or more columns."), Border = result.GetValue(_border) };
            options = options with { Margin = margin ?? options.Margin, Gutter = gutter ?? options.Gutter };
            impose = source => PdfImpose.Grid(source, options);
        }
        else
        {
            PdfPageSize size = Values.Size(result.GetValue(_fit)!, "--fit");
            impose = source => PdfImpose.FitToSize(source, size);
        }

        return (document, context) =>
        {
            // The new document draws the old one's pages, so the old one stays open until it is written.
            context.KeepOpen(document);
            return impose(document);
        };
    }

    private static (int Columns, int Rows) Grid(string text)
    {
        int x = text.IndexOf('x', StringComparison.OrdinalIgnoreCase);
        return x > 0 && int.TryParse(text[..x], NumberStyles.None, CultureInfo.InvariantCulture, out int columns)
            && int.TryParse(text[(x + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int rows) && columns > 0 && rows > 0
            ? (columns, rows)
            : throw new UsageException($"--nup '{text}' is not columns x rows, such as 2x1 or 2x2.");
    }
}

/// <summary>
/// <c>egap stamp</c> and the <c>stamp</c> step: text with placeholders, page numbers, a watermark, an image, a barcode or
/// Bates numbers on chosen pages; or <c>--remove</c> to take earlier egap stamps off.
/// </summary>
internal sealed class StampEdit : Edit
{
    private readonly Option<string?> _text = new("--text") { Description = "Text with {page}, {total-pages}, {label}, {date}, {time}, {title} and {file}, such as \"Page {page} of {total-pages}\".", HelpName = "template" };
    private readonly Option<bool> _pageNumbers = new("--page-numbers") { Description = "\"Page {page} of {total-pages}\" at the bottom centre." };
    private readonly Option<string?> _watermark = new("--watermark") { Description = "Large faint text across the middle of each page, such as DRAFT.", HelpName = "text" };
    private readonly Option<string?> _image = new("--image") { Description = "An image file (PNG, JPEG, SVG and the other formats Egami reads); give --width.", HelpName = "file" };
    private readonly Option<string?> _barcode = new("--barcode") { Description = "A barcode, format:data such as qr:https://example.com or code128:A-123; give --width.", HelpName = "format:data" };
    private readonly Option<string?> _bates = new("--bates") { Description = "Bates numbers with this prefix, such as ACME-; numbering goes on across the files of a batch.", HelpName = "prefix" };
    private readonly Option<int?> _batesDigits = new("--bates-digits") { Description = "Digits in a Bates number; 6 by default.", HelpName = "count" };
    private readonly Option<long?> _batesStart = new("--bates-start") { Description = "The first Bates number; 1 by default.", HelpName = "number" };
    private readonly Option<bool> _remove = new("--remove") { Description = "Remove the stamps egap and Egap added before." };
    private readonly Option<string?> _anchor = new("--anchor") { Description = "Where on the page: top-left, top-center, top-right, middle-left, center, middle-right, bottom-left, bottom-center or bottom-right.", HelpName = "place" };
    private readonly Option<string?> _offset = new("--offset") { Description = "Move from the anchor, x right and y up, such as 10mm,15mm.", HelpName = "x,y" };
    private readonly Option<string?> _width = new("--width") { Description = "The image's or barcode's width, such as 40mm.", HelpName = "length" };
    private readonly Option<string?> _height = new("--height") { Description = "The image's or barcode's height; by default from its shape.", HelpName = "length" };
    private readonly Option<double?> _fontSize = new("--font-size") { Description = "Text size in points.", HelpName = "points" };
    private readonly Option<string?> _color = new("--color") { Description = "Text color, such as #c00 or #336699.", HelpName = "color" };
    private readonly Option<double?> _opacity = new("--opacity") { Description = "From 0 (invisible) to 1 (solid).", HelpName = "0-1" };
    private readonly Option<double?> _rotation = new("--rotation") { Description = "Degrees, counterclockwise.", HelpName = "degrees" };
    private readonly PageOption _pages = new("The pages to stamp");
    private readonly Option<bool> _under = new("--under") { Description = "Put the stamp under the page's content instead of over it." };
    private readonly Option<string?> _layer = new("--layer") { Description = "Put the stamp on a layer of this name, which viewers can hide.", HelpName = "name" };

    /// <inheritdoc/>
    public override string Name => "stamp";

    /// <inheritdoc/>
    public override string Description => "Stamp pages with text, page numbers, a watermark, an image, a barcode or Bates numbers; or --remove earlier stamps.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        foreach (Option option in (Option[])[_text, _pageNumbers, _watermark, _image, _barcode, _bates, _batesDigits, _batesStart, _remove, _anchor, _offset, _width, _height, _fontSize, _color, _opacity, _rotation, _pages.Pages, _under, _layer])
        {
            command.Options.Add(option);
        }
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        int kinds = (result.GetValue(_text) is null ? 0 : 1) + (result.GetValue(_pageNumbers) ? 1 : 0) + (result.GetValue(_watermark) is null ? 0 : 1)
            + (result.GetValue(_image) is null ? 0 : 1) + (result.GetValue(_barcode) is null ? 0 : 1) + (result.GetValue(_bates) is null ? 0 : 1) + (result.GetValue(_remove) ? 1 : 0);
        if (kinds != 1)
        {
            throw new UsageException("Give one of --text, --page-numbers, --watermark, --image, --barcode, --bates and --remove.");
        }

        if (result.GetValue(_remove))
        {
            return (document, context) =>
            {
                session.Info($"{context.File.Display}: removed {document.RemoveStamps()} stamps");
                return document;
            };
        }

        PdfStamp stamp = Create(session, result);
        if (result.GetValue(_anchor) is { } anchor)
        {
            stamp = stamp with { Anchor = Anchor(anchor) };
        }

        if (result.GetValue(_offset) is { } offset)
        {
            (double x, double y) = Values.Pair(offset, "--offset");
            stamp = stamp with { Offset = new PdfPoint(x, y) };
        }

        stamp = stamp with
        {
            FontSize = result.GetValue(_fontSize) ?? stamp.FontSize,
            Color = result.GetValue(_color) is { } color ? Values.Color(color, "--color") : stamp.Color,
            Opacity = result.GetValue(_opacity) is double opacity ? opacity is >= 0 and <= 1 ? opacity : throw new UsageException("--opacity runs from 0 to 1.") : stamp.Opacity,
            Rotation = result.GetValue(_rotation) ?? stamp.Rotation,
            PageRange = _pages.Range(result) ?? stamp.PageRange,
            Under = result.GetValue(_under) || stamp.Under,
            Layer = result.GetValue(_layer) ?? stamp.Layer,
        };
        return (document, context) =>
        {
            PdfStampResult done = document.Stamp(stamp);
            session.Info($"{context.File.Display}: stamped {done.Pages} pages{(done.FirstBates is null ? string.Empty : $", Bates {done.FirstBates} to {done.LastBates}")}");
            return document;
        };
    }

    private PdfStamp Create(EgapSession session, ParseResult result)
    {
        if (result.GetValue(_text) is { } text)
        {
            return PdfStamp.Text(text);
        }

        if (result.GetValue(_pageNumbers))
        {
            return PdfStamp.Text("Page {page} of {total-pages}");
        }

        if (result.GetValue(_watermark) is { } watermark)
        {
            return PdfStamp.Watermark(watermark);
        }

        if (result.GetValue(_bates) is { } prefix)
        {
            // One sequence for the whole command, so the numbers run on from one file of a batch to the next.
            return PdfStamp.Bates(new PdfBatesSequence(prefix, result.GetValue(_batesDigits) ?? 6, result.GetValue(_batesStart) ?? 1));
        }

        double width = result.GetValue(_width) is { } w ? Values.Points(w, "--width") : throw new UsageException("An image or barcode stamp needs --width, such as 40mm.");
        if (result.GetValue(_image) is { } file)
        {
            Raster image = Raster.Load(session.ResolvePath(file));
            double height = result.GetValue(_height) is { } h ? Values.Points(h, "--height") : width * image.Height / image.Width;
            return PdfStamp.Image(image, width, height);
        }

        string spec = result.GetValue(_barcode)!;
        int colon = spec.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !Enum.TryParse(spec[..colon].Replace("-", string.Empty, StringComparison.Ordinal), ignoreCase: true, out BarcodeFormat format) || format is BarcodeFormat.None or BarcodeFormat.Linear or BarcodeFormat.Matrix or BarcodeFormat.All)
        {
            throw new UsageException($"--barcode '{spec}' is not format:data with a format of qr, data-matrix, pdf417, aztec, code128, code39, itf, ean13, ean8, upc-a or upc-e.");
        }

        Barcode barcode = Barcode.Create(format, spec[(colon + 1)..]);
        double barHeight = result.GetValue(_height) is { } bh ? Values.Points(bh, "--height") : format is BarcodeFormat.Qr or BarcodeFormat.DataMatrix or BarcodeFormat.Aztec ? width : width / 3;
        return PdfStamp.Barcode(barcode, width, barHeight);
    }

    private static StampAnchor Anchor(string text)
    {
        return text switch
        {
            "top-left" => StampAnchor.TopLeft,
            "top-center" => StampAnchor.TopCenter,
            "top-right" => StampAnchor.TopRight,
            "middle-left" => StampAnchor.MiddleLeft,
            "center" => StampAnchor.Center,
            "middle-right" => StampAnchor.MiddleRight,
            "bottom-left" => StampAnchor.BottomLeft,
            "bottom-center" => StampAnchor.BottomCenter,
            "bottom-right" => StampAnchor.BottomRight,
            _ => throw new UsageException($"--anchor '{text}' is not top-left, top-center, top-right, middle-left, center, middle-right, bottom-left, bottom-center or bottom-right."),
        };
    }
}
