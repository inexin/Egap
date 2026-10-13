using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

using Egap.Pdf.Content;

namespace Egap.Pdf;

/// <summary>
/// Text, links and marked content on the PDF canvas (Phase 3 design Q15 and Q16). A glyph run is written as text operators
/// showing glyph ids through an embedded Identity-H subset, so the text stays selectable and searchable: the run's text and
/// clusters give each glyph's Unicode text, and a run whose glyphs cannot say their text one by one (a ligature's parts, a
/// glyph that stands for different text elsewhere) is marked with <c>/ActualText</c>. Synthetic bold strokes the glyphs as
/// well (render mode 2), synthetic italic slants the text matrix, and glyphs with transforms (text on a path) get a text
/// matrix each. Colour glyphs are drawn by Egami's own colour glyph drawing on this canvas; faces whose licence forbids
/// embedding are drawn as outlines with a warning.
/// </summary>
public sealed partial class PdfCanvas
{
    /// <summary>The synthetic oblique of Egami's fonts: tan(14°).</summary>
    private static readonly float SyntheticSkew = MathF.Tan(14 * MathF.PI / 180);

    private int markedContent;

    /// <inheritdoc/>
    protected override void OnDrawGlyphRun(GlyphRun run, Vector2 origin, Paint paint)
    {
        if (Forward(c => c.DrawGlyphRun(run, origin, paint)))
        {
            return;
        }

        Font font = run.Font;
        Typeface face = font.EffectiveTypeface;
        if (font.ColorGlyphs && face.ColorFormats != ColorGlyphFormats.None && HasColorGlyph(run, face))
        {
            // Colour glyphs are drawn by Egami as paths, gradients and images on this canvas; the text they stand for stays.
            MarkActualText(run.Text, () => base.OnDrawGlyphRun(run, origin, paint));
            return;
        }

        if (!EmbeddedFont.CanEmbed(face))
        {
            if (options.Strict)
            {
                throw new NotSupportedException($"The licence of {face.FullName} does not allow embedding it.");
            }

            Warn(PdfWarningCode.RasterFallback, $"The licence of {face.FullName} does not allow embedding it; its glyphs were drawn as outlines.");
            MarkActualText(run.Text, () => base.OnDrawGlyphRun(run, origin, paint));
            return;
        }

        if (paint.Blur is not null || !PdfBlendModes.ContainsKey(paint.BlendMode))
        {
            Unsupported(paint, c => c.DrawGlyphRun(run, origin, paint));
            return;
        }

        EmbeddedFont embedded = EmbeddedFont.For(Page.Document, face);
        string?[] texts = GlyphTexts(run);
        bool actual = run.Text is not null && NeedsActualText(run, texts);
        for (int i = 0; i < run.Count; i++)
        {
            actual |= !embedded.Use(run.Glyphs[i], actual ? null : texts[i]);
        }

        if (actual)
        {
            CloseGroup();
            content.BeginMarkedContent("Span", new PdfDictionary { ["ActualText"] = PdfString.FromText(run.Text!) });
        }

        WriteRun(run, origin, paint, embedded);
        if (actual)
        {
            CloseGroup();
            content.EndMarkedContent();
        }
    }

    /// <summary>Writes a run's glyphs as text in a <c>q</c> of the call's own, with the paint as fill (and stroke for synthetic bold).</summary>
    private void WriteRun(GlyphRun run, Vector2 origin, Paint paint, EmbeddedFont embedded)
    {
        Font font = run.Font;
        FontSynthesis synthesis = font.AllowSynthesis ? font.Synthesis : FontSynthesis.None;
        bool bold = (synthesis & FontSynthesis.Bold) != 0;
        float skew = (synthesis & FontSynthesis.Italic) != 0 ? SyntheticSkew : 0;
        GraphicsGroup g = OpenGroup();
        List<ShadingPiece>? shadings = null;
        content.Save();
        if (paint is SolidPaint solid)
        {
            RgbaF32 c = solid.Color;
            content.Rgb(Math.Clamp(c.R, 0, 1), Math.Clamp(c.G, 0, 1), Math.Clamp(c.B, 0, 1));
            if (bold)
            {
                content.Rgb(Math.Clamp(c.R, 0, 1), Math.Clamp(c.G, 0, 1), Math.Clamp(c.B, 0, 1), stroke: true);
            }

            SetAlpha(new GraphicsGroup(g.Matrix) { Alpha = g.Alpha, Blend = g.Blend }, Math.Clamp(c.A, 0, 1) * paint.Opacity, paint.BlendMode);
        }
        else
        {
            // Other paints: the glyphs become the clip (render mode 7) and the paint fills it, as Skia's PDF backend does.
            restoreAfterDraw = false;
            content.Restore();
            if (!BeginShaded(paint, null))
            {
                Unsupported(paint, c => c.DrawGlyphRun(run, origin, paint));
                return;
            }

            restoreAfterDraw = false;
            shadings = pendingShadings;
            pendingShadings = null;
        }

        float outset = bold ? BoldOutset(font.Size) : 0;
        if (bold)
        {
            content.LineWidth(2 * outset).LineJoin(1);
        }

        PdfName name = AddResource("Font", embedded.Reference);
        content.BeginText().Font(name, 1);
        int mode = shadings is not null ? 7 : bold ? 2 : 0;
        if (mode != 0)
        {
            content.TextRenderMode(mode);
        }

        WriteGlyphs(run, origin, embedded, skew);
        content.EndText();
        if (shadings is not null)
        {
            WriteShadings(content, shadings, reference => AddResource("Shading", reference));
        }

        content.Restore();
    }

    /// <summary>
    /// Shows the glyphs: a text matrix where a glyph does not follow on from the one before at its advance, in between one
    /// <c>TJ</c> per line with adjustments for kerning and spacing.
    /// </summary>
    private void WriteGlyphs(GlyphRun run, Vector2 origin, EmbeddedFont embedded, float skew)
    {
        float size = run.Font.Size;
        ReadOnlySpan<ushort> glyphs = run.Glyphs;
        ReadOnlySpan<Vector2> positions = run.Positions;
        ReadOnlySpan<GlyphTransform> transforms = run.Transforms;
        List<(byte[] Codes, double Adjustment)> pending = [];
        List<byte> codes = [];
        Vector2 pen = default;
        bool started = false;

        void Flush()
        {
            if (codes.Count > 0 || pending.Count > 0)
            {
                pending.Add(([.. codes], 0));
                codes.Clear();
                content.ShowText(pending, hex: true);
                pending.Clear();
            }
        }

        for (int i = 0; i < glyphs.Length; i++)
        {
            ushort glyph = glyphs[i];
            if (!transforms.IsEmpty)
            {
                // Each glyph its own text matrix: the glyph's transform after the em scale, the slant and the y flip.
                Flush();
                Matrix3x2 t = transforms[i].ToMatrix() * Matrix3x2.CreateTranslation(origin);
                PdfMatrix glyphMatrix = new PdfMatrix(size, 0, size * skew, -size, 0, 0) * new PdfMatrix(t.M11, t.M12, t.M21, t.M22, t.M31, t.M32);
                content.TextMatrix(glyphMatrix);
                content.ShowText([(byte)(glyph >> 8), (byte)glyph], hex: true);
                continue;
            }

            Vector2 at = origin + positions[i];
            double advance = embedded.Width(glyph) / 1000 * size;
            if (!started || Math.Abs(at.Y - pen.Y) > 1e-4)
            {
                Flush();
                content.TextMatrix(size, 0, size * skew, -size, at.X, at.Y);
                started = true;
            }
            else if (Math.Abs(at.X - pen.X) > 1e-4)
            {
                // A kern or spacing: TJ moves left by the number given, in thousandths of the font size.
                pending.Add(([.. codes], Math.Round(-(at.X - pen.X) / size * 1000, 2)));
                codes.Clear();
            }

            codes.Add((byte)(glyph >> 8));
            codes.Add((byte)glyph);
            pen = new Vector2((float)(at.X + advance), at.Y);
        }

        Flush();
    }

    /// <summary>The text each glyph stands for, from the run's clusters: a cluster's text goes with its first glyph.</summary>
    private static string?[] GlyphTexts(GlyphRun run)
    {
        string?[] texts = new string?[run.Count];
        string? text = run.Text;
        ReadOnlySpan<int> clusters = run.Clusters;
        if (text is null || clusters.Length != run.Count)
        {
            return texts;
        }

        int[] starts = [.. clusters.ToArray().Distinct().Order()];
        HashSet<int> seen = [];
        for (int i = 0; i < run.Count; i++)
        {
            int start = Math.Clamp(clusters[i], 0, text.Length);
            if (!seen.Add(start))
            {
                continue;
            }

            int next = starts.FirstOrDefault(s => s > start, text.Length);
            texts[i] = text[start..Math.Clamp(next, start, text.Length)];
        }

        return texts;
    }

    /// <summary>
    /// Whether a run's text cannot be told glyph by glyph: a cluster of several glyphs (only the first can carry the text),
    /// or glyphs not in text order, as in right-to-left runs.
    /// </summary>
    private static bool NeedsActualText(GlyphRun run, string?[] texts)
    {
        ReadOnlySpan<int> clusters = run.Clusters;
        if (clusters.Length != run.Count)
        {
            return true;
        }

        for (int i = 0; i < run.Count; i++)
        {
            if (texts[i] is null || (i > 0 && clusters[i] < clusters[i - 1]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasColorGlyph(GlyphRun run, Typeface face)
    {
        foreach (ushort glyph in run.Glyphs)
        {
            if (face.HasColorGlyph(glyph))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Egami's synthetic bold outset at a size: an em over 24 up to 9 units, over 32 from 36, straight between.</summary>
    private static float BoldOutset(float size)
    {
        float em = MathF.Abs(size);
        float factor = em <= 9 ? 1 / 24f : em >= 36 ? 1 / 32f : (1 / 24f) + (((1 / 32f) - (1 / 24f)) * (em - 9) / 27);
        return em * factor;
    }

    /// <summary>Wraps drawing in marked content carrying its actual text, when it has text.</summary>
    private void MarkActualText(string? text, Action draw)
    {
        if (text is null)
        {
            draw();
            return;
        }

        CloseGroup();
        content.BeginMarkedContent("Span", new PdfDictionary { ["ActualText"] = PdfString.FromText(text) });
        draw();
        CloseGroup();
        content.EndMarkedContent();
    }

    /// <inheritdoc/>
    protected override void OnBeginMarkedContent(MarkedContent content)
    {
        if (Forward(c => c.BeginMarkedContent(content)))
        {
            return;
        }

        CloseGroup();
        PdfDictionary properties = [];
        if (content.NodeId is int id)
        {
            properties["MCID"] = new PdfInteger(id);
        }

        if (content.ActualText is string actual)
        {
            properties["ActualText"] = PdfString.FromText(actual);
        }

        if (content.AlternateText is string alternate)
        {
            properties["Alt"] = PdfString.FromText(alternate);
        }

        PdfName tag = PdfName.Get(content.Tag);
        if (properties.Count == 0)
        {
            this.content.BeginMarkedContent(tag);
        }
        else
        {
            this.content.BeginMarkedContent(tag, properties);
        }

        markedContent++;
    }

    /// <inheritdoc/>
    protected override void OnEndMarkedContent()
    {
        if (Forward(c => c.EndMarkedContent()))
        {
            return;
        }

        CloseGroup();
        if (markedContent > 0)
        {
            markedContent--;
            content.EndMarkedContent();
        }
    }

    /// <inheritdoc/>
    protected override void OnDrawAnnotation(RectF area, Annotation annotation)
    {
        if (Forward(c => c.DrawAnnotation(area, annotation)))
        {
            return;
        }

        // The area as the page sees it: the smallest upright rectangle around its corners in default user space.
        PdfMatrix toPage = Pdf(Matrix) * baseMatrix;
        PdfRectangle rectangle = toPage.Transform(new PdfRectangle(area.Left, area.Top, area.Right, area.Bottom));
        PdfDocument document = Page.Document;
        switch (annotation.Kind)
        {
            case AnnotationKind.Url:
                Page.Annotations.Add(new PdfLinkAnnotation(rectangle, new PdfUriAction(annotation.Value)));
                break;
            case AnnotationKind.LinkToDestination:
                Page.Annotations.Add(new PdfLinkAnnotation(rectangle, new PdfGoToAction(annotation.Value)));
                break;
            case AnnotationKind.NamedDestination:
                // The destination is the area's top left corner, as the page shows it.
                document.NamedDestinations.Set(annotation.Value, PdfDestination.ToPage(Page, PdfDestinationFit.Xyz) with { Left = rectangle.Left, Top = rectangle.Top });
                break;
        }
    }
}
