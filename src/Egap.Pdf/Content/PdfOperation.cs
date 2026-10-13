using System.Text;

using Egap.Pdf.Content;
using Egap.Pdf.IO;
using Egap.Pdf.Objects;

namespace Egap.Pdf;

/// <summary>
/// One operation of a content stream (8.2): an operator and its operands, such as <c>1 0 0 1 72 720 cm</c>. Operations are
/// values: they are never changed after they are made, so an edited content stream replaces operations instead of changing
/// them, and the operand objects (arrays and dictionaries included) should be treated as read-only.
/// </summary>
/// <remarks>
/// Operators the spec does not define are kept as they are, so content round-trips whatever it holds (Phase 3 design Q3).
/// Operands are PDF objects: numbers, names, strings, booleans, null, and arrays and dictionaries of those.
/// </remarks>
public class PdfOperation
{
    private readonly PdfObject[] operands;

    /// <summary>An operation with the given operator and operands.</summary>
    /// <param name="operator">The operator, such as <c>cm</c> or <c>Tj</c>: one or more regular characters (7.2.3).</param>
    /// <param name="operands">The operands, in the order they are written. References and streams are not allowed.</param>
    /// <exception cref="ArgumentException">The operator is empty or holds white space or delimiters, or an operand is a reference or stream.</exception>
    public PdfOperation(string @operator, params IEnumerable<PdfObject> operands)
    {
        ArgumentNullException.ThrowIfNull(@operator);
        ArgumentNullException.ThrowIfNull(operands);
        if (@operator.Length == 0 || @operator.Any(c => c > 0x7E || !Parsing.CharClass.IsRegular(c)))
        {
            throw new ArgumentException($"'{@operator}' is not an operator: it must be one or more regular ASCII characters.", nameof(@operator));
        }

        PdfObject[] items = [.. operands];
        foreach (PdfObject operand in items)
        {
            Check(operand);
        }

        Operator = Operators.TryGet(Encoding.ASCII.GetBytes(@operator), out string interned, out _) ? interned : @operator;
        this.operands = items;
    }

    /// <summary>An operation read from content, with an operator already interned and operands already checked.</summary>
    internal PdfOperation(string @operator, PdfObject[] operands, bool trusted)
    {
        _ = trusted;
        Operator = @operator;
        this.operands = operands;
    }

    /// <summary>The operator, such as <c>cm</c>.</summary>
    public string Operator { get; }

    /// <summary>The operands, in the order they are written.</summary>
    public IReadOnlyList<PdfObject> Operands => operands;

    /// <summary>Whether ISO 32000-2 defines the operator (table A.1).</summary>
    public bool IsKnown => Operators.OperandCount(Operator) is not null;

    internal PdfObject[] OperandArray => operands;

    /// <summary>The operand at <paramref name="index"/> as a number, or <see langword="null"/> when it is missing or not a number.</summary>
    public double? GetNumber(int index)
    {
        return (uint)index < (uint)operands.Length ? ObjectValues.Number(operands[index]) : null;
    }

    /// <summary>The operand at <paramref name="index"/> as a name, or <see langword="null"/> when it is missing or not a name.</summary>
    public PdfName? GetName(int index)
    {
        return (uint)index < (uint)operands.Length ? operands[index] as PdfName : null;
    }

    /// <summary>The operand at <paramref name="index"/> as a string, or <see langword="null"/> when it is missing or not a string.</summary>
    public PdfString? GetString(int index)
    {
        return (uint)index < (uint)operands.Length ? operands[index] as PdfString : null;
    }

    /// <summary>
    /// The operands of <c>cm</c> or <c>Tm</c> as a matrix: the first six operands when they are all numbers, else
    /// <see langword="null"/>.
    /// </summary>
    public PdfMatrix? GetMatrix()
    {
        if (operands.Length < 6)
        {
            return null;
        }

        Span<double> values = stackalloc double[6];
        for (int i = 0; i < 6; i++)
        {
            if (ObjectValues.Number(operands[i]) is not double value)
            {
                return null;
            }

            values[i] = value;
        }

        return new PdfMatrix(values[0], values[1], values[2], values[3], values[4], values[5]);
    }

    /// <summary>The operation as content syntax, such as <c>1 0 0 1 72 720 cm</c>.</summary>
    public override string ToString()
    {
        ByteWriter output = new();
        ContentSyntax.Write(this, output);
        return Encoding.Latin1.GetString(output.ToArray()).TrimEnd('\n');
    }

    private static void Check(PdfObject operand)
    {
        switch (operand)
        {
            case null:
                throw new ArgumentException("An operand is null; use PdfNull.Instance.");
            case PdfReference or PdfStream:
                throw new ArgumentException("References and streams cannot be content operands.");
            case PdfArray array:
                foreach (PdfObject item in array)
                {
                    Check(item);
                }

                break;
            case PdfDictionary dictionary:
                foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary)
                {
                    Check(entry.Value);
                }

                break;
        }
    }
}

/// <summary>
/// An inline image (8.9.7): <c>BI</c>, its parameters, <c>ID</c>, its data and <c>EI</c>, kept as one operation with the
/// data as stored. Its <see cref="PdfOperation.Operator"/> is <c>BI</c> and it has no operands.
/// </summary>
public sealed class PdfInlineImage : PdfOperation
{
    private readonly byte[] data;

    /// <summary>An inline image with the given parameters and data.</summary>
    /// <param name="parameters">The image dictionary, abbreviated keys (<c>/W</c>, <c>/H</c>, <c>/CS</c> and so on) or full ones.</param>
    /// <param name="data">The image data, encoded with the filters <paramref name="parameters"/> names.</param>
    public PdfInlineImage(PdfDictionary parameters, ReadOnlySpan<byte> data)
        : this(CheckedClone(parameters), data.ToArray())
    {
    }

    internal PdfInlineImage(PdfDictionary parameters, byte[] data)
        : base("BI", [], trusted: true)
    {
        Parameters = parameters;
        this.data = data;
    }

    /// <summary>The image's parameters as written, abbreviations kept.</summary>
    public PdfDictionary Parameters { get; }

    /// <summary>The image data as stored, still encoded.</summary>
    public ReadOnlySpan<byte> Data => data;

    private static PdfDictionary CheckedClone(PdfDictionary parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        _ = new PdfOperation("BI", [parameters]);
        return parameters.Clone();
    }
}
