using System.Globalization;
using System.Xml.Linq;

using Egami.Drawing;

namespace Egap.Documents.DrawingML;

/// <summary>
/// The stand-in for a SmartArt diagram whose file stores no drawing of it: its text as a stack of rounded blocks in the theme's
/// accent colours, one block for each line, with the text centred in light text colour.
/// </summary>
internal static class SmartArtList
{
    private static readonly XNamespace A = DrawingNs.A;
    private static readonly string[] Accents = ["accent1", "accent2", "accent3", "accent4", "accent5", "accent6"];

    /// <summary>The share of each block's height left as the gap below it.</summary>
    private const float GapShare = 0.15f;

    public static void Draw(Canvas canvas, RectF box, string text, ShapePaintContext context)
    {
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        // At most as many blocks as fit at a readable height; the rest of the text joins the last one.
        int count = Math.Clamp((int)(box.Height / 14), 1, lines.Length);
        if (count < lines.Length)
        {
            lines = [.. lines[..(count - 1)], string.Join(' ', lines[(count - 1)..])];
        }

        float step = box.Height / count;
        float height = step * (1 - (count > 1 ? GapShare : 0));
        float size = Math.Clamp(height * 0.4f, 6, 24);
        for (int i = 0; i < lines.Length; i++)
        {
            RectF block = new(box.X, box.Y + (i * step), box.Width, height);
            string accent = Accents[i % Accents.Length];
            XElement spPr = new(
                A + "spPr",
                new XElement(A + "prstGeom", new XAttribute("prst", "roundRect"), new XElement(A + "avLst", new XElement(A + "gd", new XAttribute("name", "adj"), new XAttribute("fmla", "val 10000")))),
                new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", accent))),
                new XElement(A + "ln", new XAttribute("w", "12700"), new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "lt1")))));
            XElement txBody = new(
                A + "txBody",
                new XElement(A + "bodyPr", new XAttribute("anchor", "ctr"), new XElement(A + "normAutofit")),
                new XElement(
                    A + "p",
                    new XElement(A + "pPr", new XAttribute("algn", "ctr")),
                    new XElement(
                        A + "r",
                        new XElement(A + "rPr", new XAttribute("sz", ((int)(size * 100)).ToString(CultureInfo.InvariantCulture)), new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "lt1")))),
                        new XElement(A + "t", lines[i]))));
            ShapePainter.Draw(canvas, block, spPr, null, context, txBody: txBody);
        }
    }
}
