using Egami.Colors;

namespace Egap.Pdf.PdfA;

/// <summary>
/// Rules the corpus found Egap did not check: halftone names and transfer functions, presentation steps, ICC profiles that
/// cannot be read, and Separation spaces of one name that disagree.
/// </summary>
internal sealed partial class PdfARules
{
    /// <summary>The colourants a halftone may not give its own transfer function for (ISO 32000-1 10.6.5.6): the primaries.</summary>
    private static readonly HashSet<string> PrimaryColorants = ["Cyan", "Magenta", "Yellow", "Black", "Red", "Green", "Blue", "Gray", "Default"];

    private void Gaps()
    {
        PresentationSteps();
        Profiles();
        SeparationNames();
    }

    /// <summary>
    /// A halftone (ISO 19005-2 6.2.5): no /HalftoneName, and a /TransferFunction only where ISO 32000-1 needs one, in a
    /// type 5 halftone's entry for a colourant that is not a primary. Both are removed; the device's own halftone is used.
    /// </summary>
    private void Halftone(PdfObject halftone, string? colorant, int depth)
    {
        PdfDictionary? dictionary = halftone switch
        {
            PdfStream stream => stream.Dictionary,
            PdfDictionary d => d,
            _ => null,
        };
        if (dictionary is null || depth > 2)
        {
            return;
        }

        if (dictionary.ContainsKey("HalftoneName"))
        {
            Issue("6.2.5", "a halftone names a device halftone (/HalftoneName); the name is removed", () => dictionary.Remove("HalftoneName"));
        }

        if (dictionary.ContainsKey("TransferFunction") && (colorant is null || PrimaryColorants.Contains(colorant)))
        {
            Issue("6.2.5", "a halftone sets a transfer function where PDF allows none; it is removed", () => dictionary.Remove("TransferFunction"));
        }

        if (dictionary.GetInt("HalftoneType") == 5)
        {
            foreach ((PdfName key, PdfObject value) in dictionary.Entries.ToList())
            {
                if (key.Value is not ("Type" or "HalftoneType" or "HalftoneName") && value.Resolve() is PdfObject inner)
                {
                    Halftone(inner, key.Value, depth + 1);
                }
            }
        }
    }

    /// <summary>Presentation steps (ISO 19005-2 6.10): a page's /PresSteps is removed; the page's content stays.</summary>
    private void PresentationSteps()
    {
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfDictionary page = document.Pages[i].Dictionary;
            if (page.ContainsKey("PresSteps"))
            {
                Issue("6.10", "a page has presentation steps (/PresSteps); they are removed", () => page.Remove("PresSteps"), i + 1);
            }
        }
    }

    /// <summary>
    /// ICCBased spaces (ISO 19005-2 6.2.4.2) whose profile Egami cannot read, so that viewers draw their alternate: the profile
    /// is replaced by the standard one with as many components (sRGB, gray with the sRGB curve, or Coated FOGRA39).
    /// </summary>
    private void Profiles()
    {
        List<(PdfStream Stream, int Components)> broken = [];
        HashSet<PdfStream> seen = new(ReferenceEqualityComparer.Instance);
        Arrays(array =>
        {
            if (array is { Count: 2 } && array.GetName(0)?.Value == "ICCBased")
            {
                Check(array);
            }
        });

        void Check(PdfArray space)
        {
            if (space.Get(1).Resolve() is not PdfStream stream || !seen.Add(stream))
            {
                return;
            }

            int n = (int)(stream.Dictionary.GetInt("N") ?? 0);
            if (n is not (1 or 3 or 4))
            {
                return;
            }

            try
            {
                ColorProfile profile = ColorProfile.FromIcc(stream.GetDecodedBytes());
                if (profile.ChannelCount == n)
                {
                    return;
                }
            }
            catch (Exception e) when (e is Egami.EgamiException or ArgumentException or InvalidDataException or PdfException and not PdfLimitException)
            {
            }

            broken.Add((stream, n));
        }

        if (broken.Count > 0)
        {
            Issue("6.2.4.2", $"{broken.Count} ICC profiles cannot be read; each is replaced by the standard profile with as many components", () =>
            {
                foreach ((PdfStream stream, int n) in broken)
                {
                    ColorProfile standard = n switch
                    {
                        1 => ColorProfile.GraySrgb,
                        3 => ColorProfile.Srgb,
                        _ => ColorProfile.Fogra39,
                    };
                    stream.Dictionary.Remove("DecodeParms");
                    stream.Dictionary.Remove("Range");
                    stream.SetDecodedBytes(standard.ToIcc());
                }
            });
        }
    }

    /// <summary>
    /// Separation spaces of one colourant name (ISO 19005-2 6.2.4.4) must agree on their alternate space and tint transform;
    /// spaces that disagree would print the same ink two ways, which only the author can settle.
    /// </summary>
    private void SeparationNames()
    {
        Dictionary<string, (PdfObject Alternate, PdfObject Transform)> first = [];
        HashSet<string> disagreeing = [];
        Arrays(array =>
        {
            if (array is { Count: 4 } && array.GetName(0)?.Value == "Separation" && array.GetName(1)?.Value is string name and not "None")
            {
                PdfObject alternate = array.Get(2), transform = array.Get(3);
                if (!first.TryGetValue(name, out (PdfObject Alternate, PdfObject Transform) known))
                {
                    first[name] = (alternate, transform);
                }
                else if (!Same(known.Alternate, alternate) || !Same(known.Transform, transform))
                {
                    disagreeing.Add(name);
                }
            }
        });

        if (disagreeing.Count > 0)
        {
            Issue("6.2.4.4", $"Separation spaces for {string.Join(", ", disagreeing.Take(5))} disagree on their alternate colour or tint transform");
        }
    }

    private static bool SameBytes(PdfStream p, PdfStream q)
    {
        try
        {
            return p.GetDecodedBytes().AsSpan().SequenceEqual(q.GetDecodedBytes());
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return false;
        }
    }

    /// <summary>Calls <paramref name="visit"/> with every array in the file, indirect or inside other objects, each once.</summary>
    private void Arrays(Action<PdfArray> visit)
    {
        HashSet<PdfObject> seen = new(ReferenceEqualityComparer.Instance);
        foreach (PdfReference reference in document.Objects)
        {
            Visit(document.Objects[reference], 0);
        }

        void Visit(PdfObject? value, int depth)
        {
            if (value is null || depth > MaxDepth || !seen.Add(value))
            {
                return;
            }

            switch (value)
            {
                case PdfArray array:
                    visit(array);
                    foreach (PdfObject item in array)
                    {
                        if (item is not PdfReference)
                        {
                            Visit(item, depth + 1);
                        }
                    }

                    break;
                case PdfStream stream:
                    Visit(stream.Dictionary, depth + 1);
                    break;
                case PdfDictionary dictionary:
                    foreach ((PdfName _, PdfObject item) in dictionary.Entries)
                    {
                        if (item is not PdfReference)
                        {
                            Visit(item, depth + 1);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>Whether two parts of colour spaces are the same: one object, or equal names, numbers and arrays of them.</summary>
    private static bool Same(PdfObject a, PdfObject b)
    {
        if (ReferenceEquals(a, b) || (a is PdfReference ra && b is PdfReference rb && ra.Equals(rb)))
        {
            return true;
        }

        PdfObject x = a.Resolve() ?? a, y = b.Resolve() ?? b;
        return ReferenceEquals(x, y) || (x, y) switch
        {
            (PdfName p, PdfName q) => p.Value == q.Value,
            (PdfArray p, PdfArray q) => p.Count == q.Count && Enumerable.Range(0, p.Count).All(i => Same(p[i], q[i])),
            (PdfStream p, PdfStream q) => p.Dictionary.ToString() == q.Dictionary.ToString() && SameBytes(p, q),
            (PdfDictionary p, PdfDictionary q) => p.ToString() == q.ToString(),
            _ => x.ToString() == y.ToString(),
        };
    }
}
