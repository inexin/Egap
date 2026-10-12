using System.Text;

using Egap.Pdf.Trees;

namespace Egap.Pdf;

/// <summary>
/// A document's named destinations (12.3.2.4), found through both lookups: the <c>/Dests</c> name tree in the catalog's
/// <c>/Names</c> (PDF 1.2 and later) and the older <c>/Dests</c> dictionary in the catalog (PDF 1.1). A name in both is
/// taken from the name tree. New names go in the name tree, which is edited in place (Phase 2 Q3).
/// </summary>
public sealed class PdfNamedDestinations
{
    private readonly PdfDocument document;

    internal PdfNamedDestinations(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>The destination with a name; false when there is none, or what it names is not a destination.</summary>
    public bool TryGet(string name, out PdfDestination destination)
    {
        ArgumentNullException.ThrowIfNull(name);
        destination = null!;
        PdfObject? value = null;
        if (Tree() is NameTree tree)
        {
            foreach (PdfString key in Keys(name))
            {
                if (tree.TryGet(key, out PdfObject found))
                {
                    value = found;
                    break;
                }
            }
        }

        value ??= document.Catalog.Get<PdfDictionary>("Dests")?[PdfName.Get(name)];
        if (Parse(value) is PdfDestination parsed)
        {
            destination = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Every name, in the name tree's order and then the old dictionary's, each once.</summary>
    public IReadOnlyList<string> GetNames()
    {
        List<string> names = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        if (Tree() is NameTree tree)
        {
            foreach (KeyValuePair<PdfString, PdfObject> entry in tree.Entries())
            {
                string name = entry.Key.ToText();
                if (seen.Add(name))
                {
                    names.Add(name);
                }
            }
        }

        if (document.Catalog.Get<PdfDictionary>("Dests") is PdfDictionary dests)
        {
            foreach (PdfName key in dests.Keys)
            {
                if (seen.Add(key.Value))
                {
                    names.Add(key.Value);
                }
            }
        }

        return names;
    }

    /// <summary>Gives a name to a destination in the name tree, replacing any destination of that name.</summary>
    public void Set(string name, PdfDestination destination)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(destination);
        document.Catalog.Get<PdfDictionary>("Dests")?.Remove(PdfName.Get(name));
        NameTree tree = Tree() ?? Create();
        foreach (PdfString key in Keys(name).Skip(1))
        {
            tree.Remove(key);
        }

        tree.Set(Keys(name)[0], destination.ToArray());
    }

    /// <summary>Removes a named destination from both lookups.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool removed = document.Catalog.Get<PdfDictionary>("Dests")?.Remove(PdfName.Get(name)) ?? false;
        if (Tree() is NameTree tree)
        {
            foreach (PdfString key in Keys(name))
            {
                removed |= tree.Remove(key);
            }
        }

        return removed;
    }

    /// <summary>
    /// A destination from what a name, an action's <c>/D</c> or a link's <c>/Dest</c> holds: an array, a dictionary with
    /// <c>/D</c> (12.3.2.4), or a name or string naming another destination.
    /// </summary>
    internal PdfDestination? Resolve(PdfObject? value)
    {
        return value?.Resolve() switch
        {
            PdfName name => TryGet(name.Value, out PdfDestination named) ? named : null,
            PdfString text => TryGet(text.ToText(), out PdfDestination named) ? named : null,
            var other => Parse(other),
        };
    }

    /// <summary>An array, or a dictionary with an array in /D.</summary>
    private static PdfDestination? Parse(PdfObject? value)
    {
        return value?.Resolve() switch
        {
            PdfArray array => PdfDestination.FromArray(array),
            PdfDictionary dictionary => PdfDestination.FromArray(dictionary.Get<PdfArray>("D")),
            _ => null,
        };
    }

    /// <summary>
    /// The name tree keys a name may be stored under: as a text string, and as its UTF-8 bytes, which some writers use for
    /// names that are not PDFDocEncoding.
    /// </summary>
    private static List<PdfString> Keys(string name)
    {
        PdfString text = PdfString.FromText(name);
        PdfString utf8 = PdfString.FromBytes(Encoding.UTF8.GetBytes(name));
        return text.Equals(utf8) ? [text] : [text, utf8];
    }

    private NameTree? Tree()
    {
        return document.Catalog.Get<PdfDictionary>("Names")?.Get<PdfDictionary>("Dests") is PdfDictionary root ? new(root, document.Objects, document.Log) : null;
    }

    private NameTree Create()
    {
        if (document.Catalog.Get<PdfDictionary>("Names") is not PdfDictionary names)
        {
            names = [];
            document.Catalog["Names"] = names;
        }

        PdfDictionary root = new() { ["Names"] = new PdfArray() };
        names["Dests"] = document.Objects.Add(root);
        return new((PdfDictionary)document.Objects[(PdfReference)names["Dests"]!], document.Objects, document.Log);
    }
}
