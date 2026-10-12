using Egap.Pdf.IO;

namespace Egap.Pdf.Trees;

/// <summary>
/// A number tree (7.9.7): integer keys in numeric order, as for page labels and the structure tree's parent tree. A real
/// used as a key is read as the integer it truncates to, with a warning.
/// </summary>
internal sealed class NumberTree(PdfDictionary root, PdfObjectTable objects, WarningLog log) : PdfTree<long>(root, objects, log)
{
    private protected override PdfName EntriesKey { get; } = "Nums";

    private protected override string Kind => "number tree";

    private protected override (long Key, bool Repaired)? ReadKey(PdfObject key)
    {
        return key switch
        {
            PdfInteger integer => (integer.Value, false),
            PdfReal real when double.IsFinite(real.Value) && Math.Abs(real.Value) < long.MaxValue => ((long)real.Value, true),
            _ => null,
        };
    }

    private protected override PdfObject WriteKey(long key)
    {
        return new PdfInteger(key);
    }

    private protected override int Compare(long a, long b)
    {
        return a.CompareTo(b);
    }
}
