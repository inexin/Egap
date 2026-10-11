using System.Text;

using Egap.Pdf.Objects;

namespace Egap.Pdf;

/// <summary>
/// A PDF object (ISO 32000-2, 7.3): <see cref="PdfBoolean"/>, <see cref="PdfInteger"/>, <see cref="PdfReal"/>,
/// <see cref="PdfString"/>, <see cref="PdfName"/>, <see cref="PdfArray"/>, <see cref="PdfDictionary"/>,
/// <see cref="PdfStream"/>, <see cref="PdfNull"/> or a <see cref="PdfReference"/> to an indirect object.
/// </summary>
/// <remarks>
/// Booleans, numbers, strings, names, null and references are immutable and may be shared freely. Arrays, dictionaries and
/// streams are mutable and belong to one place: putting one that is already in a container into another throws, so use
/// <c>Clone()</c> for a copy, or make it an indirect object and store references to it.
/// </remarks>
public abstract class PdfObject
{
    private protected PdfObject()
    {
    }

    /// <summary>The object itself, or for a reference the object it points to (<see cref="PdfNull"/> if there is none).</summary>
    public virtual PdfObject Resolve()
    {
        return this;
    }

    /// <summary>The object in PDF syntax, for debugging. A stream shows its dictionary and the size of its data.</summary>
    public override string ToString()
    {
        return Encoding.Latin1.GetString(ObjectWriter.ToBytes(this, includeStreamData: false));
    }

    /// <summary>An integer object.</summary>
    public static implicit operator PdfObject(long value)
    {
        return new PdfInteger(value);
    }

    /// <summary>A real number object.</summary>
    public static implicit operator PdfObject(double value)
    {
        return new PdfReal(value);
    }

    /// <summary>A boolean object.</summary>
    public static implicit operator PdfObject(bool value)
    {
        return PdfBoolean.Get(value);
    }

    /// <summary>An integer object, the named alternative to the implicit conversion.</summary>
    public static PdfObject FromInt64(long value)
    {
        return new PdfInteger(value);
    }

    /// <summary>A real number object, the named alternative to the implicit conversion.</summary>
    public static PdfObject FromDouble(double value)
    {
        return new PdfReal(value);
    }

    /// <summary>A boolean object, the named alternative to the implicit conversion.</summary>
    public static PdfObject FromBoolean(bool value)
    {
        return PdfBoolean.Get(value);
    }
}
