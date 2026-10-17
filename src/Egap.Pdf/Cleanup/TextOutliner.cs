using Egami.Fonts;

using Egap.Pdf.Rendering;

namespace Egap.Pdf.Cleanup;

/// <summary>
/// Text to outlines (Phase 7 design 7.5 and Q14, Build Plan task 19), as printers want fonts removed: every glyph a page
/// shows becomes its outline, filled, stroked or added to the clip as the text render mode says, with the colours and line
/// width the text had; Type 3 glyphs become their procedures drawn as form XObjects. The outlines come from the same
/// programs, or substitutes, the renderer draws with, so the page renders as before; afterwards text extraction finds no
/// text. Text positioning goes; text state, colours and everything else stay. Forms the page draws and its annotation
/// appearances are converted too. Glyphs whose font has no program and no substitute keep their text, each with a warning.
/// </summary>
internal sealed class TextOutliner
{
    private static readonly HashSet<string> Shows = ["Tj", "TJ", "'", "\""];
    private static readonly HashSet<string> Positioning = ["Td", "TD", "Tm", "T*"];

    private readonly PdfDocument document;
    private readonly HashSet<PdfObject> done = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(PdfDictionary Font, int Code), PdfReference> type3Forms = [];

    /// <summary>The text render mode each form is first drawn in, which its text inherits (text state is graphics state).</summary>
    private readonly Dictionary<PdfStream, int> formModes = new(ReferenceEqualityComparer.Instance);
    private int glyphs;

    private TextOutliner(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>Converts a page's text to outlines; how many glyphs were drawn as outlines.</summary>
    public static int Run(PdfPage page)
    {
        TextOutliner outliner = new(page.Document);
        PdfDictionary resources = page.Resources ?? [];
        try
        {
            if (outliner.Rewrite(page.GetContent(), resources, 0) is PdfContent content)
            {
                page.SetContent(content);
            }
        }
        catch (PdfException e) when (e is not PdfLimitException && !page.Document.Options.Strict)
        {
            // Content that cannot be read keeps its text: there is nothing to draw it from.
        }

        outliner.Forms(page.Resources);
        foreach (PdfAnnotation annotation in page.Annotations)
        {
            if (annotation.Dictionary.Get<PdfDictionary>("AP") is not PdfDictionary ap)
            {
                continue;
            }

            foreach (PdfObject value in ap.Entries.Select(e => e.Value.Resolve()))
            {
                IEnumerable<PdfStream> streams = value switch
                {
                    PdfStream single => [single],
                    PdfDictionary states => states.Entries.Select(s => s.Value.Resolve()).OfType<PdfStream>(),
                    _ => [],
                };
                foreach (PdfStream stream in streams)
                {
                    outliner.Form(stream, 0);
                }
            }
        }

        page.Document.ResetRenderCache();
        return outliner.glyphs;
    }

    private void Forms(PdfDictionary? resources)
    {
        if (resources is null || !done.Add(resources))
        {
            return;
        }

        foreach (PdfStream form in (resources.Get<PdfDictionary>("XObject")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfStream>().ToList())
        {
            if (form.Dictionary.GetName("Subtype")?.Value == "Form")
            {
                Form(form, formModes.GetValueOrDefault(form));
            }
        }

        foreach (PdfStream pattern in (resources.Get<PdfDictionary>("Pattern")?.Entries ?? []).Select(e => e.Value.Resolve()).OfType<PdfStream>().ToList())
        {
            Form(pattern, 0);
        }
    }

    /// <summary>A form's content, its text drawn as outlines; <paramref name="mode"/> is the text render mode its callers draw it in.</summary>
    private void Form(PdfStream form, int mode)
    {
        if (!done.Add(form))
        {
            return;
        }

        PdfDictionary resources = form.Dictionary.Get<PdfDictionary>("Resources") ?? [];
        PdfContent? content;
        try
        {
            content = Rewrite(PdfContent.Read(form), resources, mode);
        }
        catch (PdfException e) when (e is not PdfLimitException && !document.Options.Strict)
        {
            content = null;
        }

        if (content is not null)
        {
            form.Dictionary.Remove("DecodeParms");
            form.Dictionary["Filter"] = PdfName.Get("FlateDecode");
            form.SetDecodedBytes(content.ToBytes());
            if (form.Dictionary.ContainsKey("PatternType"))
            {
                PageRenderer.ForgetPattern(form);
            }
        }

        Forms(form.Dictionary.Get<PdfDictionary>("Resources"));
    }

    /// <summary>The content with its text drawn as outlines, or null when it shows no text.</summary>
    private PdfContent? Rewrite(PdfContent content, PdfDictionary resources, int inheritedMode)
    {
        Recorder recorder = new(inheritedMode);
        recorder.Walk(content, resources, PdfMatrix.Identity, document.Options);
        foreach ((PdfStream form, int mode) in recorder.FormModes)
        {
            formModes.TryAdd(form, mode);
        }

        if (recorder.Text.Count == 0)
        {
            return null;
        }

        PdfContent result = [];
        PdfPath clip = new();
        bool clipping = false;
        for (int i = 0; i < content.Count; i++)
        {
            PdfOperation op = content[i];
            switch (op.Operator)
            {
                case "BT":
                    clip = new PdfPath();
                    clipping = false;
                    continue;
                case "ET":
                    if (clipping)
                    {
                        Emit(result, clip, "n", clip: true);
                    }

                    continue;
                case var o when Positioning.Contains(o):
                    continue;
                case "BDC" when op.Operands.Count > 1 && op.Operands[1] is PdfDictionary { } properties && properties.ContainsKey("ActualText"):
                    PdfDictionary stripped = properties.Clone();
                    stripped.Remove("ActualText");
                    result.Add(new PdfOperation("BDC", [op.Operands[0], stripped]));
                    continue;
            }

            if (!Shows.Contains(op.Operator) || !recorder.Text.TryGetValue(i, out ShownText? shown))
            {
                result.Add(op);
                continue;
            }

            if (Outlines(shown, resources) is not (PdfPath path, List<(PdfReference Form, PdfMatrix Matrix)> forms))
            {
                // No program to draw from: the text stays, placed where it was.
                PdfMatrix at = op.Operator is "'" or "\"" ? shown.LineMatrix : shown.TextMatrix;
                result.Add(new PdfOperation("BT", []));
                result.Add(new PdfOperation("Tm", [new PdfReal(at.A), new PdfReal(at.B), new PdfReal(at.C), new PdfReal(at.D), new PdfReal(at.E), new PdfReal(at.F)]));
                result.Add(op);
                result.Add(new PdfOperation("ET", []));
                continue;
            }

            int mode = shown.Mode;
            if (mode is 0 or 4)
            {
                Emit(result, path, "f");
            }
            else if (mode is 1 or 5)
            {
                Emit(result, path, "S");
            }
            else if (mode is 2 or 6)
            {
                Emit(result, path, "B");
            }

            if (mode >= 4)
            {
                clipping = true;
                foreach (PdfPathSegment segment in path.Segments)
                {
                    _ = segment.Verb switch
                    {
                        PdfPathVerb.MoveTo => clip.MoveTo(segment.Point.X, segment.Point.Y),
                        PdfPathVerb.LineTo => clip.LineTo(segment.Point.X, segment.Point.Y),
                        PdfPathVerb.CurveTo => clip.CurveTo(segment.Control1.X, segment.Control1.Y, segment.Control2.X, segment.Control2.Y, segment.Point.X, segment.Point.Y),
                        _ => clip.Close(),
                    };
                }
            }

            foreach ((PdfReference form, PdfMatrix matrix) in forms)
            {
                PdfName name = Name(resources, form);
                result.Add(new PdfOperation("q", []));
                result.Add(new PdfOperation("cm", [new PdfReal(matrix.A), new PdfReal(matrix.B), new PdfReal(matrix.C), new PdfReal(matrix.D), new PdfReal(matrix.E), new PdfReal(matrix.F)]));
                result.Add(new PdfOperation("Do", [name]));
                result.Add(new PdfOperation("Q", []));
            }
        }

        return result;
    }

    private static void Emit(PdfContent result, PdfPath path, string paint, bool clip = false)
    {
        if (path.IsEmpty)
        {
            return;
        }

        PdfContentWriter writer = new();
        writer.Path(path);
        if (clip)
        {
            writer.Clip();
        }

        byte[] ops = writer.ToArray();
        foreach (PdfOperation op in PdfContent.Parse(ops))
        {
            result.Add(op);
        }

        result.Add(new PdfOperation(paint, []));
    }

    /// <summary>A shown string's glyph outlines in the content's user space, and its Type 3 glyphs as forms; null without a program.</summary>
    private (PdfPath, List<(PdfReference, PdfMatrix)>)? Outlines(ShownText shown, PdfDictionary resources)
    {
        PdfFont font = PdfFont.FromDictionary(shown.Font);
        if (!shown.Ctm.TryInvert(out PdfMatrix toUser))
        {
            return (new PdfPath(), []);
        }

        PdfPath path = new();
        List<(PdfReference, PdfMatrix)> forms = [];
        if (font.Type == PdfFontType.Type3)
        {
            foreach (PdfGlyph glyph in shown.Glyphs)
            {
                if (Type3Form(shown.Font, glyph.Code, resources) is PdfReference form)
                {
                    forms.Add((form, font.FontMatrix * glyph.Matrix * toUser));
                    glyphs++;
                }
            }

            return (path, forms);
        }

        Typeface? face = font.GetTypeface();
        Func<int, ushort> glyphOf;
        if (face is not null)
        {
            glyphOf = code => font.GetGlyphId(code) ?? 0;
        }
        else if (document.RenderCache.Substitute(font, systemFonts: true) is Fonts.SubstituteFont substitute)
        {
            face = substitute.Program.Typeface;
            glyphOf = code => font.GetSubstituteGlyph(substitute.Program, code);
        }
        else
        {
            return null;
        }

        foreach (PdfGlyph glyph in shown.Glyphs)
        {
            if (PageRenderer.Outline(face, glyphOf(glyph.Code)) is Egami.Drawing.Path outline)
            {
                PageRenderer.Append(path, outline, PageRenderer.ToMatrix(glyph.Matrix * toUser));
            }

            glyphs++;
        }

        return (path, forms);
    }

    /// <summary>A Type 3 glyph's procedure as a form XObject, made once per font and code.</summary>
    private PdfReference? Type3Form(PdfDictionary font, int code, PdfDictionary pageResources)
    {
        if (type3Forms.TryGetValue((font, code), out PdfReference? made))
        {
            return made;
        }

        PdfFont reader = PdfFont.FromDictionary(font);
        if (reader.GetGlyphName(code) is not string name || font.Get<PdfDictionary>("CharProcs")?.Get(name)?.Resolve() is not PdfStream procedure)
        {
            return null;
        }

        // d0 and d1 belong to a glyph procedure only; the form draws what follows them.
        PdfContent drawing = [.. PdfContent.Read(procedure).Where(op => op.Operator is not ("d0" or "d1"))];
        PdfRectangle box = reader.BoundingBox;
        PdfStream form = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfReal(box.Left), new PdfReal(box.Bottom), new PdfReal(box.Right), new PdfReal(box.Top) },
        });
        if ((font.Get<PdfDictionary>("Resources") ?? pageResources) is PdfDictionary own)
        {
            form.Dictionary["Resources"] = own.Clone();
        }

        if (box.IsEmpty)
        {
            form.Dictionary["BBox"] = new PdfArray { new PdfInteger(-100000), new PdfInteger(-100000), new PdfInteger(100000), new PdfInteger(100000) };
        }

        form.SetDecodedBytes(drawing.ToBytes());
        made = document.Objects.Add(form);
        type3Forms[(font, code)] = made;
        return made;
    }

    private static PdfName Name(PdfDictionary resources, PdfReference form)
    {
        if (resources.Get<PdfDictionary>("XObject") is not PdfDictionary xobjects)
        {
            resources["XObject"] = xobjects = [];
        }

        foreach ((PdfName key, PdfObject value) in xobjects.Entries)
        {
            if (value.Equals(form))
            {
                return key;
            }
        }

        PdfName name = Pages.PageDrawing.FreeName(xobjects, "XObject");
        xobjects[name] = form;
        return name;
    }

    /// <summary>What one text-showing operation showed, and the state it showed it in.</summary>
    private sealed record ShownText(PdfDictionary Font, int Mode, PdfMatrix Ctm, PdfMatrix TextMatrix, PdfMatrix LineMatrix, List<PdfGlyph> Glyphs);

    /// <summary>
    /// A walker that records each text operation's glyphs by its index in the content, without entering forms; until the
    /// content sets <c>Tr</c>, text takes the mode it inherits.
    /// </summary>
    private sealed class Recorder(int inheritedMode) : PdfContentWalker
    {
        private int index = -1;
        private bool modeSet;

        public Dictionary<int, ShownText> Text { get; } = [];

        /// <summary>The text render mode each form is drawn in.</summary>
        public Dictionary<PdfStream, int> FormModes { get; } = new(ReferenceEqualityComparer.Instance);

        private int Mode => modeSet ? State.TextRenderMode : inheritedMode;

        protected override void OnOperation(PdfOperation operation)
        {
            if (Depth == 0)
            {
                index++;
                modeSet |= operation.Operator == "Tr";
            }
        }

        protected override void OnGlyph(PdfGlyph glyph)
        {
            if (Depth != 0 || State.Font is not PdfDictionary font)
            {
                return;
            }

            if (!Text.TryGetValue(index, out ShownText? shown))
            {
                Text[index] = shown = new ShownText(font, Mode, State.Matrix, TextMatrix, TextLineMatrix, []);
            }

            shown.Glyphs.Add(glyph);
        }

        protected override bool OnBeginForm(PdfStream form, PdfName name)
        {
            FormModes.TryAdd(form, Mode);
            return false;
        }

        protected override bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure) => false;
    }
}
