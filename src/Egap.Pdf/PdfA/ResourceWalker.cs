namespace Egap.Pdf.PdfA;

/// <summary>
/// Every resource dictionary a document's drawing can reach (Phase 5 task 14): the pages', their form XObjects', tiling
/// patterns', Type 3 fonts', soft masks' groups', annotation appearances' and the form's default resources, each once.
/// The PDF/A writer rules and the pre-check look through them for fonts, graphics states and images.
/// </summary>
internal static class ResourceWalker
{
    private const int MaxDictionaries = 100_000;

    /// <summary>The resource dictionaries, and the content streams found on the way: forms, appearances, soft mask groups and tiling patterns.</summary>
    public static (List<PdfDictionary> Resources, List<PdfStream> Forms) Walk(PdfDocument document)
    {
        HashSet<PdfObject> seen = new(ReferenceEqualityComparer.Instance);
        List<PdfDictionary> resources = [];
        List<PdfStream> forms = [];
        Stack<PdfDictionary?> pending = [];

        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            pending.Push(page.Resources);
            foreach (PdfAnnotation annotation in page.Annotations)
            {
                Appearances(annotation.Dictionary.Get<PdfDictionary>("AP"), forms, pending, seen);
            }
        }

        pending.Push(document.Catalog.Get<PdfDictionary>("AcroForm")?.Get<PdfDictionary>("DR"));
        while (pending.TryPop(out PdfDictionary? dictionary) && resources.Count < MaxDictionaries)
        {
            if (dictionary is null || !seen.Add(dictionary))
            {
                continue;
            }

            resources.Add(dictionary);
            foreach (PdfObject value in Values(dictionary.Get<PdfDictionary>("XObject")))
            {
                if (value is PdfStream { Dictionary: var x } stream && x.GetName("Subtype")?.Value == "Form" && seen.Add(stream))
                {
                    forms.Add(stream);
                    pending.Push(x.Get<PdfDictionary>("Resources"));
                }
            }

            foreach (PdfObject value in Values(dictionary.Get<PdfDictionary>("Pattern")))
            {
                if (value is PdfStream pattern && seen.Add(pattern))
                {
                    forms.Add(pattern);
                    pending.Push(pattern.Dictionary.Get<PdfDictionary>("Resources"));
                }
            }

            foreach (PdfObject value in Values(dictionary.Get<PdfDictionary>("Font")))
            {
                if (value is PdfDictionary font && font.GetName("Subtype")?.Value == "Type3")
                {
                    pending.Push(font.Get<PdfDictionary>("Resources"));
                }
            }

            foreach (PdfObject value in Values(dictionary.Get<PdfDictionary>("ExtGState")))
            {
                if (value is PdfDictionary state && state.Get<PdfDictionary>("SMask")?.Get<PdfStream>("G") is PdfStream group && seen.Add(group))
                {
                    forms.Add(group);
                    pending.Push(group.Dictionary.Get<PdfDictionary>("Resources"));
                }
            }
        }

        return (resources, forms);
    }

    /// <summary>The fonts the resources name, each once.</summary>
    public static List<PdfDictionary> Fonts(IEnumerable<PdfDictionary> resources)
    {
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        return [.. resources.SelectMany(r => Values(r.Get<PdfDictionary>("Font"))).OfType<PdfDictionary>().Where(seen.Add)];
    }

    /// <summary>A dictionary's values, resolved.</summary>
    public static IEnumerable<PdfObject> Values(PdfDictionary? dictionary)
    {
        if (dictionary is null)
        {
            yield break;
        }

        foreach ((PdfName _, PdfObject value) in dictionary.Entries)
        {
            if (value.Resolve() is PdfObject resolved)
            {
                yield return resolved;
            }
        }
    }

    /// <summary>The appearance streams of an appearance dictionary (each state of a subdictionary too).</summary>
    private static void Appearances(PdfDictionary? appearance, List<PdfStream> forms, Stack<PdfDictionary?> pending, HashSet<PdfObject> seen)
    {
        foreach (PdfObject kind in Values(appearance))
        {
            IEnumerable<PdfObject> streams = kind is PdfDictionary states ? Values(states) : [kind];
            foreach (PdfObject value in streams)
            {
                if (value is PdfStream stream && seen.Add(stream))
                {
                    forms.Add(stream);
                    pending.Push(stream.Dictionary.Get<PdfDictionary>("Resources"));
                }
            }
        }
    }
}
