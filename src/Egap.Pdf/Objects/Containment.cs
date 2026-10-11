namespace Egap.Pdf.Objects;

/// <summary>An array, dictionary or stream: a mutable object that belongs to exactly one place.</summary>
internal interface IContainer
{
    /// <summary>The container holding this one, or the <see cref="IObjectOwner"/> of the indirect object it is, or null.</summary>
    object? Parent { get; set; }
}

/// <summary>The indirect object an object tree belongs to, told when anything in the tree changes.</summary>
internal interface IObjectOwner
{
    void OnChanged();
}

/// <summary>The one-place rule for containers (design Q3) and change tracking (hub Q3).</summary>
internal static class Containment
{
    /// <summary>
    /// Takes <paramref name="value"/> into <paramref name="container"/>. A container already in another place, or one that
    /// would contain itself, throws; streams must be indirect objects.
    /// </summary>
    public static void Adopt(object container, PdfObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is PdfStream)
        {
            throw new ArgumentException("A stream must be an indirect object: add it to the document's objects and store the reference instead.", nameof(value));
        }

        if (value is not IContainer contained)
        {
            return;
        }

        if (contained.Parent is not null)
        {
            throw new InvalidOperationException(
                "This array or dictionary is already in another container or is an indirect object. Use Clone() to put a copy here, or add it to the document's objects and store the reference.");
        }

        for (object? at = container; at is IContainer c; at = c.Parent)
        {
            if (ReferenceEquals(at, value))
            {
                throw new InvalidOperationException("An array or dictionary cannot contain itself.");
            }
        }

        contained.Parent = container;
    }

    /// <summary>Lets go of a value removed from a container, so it can be put somewhere else.</summary>
    public static void Release(PdfObject? value)
    {
        if (value is IContainer contained)
        {
            contained.Parent = null;
        }
    }

    /// <summary>Makes <paramref name="root"/> the top object of an indirect object owned by <paramref name="owner"/>.</summary>
    public static void Bind(PdfObject root, IObjectOwner owner)
    {
        if (root is IContainer contained)
        {
            if (contained.Parent is not null && !ReferenceEquals(contained.Parent, owner))
            {
                throw new InvalidOperationException(
                    "This array, dictionary or stream is already in a container or is another indirect object. Use Clone() to add a copy.");
            }

            contained.Parent = owner;
        }
    }

    /// <summary>Tells the owning indirect object, if any, that something in its tree changed.</summary>
    public static void Changed(IContainer container)
    {
        object? at = container;
        while (at is IContainer c)
        {
            at = c.Parent;
        }

        (at as IObjectOwner)?.OnChanged();
    }
}
