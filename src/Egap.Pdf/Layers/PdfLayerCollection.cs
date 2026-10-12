using System.Collections;

namespace Egap.Pdf;

/// <summary>
/// The document's optional content groups (8.11), its layers, from the catalog's <c>/OCProperties /OCGs</c>, each once in
/// the order listed. Removing a layer takes it out of the configurations only; the content it marks stays on the pages
/// until content stream editing (Phase 3.1) can remove it.
/// </summary>
public sealed class PdfLayerCollection : IReadOnlyList<PdfLayer>
{
    private readonly PdfDocument document;

    internal PdfLayerCollection(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>The number of layers.</summary>
    public int Count => List().Count;

    /// <summary>The layer at an index, in <c>/OCGs</c> order.</summary>
    public PdfLayer this[int index] => List()[index];

    /// <summary>
    /// Removes a layer from <c>/OCGs</c> and from every configuration, the default and the alternates in <c>/Configs</c>:
    /// their <c>/ON</c>, <c>/OFF</c>, <c>/Order</c>, <c>/Locked</c>, <c>/RBGroups</c> and <c>/AS</c> lists. A viewer then no
    /// longer lists it; content marked with it is shown or hidden as content of no layer would be.
    /// </summary>
    /// <returns>Whether it was one of the document's layers.</returns>
    public bool Remove(PdfLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (!List().Contains(layer) || document.Catalog.Get<PdfDictionary>("OCProperties") is not PdfDictionary properties)
        {
            return false;
        }

        layer.RemoveFrom(properties.Get<PdfArray>("OCGs"));
        List<PdfDictionary> configurations = [];
        if (properties.Get<PdfDictionary>("D") is PdfDictionary defaults)
        {
            configurations.Add(defaults);
        }

        if (properties.Get<PdfArray>("Configs") is PdfArray configs)
        {
            configurations.AddRange(Enumerable.Range(0, configs.Count).Select(configs.Get<PdfDictionary>).OfType<PdfDictionary>());
        }

        foreach (PdfDictionary configuration in configurations)
        {
            foreach (string key in new[] { "ON", "OFF", "Order", "Locked", "RBGroups" })
            {
                layer.RemoveFrom(configuration.Get<PdfArray>(key));
            }

            if (configuration.Get<PdfArray>("AS") is PdfArray usages)
            {
                for (int i = 0; i < usages.Count; i++)
                {
                    layer.RemoveFrom(usages.Get<PdfDictionary>(i)?.Get<PdfArray>("OCGs"));
                }
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public IEnumerator<PdfLayer> GetEnumerator()
    {
        return List().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private List<PdfLayer> List()
    {
        List<PdfLayer> layers = [];
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        if (document.Catalog.Get<PdfDictionary>("OCProperties")?.Get<PdfArray>("OCGs") is PdfArray groups)
        {
            for (int i = 0; i < groups.Count; i++)
            {
                if (groups[i] is PdfReference reference && reference.Resolve() is PdfDictionary group && seen.Add(group))
                {
                    layers.Add(new PdfLayer(document, group, reference));
                }
            }
        }

        return layers;
    }
}
