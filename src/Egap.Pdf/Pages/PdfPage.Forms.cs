using System.Globalization;

using Egap.Pdf.Annotations;

namespace Egap.Pdf;

/// <summary>
/// A page drawn as a form XObject (Phase 7 design Q8): a whole page that another page can draw, scaled and placed, as
/// imposition and page stamps do. Its space is the page as a viewer shows it: points from the bottom left of the crop box,
/// rotation applied, <see cref="Width"/> by <see cref="Height"/>.
/// </summary>
public sealed class PdfFormXObject
{
    internal PdfFormXObject(PdfStream stream, PdfReference reference, double width, double height, IReadOnlyList<PdfWarning> warnings)
    {
        Stream = stream;
        Reference = reference;
        Width = width;
        Height = height;
        Warnings = warnings;
    }

    /// <summary>The form's stream in the target document.</summary>
    public PdfStream Stream { get; }

    /// <summary>The reference to the form, for a page's <c>/XObject</c> resources.</summary>
    public PdfReference Reference { get; }

    /// <summary>The width the page is shown at, in points.</summary>
    public double Width { get; }

    /// <summary>The height the page is shown at, in points.</summary>
    public double Height { get; }

    /// <summary>What the form leaves out: links, fields without appearances, annotations that do not print, the page's tags.</summary>
    public IReadOnlyList<PdfWarning> Warnings { get; }
}

/// <content>Pages as form XObjects (Phase 7 design Q8, Build Plan task 9).</content>
public sealed partial class PdfPage
{
    /// <summary>
    /// Makes the page a form XObject in <paramref name="target"/> (Phase 7 design Q8), for drawing it inside other pages: its
    /// content and resources, with its crop box as the bounding box and its rotation and user unit folded into the form's
    /// matrix, so the form shows the page as a viewer does. The appearances of annotations that print (stamps, filled
    /// fields, drawings) are drawn into it; links, fields without appearances, annotations that do not print and the page's
    /// tags are left out, each with a warning. Resources are copied once per source document and shared by every form made
    /// from it.
    /// </summary>
    /// <example>
    /// <code>
    /// PdfFormXObject form = source.Pages[0].ToFormXObject(target);
    /// using PdfCanvas canvas = new(target.Pages[0]);
    /// canvas.DrawForm(form, new RectF(36, 36, (float)form.Width / 2, (float)form.Height / 2));
    /// </code>
    /// </example>
    /// <param name="target">The document the form is made in; the page's own document works too.</param>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> is null.</exception>
    /// <exception cref="PdfException">The page's content is damaged and its document was opened in strict mode.</exception>
    public PdfFormXObject ToFormXObject(PdfDocument target)
    {
        ArgumentNullException.ThrowIfNull(target);
        bool same = ReferenceEquals(target, Document);
        Writing.ObjectCopier? copier = same ? null : target.CopierFor(Document);
        PdfObject Bring(PdfObject value) => copier is null ? value is PdfDictionary d ? d.Clone() : value is PdfArray a ? a.Clone() : value : copier.Copy(value);

        List<PdfWarning> warnings = [];
        PdfRectangle crop = CropBox.Normalize();
        (PdfMatrix matrix, double width, double height) = Displayed(crop, Rotate, UserUnit);

        PdfContentWriter content = new();
        content.Save();
        byte[] own = ContentBytes();
        PdfDictionary resources = Resources is PdfDictionary pageResources ? (PdfDictionary)Bring(pageResources) : [];
        PdfDictionary annotationForms = [];
        int printed = 0, links = 0, widgets = 0, hidden = 0;
        foreach (PdfAnnotation annotation in Annotations)
        {
            PdfAnnotationFlags flags = annotation.Flags;
            if (annotation.Subtype == "Link")
            {
                links++;
                continue;
            }

            if (annotation.Subtype == "Popup")
            {
                continue;
            }

            if (!flags.HasFlag(PdfAnnotationFlags.Print) || flags.HasFlag(PdfAnnotationFlags.Hidden))
            {
                hidden++;
                continue;
            }

            if (annotation.GetAppearance() is not PdfStream appearance || annotation.Rectangle.IsEmpty
                || Flattener.Placement(appearance, annotation.Rectangle) is not double[] m)
            {
                widgets += annotation.Subtype == "Widget" ? 1 : 0;
                hidden += annotation.Subtype == "Widget" ? 0 : 1;
                continue;
            }

            PdfReference reference = AppearanceReference(annotation, appearance, target, Bring);
            string name = string.Create(CultureInfo.InvariantCulture, $"EgAp{printed++}");
            annotationForms[name] = reference;
            content.Save().Transform(new PdfMatrix(m[0], m[1], m[2], m[3], m[4], m[5])).Draw(PdfName.Get(name)).Restore();
        }

        if (annotationForms.Entries.Any())
        {
            if (resources.Get<PdfDictionary>("XObject") is not PdfDictionary xobjects)
            {
                resources["XObject"] = xobjects = [];
            }

            foreach ((PdfName key, PdfObject value) in annotationForms.Entries.ToList())
            {
                annotationForms.Remove(key);
                xobjects[key] = value;
            }
        }

        if (links > 0)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.NotImported, $"{links} links were left out: a form XObject draws, it does not link."));
        }

        if (widgets > 0)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.NotImported, $"{widgets} form fields without an appearance were left out."));
        }

        if (hidden > 0)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.NotImported, $"{hidden} annotations that do not print, or have no appearance, were left out."));
        }

        if (Dictionary.Get("StructParents") is not null)
        {
            warnings.Add(new PdfWarning(PdfWarningCode.NotImported, "The page's tags were left out: a form XObject has no place in the structure tree."));
        }

        // The page's content first, in a q and Q of its own so its state cannot reach the appearances drawn over it.
        using MemoryStream data = new();
        data.Write("q\n"u8);
        data.Write(own);
        data.Write("\nQ\n"u8);
        data.Write(content.Restore().ToArray());

        PdfStream form = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfReal(crop.Left), new PdfReal(crop.Bottom), new PdfReal(crop.Right), new PdfReal(crop.Top) },
            ["Matrix"] = new PdfArray { new PdfReal(matrix.A), new PdfReal(matrix.B), new PdfReal(matrix.C), new PdfReal(matrix.D), new PdfReal(matrix.E), new PdfReal(matrix.F) },
            ["Resources"] = resources,
        });
        if (Dictionary.Get<PdfDictionary>("Group") is PdfDictionary group)
        {
            form.Dictionary["Group"] = Bring(group);
        }

        form.SetDecodedBytes(data.ToArray());
        return new PdfFormXObject(form, target.Objects.Add(form), width, height, warnings);
    }

    /// <summary>
    /// The matrix from page space to the page as shown (crop box at the origin, the rotation applied, the user unit as points),
    /// and the shown width and height.
    /// </summary>
    internal static (PdfMatrix Matrix, double Width, double Height) Displayed(PdfRectangle crop, int rotate, double unit)
    {
        double x0 = crop.Left, y0 = crop.Bottom, x1 = crop.Right, y1 = crop.Top;
        int quarter = ((rotate % 360) + 360) % 360;
        (PdfMatrix turn, double w, double h) = quarter switch
        {
            90 => (new PdfMatrix(0, -1, 1, 0, -y0, x1), crop.Height, crop.Width),
            180 => (new PdfMatrix(-1, 0, 0, -1, x1, y1), crop.Width, crop.Height),
            270 => (new PdfMatrix(0, 1, -1, 0, y1, -x0), crop.Height, crop.Width),
            _ => (new PdfMatrix(1, 0, 0, 1, -x0, -y0), crop.Width, crop.Height),
        };
        return unit == 1 ? (turn, w, h) : (turn * PdfMatrix.Scale(unit, unit), w * unit, h * unit);
    }

    /// <summary>The page's content streams' decoded bytes, joined by line breaks.</summary>
    private byte[] ContentBytes()
    {
        List<PdfStream> parts = Dictionary.Get("Contents")?.Resolve() switch
        {
            PdfStream single => [single],
            PdfArray array => [.. array.Select(p => p.Resolve()).OfType<PdfStream>()],
            _ => [],
        };
        using MemoryStream joined = new();
        foreach (PdfStream part in parts)
        {
            try
            {
                joined.Write(part.GetDecodedBytes());
            }
            catch (PdfException) when (!Document.Options.Strict)
            {
                // A stream that cannot be decoded draws nothing, as when the page is shown.
            }

            joined.WriteByte((byte)'\n');
        }

        return joined.ToArray();
    }

    /// <summary>The annotation's appearance in the target: the stream it holds by reference, or a copy of a direct one.</summary>
    private static PdfReference AppearanceReference(PdfAnnotation annotation, PdfStream appearance, PdfDocument target, Func<PdfObject, PdfObject> bring)
    {
        PdfDictionary? ap = annotation.Dictionary.Get<PdfDictionary>("AP");
        PdfObject? normal = ap?["N"];
        PdfObject? held = normal is PdfReference r ? r : normal?.Resolve() is PdfDictionary states && annotation.AppearanceState is string state ? states[state] : null;
        if (held is PdfReference reference)
        {
            return bring(reference) as PdfReference ?? target.Objects.Add(appearance.Clone());
        }

        return target.Objects.Add(bring(appearance.Clone()) is PdfStream copied ? copied : appearance.Clone());
    }
}
