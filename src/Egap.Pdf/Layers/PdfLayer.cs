namespace Egap.Pdf;

/// <summary>
/// An optional content group (8.11.2), a layer: a thin view over its dictionary. Its visibility and lock are read from and
/// written to the document's default configuration (<c>/OCProperties /D</c>). Two views are equal when they wrap the same
/// dictionary.
/// </summary>
public sealed class PdfLayer : IEquatable<PdfLayer>
{
    private readonly PdfDocument document;

    internal PdfLayer(PdfDocument document, PdfDictionary dictionary, PdfReference reference)
    {
        this.document = document;
        Dictionary = dictionary;
        Reference = reference;
    }

    /// <summary>The group's dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The reference to the group, which content and configurations use to name it.</summary>
    public PdfReference Reference { get; }

    /// <summary>The name shown in a viewer's layer panel (<c>/Name</c>).</summary>
    public string Name
    {
        get => Dictionary.GetText("Name") ?? "";
        set => Dictionary["Name"] = PdfString.FromText(value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>The intents (<c>/Intent</c>): <c>View</c> when missing, as the spec says, or <c>Design</c> and others.</summary>
    public IReadOnlyList<string> Intents => Dictionary.Get("Intent") switch
    {
        PdfName name => [name.Value],
        PdfArray array => [.. Enumerable.Range(0, array.Count).Select(array.GetName).OfType<PdfName>().Select(n => n.Value)],
        _ => ["View"],
    };

    /// <summary>
    /// Whether the layer is shown when the document opens (8.11.4.3): listed in the default configuration's <c>/ON</c> or
    /// <c>/OFF</c>, else its <c>/BaseState</c> (on unless <c>/OFF</c>). Setting it moves the layer between the two lists.
    /// </summary>
    public bool IsVisible
    {
        get
        {
            PdfDictionary? config = DefaultConfiguration(create: false);
            if (Contains(config?.Get<PdfArray>("OFF")))
            {
                return false;
            }

            return Contains(config?.Get<PdfArray>("ON")) || config?.GetName("BaseState")?.Value != "OFF";
        }

        set
        {
            PdfDictionary config = DefaultConfiguration(create: true)!;
            RemoveFrom(config.Get<PdfArray>("ON"));
            RemoveFrom(config.Get<PdfArray>("OFF"));
            bool baseOn = config.GetName("BaseState")?.Value != "OFF";
            if (value != baseOn)
            {
                Array(config, value ? "ON" : "OFF").Add(Reference);
            }
        }
    }

    /// <summary>Whether a viewer lets the reader change the layer's visibility: locked when in <c>/Locked</c>.</summary>
    public bool IsLocked
    {
        get => Contains(DefaultConfiguration(create: false)?.Get<PdfArray>("Locked"));
        set
        {
            PdfDictionary config = DefaultConfiguration(create: true)!;
            RemoveFrom(config.Get<PdfArray>("Locked"));
            if (value)
            {
                Array(config, "Locked").Add(Reference);
            }
        }
    }

    /// <summary>Whether both views wrap the same group dictionary.</summary>
    public bool Equals(PdfLayer? other)
    {
        return other is not null && ReferenceEquals(Dictionary, other.Dictionary);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfLayer);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Dictionary);
    }

    /// <summary>The name, for debugging.</summary>
    public override string ToString()
    {
        return Name;
    }

    /// <summary>Whether an array lists this group.</summary>
    internal bool Contains(PdfArray? array)
    {
        if (array is null)
        {
            return false;
        }

        for (int i = 0; i < array.Count; i++)
        {
            if (ReferenceEquals(array.Get(i), Dictionary))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Removes every mention of this group from an array, and from arrays nested in it (as in /Order).</summary>
    internal void RemoveFrom(PdfArray? array, int depth = 0)
    {
        if (array is null || depth > 64)
        {
            return;
        }

        for (int i = array.Count - 1; i >= 0; i--)
        {
            PdfObject item = array.Get(i);
            if (ReferenceEquals(item, Dictionary))
            {
                array.RemoveAt(i);
            }
            else if (item is PdfArray nested)
            {
                RemoveFrom(nested, depth + 1);
            }
        }
    }

    private PdfDictionary? DefaultConfiguration(bool create)
    {
        PdfDictionary? properties = document.Catalog.Get<PdfDictionary>("OCProperties");
        if (properties?.Get<PdfDictionary>("D") is PdfDictionary config)
        {
            return config;
        }

        if (!create || properties is null)
        {
            return null;
        }

        config = [];
        properties["D"] = config;
        return config;
    }

    private static PdfArray Array(PdfDictionary config, string key)
    {
        if (config.Get<PdfArray>(key) is PdfArray array)
        {
            return array;
        }

        array = [];
        config[key] = array;
        return array;
    }
}
