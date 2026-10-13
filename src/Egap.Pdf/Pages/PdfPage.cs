namespace Egap.Pdf;

/// <summary>
/// A page (7.7.3.3): a thin view over the page's dictionary, which it reads and writes directly, so anything it does not
/// cover is still there in <see cref="Dictionary"/>. Two views are equal when they wrap the same dictionary (Phase 2 Q1).
/// </summary>
/// <remarks>
/// <see cref="Resources"/>, <see cref="MediaBox"/>, <see cref="CropBox"/> and <see cref="Rotate"/> are inherited from the
/// page tree when the page has none (7.7.3.4). Setting one writes it on the page itself. The boxes read are the ones that
/// take effect (14.11.2): the crop box is clipped to the media box, and the bleed, trim and art boxes default to the crop box
/// and are clipped to the media box. A missing or unusable media box is US Letter, as pdf.js and PDFium take it.
/// </remarks>
public sealed class PdfPage : IEquatable<PdfPage>
{
    /// <summary>The media box of a page that has none: US Letter, 612 by 792 points.</summary>
    public static readonly PdfRectangle DefaultMediaBox = new(0, 0, 612, 792);

    /// <summary>How far up the page tree inherited attributes are looked for, against parent loops.</summary>
    private const int MaxInheritanceDepth = 1024;

    private static readonly PdfName ParentKey = "Parent";

    internal PdfPage(PdfDictionary dictionary, PdfReference? reference, PdfDocument document)
    {
        Dictionary = dictionary;
        Reference = reference;
        Document = document;
    }

    /// <summary>The page's dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The reference to the page's dictionary, or <see langword="null"/> for a page written directly in its parent's <c>/Kids</c>.</summary>
    public PdfReference? Reference { get; }

    /// <summary>The page's annotations (12.5), its <c>/Annots</c> array.</summary>
    public PdfAnnotationCollection Annotations => new(this);

    /// <summary>The document the page belongs to.</summary>
    internal PdfDocument Document { get; }

    /// <summary>The page's resources, inherited when the page has none; <see langword="null"/> when there are none at all.</summary>
    public PdfDictionary? Resources
    {
        get => Inherited("Resources") as PdfDictionary;
        set => Dictionary["Resources"] = value;
    }

    /// <summary>The media box: the size of the medium the page is drawn on. Inherited; US Letter when missing or unusable.</summary>
    public PdfRectangle MediaBox
    {
        get => PdfRectangle.FromArray(Inherited("MediaBox") as PdfArray) is { IsEmpty: false } box ? box : DefaultMediaBox;
        set => Dictionary["MediaBox"] = value.Normalize().ToArray();
    }

    /// <summary>The crop box: what a viewer shows. Inherited; defaults to the media box and is clipped to it.</summary>
    public PdfRectangle CropBox
    {
        get => Clipped(PdfRectangle.FromArray(Inherited("CropBox") as PdfArray), MediaBox);
        set => Dictionary["CropBox"] = value.Normalize().ToArray();
    }

    /// <summary>The bleed box, for production: defaults to the crop box and is clipped to the media box.</summary>
    public PdfRectangle BleedBox
    {
        get => Box("BleedBox");
        set => Dictionary["BleedBox"] = value.Normalize().ToArray();
    }

    /// <summary>The trim box: the finished page after trimming. Defaults to the crop box and is clipped to the media box.</summary>
    public PdfRectangle TrimBox
    {
        get => Box("TrimBox");
        set => Dictionary["TrimBox"] = value.Normalize().ToArray();
    }

    /// <summary>The art box: the page's meaningful content. Defaults to the crop box and is clipped to the media box.</summary>
    public PdfRectangle ArtBox
    {
        get => Box("ArtBox");
        set => Dictionary["ArtBox"] = value.Normalize().ToArray();
    }

    /// <summary>
    /// The size of a unit of user space in points (PDF 1.6, 14.11.2.1); 1 when missing or not a positive number. Setting 1
    /// removes the entry.
    /// </summary>
    public double UserUnit
    {
        get => Dictionary.GetNumber("UserUnit") is double unit && unit > 0 && double.IsFinite(unit) ? unit : 1;
        set
        {
            if (!(value > 0) || !double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The user unit must be a positive number.");
            }

            Dictionary["UserUnit"] = value == 1 ? null : new PdfReal(value);
        }
    }

    /// <summary>
    /// How many degrees clockwise the page is turned when shown: 0, 90, 180 or 270. Inherited; a negative or larger angle is
    /// brought into that range, and one that is not a multiple of 90 counts as 0, as in pdf.js. Rotating changes only this
    /// entry, never the page's content.
    /// </summary>
    public int Rotate
    {
        get
        {
            long? angle = Inherited("Rotate") is PdfObject value ? Objects.ObjectValues.Int(value) : null;
            return angle is long degrees && degrees % 90 == 0 ? (int)(((degrees % 360) + 360) % 360) : 0;
        }

        set
        {
            if (value % 90 != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Pages turn by multiples of 90 degrees.");
            }

            Dictionary["Rotate"] = new PdfInteger(((value % 360) + 360) % 360);
        }
    }

    /// <summary>Whether two views wrap the same page dictionary.</summary>
    public static bool operator ==(PdfPage? left, PdfPage? right)
    {
        return left is null ? right is null : left.Equals(right);
    }

    /// <summary>Whether two views wrap different page dictionaries.</summary>
    public static bool operator !=(PdfPage? left, PdfPage? right)
    {
        return !(left == right);
    }

    /// <summary>Whether both views wrap the same page dictionary.</summary>
    public bool Equals(PdfPage? other)
    {
        return other is not null && ReferenceEquals(Dictionary, other.Dictionary);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfPage);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Dictionary);
    }

    /// <summary>The page as its reference and media box, for debugging.</summary>
    public override string ToString()
    {
        return $"Page {(Reference is null ? "(direct)" : Reference.ToString())} {MediaBox}";
    }

    /// <summary>
    /// A value as stored (a reference is not followed) on the page, or else on the nearest page tree node above it that has
    /// one (7.7.3.4).
    /// </summary>
    internal PdfObject? InheritedValue(PdfName key)
    {
        PdfDictionary? node = Dictionary;
        for (int depth = 0; node is not null && depth < MaxInheritanceDepth; depth++)
        {
            if (node.Get(key) is PdfObject and not PdfNull)
            {
                return node[key];
            }

            node = node.Get<PdfDictionary>(ParentKey);
        }

        return null;
    }

    /// <summary>An inherited value with references followed.</summary>
    private PdfObject? Inherited(PdfName key)
    {
        return InheritedValue(key)?.Resolve();
    }

    private PdfRectangle Box(PdfName key)
    {
        PdfRectangle media = MediaBox;
        return PdfRectangle.FromArray(Dictionary.Get<PdfArray>(key)) is PdfRectangle box && !box.Intersect(media).IsEmpty
            ? box.Intersect(media)
            : Clipped(PdfRectangle.FromArray(Inherited("CropBox") as PdfArray), media);
    }

    /// <summary>A box clipped to the media box; the media box itself when there is no box or nothing of it is left.</summary>
    private static PdfRectangle Clipped(PdfRectangle? box, PdfRectangle media)
    {
        return box is PdfRectangle b && b.Intersect(media) is { IsEmpty: false } clipped ? clipped : media;
    }
}
