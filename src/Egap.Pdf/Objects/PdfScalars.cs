using System.Globalization;

namespace Egap.Pdf;

/// <summary>The PDF null object. There is one instance, <see cref="Instance"/>.</summary>
public sealed class PdfNull : PdfObject
{
    private PdfNull()
    {
    }

    /// <summary>The null object.</summary>
    public static PdfNull Instance { get; } = new();
}

/// <summary>A PDF boolean. There are two instances, <see cref="True"/> and <see cref="False"/>.</summary>
public sealed class PdfBoolean : PdfObject
{
    private PdfBoolean(bool value)
    {
        Value = value;
    }

    /// <summary>The <c>true</c> object.</summary>
    public static PdfBoolean True { get; } = new(true);

    /// <summary>The <c>false</c> object.</summary>
    public static PdfBoolean False { get; } = new(false);

    /// <summary>The value.</summary>
    public bool Value { get; }

    /// <summary>The object for a value.</summary>
    public static PdfBoolean Get(bool value)
    {
        return value ? True : False;
    }
}

/// <summary>A PDF integer, held as 64 bits.</summary>
/// <param name="value">The value.</param>
public sealed class PdfInteger(long value) : PdfObject, IEquatable<PdfInteger>
{
    /// <summary>The value.</summary>
    public long Value { get; } = value;

    /// <inheritdoc/>
    public bool Equals(PdfInteger? other)
    {
        return other is not null && other.Value == Value;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfInteger);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }
}

/// <summary>A PDF real number, held as a <see cref="double"/>.</summary>
/// <remarks>A real read from a file keeps its text, so an unchanged real writes back exactly as it was.</remarks>
public sealed class PdfReal : PdfObject, IEquatable<PdfReal>
{
    /// <summary>A real number object.</summary>
    /// <param name="value">The value, which must be finite: PDF has no infinities or NaN.</param>
    public PdfReal(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "PDF real numbers must be finite.");
        }

        Value = value;
    }

    internal PdfReal(double value, byte[]? text)
        : this(value)
    {
        Text = text;
    }

    /// <summary>The value.</summary>
    public double Value { get; }

    /// <summary>The number as written in the file, when it was read from one and was well formed.</summary>
    internal byte[]? Text { get; }

    /// <inheritdoc/>
    public bool Equals(PdfReal? other)
    {
        return other is not null && other.Value.Equals(Value);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfReal);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return Value.GetHashCode();
    }
}

/// <summary>A reference to an indirect object: its object number and generation (<c>12 0 R</c>).</summary>
public sealed class PdfReference : PdfObject, IEquatable<PdfReference>
{
    /// <summary>A reference to an object.</summary>
    /// <param name="objectNumber">The object number, which must be positive.</param>
    /// <param name="generation">The generation number, 0 to 65,535.</param>
    public PdfReference(int objectNumber, int generation)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectNumber);
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, 65535);
        ObjectNumber = objectNumber;
        Generation = generation;
    }

    internal PdfReference(int objectNumber, int generation, IObjectResolver? resolver)
        : this(objectNumber, generation)
    {
        Resolver = resolver;
    }

    /// <summary>The object number.</summary>
    public int ObjectNumber { get; }

    /// <summary>The generation number.</summary>
    public int Generation { get; }

    /// <summary>The document that resolves the reference, when it came from one.</summary>
    internal IObjectResolver? Resolver { get; }

    /// <summary>The object referred to, loaded on first use; <see cref="PdfNull"/> when it does not exist (7.3.10).</summary>
    public override PdfObject Resolve()
    {
        return Resolver?.Resolve(this) ?? PdfNull.Instance;
    }

    /// <inheritdoc/>
    public bool Equals(PdfReference? other)
    {
        return other is not null && other.ObjectNumber == ObjectNumber && other.Generation == Generation;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfReference);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return HashCode.Combine(ObjectNumber, Generation);
    }

    /// <summary>The reference in PDF syntax, such as <c>12 0 R</c>.</summary>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{ObjectNumber} {Generation} R");
    }
}

/// <summary>Resolves references to the objects of one document.</summary>
internal interface IObjectResolver
{
    PdfObject Resolve(PdfReference reference);

    /// <summary>
    /// Told of every reference the parser reads, so new objects never take a number a reference already names, not even a
    /// reference to an object that does not exist.
    /// </summary>
    void Saw(int objectNumber)
    {
    }
}
