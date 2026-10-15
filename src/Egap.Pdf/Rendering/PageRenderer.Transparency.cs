using Egami.Drawing;
using Egami.Pixels;

using Egap.Pdf.Images;

using BlendMode = Egami.Operations.BlendMode;

namespace Egap.Pdf.Rendering;

/// <summary>
/// Transparency (11, design Q9): blend modes and constant alpha on each paint; a transparency group XObject as an Egami
/// layer that is isolated or not and knockout or not, taking the group's alpha, blend mode and soft mask as a whole while its
/// content starts from the initial values; a soft mask in the graphics state as a layer around each object it masks, the
/// mask group drawn by a renderer of its own with its backdrop colour and transfer function. Blending is in sRGB whatever
/// the group's colour space says.
/// </summary>
internal sealed partial class PageRenderer
{
    /// <summary>Soft masks inside soft masks are drawn at most this deep, against masks that draw themselves.</summary>
    private const int MaxMaskDepth = 8;

    private readonly Stack<Group> groups = new();
    private PdfReadOptions? readOptions;
    private int maskDepth;

    /// <summary>
    /// Mirrors the clips and sets the matrix for one object, then opens a masked layer when the graphics state has a soft
    /// mask; returns the save count <see cref="End"/> restores to, or null when the matrix is degenerate.
    /// </summary>
    private int? Begin(PdfGraphicsState state, int skip, PdfMatrix matrix)
    {
        if (hidden > 0 || !SetUp(state.Clips, skip, matrix))
        {
            return null;
        }

        int restore = canvas.SaveCount;
        if (state.SoftMask is PdfDictionary mask && Mask(mask, state.SoftMaskMatrix) is SoftMask soft)
        {
            canvas.SaveLayer(new LayerOptions { Mask = soft });
        }

        return restore;
    }

    private void End(int restore)
    {
        if (canvas.SaveCount > restore)
        {
            canvas.RestoreToCount(restore);
        }
    }

    protected override bool OnBeginForm(PdfStream form, PdfName name)
    {
        // A form inside hidden optional content, or hidden itself, is not walked (8.11.3.3).
        if (hidden > 0 || settings.Layers?.IsVisible(form.Dictionary.Get<PdfDictionary>("OC")) == false)
        {
            return false;
        }

        // Patterns used in a form are in the form's space (8.7.3.1).
        if (form.Dictionary.Get<PdfDictionary>("Group") is not PdfDictionary group || group.GetName("S")?.Value != "Transparency")
        {
            patternSpaces.Push(State.Matrix);
            return true;
        }

        // The group composites as one object with the state's alpha, blend mode and soft mask (11.6.6); its own content
        // starts from alpha 1, Normal and no mask.
        PdfGraphicsState state = State;
        if (!SetUp(state.Clips, 0, state.Matrix))
        {
            return false;
        }

        SoftMask? soft = state.SoftMask is PdfDictionary mask ? Mask(mask, state.SoftMaskMatrix) : null;
        int before = canvas.SaveLayer(new LayerOptions
        {
            Isolated = group.GetBool("I") == true,
            Knockout = group.GetBool("K") == true,
            Opacity = (float)state.FillAlpha,
            BlendMode = BlendModeOf(state.BlendMode),
            Mask = soft,
        });
        groups.Push(new Group(form, before, clipFloor));
        patternSpaces.Push(state.Matrix);
        clipFloor = applied.Count;
        state.FillAlpha = state.StrokeAlpha = 1;
        state.BlendMode = PdfName.Get("Normal");
        state.SoftMask = null;
        return true;
    }

    protected override void OnEndForm(PdfStream form, PdfName name)
    {
        patternSpaces.TryPop(out _);
        if (groups.TryPeek(out Group? open) && ReferenceEquals(open.Form, form))
        {
            FlushText();
            groups.Pop();
            canvas.RestoreToCount(open.SaveCount);
            int keep = clipFloor;
            applied.RemoveRange(keep, applied.Count - keep);
            clipSaves.RemoveRange(keep, clipSaves.Count - keep);
            clipFloor = open.ClipFloor;
        }
    }

    /// <summary>
    /// A soft mask dictionary (11.6.5.2) as an Egami soft mask: its group drawn under the matrix the mask was set with, its
    /// alpha or its luminosity over the backdrop colour, through the transfer function; null when it cannot be used.
    /// </summary>
    private SoftMask? Mask(PdfDictionary mask, PdfMatrix matrix)
    {
        if (mask.Get("G")?.Resolve() is not PdfStream group || maskDepth >= MaxMaskDepth)
        {
            Warn(PdfWarningCode.MalformedContent, "A soft mask has no group to draw, or nests too deep; it was left out.");
            return null;
        }

        SoftMaskKind kind = mask.GetName("S")?.Value == "Luminosity" ? SoftMaskKind.Luminosity : SoftMaskKind.Alpha;
        RgbaF32? backdrop = null;
        if (kind == SoftMaskKind.Luminosity && mask.Get<PdfArray>("BC") is PdfArray bc)
        {
            PdfObject space = group.Dictionary.Get<PdfDictionary>("Group")?.Get("CS")?.Resolve() ?? PdfName.Get(bc.Count switch { 1 => "DeviceGray", 4 => "DeviceCMYK", _ => "DeviceRGB" });
            PdfColor color = new(space, [.. Enumerable.Range(0, bc.Count).Select(i => bc.GetNumber(i) ?? 0)]);
            backdrop = colors.ToRgb(color, group.Dictionary.Get<PdfDictionary>("Resources") ?? Resources, ColorContext.IntentOf(State.RenderingIntent));
        }

        Func<float, float>? transfer = null;
        if (mask.Get("TR")?.Resolve() is PdfObject tr and not PdfName)
        {
            try
            {
                FunctionTable table = PdfFunction.Read(tr).Sample(256, 1);
                transfer = v =>
                {
                    Span<double> output = stackalloc double[1];
                    table.Evaluate(v, output);
                    return (float)output[0];
                };
            }
            catch (InvalidDataException e)
            {
                Warn(PdfWarningCode.MalformedContent, $"A soft mask's transfer function could not be read: {e.Message}");
            }
        }

        PdfDictionary? resources = group.Dictionary.Get<PdfDictionary>("Resources") ?? Resources;
        return new SoftMask(c => DrawMaskGroup(c, group, matrix, resources), kind, backdrop, transfer);
    }

    /// <summary>
    /// Draws a soft mask's group on the mask's canvas (11.6.5.2): its content with the initial graphics state, under its own
    /// matrix and the one the mask was set with, clipped to its bounding box.
    /// </summary>
    private void DrawMaskGroup(Canvas target, PdfStream group, PdfMatrix matrix, PdfDictionary? resources)
    {
        PageRenderer inner = new(target, pageToDevice, settings, cache) { maskDepth = maskDepth + 1 };
        PdfContent content = [];
        if (PdfRectangle.FromArray(group.Dictionary.Get<PdfArray>("BBox")) is PdfRectangle box)
        {
            content.AddRange(PdfContent.Parse(System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"{box.Left} {box.Bottom} {box.Width} {box.Height} re W n"))));
        }

        try
        {
            content.AddRange(PdfContent.Read(group));
            PdfMatrix own = PdfMatrix.FromArray(group.Dictionary.Get<PdfArray>("Matrix")) ?? PdfMatrix.Identity;
            inner.Render(content, resources, own * matrix, readOptions);
        }
        catch (PdfException e) when (e is not PdfLimitException && readOptions?.Strict != true)
        {
            Warn(PdfWarningCode.MalformedContent, $"A soft mask's group could not be drawn: {e.Message}");
        }
    }

    /// <summary>A PDF blend mode (11.3.5) as Egami's; Normal is source-over.</summary>
    private static BlendMode BlendModeOf(PdfName mode)
    {
        return mode.Value switch
        {
            "Multiply" => BlendMode.Multiply,
            "Screen" => BlendMode.Screen,
            "Overlay" => BlendMode.Overlay,
            "Darken" => BlendMode.Darken,
            "Lighten" => BlendMode.Lighten,
            "ColorDodge" => BlendMode.ColorDodge,
            "ColorBurn" => BlendMode.ColorBurn,
            "HardLight" => BlendMode.HardLight,
            "SoftLight" => BlendMode.SoftLight,
            "Difference" => BlendMode.Difference,
            "Exclusion" => BlendMode.Exclusion,
            "Hue" => BlendMode.Hue,
            "Saturation" => BlendMode.Saturation,
            "Color" => BlendMode.Color,
            "Luminosity" => BlendMode.Luminosity,
            _ => BlendMode.SourceOver,
        };
    }

    private sealed record Group(PdfStream Form, int SaveCount, int ClipFloor);
}
