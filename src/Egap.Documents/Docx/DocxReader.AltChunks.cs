using System.Xml.Linq;

using Egap.Documents.Rtf;

namespace Egap.Documents.Docx;

/// <summary>
/// Embedded parts (<c>w:altChunk</c>): one whose content is RTF is read with the RTF reader and its blocks take the chunk's
/// place, as Word imports it when it opens the file. Other chunks (HTML, MHT, plain text, DOCX) stay block islands that
/// keep the element and its part.
/// </summary>
internal sealed partial class DocxReader
{
    private static readonly HashSet<string> RtfContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/rtf",
        "text/rtf",
    };

    private readonly DocumentLimits limits;
    private readonly Dictionary<(ConversionWarningKind Kind, string Message), int> warnings = [];

    /// <summary>Reads an RTF altChunk in place; false for a chunk that stays an island.</summary>
    private bool ReadAltChunk(XElement element, NodeCollection<Block> blocks)
    {
        if ((string?)element.Attribute(Ns.R + "id") is not { } id
            || currentPart.GetRelationship(id) is not { External: false } relationship
            || package.GetPart(relationship.TargetName) is not { } part
            || !RtfContentTypes.Contains(part.ContentType.Split(';')[0].Trim()))
        {
            return false;
        }

        Document chunk;
        try
        {
            chunk = RtfReader.Read(part.Data, new RtfLoadOptions { Limits = limits }, out _);
        }
        catch (DocumentException)
        {
            Warn(ConversionWarningKind.ResourceNotLoaded, "An embedded RTF part (altChunk) could not be read; it was kept as it was.");
            return false;
        }

        // One import for the whole chunk, so each style and list comes over once; a style the document has by the same id
        // is the document's, as when Word merges the chunk.
        CloneContext context = new(document);
        List<Block> copies = [];
        foreach (Section section in chunk.Sections)
        {
            foreach (Block block in section.Body.Blocks)
            {
                copies.Add((Block)block.CloneInto(context, deep: true, finish: false));
            }
        }

        context.Finish();
        if (noteDepth > 0 || commentDepth > 0 || headerDepth > 0)
        {
            DropNotesAndComments(copies);
        }

        if (copies.Count == 0)
        {
            // An empty chunk adds nothing; its element goes back where it was, as markup Egap does not read.
            pendingRaw.Add(element);
            return true;
        }

        TakeBefore(copies[0]);
        foreach (Block copy in copies)
        {
            blocks.AddTrusted(copy);
            if (copy is Paragraph paragraph)
            {
                lastParagraph = paragraph;
            }
        }

        return true;
    }

    /// <summary>
    /// Takes notes and comments out of a chunk read into a note, comment, header or text box, which cannot hold them: their
    /// references go, and the text around them stays.
    /// </summary>
    private void DropNotesAndComments(List<Block> copies)
    {
        int dropped = 0;
        foreach (Block block in copies)
        {
            foreach (Inline inline in block.Descendants<Inline>().Where(i => i is Footnote or Comment or CommentRangeStart or CommentRangeEnd).ToList())
            {
                inline.Remove();
                dropped++;
            }
        }

        if (dropped > 0)
        {
            Warn(ConversionWarningKind.CommentsDropped, "Notes and comments in an embedded RTF part were left out where they cannot go.");
        }
    }

    private void Warn(ConversionWarningKind kind, string message)
    {
        warnings[(kind, message)] = warnings.GetValueOrDefault((kind, message)) + 1;
    }

    private List<ConversionWarning> Warnings()
    {
        return [.. warnings.Select(w => new ConversionWarning(w.Key.Kind, w.Key.Message, w.Value))];
    }
}
