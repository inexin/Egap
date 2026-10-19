using System.Globalization;

using Egami;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Pdf;

/// <summary>Where on the shown page a stamp is anchored: one of nine points, the stamp's own box aligned to it.</summary>
public enum StampAnchor
{
    /// <summary>The top left corner.</summary>
    TopLeft,

    /// <summary>The middle of the top edge.</summary>
    TopCenter,

    /// <summary>The top right corner.</summary>
    TopRight,

    /// <summary>The middle of the left edge.</summary>
    MiddleLeft,

    /// <summary>The page's centre.</summary>
    Center,

    /// <summary>The middle of the right edge.</summary>
    MiddleRight,

    /// <summary>The bottom left corner.</summary>
    BottomLeft,

    /// <summary>The middle of the bottom edge.</summary>
    BottomCenter,

    /// <summary>The bottom right corner.</summary>
    BottomRight,
}

/// <summary>The kind of page furniture a stamp is marked as (ISO 32000-2 14.8.2.2, pagination artifacts).</summary>
public enum PdfStampSubtype
{
    /// <summary>A running head.</summary>
    Header,

    /// <summary>A running foot.</summary>
    Footer,

    /// <summary>A watermark.</summary>
    Watermark,
}

/// <summary>
/// A stamp: text, an image or another page drawn on every page (or the pages chosen) as removable page furniture (Phase 7
/// design 7.3, Q10 and Q11). Make one with <see cref="Text"/>, <see cref="Watermark"/>, <see cref="Bates"/>,
/// <see cref="Image(Raster, double, double)"/> or <see cref="Page"/>, then set its placement with <c>with</c>.
/// </summary>
/// <remarks>
/// Text is a template whose fields are filled per page: <c>{page}</c>, <c>{pages}</c>, <c>{label}</c> (the page label),
/// <c>{date}</c> or <c>{date:format}</c>, <c>{title}</c>, <c>{file}</c> and, for a Bates stamp, <c>{bates}</c>. Numbers and
/// dates use <see cref="Culture"/>, the invariant culture unless given. Text is drawn in Helvetica, a standard font that is
/// not embedded, unless <see cref="Font"/> names a typeface, which is embedded as a subset; a PDF/A document needs one.
/// </remarks>
public sealed record PdfStamp
{
    private PdfStamp(PdfStampKind kind)
    {
        Kind = kind;
    }

    /// <summary>Gets the template, for a text, watermark or Bates stamp.</summary>
    public string? Template { get; init; }

    /// <summary>Gets the anchor the stamp's box is aligned to on the shown page.</summary>
    public StampAnchor Anchor { get; init; } = StampAnchor.BottomCenter;

    /// <summary>Gets how far the stamp is moved from its anchor, in points: x to the right, y up.</summary>
    public PdfPoint Offset { get; init; }

    /// <summary>Gets the turn about the stamp's centre, in degrees counterclockwise.</summary>
    public double Rotation { get; init; }

    /// <summary>Gets the opacity, 0 to 1. Defaults to 1.</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>Gets the text size in points. Defaults to 10.</summary>
    public double FontSize { get; init; } = 10;

    /// <summary>Gets the typeface text is drawn in, embedded as a subset; null for Helvetica, not embedded.</summary>
    public Typeface? Font { get; init; }

    /// <summary>Gets the text colour. Defaults to black.</summary>
    public Rgba8 Color { get; init; } = new(0, 0, 0, 255);

    /// <summary>Gets whether the stamp goes under the page's content instead of over it. Defaults to false.</summary>
    public bool Under { get; init; }

    /// <summary>Gets the name of a layer (optional content group) the stamp goes on, so viewers can hide it; null for none.</summary>
    public string? Layer { get; init; }

    /// <summary>
    /// Gets the kind of page furniture it is marked as; null (the default) to take it from the anchor: a header at the top,
    /// a footer at the bottom, a watermark in the middle row.
    /// </summary>
    public PdfStampSubtype? Subtype { get; init; }

    /// <summary>Gets the culture numbers and dates in the template are written in; null for the invariant culture.</summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>Gets the 1-based page numbers to stamp; null (the default) for every page.</summary>
    public IReadOnlyList<int>? Pages { get; init; }

    /// <summary>
    /// Gets the pages to stamp as a range such as <c>PdfPageRange.Parse("2-z")</c>; null (the default) for every page. When
    /// both this and <see cref="Pages"/> are set, a page is stamped only when both choose it.
    /// </summary>
    public PdfPageRange? PageRange { get; init; }

    /// <summary>Gets the size an image or page stamp is drawn at, as a multiple of its own. Defaults to 1.</summary>
    public double Scale { get; init; } = 1;

    internal PdfStampKind Kind { get; }

    internal Raster? ImageRaster { get; private init; }

    internal double ImageWidth { get; private init; }

    internal double ImageHeight { get; private init; }

    internal PdfPage? SourcePage { get; private init; }

    internal PdfBatesSequence? Sequence { get; private init; }

    /// <summary>A text stamp from a template, such as <c>"Page {page} of {pages}"</c>, at the bottom centre.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="template"/> is null.</exception>
    public static PdfStamp Text(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return new PdfStamp(PdfStampKind.Text) { Template = template };
    }

    /// <summary>
    /// A watermark: large grey text across the middle of each page, turned 45 degrees and three-tenths opaque, marked as a
    /// watermark artifact.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    public static PdfStamp Watermark(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PdfStamp(PdfStampKind.Text)
        {
            Template = text,
            Anchor = StampAnchor.Center,
            Rotation = 45,
            FontSize = 72,
            Opacity = 0.3,
            Color = new Rgba8(128, 128, 128, 255),
            Subtype = PdfStampSubtype.Watermark,
        };
    }

    /// <summary>
    /// A Bates number from a sequence that continues across documents, at the bottom right: each page stamped takes the next
    /// number. Change <see cref="Template"/> to put text around it (<c>{bates}</c> is the number).
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="sequence"/> is null.</exception>
    public static PdfStamp Bates(PdfBatesSequence sequence)
    {
        ArgumentNullException.ThrowIfNull(sequence);
        return new PdfStamp(PdfStampKind.Text) { Template = "{bates}", Sequence = sequence, Anchor = StampAnchor.BottomRight };
    }

    /// <summary>An image stamp, <paramref name="width"/> by <paramref name="height"/> points before <see cref="Scale"/>, at the top right.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    public static PdfStamp Image(Raster image, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!(width > 0 && height > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The size must be positive.");
        }

        return new PdfStamp(PdfStampKind.Image) { ImageRaster = image, ImageWidth = width, ImageHeight = height, Anchor = StampAnchor.TopRight };
    }

    /// <summary>A page stamp: another page drawn as it is shown, such as a letterhead, under each page by default.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="page"/> is null.</exception>
    public static PdfStamp Page(PdfPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return new PdfStamp(PdfStampKind.Page) { SourcePage = page, Anchor = StampAnchor.Center, Under = true };
    }

    /// <summary>Throws when a value is outside what it can mean.</summary>
    internal void Check(string name)
    {
        if (Opacity is not (>= 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(name, Opacity, "Opacity must be 0 to 1.");
        }

        if (!(FontSize > 0 && double.IsFinite(FontSize)) || !(Scale > 0 && double.IsFinite(Scale)) || !double.IsFinite(Rotation))
        {
            throw new ArgumentOutOfRangeException(name, "FontSize and Scale must be positive, and Rotation finite.");
        }
    }
}

/// <summary>What a stamp is made of.</summary>
internal enum PdfStampKind
{
    Text,
    Image,
    Page,
}

/// <summary>
/// A Bates numbering sequence (Phase 7 design Q11): numbers with a prefix, a suffix and a fixed number of digits, continuing
/// across every document stamped with it, so a set of documents is numbered page by page from one start.
/// </summary>
/// <example>
/// <code>
/// PdfBatesSequence bates = new("ACME-", digits: 6, start: 1);
/// foreach (PdfDocument d in set)
/// {
///     d.Stamp(PdfStamp.Bates(bates));
/// }
/// </code>
/// </example>
public sealed class PdfBatesSequence
{
    /// <summary>A sequence.</summary>
    /// <param name="prefix">Text before the number.</param>
    /// <param name="digits">The least number of digits, padded with zeros.</param>
    /// <param name="start">The first number.</param>
    /// <param name="suffix">Text after the number.</param>
    /// <param name="step">How far each page advances the number.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="digits"/> is negative, or <paramref name="step"/> is not positive.</exception>
    public PdfBatesSequence(string prefix = "", int digits = 6, long start = 1, string suffix = "", int step = 1)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(suffix);
        ArgumentOutOfRangeException.ThrowIfNegative(digits);
        ArgumentOutOfRangeException.ThrowIfLessThan(step, 1);
        Prefix = prefix;
        Digits = digits;
        Suffix = suffix;
        Step = step;
        Next = start;
    }

    /// <summary>The text before the number.</summary>
    public string Prefix { get; }

    /// <summary>The least number of digits.</summary>
    public int Digits { get; }

    /// <summary>The text after the number.</summary>
    public string Suffix { get; }

    /// <summary>How far each page advances the number.</summary>
    public int Step { get; }

    /// <summary>The number the next page stamped gets.</summary>
    public long Next { get; private set; }

    /// <summary>A number as the sequence writes it, such as <c>ACME-000042</c>.</summary>
    public string Format(long number)
    {
        return Prefix + number.ToString(new string('0', Math.Max(1, Digits)), CultureInfo.InvariantCulture) + Suffix;
    }

    /// <summary>Takes the next number.</summary>
    internal long Take()
    {
        long number = Next;
        Next += Step;
        return number;
    }
}

/// <summary>What <see cref="PdfDocument.Stamp"/> did.</summary>
/// <param name="Pages">How many pages were stamped.</param>
/// <param name="FirstBates">The first Bates number used in this document, written out; null without one.</param>
/// <param name="LastBates">The last Bates number used in this document; null without one.</param>
public sealed record PdfStampResult(int Pages, string? FirstBates, string? LastBates);
