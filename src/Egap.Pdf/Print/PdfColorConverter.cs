using System.Globalization;

using Egami.Colors;

namespace Egap.Pdf;

/// <summary>What colour conversion does with spot colours (Separation and DeviceN).</summary>
public enum PdfSpotConversion
{
    /// <summary>Spot colours keep their plates; their alternate colours are converted, so they look right where a plate is missing.</summary>
    Keep,

    /// <summary>Every spot colour is converted into the target through its alternate colour.</summary>
    Convert,

    /// <summary>Spot colours are converted, except those named in <see cref="PdfColorConversionOptions.KeepSpots"/>.</summary>
    ConvertAllButNamed,
}

/// <summary>How colour conversion treats black and gray text and thin rules.</summary>
public enum PdfBlackText
{
    /// <summary>
    /// Neutral text and thin rules go on black ink alone (0/0/0/100 for black), so small type does not misregister on press.
    /// Images and other colours are converted colorimetrically.
    /// </summary>
    PureBlack,

    /// <summary>Everything is converted colorimetrically, so RGB black becomes a rich black.</summary>
    Colorimetric,
}

/// <summary>How <see cref="PdfColorConverter.Convert(PdfDocument, PdfColorConversionOptions?, CancellationToken)"/> converts a document.</summary>
public sealed record PdfColorConversionOptions
{
    /// <summary>The default options: into the document's CMYK output intent (FOGRA39 without one), relative colorimetric, spots kept, pure black text.</summary>
    public static PdfColorConversionOptions Default { get; } = new();

    /// <summary>
    /// Gets the profile colours convert into: a CMYK press profile, or a gray or RGB one. Null takes the document's CMYK output
    /// intent, or Egami's bundled FOGRA39 when there is none. Defaults to null.
    /// </summary>
    /// <exception cref="ArgumentException">The profile is not a gray, RGB or CMYK profile.</exception>
    public ColorProfile? Target
    {
        get;
        init => field = value is null or { Model: ColorModel.Gray or ColorModel.Rgb or ColorModel.Cmyk } ? value
            : throw new ArgumentException("Colour conversion targets a gray, RGB or CMYK profile.", nameof(value));
    }

    /// <summary>Gets the rendering intent of every conversion. Defaults to relative colorimetric.</summary>
    public RenderingIntent Intent { get; init; } = RenderingIntent.RelativeColorimetric;

    /// <summary>Gets whether conversions use black point compensation. Defaults to true.</summary>
    public bool BlackPointCompensation { get; init; } = true;

    /// <summary>Gets what happens to spot colours. Defaults to <see cref="PdfSpotConversion.Keep"/>.</summary>
    public PdfSpotConversion Spots { get; init; }

    /// <summary>Gets the spot inks kept under <see cref="PdfSpotConversion.ConvertAllButNamed"/>. Defaults to none.</summary>
    public IReadOnlyCollection<string> KeepSpots { get; init; } = [];

    /// <summary>
    /// Gets whether CMYK colours are converted too, from <see cref="SourceCmyk"/> into the target, as when moving a coated job
    /// to newspaper. Off, DeviceCMYK keeps its numbers. Defaults to false.
    /// </summary>
    public bool RetargetCmyk { get; init; }

    /// <summary>
    /// Gets the profile DeviceCMYK is in when <see cref="RetargetCmyk"/> converts it. Null takes the document's CMYK output
    /// intent, or FOGRA39. Defaults to null.
    /// </summary>
    /// <exception cref="ArgumentException">The profile is not a CMYK profile.</exception>
    public ColorProfile? SourceCmyk
    {
        get;
        init => field = value is null or { Model: ColorModel.Cmyk } ? value : throw new ArgumentException("DeviceCMYK needs a CMYK profile.", nameof(value));
    }

    /// <summary>Gets how black and gray text and thin rules convert. Defaults to <see cref="PdfBlackText.PureBlack"/>.</summary>
    public PdfBlackText BlackText { get; init; }

    /// <summary>Gets the widest stroke, in points, that counts as a thin rule for <see cref="BlackText"/>. Defaults to 0.5.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public double ThinRuleWidth
    {
        get;
        init => field = value >= 0 && double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The width must be zero or more.");
    } = 0.5;
}

/// <summary>What a colour conversion changed.</summary>
public sealed class PdfColorConversionResult
{
    internal PdfColorConversionResult(ColorProfile target, Cleanup.ColorRewriter run)
    {
        Target = target;
        Colors = run.Changed.Colors;
        Images = run.Changed.Images;
        Shadings = run.Changed.Shadings;
        Alternates = run.Changed.Alternates;
        PureBlack = run.Changed.PureBlack;
        Annotations = run.Changed.Annotations;
        Warnings = run.Warnings;
    }

    /// <summary>The profile colours were converted into.</summary>
    public ColorProfile Target { get; }

    /// <summary>The colour operators rewritten in content.</summary>
    public int Colors { get; }

    /// <summary>The images and inline images converted, Indexed palettes included.</summary>
    public int Images { get; }

    /// <summary>The shadings converted.</summary>
    public int Shadings { get; }

    /// <summary>The spot colour spaces kept whose alternate colours were converted.</summary>
    public int Alternates { get; }

    /// <summary>The times neutral text or a thin rule was put on black ink alone.</summary>
    public int PureBlack { get; }

    /// <summary>The annotation colours converted.</summary>
    public int Annotations { get; }

    /// <summary>What could not be converted and kept its colour.</summary>
    public IReadOnlyList<PdfWarning> Warnings { get; }

    /// <summary>Whether anything was converted.</summary>
    public bool Changed => Colors + Images + Shadings + Alternates + PureBlack + Annotations > 0;

    /// <summary>A line saying what changed.</summary>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture,
            $"into {Target.Name}: {Colors} colours, {Images} images, {Shadings} shadings, {Alternates} spot alternates, {PureBlack} pure black, {Annotations} annotation colours{(Warnings.Count > 0 ? $", {Warnings.Count} warnings" : "")}");
    }
}

/// <summary>
/// Converts a document's colours into a print profile (Phase 8 design Q6 to Q8): RGB, calibrated, Lab and ICC-based colours
/// go to the target, CMYK and spot colours stay unless the options say otherwise, and vectors stay vectors. Colour
/// operators, images, shadings, patterns, soft masks, Type 3 glyphs, annotation appearances and spot alternates are all
/// converted. Ink coverage is not limited: the target profile sets it, and preflight reports anything over a print
/// profile's limit.
/// </summary>
/// <example>
/// <code>
/// using PdfDocument pdf = PdfDocument.Open("rgb.pdf");
/// PdfColorConversionResult result = PdfColorConverter.Convert(pdf, new PdfColorConversionOptions { Target = ColorProfile.Fogra39 });
/// pdf.Save("cmyk.pdf");
/// </code>
/// </example>
public static class PdfColorConverter
{
    /// <summary>Converts the document's colours in place.</summary>
    /// <param name="document">The document to convert.</param>
    /// <param name="options">The target, intent, spot, CMYK and black handling; <see cref="PdfColorConversionOptions.Default"/> when null.</param>
    /// <param name="cancellationToken">Stops the work between pages; what was converted by then stays converted and valid.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    public static PdfColorConversionResult Convert(PdfDocument document, PdfColorConversionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return Convert(document, options, forceDevice: false, cancellationToken);
    }

    /// <summary>The conversion; with <paramref name="forceDevice"/>, ICC-based CMYK in the target's own profile becomes DeviceCMYK too (PDF/X-1a).</summary>
    internal static PdfColorConversionResult Convert(PdfDocument document, PdfColorConversionOptions? options, bool forceDevice, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= PdfColorConversionOptions.Default;
        ColorProfile intent = PdfPage.PressProfile(document);
        ColorProfile target = options.Target ?? intent;
        Cleanup.IccTarget icc = new(target, options.Intent, options.BlackPointCompensation)
        {
            Spots = options.Spots,
            KeptSpots = new HashSet<string>(options.KeepSpots, StringComparer.Ordinal),
            RetargetCmyk = options.RetargetCmyk,
            SourceCmyk = options.SourceCmyk ?? intent,
            BlackText = options.BlackText,
            ThinRuleWidth = options.ThinRuleWidth,
            ForceDevice = forceDevice,
        };
        try
        {
            return new PdfColorConversionResult(target, Cleanup.ColorRewriter.Run(document, icc, cancellationToken));
        }
        finally
        {
            document.ResetRenderCache();
        }
    }
}
