namespace Egap.Pdf.Writing;

/// <summary>
/// Copies objects from one document into another (Phase 2 task 18), following references, with one map per source so an
/// object many pages share, such as a font or an image, is copied once (Q4). Streams keep their encoded data as the source
/// holds it, read only when the target is written, so the source must stay open until then. Pages are copied only through
/// <see cref="MapPage"/>: a reference to any other page, or to a page tree node, becomes null, so following a link or an
/// annotation's <c>/P</c> never pulls in the rest of the source.
/// </summary>
internal sealed class ObjectCopier
{
    private static readonly PdfName TypeKey = "Type";

    private readonly PdfDocument source;
    private readonly PdfDocument target;
    private readonly Dictionary<(int, int), PdfReference> map = [];
    private readonly Queue<(PdfReference From, PdfReference To)> pending = [];

    public ObjectCopier(PdfDocument source, PdfDocument target)
    {
        this.source = source;
        this.target = target;
    }

    /// <summary>The source document.</summary>
    public PdfDocument Source => source;

    /// <summary>Whether the source's attachments and outline were brought along already, which happens once per source.</summary>
    public bool ExtrasBrought { get; set; }

    /// <summary>Makes references to a source page lead to a target page; the first mapping of a page stays.</summary>
    public void MapPage(PdfReference sourcePage, PdfReference targetPage)
    {
        map.TryAdd((sourcePage.ObjectNumber, sourcePage.Generation), targetPage);
    }

    /// <summary>The target object for a source reference already copied or mapped, or null.</summary>
    public PdfReference? Mapped(PdfReference sourceReference)
    {
        return map.GetValueOrDefault((sourceReference.ObjectNumber, sourceReference.Generation));
    }

    /// <summary>
    /// A copy of a source value for the target: containers are copied, references lead to copies of their objects, made
    /// the first time and kept in the map; everything the copy reaches is copied before this returns.
    /// </summary>
    public PdfObject Copy(PdfObject value)
    {
        PdfObject copy = CopyDirect(value, 0);
        while (pending.TryDequeue(out (PdfReference From, PdfReference To) next))
        {
            // An object whose value is itself a reference is copied as the object it leads to.
            target.Objects.Set(next.To, Follow(source.Objects[next.From]) is PdfObject resolvedValue and not PdfNull ? CopyDirect(resolvedValue, 0) : PdfNull.Instance);
        }

        return copy;
    }

    /// <summary>Copies a value's direct parts; references are mapped or queued.</summary>
    private PdfObject CopyDirect(PdfObject value, int depth)
    {
        if (depth > 256 || !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new PdfLimitException(nameof(PdfReadOptions.MaxNestingDepth), "Objects nest too deep to copy.");
        }

        switch (value)
        {
            case PdfReference reference:
                return Reference(reference);
            case PdfArray array:
                PdfArray arrayCopy = [];
                for (int i = 0; i < array.Count; i++)
                {
                    arrayCopy.Add(CopyDirect(array[i], depth + 1));
                }

                return arrayCopy;
            case PdfDictionary dictionary:
                return CopyDictionary(dictionary, depth);
            case PdfStream stream:
                PdfStream streamCopy = new(CopyDictionary(stream.Dictionary, depth), stream.Data) { Origin = stream.Origin };
                return streamCopy;
            default:
                // Scalars are immutable and belong to no container, so they are shared.
                return value;
        }
    }

    /// <summary>A value with references followed to the object at the end; null for a loop or a chain longer than 32.</summary>
    private static PdfObject Follow(PdfObject value)
    {
        for (int hop = 0; hop < 32 && value is PdfReference reference; hop++)
        {
            value = reference.Resolve() ?? PdfNull.Instance;
        }

        return value is PdfReference ? PdfNull.Instance : value;
    }

    private PdfDictionary CopyDictionary(PdfDictionary dictionary, int depth)
    {
        PdfDictionary copy = [];
        foreach ((PdfName key, PdfObject item) in dictionary.Entries)
        {
            copy[key] = CopyDirect(item, depth + 1);
        }

        return copy;
    }

    /// <summary>The target reference for a source reference: mapped, or a new object queued for copying; null for a page or page tree node not mapped, and for a missing object.</summary>
    private PdfObject Reference(PdfReference reference)
    {
        if (Mapped(reference) is PdfReference mapped)
        {
            return mapped;
        }

        PdfObject resolved = Follow(source.Objects[reference]);
        if (resolved is PdfNull || (resolved is PdfDictionary dictionary && dictionary.GetName(TypeKey)?.Value is "Page" or "Pages"))
        {
            return PdfNull.Instance;
        }

        PdfReference made = target.Objects.Add(PdfNull.Instance);
        map[(reference.ObjectNumber, reference.Generation)] = made;
        pending.Enqueue((reference, made));
        return made;
    }
}
