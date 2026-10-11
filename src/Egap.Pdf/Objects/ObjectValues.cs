namespace Egap.Pdf.Objects;

/// <summary>Value helpers shared by arrays and dictionaries.</summary>
internal static class ObjectValues
{
    public static double? Number(PdfObject? value)
    {
        return value switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => null,
        };
    }

    public static long? Int(PdfObject? value)
    {
        return value switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real when Math.Abs(real.Value) < 9.2e18 => (long)real.Value,
            _ => null,
        };
    }

    /// <summary>A copy of <paramref name="value"/> to put in <paramref name="parent"/>: containers are copied, the rest shared.</summary>
    public static PdfObject CloneInto(IContainer parent, PdfObject value)
    {
        PdfObject copy = value switch
        {
            PdfArray array => array.Clone(),
            PdfDictionary dictionary => dictionary.Clone(),
            _ => value,
        };
        if (copy is IContainer contained)
        {
            contained.Parent = parent;
        }

        return copy;
    }
}
