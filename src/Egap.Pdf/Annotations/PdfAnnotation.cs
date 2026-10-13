using Egap.Pdf.Metadata;

namespace Egap.Pdf;

/// <summary>
/// An annotation (12.5): a thin view over its dictionary, typed by <c>/Subtype</c> through <see cref="FromDictionary(PdfDictionary)"/>
/// (Phase 2 Q10). Subtypes Egap has no class for (Sound, Movie, Screen, 3D, RichMedia, PrinterMark, TrapNet, Watermark,
/// Projection and any unknown one) are plain <see cref="PdfAnnotation"/>s, read and written through <see cref="Dictionary"/>.
/// Two views are equal when they wrap the same dictionary.
/// </summary>
/// <remarks>
/// A new annotation, made with a subtype's constructor, is stored once it is added to a page with
/// <see cref="PdfAnnotationCollection.Add{T}(T)"/>, and from then on reads the document and redraws its appearance when a
/// property the appearance shows is set.
/// </remarks>
public class PdfAnnotation : IEquatable<PdfAnnotation>
{
    private protected static readonly PdfName SubtypeKey = "Subtype";

    private protected PdfAnnotation(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        Dictionary = dictionary;
        Reference = reference;
        Document = document;
    }

    /// <summary>
    /// A new annotation dictionary of a subtype covering a rectangle, with the print flag set, as Acrobat sets it and PDF/A
    /// requires.
    /// </summary>
    private protected PdfAnnotation(string subtype, PdfRectangle rectangle)
        : this(new PdfDictionary { ["Type"] = PdfName.Get("Annot"), [SubtypeKey] = PdfName.Get(subtype), ["F"] = new PdfInteger((int)PdfAnnotationFlags.Print) }, null, null)
    {
        Rectangle = rectangle;
    }

    /// <summary>The annotation's dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// The reference to the annotation's dictionary, or <see langword="null"/> for one written directly in <c>/Annots</c>
    /// or not yet added to a page.
    /// </summary>
    public PdfReference? Reference { get; private set; }

    /// <summary>The subtype, <c>/Subtype</c>, such as <c>Link</c> or <c>Highlight</c>; empty when missing.</summary>
    public string Subtype => Dictionary.GetName(SubtypeKey)?.Value ?? "";

    /// <summary>Where the annotation is on the page, <c>/Rect</c>, normalized; empty when missing or unusable.</summary>
    public PdfRectangle Rectangle
    {
        get => PdfRectangle.FromArray(Dictionary.Get<PdfArray>("Rect"))?.Normalize() ?? default;
        set
        {
            Dictionary["Rect"] = value.Normalize().ToArray();
            Changed();
        }
    }

    /// <summary>The text shown for the annotation, or its description when it shows none (<c>/Contents</c>).</summary>
    public string? Contents
    {
        get => Dictionary.GetText("Contents");
        set
        {
            Dictionary["Contents"] = value is null ? null : PdfString.FromText(value);
            if (this is PdfFreeTextAnnotation)
            {
                Changed();
            }
        }
    }

    /// <summary>The annotation's name, unique on its page (<c>/NM</c>, PDF 1.4).</summary>
    public string? Name
    {
        get => Dictionary.GetText("NM");
        set => Dictionary["NM"] = value is null ? null : PdfString.FromText(value);
    }

    /// <summary>When the annotation was last changed (<c>/M</c>); <see langword="null"/> when missing or not a date.</summary>
    public DateTimeOffset? ModificationDate
    {
        get => PdfDate.ParsePdf(Dictionary.GetText("M"));
        set => Dictionary["M"] = value is DateTimeOffset date ? PdfString.FromText(PdfDate.FormatPdf(date)) : null;
    }

    /// <summary>The annotation flags (<c>/F</c>). Setting none removes the entry.</summary>
    public PdfAnnotationFlags Flags
    {
        get => (PdfAnnotationFlags)(int)(Dictionary.GetInt("F") ?? 0);
        set => Dictionary["F"] = value == 0 ? null : new PdfInteger((int)value);
    }

    /// <summary>
    /// The colour (<c>/C</c>): of the border, the title bar of a popup, or a link's border. Gray and CMYK colours are read as
    /// RGB; <see langword="null"/> when missing or transparent (an empty array). Setting one writes RGB.
    /// </summary>
    public PdfRgbColor? Color
    {
        get => ReadColor("C");
        set
        {
            Dictionary["C"] = value?.ToArray();
            Changed();
        }
    }

    /// <summary>
    /// The border width in points: <c>/BS /W</c>, else the third number of <c>/Border</c>, else 1 (12.5.2, 12.5.4). Setting
    /// it writes <c>/BS /W</c> and keeps an old <c>/Border</c> in step.
    /// </summary>
    public double BorderWidth
    {
        get => Dictionary.Get<PdfDictionary>("BS") is PdfDictionary bs
            ? NonNegative(bs.GetNumber("W"), 1)
            : NonNegative(Dictionary.Get<PdfArray>("Border") is { Count: >= 3 } border ? border.GetNumber(2) : null, 1);
        set
        {
            if (!(value >= 0) || !double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "A border width is a number of 0 or more.");
            }

            BorderStyleDictionary()["W"] = new PdfReal(value);
            if (Dictionary.Get<PdfArray>("Border") is { Count: >= 3 } border)
            {
                border[2] = new PdfReal(value);
            }

            Changed();
        }
    }

    /// <summary>The border style (<c>/BS /S</c>); solid when missing or unknown.</summary>
    public PdfBorderStyle BorderStyle
    {
        get => Dictionary.Get<PdfDictionary>("BS")?.GetName("S")?.Value switch
        {
            "D" => PdfBorderStyle.Dashed,
            "B" => PdfBorderStyle.Beveled,
            "I" => PdfBorderStyle.Inset,
            "U" => PdfBorderStyle.Underline,
            _ => PdfBorderStyle.Solid,
        };
        set
        {
            BorderStyleDictionary()["S"] = PdfName.Get(value switch
            {
                PdfBorderStyle.Dashed => "D",
                PdfBorderStyle.Beveled => "B",
                PdfBorderStyle.Inset => "I",
                PdfBorderStyle.Underline => "U",
                _ => "S",
            });
            Changed();
        }
    }

    /// <summary>
    /// The dash pattern of a dashed border (<c>/BS /D</c>): lengths of dashes and gaps in turn; 3 on, 3 off when missing.
    /// </summary>
    public IReadOnlyList<double> BorderDash
    {
        get => Dictionary.Get<PdfDictionary>("BS")?.Get<PdfArray>("D") is PdfArray dash
            && Enumerable.Range(0, dash.Count).Select(dash.GetNumber).ToList() is List<double?> lengths && lengths.All(l => l >= 0)
                ? [.. lengths.Select(l => l!.Value)]
                : [3];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            BorderStyleDictionary()["D"] = new PdfArray(value.Select(l => (PdfObject)new PdfReal(l)));
            Changed();
        }
    }

    /// <summary>The appearance state (<c>/AS</c>): which appearance of a sub-dictionary is shown, such as a check box's <c>On</c>.</summary>
    public string? AppearanceState
    {
        get => Dictionary.GetName("AS")?.Value;
        set => Dictionary["AS"] = value is null ? null : PdfName.Get(value);
    }

    /// <summary>
    /// The page the annotation says it is on (<c>/P</c>); <see langword="null"/> when it does not say or the annotation was
    /// not read from a document.
    /// </summary>
    public PdfPage? Page => Document is not null && Dictionary.Get<PdfDictionary>("P") is PdfDictionary page ? new PdfPage(page, Dictionary["P"] as PdfReference, Document) : null;

    /// <summary>The document the annotation was read from, or <see langword="null"/> for a new one.</summary>
    internal PdfDocument? Document { get; private set; }

    /// <summary>
    /// The annotation for a dictionary, typed by its <c>/Subtype</c>. The view has no document, so named destinations and
    /// the page are not looked up; read annotations through <see cref="PdfPage.Annotations"/> for those.
    /// </summary>
    public static PdfAnnotation FromDictionary(PdfDictionary dictionary)
    {
        return FromDictionary(dictionary, null, null);
    }

    /// <summary>
    /// The appearance stream of a kind (12.5.5): <c>/AP /N</c>, <c>/R</c> or <c>/D</c>, the rollover and down appearances
    /// falling back to the normal one. When the entry holds one stream per state, the one named by
    /// <see cref="AppearanceState"/> is returned. <see langword="null"/> when there is none.
    /// </summary>
    public PdfStream? GetAppearance(PdfAppearanceKind kind = PdfAppearanceKind.Normal)
    {
        PdfDictionary? appearances = Dictionary.Get<PdfDictionary>("AP");
        PdfObject? entry = kind switch
        {
            PdfAppearanceKind.Rollover => appearances?.Get("R"),
            PdfAppearanceKind.Down => appearances?.Get("D"),
            _ => null,
        } ?? appearances?.Get("N");
        return entry switch
        {
            PdfStream stream => stream,
            PdfDictionary states when AppearanceState is string state => states.Get<PdfStream>(state),
            _ => null,
        };
    }

    /// <summary>
    /// Draws the annotation's normal appearance (<c>/AP /N</c>) from its properties, replacing any it had (Phase 2 Q11):
    /// for square, circle, line, polygon, polyline, ink, highlight, underline, squiggly, strikeout, caret and text note
    /// annotations with paths, and for free text and stamps with the standard 14 fonts (Q12). Egap also draws it when a new
    /// annotation is added, and when a property the appearance shows is set on one read from a document.
    /// </summary>
    /// <returns>
    /// Whether an appearance was drawn: false for other subtypes, and for free text the standard 14 fonts cannot show (a
    /// warning says so), which keep the appearance they had.
    /// </returns>
    /// <exception cref="InvalidOperationException">The annotation is new and not yet added to a page.</exception>
    public bool UpdateAppearance()
    {
        return Document is null
            ? throw new InvalidOperationException("Add the annotation to a page before drawing its appearance.")
            : Annotations.AppearanceBuilder.Update(this, Document);
    }

    /// <summary>Whether both views wrap the same annotation dictionary.</summary>
    public bool Equals(PdfAnnotation? other)
    {
        return other is not null && ReferenceEquals(Dictionary, other.Dictionary);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfAnnotation);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Dictionary);
    }

    /// <summary>The subtype and rectangle, for debugging.</summary>
    public override string ToString()
    {
        return $"{(Subtype.Length == 0 ? "Untyped" : Subtype)} annotation {Rectangle}";
    }

    /// <summary>Makes a new annotation's view the stored one, once it is added to a page.</summary>
    internal void Attach(PdfReference reference, PdfDocument document)
    {
        Reference = reference;
        Document = document;
    }

    /// <summary>The view for an annotation dictionary, typed by its <c>/Subtype</c>.</summary>
    internal static PdfAnnotation FromDictionary(PdfDictionary dictionary, PdfReference? reference, PdfDocument? document)
    {
        ArgumentNullException.ThrowIfNull(dictionary);
        return dictionary.GetName(SubtypeKey)?.Value switch
        {
            "Text" => new PdfTextAnnotation(dictionary, reference, document),
            "Link" => new PdfLinkAnnotation(dictionary, reference, document),
            "FreeText" => new PdfFreeTextAnnotation(dictionary, reference, document),
            "Line" => new PdfLineAnnotation(dictionary, reference, document),
            "Square" => new PdfSquareAnnotation(dictionary, reference, document),
            "Circle" => new PdfCircleAnnotation(dictionary, reference, document),
            "Polygon" => new PdfPolygonAnnotation(dictionary, reference, document),
            "PolyLine" => new PdfPolyLineAnnotation(dictionary, reference, document),
            "Highlight" => new PdfHighlightAnnotation(dictionary, reference, document),
            "Underline" => new PdfUnderlineAnnotation(dictionary, reference, document),
            "Squiggly" => new PdfSquigglyAnnotation(dictionary, reference, document),
            "StrikeOut" => new PdfStrikeOutAnnotation(dictionary, reference, document),
            "Caret" => new PdfCaretAnnotation(dictionary, reference, document),
            "Stamp" => new PdfStampAnnotation(dictionary, reference, document),
            "Ink" => new PdfInkAnnotation(dictionary, reference, document),
            "Popup" => new PdfPopupAnnotation(dictionary, reference, document),
            "FileAttachment" => new PdfFileAttachmentAnnotation(dictionary, reference, document),
            "Redact" => new PdfRedactAnnotation(dictionary, reference, document),
            "Widget" => new PdfWidgetAnnotation(dictionary, reference, document),
            _ => new PdfAnnotation(dictionary, reference, document),
        };
    }

    /// <summary>The view of an annotation an entry points at, or <see langword="null"/> when it is not a dictionary.</summary>
    private protected PdfAnnotation? Linked(PdfName key)
    {
        return Dictionary.Get<PdfDictionary>(key) is PdfDictionary linked ? FromDictionary(linked, Dictionary[key] as PdfReference, Document) : null;
    }

    /// <summary>Redraws the appearance after a property it shows was set, when the annotation is stored and Egap draws its subtype.</summary>
    private protected void Changed()
    {
        if (Document is not null && Annotations.AppearanceBuilder.Supports(this))
        {
            Annotations.AppearanceBuilder.Update(this, Document);
        }
    }

    /// <summary>A colour array of 1 (gray), 3 (RGB) or 4 (CMYK) numbers as RGB; null for anything else.</summary>
    private protected PdfRgbColor? ReadColor(PdfName key)
    {
        PdfArray? array = Dictionary.Get<PdfArray>(key);
        double? At(int i)
        {
            return array!.GetNumber(i) is double value && !double.IsNaN(value) ? Math.Clamp(value, 0, 1) : null;
        }

        return array?.Count switch
        {
            1 when At(0) is double gray => new PdfRgbColor(gray, gray, gray),
            3 => PdfRgbColor.FromArray(array),
            4 when At(0) is double c && At(1) is double m && At(2) is double y && At(3) is double k => new PdfRgbColor((1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k)),
            _ => null,
        };
    }

    /// <summary>Pairs of numbers from an array as points; a trailing odd number or a pair with a non-number is left out.</summary>
    private protected IReadOnlyList<PdfPoint> ReadPoints(PdfName key)
    {
        return PdfPoint.FromArray(Dictionary.Get<PdfArray>(key));
    }

    /// <summary>Writes points as a flat array of numbers.</summary>
    private protected void WritePoints(PdfName key, IReadOnlyList<PdfPoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        Dictionary[key] = PdfPoint.ToArray(points);
    }

    /// <summary>
    /// The bounds of points, grown by a margin on every side for the stroke, as the rectangle of a new annotation drawn
    /// through them; empty for no points.
    /// </summary>
    private protected static PdfRectangle Bounds(IEnumerable<PdfPoint> points, double margin = 1)
    {
        List<PdfPoint> list = [.. points];
        return list.Count == 0
            ? default
            : new PdfRectangle(list.Min(p => p.X) - margin, list.Min(p => p.Y) - margin, list.Max(p => p.X) + margin, list.Max(p => p.Y) + margin);
    }

    /// <summary>A line ending style from its name (table 179); none for a missing or unknown name.</summary>
    private protected static PdfLineEnding Ending(PdfName? name)
    {
        return Enum.TryParse(name?.Value, ignoreCase: false, out PdfLineEnding ending) && Enum.IsDefined(ending) ? ending : PdfLineEnding.None;
    }

    /// <summary>The line ending named at an index of <c>/LE</c>; none when the array is shorter or the name unknown.</summary>
    private protected PdfLineEnding EndingAt(int index)
    {
        return Dictionary.Get<PdfArray>("LE") is PdfArray endings && index < endings.Count ? Ending(endings.GetName(index)) : PdfLineEnding.None;
    }

    private static double NonNegative(double? value, double fallback)
    {
        return value is double v && v >= 0 && double.IsFinite(v) ? v : fallback;
    }

    private PdfDictionary BorderStyleDictionary()
    {
        if (Dictionary.Get<PdfDictionary>("BS") is not PdfDictionary bs)
        {
            bs = new PdfDictionary { ["Type"] = PdfName.Get("Border") };
            Dictionary["BS"] = bs;
        }

        return bs;
    }
}

/// <summary>Annotation flags (12.5.3, table 167).</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The spec names these the annotation flags.")]
public enum PdfAnnotationFlags
{
    /// <summary>A viewer shows an unknown subtype only if it has an appearance.</summary>
    Invisible = 1,

    /// <summary>Never shown or printed.</summary>
    Hidden = 2,

    /// <summary>Printed.</summary>
    Print = 4,

    /// <summary>The appearance keeps its size when the page is zoomed.</summary>
    NoZoom = 8,

    /// <summary>The appearance keeps its orientation when the page is rotated.</summary>
    NoRotate = 16,

    /// <summary>Not shown on screen, but may be printed.</summary>
    NoView = 32,

    /// <summary>Not interactive.</summary>
    ReadOnly = 64,

    /// <summary>Cannot be deleted or have its properties changed, though its contents may be.</summary>
    Locked = 128,

    /// <summary>Inverts <see cref="NoView"/> for some events, such as the mouse passing over.</summary>
    ToggleNoView = 256,

    /// <summary>Its contents cannot be changed.</summary>
    LockedContents = 512,
}

/// <summary>A border style (12.5.4, table 168).</summary>
public enum PdfBorderStyle
{
    /// <summary>A solid line.</summary>
    Solid,

    /// <summary>A dashed line, with the pattern of <see cref="PdfAnnotation.BorderDash"/>.</summary>
    Dashed,

    /// <summary>An embossed look.</summary>
    Beveled,

    /// <summary>An engraved look.</summary>
    Inset,

    /// <summary>A line along the bottom only.</summary>
    Underline,
}

/// <summary>Which appearance of an annotation (12.5.5).</summary>
public enum PdfAppearanceKind
{
    /// <summary>The appearance shown normally, <c>/N</c>.</summary>
    Normal,

    /// <summary>The appearance while the pointer is over the annotation, <c>/R</c>.</summary>
    Rollover,

    /// <summary>The appearance while the annotation is pressed, <c>/D</c>.</summary>
    Down,
}

/// <summary>A line ending style (12.5.6.7, table 179), named as in the file.</summary>
public enum PdfLineEnding
{
    /// <summary>No ending.</summary>
    None,

    /// <summary>A filled square.</summary>
    Square,

    /// <summary>A filled circle.</summary>
    Circle,

    /// <summary>A filled diamond.</summary>
    Diamond,

    /// <summary>Two short lines meeting at the end.</summary>
    OpenArrow,

    /// <summary>A filled triangle pointing out.</summary>
    ClosedArrow,

    /// <summary>A short line across the end.</summary>
    Butt,

    /// <summary>Two short lines meeting back from the end.</summary>
    ROpenArrow,

    /// <summary>A filled triangle pointing in.</summary>
    RClosedArrow,

    /// <summary>A short slanted line across the end.</summary>
    Slash,
}
