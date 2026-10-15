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
public sealed partial class PdfPage : IEquatable<PdfPage>
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

    /// <summary>
    /// The page's text as glyphs, words and lines (Phase 3 design Q10): content order by default, spaces from space glyphs
    /// and gaps, Unicode from ToUnicode maps, then glyph names, then a private-use character counted in a warning.
    /// </summary>
    public PdfTextPage GetText(PdfTextOptions? options = null)
    {
        return Text.TextCollector.Read(this, options ?? PdfTextOptions.Default);
    }

    /// <summary>The page's text: its lines, as <see cref="GetText"/> builds them, joined with newlines.</summary>
    public string ExtractText(PdfTextOptions? options = null)
    {
        return GetText(options).ToString();
    }

    /// <summary>
    /// The images the page draws, in content order, with where each is drawn (Phase 3 design Q11): image XObjects and
    /// inline images, those inside form XObjects and tiling patterns included. An image drawn twice is listed twice; an image
    /// in a tiling pattern is listed once where the pattern is used, placed for the pattern's first tile.
    /// </summary>
    public IReadOnlyList<PdfPlacedImage> GetImages()
    {
        ImageCollector collector = new(Document);
        collector.Walk(this);
        return collector.Images;
    }

    /// <summary>The fonts the page's resources name, by resource name (Phase 3 design Q7).</summary>
    public PdfFontCollection Fonts => new(this);

    /// <summary>The page's annotations (12.5), its <c>/Annots</c> array.</summary>
    public PdfAnnotationCollection Annotations => new(this);

    /// <summary>The document the page belongs to.</summary>
    internal PdfDocument Document { get; }

    /// <summary>
    /// Moves the page's finished content out of memory (Phase 3 design, after Phase 2 Q14): its content streams, and the
    /// images, forms, patterns and shadings its resources and annotation appearances use, go to the document's scratch file,
    /// and a save copies them from there. For generating many pages: draw a page, dispose its canvas, flush it, and memory
    /// holds only small dictionaries. Font subsets stay open until the save, as later pages may add glyphs. The page can
    /// still be read and changed afterwards; its streams are then read back from the scratch file.
    /// </summary>
    public void Flush()
    {
        HashSet<PdfObject> seen = new(ReferenceEqualityComparer.Instance);
        Spill(Dictionary["Contents"], seen, 0);
        Spill(Inherited("Resources"), seen, 0);
        if (Dictionary["Annots"]?.Resolve() is PdfArray annotations)
        {
            foreach (PdfObject annotation in annotations)
            {
                if (annotation.Resolve() is PdfDictionary a)
                {
                    Spill(a["AP"], seen, 0);
                }
            }
        }
    }

    /// <summary>Moves every in-memory stream under an object to the scratch file, not following pages or parents.</summary>
    private void Spill(PdfObject? value, HashSet<PdfObject> seen, int depth)
    {
        if (value is null || depth > 64)
        {
            return;
        }

        PdfObject resolved = value.Resolve();
        if (!seen.Add(resolved))
        {
            return;
        }

        switch (resolved)
        {
            case PdfStream stream:
                if (stream.Data is BytesData bytes && bytes.Length >= MinSpilled)
                {
                    IO.ScratchFile scratch = Document.Scratch;
                    stream.ReplaceData(new IO.ScratchData(scratch, scratch.Append(bytes.Span), (int)bytes.Length));
                }

                Spill(stream.Dictionary, seen, depth + 1);
                break;
            case PdfDictionary dictionary:
                foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary.Entries)
                {
                    if (entry.Key.Value is not ("Parent" or "P" or "Dest" or "A" or "Next" or "Prev" or "First" or "Last"))
                    {
                        Spill(entry.Value, seen, depth + 1);
                    }
                }

                break;
            case PdfArray array:
                foreach (PdfObject item in array)
                {
                    Spill(item, seen, depth + 1);
                }

                break;
        }
    }

    /// <summary>Streams smaller than this stay in memory: moving them saves less than it costs.</summary>
    private const int MinSpilled = 256;

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

    /// <summary>
    /// Reads the page's content: the operations of its <c>/Contents</c> stream, or of the streams of its <c>/Contents</c>
    /// array as one sequence (Phase 3 design Q3). Each call reads the content again; repairs are in
    /// <see cref="PdfContent.Warnings"/>.
    /// </summary>
    /// <exception cref="PdfException">The content is damaged and the document was opened in strict mode.</exception>
    /// <exception cref="PdfLimitException">The content decodes to more than the read options allow, or nests too deep.</exception>
    public PdfContent GetContent()
    {
        return PdfContent.Read(this);
    }

    /// <summary>
    /// Makes <paramref name="content"/> the page's content (Phase 3 design Q4). Content read from this page with
    /// <see cref="GetContent"/> keeps every stream whose operations are unchanged, byte for byte, and writes only new and
    /// changed operations, compactly, into new streams; unchanged content changes nothing. Other content replaces the page's
    /// in one new stream. New streams are compressed unless the page's streams were all uncompressed.
    /// </summary>
    public void SetContent(PdfContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        PdfContent.WriteBack(this, content);
    }

    /// <summary>
    /// Draws over or under what the page shows, without touching its content streams (Phase 3 design Q5): over, the old
    /// content is bracketed by a <c>q</c> stream in front and a <c>Q</c> before the new drawing, so the drawing starts from a
    /// clean graphics state; under, the drawing comes first in a <c>q</c> and <c>Q</c> of its own. The drawing is in default
    /// user space (PDF's own coordinates); names it uses must be in the page's resources, see <see cref="AddResource"/>.
    /// </summary>
    /// <param name="content">The drawing's content stream bytes, such as <see cref="PdfContentWriter.ToArray"/> gives.</param>
    /// <param name="placement">Whether the drawing goes over or under the page's content.</param>
    public void AddContent(ReadOnlySpan<byte> content, PdfCanvasPlacement placement = PdfCanvasPlacement.Over)
    {
        Pages.PageDrawing.Add(this, content, placement);
    }

    /// <summary>
    /// Adds a resource, such as a font, image or graphics state, to the page and returns the name content uses for it
    /// (Phase 3 design Q6). Resources that are inherited or that other pages may share are first copied onto the page,
    /// shallowly, so other pages never change. A reference the category already holds keeps its name; a new resource is named
    /// <c>Eg</c>, a letter for its category (<c>EgX</c> for XObjects, <c>EgG</c> for graphics states, <c>EgF</c> for fonts) and a
    /// counter, clear of every name already there.
    /// </summary>
    /// <param name="category">The resource category: <c>Font</c>, <c>XObject</c>, <c>ExtGState</c>, <c>ColorSpace</c>, <c>Pattern</c>, <c>Shading</c> or <c>Properties</c>.</param>
    /// <param name="value">The resource, normally a reference to it.</param>
    public PdfName AddResource(PdfName category, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(value);
        return Pages.PageDrawing.AddResource(this, category, value);
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

    /// <summary>
    /// A walker that keeps every image it is shown with its placement, those inside the tiling patterns it paints with
    /// included: each pattern once where it is used, placed for its first tile (review Q8).
    /// </summary>
    private sealed class ImageCollector(PdfDocument document, HashSet<PdfStream>? patternChain = null) : PdfContentWalker
    {
        private readonly HashSet<PdfStream> chain = patternChain ?? new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<(PdfStream, PdfMatrix)> painted = new(new PatternUseComparer());
        private readonly Stack<PdfMatrix> spaces = new();

        public List<PdfPlacedImage> Images { get; } = [];

        protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
        {
            if ((paint & (PdfPathPaint.Fill | PdfPathPaint.EvenOddFill)) != 0)
            {
                Pattern(State.FillColor);
            }

            if ((paint & PdfPathPaint.Stroke) != 0)
            {
                Pattern(State.StrokeColor);
            }
        }

        protected override void OnGlyph(PdfGlyph glyph)
        {
            Pattern(State.FillColor);
        }

        protected override bool OnBeginForm(PdfStream form, PdfName name)
        {
            // A pattern used in a form is in the form's space (8.7.3.1).
            spaces.Push(State.Matrix);
            return true;
        }

        protected override void OnEndForm(PdfStream form, PdfName name)
        {
            spaces.TryPop(out _);
        }

        protected override void OnImage(PdfStream? image, PdfInlineImage? inlineImage, PdfName? name)
        {
            if (image is not null)
            {
                Images.Add(new PdfPlacedImage(new PdfImage(image, Resources, document), State.Matrix, name?.Value));
            }
            else if (inlineImage is not null)
            {
                Images.Add(new PdfPlacedImage(new PdfImage(inlineImage, Resources), State.Matrix, null));
            }
        }

        /// <summary>Walks a tiling pattern painted with, once per pattern and space, and keeps its images.</summary>
        private void Pattern(PdfColor? color)
        {
            if (color?.Pattern is not PdfName name
                || Resources?.Get<PdfDictionary>("Pattern")?.Get(name.Value) is not PdfStream pattern
                || pattern.Dictionary.GetInt("PatternType") != 1
                || chain.Contains(pattern))
            {
                return;
            }

            PdfMatrix space = spaces.Count > 0 ? spaces.Peek() : PdfMatrix.Identity;
            if (!painted.Add((pattern, space)))
            {
                return;
            }

            PdfMatrix matrix = (PdfMatrix.FromArray(pattern.Dictionary.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity) * space;
            chain.Add(pattern);
            try
            {
                ImageCollector inner = new(document, chain);
                inner.Walk(PdfContent.Read(pattern), pattern.Dictionary.Get<PdfDictionary>("Resources"), matrix, document.Options);
                Images.AddRange(inner.Images);
            }
            finally
            {
                chain.Remove(pattern);
            }
        }

        private sealed class PatternUseComparer : IEqualityComparer<(PdfStream Pattern, PdfMatrix Space)>
        {
            public bool Equals((PdfStream Pattern, PdfMatrix Space) x, (PdfStream Pattern, PdfMatrix Space) y)
            {
                return ReferenceEquals(x.Pattern, y.Pattern) && x.Space == y.Space;
            }

            public int GetHashCode((PdfStream Pattern, PdfMatrix Space) obj)
            {
                return HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Pattern), obj.Space);
            }
        }
    }
}
