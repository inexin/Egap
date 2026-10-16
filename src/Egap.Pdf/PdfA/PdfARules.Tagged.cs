namespace Egap.Pdf.PdfA;

/// <summary>Level a (ISO 19005-2 6.7): the document tagged, its structure of standard types.</summary>
internal sealed partial class PdfARules
{
    private void Tagged()
    {
        if (conformance is not (PdfAConformance.PdfA2a or PdfAConformance.PdfA3a))
        {
            return;
        }

        PdfStructureTree structure = document.Structure;
        if (!structure.Exists)
        {
            Issue("6.7.2.2", "level a needs a tagged document: it has no structure tree (tag content with PdfCanvas.BeginTag)");
            return;
        }

        if (!structure.IsMarked)
        {
            Issue("6.7.2.2", "the document does not say it is tagged (/MarkInfo /Marked); it is set", () => structure.IsMarked = true);
        }

        HashSet<string> unmapped = [.. structure.Descendants().Take(200_000).Where(e => !e.StandardType.IsStandard).Select(e => e.Type.Name)];
        if (unmapped.Count > 0)
        {
            Issue("6.7.3.4", $"structure types neither standard nor role-mapped to one: {string.Join(", ", unmapped.Take(10))}");
        }
    }
}
