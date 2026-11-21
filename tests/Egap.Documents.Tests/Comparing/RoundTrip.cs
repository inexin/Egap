using System.Text;

using Egap.Documents.Comparing;

namespace Egap.Documents.Tests.Comparing;

/// <summary>
/// The comparison's promise, checked without an oracle: accepting every change of the result gives the revised document and
/// rejecting every change gives the original, block by block (paragraph text and the shape of tables and containers).
/// </summary>
internal static class RoundTrip
{
    /// <summary>Compares the two documents and checks the result both ways; returns the comparison.</summary>
    public static DocumentComparison Check(Document original, Document revised, DocumentCompareOptions? options = null)
    {
        DocumentComparison comparison = DocumentCompare.Run(original, revised, options);
        Check(original, revised, comparison);
        return comparison;
    }

    /// <summary>Checks that <paramref name="comparison"/> accepts to <paramref name="revised"/> and rejects to <paramref name="original"/>.</summary>
    public static void Check(Document original, Document revised, DocumentComparison comparison)
    {
        Document accepted = comparison.Document.Clone();
        accepted.Revisions.AcceptAll();
        Document rejected = comparison.Document.Clone();
        rejected.Revisions.RejectAll();
        Assert.Equal(Blocks(revised), Blocks(accepted));
        Assert.Equal(Blocks(original), Blocks(rejected));
    }

    /// <summary>A document's blocks in order, every section's body as one run, each as one line of text.</summary>
    public static List<string> Blocks(Document document)
    {
        if (document.Revisions.Count > 0)
        {
            document = document.Clone();
            document.Revisions.AcceptAll();
        }

        List<string> lines = [];
        foreach (Section section in document.Sections)
        {
            foreach (Block block in section.Body.Blocks)
            {
                lines.Add(Describe(block));
            }
        }

        return lines;
    }

    private static string Describe(Block block)
    {
        return block switch
        {
            // Comments are not compared yet, so their reference marks are left out of the text.
            Paragraph paragraph => "P|" + paragraph.GetText().Replace(ControlChar.CommentReference.ToString(), "", StringComparison.Ordinal),
            Table table => "T[" + string.Join(" ", table.Rows.Select(row => "R(" + string.Join(" ", row.Cells.Select(cell => "C{" + List(cell.Blocks) + "}")) + ")")) + "]",
            ContentControl control => "CC{" + List(control.Blocks) + "}",
            FlexContainer flex => "FLEX{" + string.Join(" ", flex.Items.Select(item => "{" + List(item.Blocks) + "}")) + "}",
            GridContainer grid => "GRID{" + string.Join(" ", grid.Items.Select(item => "{" + List(item.Blocks) + "}")) + "}",
            _ => block.GetType().Name + "|" + block.GetText(),
        };
    }

    private static string List(NodeCollection<Block> blocks)
    {
        StringBuilder text = new();
        foreach (Block block in blocks)
        {
            text.Append(Describe(block)).Append(" / ");
        }

        return text.ToString();
    }
}
