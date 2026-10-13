using System.Globalization;

using Egap.Pdf.Content;

namespace Egap.Pdf.Annotations;

/// <summary>
/// Draws annotations' appearances into page content and removes the annotations (Phase 2 Q13). Each appearance stays a
/// form XObject, added to the page's resources and drawn with the matrix that maps its transformed bounding box onto the
/// annotation's rectangle (12.5.5, algorithm 8.1); the page's own content is wrapped in <c>q</c>/<c>Q</c> first, so its
/// graphics state cannot leak into the drawings.
/// </summary>
internal static class Flattener
{
    /// <summary>
    /// Flattens annotations of one page. Hidden annotations and those without an appearance are removed without being
    /// drawn; so are popups of the annotations flattened.
    /// </summary>
    /// <returns>How many were drawn.</returns>
    public static int Flatten(PdfPage page, IReadOnlyCollection<PdfAnnotation> annotations, PdfFlattenFilter filter)
    {
        if (annotations.Count == 0)
        {
            return 0;
        }

        page = page.Document.Pages.EnsureTree(page);
        ContentWriter drawing = new();
        PdfDictionary? xObjects = null;
        int drawn = 0;
        foreach (PdfAnnotation annotation in annotations)
        {
            if (!Shown(annotation.Flags, filter) || annotation.GetAppearance() is not PdfStream appearance || annotation.Rectangle.IsEmpty)
            {
                continue;
            }

            PdfReference? reference = annotation.Dictionary.Get<PdfDictionary>("AP") is PdfDictionary ap ? AppearanceReference(ap, annotation.AppearanceState) : null;
            if (reference is null || Placement(appearance, annotation.Rectangle) is not double[] matrix)
            {
                continue;
            }

            xObjects ??= XObjects(page);
            PdfName name = FreeName(xObjects);
            xObjects[name] = reference;
            drawing.Save().Transform(matrix[0], matrix[1], matrix[2], matrix[3], matrix[4], matrix[5]).Draw(name).Restore();
            drawn++;
        }

        if (drawn > 0)
        {
            Wrap(page, drawing.ToArray());
        }

        HashSet<PdfDictionary> removed = new(annotations.Select(a => a.Dictionary), ReferenceEqualityComparer.Instance);
        foreach (PdfAnnotation annotation in annotations)
        {
            if (annotation.Dictionary.Get<PdfDictionary>("Popup") is PdfDictionary popup)
            {
                removed.Add(popup);
            }
        }

        if (page.Dictionary.Get<PdfArray>("Annots") is PdfArray annots)
        {
            for (int i = annots.Count - 1; i >= 0; i--)
            {
                if (annots.Get(i) is PdfDictionary dictionary && removed.Contains(dictionary))
                {
                    annots.RemoveAt(i);
                }
            }

            if (annots.Count == 0)
            {
                page.Dictionary.Remove("Annots");
            }
        }

        return drawn;
    }

    /// <summary>Whether a filter keeps an annotation with these flags: never hidden ones; for print, printed ones; for screen, viewed ones.</summary>
    public static bool Shown(PdfAnnotationFlags flags, PdfFlattenFilter filter)
    {
        if (flags.HasFlag(PdfAnnotationFlags.Hidden))
        {
            return false;
        }

        return filter switch
        {
            PdfFlattenFilter.Print => flags.HasFlag(PdfAnnotationFlags.Print),
            PdfFlattenFilter.Screen => !flags.HasFlag(PdfAnnotationFlags.NoView),
            _ => true,
        };
    }

    /// <summary>The reference to the normal appearance stream chosen by the appearance state, or null when it is not indirect.</summary>
    private static PdfReference? AppearanceReference(PdfDictionary appearances, string? state)
    {
        return appearances["N"] switch
        {
            PdfReference reference when reference.Resolve() is PdfStream => reference,
            PdfReference reference when reference.Resolve() is PdfDictionary states && state is not null => states[state] as PdfReference,
            PdfDictionary states when state is not null => states[state] as PdfReference,
            _ => null,
        };
    }

    /// <summary>
    /// The matrix that maps the appearance's bounding box, transformed by its own <c>/Matrix</c>, onto the rectangle
    /// (12.5.5, algorithm 8.1); null when the box is unusable or degenerate.
    /// </summary>
    private static double[]? Placement(PdfStream appearance, PdfRectangle rectangle)
    {
        if (PdfRectangle.FromArray(appearance.Dictionary.Get<PdfArray>("BBox"))?.Normalize() is not PdfRectangle box)
        {
            return null;
        }

        double[] m = [1, 0, 0, 1, 0, 0];
        if (appearance.Dictionary.Get<PdfArray>("Matrix") is { Count: 6 } matrix)
        {
            for (int i = 0; i < 6; i++)
            {
                m[i] = matrix.GetNumber(i) ?? m[i];
            }
        }

        (double X, double Y)[] corners =
        [
            (box.Left, box.Bottom), (box.Right, box.Bottom), (box.Left, box.Top), (box.Right, box.Top),
        ];
        double[] xs = [.. corners.Select(c => (m[0] * c.X) + (m[2] * c.Y) + m[4])];
        double[] ys = [.. corners.Select(c => (m[1] * c.X) + (m[3] * c.Y) + m[5])];
        double width = xs.Max() - xs.Min();
        double height = ys.Max() - ys.Min();
        if (!(width > 0) || !(height > 0) || !double.IsFinite(width) || !double.IsFinite(height))
        {
            return null;
        }

        double sx = rectangle.Width / width;
        double sy = rectangle.Height / height;
        return [sx, 0, 0, sy, rectangle.Left - (xs.Min() * sx), rectangle.Bottom - (ys.Min() * sy)];
    }

    /// <summary>The page's own <c>/Resources /XObject</c>, made when missing; inherited resources are copied onto the page first.</summary>
    private static PdfDictionary XObjects(PdfPage page)
    {
        if (page.Dictionary.Get<PdfDictionary>("Resources") is not PdfDictionary resources)
        {
            resources = page.Resources?.Clone() ?? [];
            page.Dictionary["Resources"] = resources;
        }

        if (resources.Get<PdfDictionary>("XObject") is not PdfDictionary xObjects)
        {
            xObjects = [];
            resources["XObject"] = xObjects;
        }

        return xObjects;
    }

    private static PdfName FreeName(PdfDictionary xObjects)
    {
        for (int i = 0; ; i++)
        {
            PdfName name = PdfName.Get(string.Create(CultureInfo.InvariantCulture, $"EgapFlat{i}"));
            if (xObjects.Get(name) is null)
            {
                return name;
            }
        }
    }

    /// <summary>Makes the page's content <c>q</c>, its old content, <c>Q</c> and then the drawings.</summary>
    private static void Wrap(PdfPage page, byte[] drawing)
    {
        PdfDocument document = page.Document;
        PdfStream open = new();
        open.SetDecodedBytes("q\n"u8);
        PdfStream close = new();
        close.SetDecodedBytes([.. "\nQ\n"u8, .. drawing]);
        PdfArray contents = [document.Objects.Add(open)];
        switch (page.Dictionary["Contents"])
        {
            case PdfReference reference when reference.Resolve() is PdfArray array:
                AddAll(contents, array);
                break;
            case PdfReference reference when reference.Resolve() is PdfStream:
                contents.Add(reference);
                break;
            case PdfArray array:
                AddAll(contents, array);
                break;
        }

        contents.Add(document.Objects.Add(close));
        page.Dictionary["Contents"] = contents;
    }

    private static void AddAll(PdfArray contents, PdfArray array)
    {
        for (int i = 0; i < array.Count; i++)
        {
            if (array[i] is PdfReference reference && reference.Resolve() is PdfStream)
            {
                contents.Add(reference);
            }
        }
    }
}
