using Egap.Pdf.IO;

namespace Egap.Pdf.Trees;

/// <summary>
/// A name tree (7.9.6): string keys in byte order, as for named destinations, embedded files and JavaScript. A name used as
/// a key, as some writers do, is read as a string with its bytes, with a warning.
/// </summary>
internal sealed class NameTree(PdfDictionary root, PdfObjectTable objects, WarningLog log) : PdfTree<PdfString>(root, objects, log)
{
    private protected override PdfName EntriesKey { get; } = "Names";

    private protected override string Kind => "name tree";

    private protected override (PdfString Key, bool Repaired)? ReadKey(PdfObject key)
    {
        return key switch
        {
            PdfString text => (text, false),
            PdfName name => (PdfString.FromBytes(name.Bytes), true),
            _ => null,
        };
    }

    private protected override PdfObject WriteKey(PdfString key)
    {
        return key;
    }

    private protected override int Compare(PdfString a, PdfString b)
    {
        return a.Bytes.SequenceCompareTo(b.Bytes);
    }
}
