using System.Globalization;

namespace Egap.Pdf.Redaction;

/// <summary>
/// Removes what redaction areas cover from one content stream (Phase 5 design Q9): it walks the content once to find each
/// glyph, path, shading, image and form the areas reach, then writes the content again without them. A glyph goes when its
/// box overlaps an area, and the glyphs around it keep their places through TJ displacements; a path goes by the paths
/// option; a shading and a pattern-filled path are clipped to leave the areas out; a form the areas reach is redacted in a
/// copy of its own. Areas are in the space the walk starts in, the page's default user space. For sanitising (design Q10),
/// <c>hidden</c> says which marked content's glyphs go too, such as a hidden layer's.
/// </summary>
internal sealed class ContentRedactor(PdfDocument document, IReadOnlyList<PdfRectangle> areas, IReadOnlyList<PdfRgbColor?> fills, PdfRedactionOptions options, RedactionCounters counters, Func<IReadOnlyList<PdfMarkedContent>, bool>? hidden = null)
{
    /// <summary>Redacts content drawn with <paramref name="resources"/> at <paramref name="matrix"/>; null when nothing was covered.</summary>
    public PdfContent? Redact(PdfContent content, PdfDictionary? resources, PdfMatrix matrix, int depth)
    {
        Recorder recorder = new(areas, options, hidden);
        recorder.Walk(content, resources, matrix, document.Options);
        if (!recorder.Touched)
        {
            return null;
        }

        PdfContent result = new();
        Dictionary<int, List<GlyphHit>> text = recorder.Glyphs.GroupBy(g => g.Operation).ToDictionary(g => g.Key, g => g.ToList());
        HashSet<int> removed = [];
        foreach ((int start, int end) in recorder.RemovedPaths)
        {
            for (int i = start; i <= end; i++)
            {
                removed.Add(i);
            }
        }

        counters.Paths += recorder.RemovedPaths.Count + recorder.ClipOnly.Count;
        for (int i = 0; i < content.Count; i++)
        {
            PdfOperation operation = content[i];
            if (removed.Contains(i))
            {
                continue;
            }

            if (recorder.ClipOnly.TryGetValue(i, out string? clipOperator))
            {
                // The path also set a clip: the clip stays, the painting goes.
                result.Add(new PdfOperation(clipOperator));
                result.Add(new PdfOperation("n"));
                continue;
            }

            if (text.TryGetValue(i, out List<GlyphHit>? glyphs) && glyphs.Any(g => g.Covered))
            {
                counters.Glyphs += glyphs.Count(g => g.Covered);
                result.AddRange(RewriteText(operation, glyphs));
                continue;
            }

            if (recorder.Excluded.TryGetValue(i, out PdfMatrix ctm))
            {
                // A shading or a pattern fill reaching the areas: drawn inside a clip that leaves them out.
                result.Add(new PdfOperation("q"));
                result.AddRange(ExclusionClip(ctm));
                result.Add(operation);
                result.Add(new PdfOperation("Q"));
                continue;
            }

            if (recorder.Forms.TryGetValue(i, out FormHit? form) && RedactForm(form, resources, depth) is PdfName copy)
            {
                result.Add(new PdfOperation("Do", copy));
                continue;
            }

            if (recorder.Images.TryGetValue(i, out ImageHit? image))
            {
                if (options.Images == PdfRedactImages.Remove)
                {
                    counters.Images++;
                    continue;
                }

                if (options.Images == PdfRedactImages.Pixels && ImageRedactor.Redact(document, image.Image, image.Matrix, areas, fills) is PdfStream redactedImage && resources is not null)
                {
                    // Inline images too become XObjects of their own: an inline image cannot be written back in place.
                    counters.Images++;
                    result.Add(new PdfOperation("Do", AddXObject(resources, document.Objects.Add(redactedImage), image.Name?.Value ?? "Im")));
                    continue;
                }
            }

            result.Add(operation);
        }

        return result;
    }

    /// <summary>
    /// The text operation without its covered glyphs: one <c>TJ</c> whose displacements stand where the removed glyphs were,
    /// so every kept glyph stays where it was. <c>'</c> and <c>"</c> keep their line move and spacing as operations of their own.
    /// </summary>
    private static List<PdfOperation> RewriteText(PdfOperation operation, List<GlyphHit> glyphs)
    {
        List<PdfOperation> result = [];
        List<PdfObject> items;
        switch (operation.Operator)
        {
            case "TJ" when operation.Operands is [PdfArray array]:
                items = [.. array.Select(i => i.Resolve())];
                break;
            case "Tj" when operation.Operands is [PdfString single]:
                items = [single];
                break;
            case "'" when operation.Operands is [PdfString quoted]:
                result.Add(new PdfOperation("T*"));
                items = [quoted];
                break;
            case "\"" when operation.Operands is [var wordSpacing, var characterSpacing, PdfString quoted2]:
                result.Add(new PdfOperation("Tw", wordSpacing));
                result.Add(new PdfOperation("Tc", characterSpacing));
                result.Add(new PdfOperation("T*"));
                items = [quoted2];
                break;
            default:
                return [operation];
        }

        PdfArray rewritten = [];
        int next = 0;
        bool hex = false;
        double pending = 0;
        List<byte> kept = [];

        void FlushKept()
        {
            if (kept.Count > 0)
            {
                rewritten.Add(PdfString.FromBytes([.. kept], hex));
                kept.Clear();
            }
        }

        void FlushPending()
        {
            if (pending != 0)
            {
                rewritten.Add(new PdfReal(pending));
                pending = 0;
            }
        }

        foreach (PdfObject item in items)
        {
            if (item is PdfInteger or PdfReal)
            {
                FlushKept();
                pending += item is PdfInteger integer ? integer.Value : ((PdfReal)item).Value;
                continue;
            }

            if (item is not PdfString text)
            {
                continue;
            }

            hex = text.IsHex;
            ReadOnlySpan<byte> bytes = text.Bytes;
            int at = 0;
            while (at < bytes.Length && next < glyphs.Count)
            {
                GlyphHit glyph = glyphs[next++];
                int length = Math.Min(glyph.Glyph.CodeLength, bytes.Length - at);
                if (glyph.Covered)
                {
                    // Where the glyph's advance went: a displacement of the same size (9.4.3), in thousandths of the font size.
                    FlushKept();
                    pending -= glyph.Advance;
                }
                else
                {
                    FlushPending();
                    kept.AddRange(bytes.Slice(at, length));
                }

                at += length;
            }

            // Codes the walk did not show (it stopped early): kept as they were.
            if (at < bytes.Length)
            {
                FlushPending();
                kept.AddRange(bytes[at..]);
            }
        }

        FlushKept();
        FlushPending();
        // The array is one operand: passed alone, it would be taken as the operand list.
        result.Add(new PdfOperation("TJ", new PdfObject[] { rewritten }));
        return result;
    }

    /// <summary>
    /// A clip that leaves the areas out, in the user space of <paramref name="ctm"/>: a rectangle far beyond the page with each
    /// area cut out by the even-odd rule.
    /// </summary>
    private List<PdfOperation> ExclusionClip(PdfMatrix ctm)
    {
        List<PdfOperation> clip = [];
        if (!ctm.TryInvert(out PdfMatrix inverse))
        {
            return [new PdfOperation("re", Number(0), Number(0), Number(0), Number(0)), new PdfOperation("W"), new PdfOperation("n")];
        }

        PdfRectangle far = inverse.Transform(new PdfRectangle(-100_000, -100_000, 100_000, 100_000));
        clip.Add(new PdfOperation("re", Number(far.Left), Number(far.Bottom), Number(far.Width), Number(far.Height)));
        foreach (PdfRectangle area in areas)
        {
            // Each corner mapped on its own, as the current space may be turned or skewed against the page.
            PdfPoint[] corners =
            [
                inverse.Transform(new PdfPoint(area.Left, area.Bottom)), inverse.Transform(new PdfPoint(area.Right, area.Bottom)),
                inverse.Transform(new PdfPoint(area.Right, area.Top)), inverse.Transform(new PdfPoint(area.Left, area.Top)),
            ];
            clip.Add(new PdfOperation("m", Number(corners[0].X), Number(corners[0].Y)));
            for (int i = 1; i < 4; i++)
            {
                clip.Add(new PdfOperation("l", Number(corners[i].X), Number(corners[i].Y)));
            }

            clip.Add(new PdfOperation("h"));
        }

        clip.Add(new PdfOperation("W*"));
        clip.Add(new PdfOperation("n"));
        return clip;
    }

    /// <summary>
    /// Redacts a form the areas reach, in a copy of its own (so other pages and other uses keep the original until the save
    /// drops it), registered in <paramref name="resources"/> under a new name; null when nothing in it was covered.
    /// </summary>
    private PdfName? RedactForm(FormHit form, PdfDictionary? resources, int depth)
    {
        if (depth >= document.Options.MaxNestingDepth || resources is null)
        {
            return null;
        }

        PdfContent content;
        try
        {
            content = PdfContent.Parse(form.Form.GetDecodedBytes());
        }
        catch (PdfException)
        {
            return null;
        }

        PdfDictionary? formResources = form.Form.Dictionary.Get<PdfDictionary>("Resources")?.Clone() ?? resources;
        PdfContent? redacted = Redact(content, formResources, form.Matrix, depth + 1);
        if (redacted is null)
        {
            return null;
        }

        counters.Forms++;
        PdfStream copy = new(form.Form.Dictionary.Clone());
        copy.Dictionary.Remove("Filter");
        copy.Dictionary.Remove("DecodeParms");
        if (form.Form.Dictionary.ContainsKey("Resources"))
        {
            copy.Dictionary["Resources"] = formResources;
        }

        copy.SetDecodedBytes(redacted.ToBytes());
        PdfReference reference = document.Objects.Add(copy);
        if (resources.Get<PdfDictionary>("XObject") is not PdfDictionary xobjects)
        {
            xobjects = [];
            resources["XObject"] = xobjects;
        }

        string stem = form.Name.Value + "R";
        string name = stem;
        for (int n = 1; xobjects.ContainsKey(name); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{stem}{n}");
        }

        xobjects[name] = reference;
        return PdfName.Get(name);
    }

    /// <summary>Registers an XObject in the resources under a name not yet taken, made from <paramref name="stem"/>.</summary>
    private static PdfName AddXObject(PdfDictionary resources, PdfReference reference, string stem)
    {
        if (resources.Get<PdfDictionary>("XObject") is not PdfDictionary xobjects)
        {
            xobjects = [];
            resources["XObject"] = xobjects;
        }

        stem += "R";
        string name = stem;
        for (int n = 1; xobjects.ContainsKey(name); n++)
        {
            name = string.Create(CultureInfo.InvariantCulture, $"{stem}{n}");
        }

        xobjects[name] = reference;
        return PdfName.Get(name);
    }

    private static PdfReal Number(double value)
    {
        return new PdfReal(Math.Round(value, 4));
    }

    /// <summary>One glyph found: its operation, where it is, whether an area covers it, and its advance in TJ units.</summary>
    internal sealed record GlyphHit(int Operation, PdfGlyph Glyph, bool Covered, double Advance);

    /// <summary>A form drawn by an operation, with the matrix that places it.</summary>
    internal sealed record FormHit(PdfStream Form, PdfName Name, PdfMatrix Matrix);

    /// <summary>An image drawn by an operation (an XObject or inline), with the matrix that places its unit square.</summary>
    internal sealed record ImageHit(PdfImage Image, PdfName? Name, PdfMatrix Matrix);

    /// <summary>The walk that finds what the areas cover, operation by operation, at the content's own level only.</summary>
    private sealed class Recorder(IReadOnlyList<PdfRectangle> areas, PdfRedactionOptions options, Func<IReadOnlyList<PdfMarkedContent>, bool>? hidden) : PdfContentWalker
    {
        private int index = -1;
        private int pathStart = -1;

        public List<GlyphHit> Glyphs { get; } = [];

        public List<(int Start, int End)> RemovedPaths { get; } = [];

        public Dictionary<int, string> ClipOnly { get; } = [];

        public Dictionary<int, PdfMatrix> Excluded { get; } = [];

        public Dictionary<int, FormHit> Forms { get; } = [];

        public Dictionary<int, ImageHit> Images { get; } = [];

        public bool Touched => Glyphs.Any(g => g.Covered) || RemovedPaths.Count > 0 || ClipOnly.Count > 0 || Excluded.Count > 0 || Forms.Count > 0
            || (Images.Count > 0 && options.Images != PdfRedactImages.None);

        protected override void OnOperation(PdfOperation operation)
        {
            if (Depth != 0)
            {
                return;
            }

            index++;
            if (operation.Operator is "m" or "re" && pathStart < 0)
            {
                pathStart = index;
            }
        }

        protected override void OnGlyph(PdfGlyph glyph)
        {
            if (Depth != 0)
            {
                return;
            }

            PdfGraphicsState state = State;
            PdfDictionary? font = state.Font;
            PdfFont metrics = font is null ? PdfFont.FromDictionary([]) : PdfFont.FromDictionary(font);
            (double ascent, double descent) = Extent(font);
            PdfRectangle box;
            double advance;
            if (metrics.GetVerticalMetrics(glyph.Code) is { } vertical)
            {
                // A vertical glyph hangs below its origin, half its width either side (9.7.4.3).
                double half = glyph.Width / 2000;
                box = glyph.Matrix.Transform(new PdfRectangle(-half + (vertical.OriginX / 1000), vertical.Advance / 1000, half + (vertical.OriginX / 1000), 0));
                double spacing = state.CharacterSpacing + (glyph.IsWordSpace ? state.WordSpacing : 0);
                double move = (vertical.Advance / 1000 * state.FontSize) - spacing;
                advance = state.FontSize == 0 ? 0 : move / state.FontSize * 1000;
            }
            else
            {
                box = glyph.Matrix.Transform(new PdfRectangle(0, descent, glyph.Width / 1000, ascent));
                double spacing = state.CharacterSpacing + (glyph.IsWordSpace ? state.WordSpacing : 0);
                advance = state.FontSize == 0 ? glyph.Width : glyph.Width + (spacing / state.FontSize * 1000);
            }

            bool covered = areas.Any(a => Covers(a, box, options.GlyphOverlap)) || (hidden is not null && MarkedContent.Count > 0 && hidden(MarkedContent));
            Glyphs.Add(new GlyphHit(index, glyph, covered, advance));
        }

        protected override void OnPaintPath(PdfPath path, PdfPathPaint paint)
        {
            if (Depth != 0)
            {
                return;
            }

            int start = pathStart < 0 ? index : pathStart;
            pathStart = -1;
            if (paint == PdfPathPaint.None || path.IsEmpty || path.GetBounds(State.Matrix) is not PdfRectangle bounds)
            {
                return;
            }

            // A stroke reaches half its width beyond the path.
            if ((paint & PdfPathPaint.Stroke) != 0)
            {
                double half = State.LineWidth / 2 * Math.Max(Math.Abs(State.Matrix.A) + Math.Abs(State.Matrix.C), Math.Abs(State.Matrix.B) + Math.Abs(State.Matrix.D));
                bounds = new PdfRectangle(bounds.Left - half, bounds.Bottom - half, bounds.Right + half, bounds.Top + half);
            }

            bool touched = areas.Any(a => Overlaps(a, bounds));
            if (!touched)
            {
                return;
            }

            bool painted = (paint & (PdfPathPaint.Stroke | PdfPathPaint.Fill | PdfPathPaint.EvenOddFill)) != 0;
            bool covered = areas.Any(a => Contains(a, bounds));
            bool pattern = (paint & (PdfPathPaint.Fill | PdfPathPaint.EvenOddFill)) != 0 && (State.FillColor.Pattern is not null || State.FillColor.Family == "Pattern");
            bool remove = painted && options.Paths switch
            {
                PdfRedactPaths.Touched => true,
                PdfRedactPaths.Covered => covered,
                _ => false,
            };
            if (remove)
            {
                if ((paint & (PdfPathPaint.Clip | PdfPathPaint.EvenOddClip)) != 0)
                {
                    ClipOnly[index] = (paint & PdfPathPaint.EvenOddClip) != 0 ? "W*" : "W";
                }
                else
                {
                    RemovedPaths.Add((start, index));
                }
            }
            else if (pattern)
            {
                Excluded[index] = State.Matrix;
            }
        }

        protected override void OnImage(PdfStream? image, PdfInlineImage? inlineImage, PdfName? name)
        {
            if (Depth != 0 || options.Images == PdfRedactImages.None)
            {
                return;
            }

            PdfRectangle placed = State.Matrix.Transform(new PdfRectangle(0, 0, 1, 1));
            if (!areas.Any(a => Overlaps(a, placed)))
            {
                return;
            }

            PdfImage? drawn = image is not null ? new PdfImage(image, Resources) : inlineImage is not null ? new PdfImage(inlineImage, Resources) : null;
            if (drawn is not null)
            {
                Images[index] = new ImageHit(drawn, name, State.Matrix);
            }
        }

        protected override void OnShading(PdfObject? shading, PdfName name)
        {
            if (Depth == 0)
            {
                Excluded[index] = State.Matrix;
            }
        }

        protected override bool OnBeginForm(PdfStream form, PdfName name)
        {
            if (Depth == 0)
            {
                PdfRectangle box = form.Dictionary.Get<PdfArray>("BBox") is { Count: 4 } b ? Box(b) : new PdfRectangle(-100_000, -100_000, 100_000, 100_000);
                if (areas.Any(a => Overlaps(a, State.Matrix.Transform(box))))
                {
                    Forms[index] = new FormHit(form, name, State.Matrix);
                }
            }

            return false;
        }

        protected override bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure)
        {
            return false;
        }

        /// <summary>A rectangle from an array of four numbers, its corners in either order.</summary>
        private static PdfRectangle Box(PdfArray array)
        {
            double[] n = [.. Enumerable.Range(0, 4).Select(i => array.Get<PdfObject>(i) switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => 0 })];
            return new PdfRectangle(Math.Min(n[0], n[2]), Math.Min(n[1], n[3]), Math.Max(n[0], n[2]), Math.Max(n[1], n[3]));
        }

        /// <summary>The font's ascent and descent in text space: its descriptor's, or the usual 0.8 and −0.2 of the em.</summary>
        private static (double Ascent, double Descent) Extent(PdfDictionary? font)
        {
            PdfDictionary? descriptor = font?.Get<PdfDictionary>("FontDescriptor")
                ?? font?.Get<PdfArray>("DescendantFonts")?.Get<PdfDictionary>(0)?.Get<PdfDictionary>("FontDescriptor");
            double ascent = descriptor?.GetNumber("Ascent") is double a && a > 0 ? a / 1000 : 0.8;
            double descent = descriptor?.GetNumber("Descent") is double d && d < 0 ? d / 1000 : -0.2;
            return (ascent, descent);
        }
    }

    internal static bool Overlaps(PdfRectangle a, PdfRectangle b)
    {
        return a.Left < b.Right && b.Left < a.Right && a.Bottom < b.Top && b.Bottom < a.Top;
    }

    /// <summary>Whether at least <paramref name="share"/> of the box lies inside the area; 0 means any overlap at all.</summary>
    internal static bool Covers(PdfRectangle area, PdfRectangle box, double share)
    {
        if (!Overlaps(area, box))
        {
            return false;
        }

        double size = (box.Right - box.Left) * (box.Top - box.Bottom);
        if (share <= 0 || size <= 0)
        {
            return true;
        }

        double inside = (Math.Min(area.Right, box.Right) - Math.Max(area.Left, box.Left)) * (Math.Min(area.Top, box.Top) - Math.Max(area.Bottom, box.Bottom));
        return inside >= share * size;
    }

    internal static bool Contains(PdfRectangle outer, PdfRectangle inner)
    {
        return outer.Left <= inner.Left && outer.Bottom <= inner.Bottom && inner.Right <= outer.Right && inner.Top <= outer.Top;
    }
}

/// <summary>What a redaction run removed, counted as it goes.</summary>
internal sealed class RedactionCounters
{
    public int Glyphs { get; set; }

    public int Paths { get; set; }

    public int Images { get; set; }

    public int Forms { get; set; }

    public int Annotations { get; set; }
}
