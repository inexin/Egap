using Egap.Documents;

namespace Egap.Pdf.Layout;

internal sealed partial class ModelBuilder
{
    private readonly Dictionary<BlockDraft, double> spaceBefore = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Space before and after paragraphs (design Q6): the white space between neighbouring blocks in one column, less the
    /// space between lines inside a paragraph, measured per pair of styles. A style's space after is what usually follows
    /// it before the same style; its space before is what usually precedes it beyond the other style's space after. A
    /// paragraph whose gap is more than 3 points off what its styles give gets its own space before.
    /// </summary>
    private void Spacing(List<SectionPlan> plans)
    {
        List<double> inner = [];
        foreach (BlockDraft block in plans.SelectMany(p => p.Items).Select(i => i.Item).OfType<BlockDraft>())
        {
            for (int i = 1; i < block.Lines.Count; i++)
            {
                double gap = block.Lines[i].Box.Top - block.Lines[i - 1].Box.Bottom;
                if (gap > -2 && gap < block.Lines[i].Size * 2)
                {
                    inner.Add(gap);
                }
            }
        }

        inner.Sort();
        double within = inner.Count == 0 ? 0 : inner[inner.Count / 2];
        List<(BlockDraft Before, BlockDraft After, double Gap)> pairs = [];
        foreach (SectionPlan plan in plans)
        {
            for (int i = 1; i < plan.Items.Count; i++)
            {
                PlacedItem a = plan.Items[i - 1], b = plan.Items[i];
                if (a.Item is BlockDraft before && b.Item is BlockDraft after && ReferenceEquals(a.Page, b.Page) && a.Frame == b.Frame
                    && after.Box.Top >= before.Box.Bottom - 1 && !after.Continues && before.Role != LayoutRole.Footnote && after.Role != LayoutRole.Footnote)
                {
                    pairs.Add((before, after, Math.Max(0, after.Box.Top - before.Box.Bottom - within)));
                }
            }
        }

        Dictionary<StyleEntry, double> afterOf = new(ReferenceEqualityComparer.Instance);
        Dictionary<StyleEntry, double> beforeOf = new(ReferenceEqualityComparer.Instance);
        List<StyleEntry> entries = [.. styles.Entries.Select(e => e.Entry)];
        foreach (StyleEntry entry in entries)
        {
            List<double> same = [.. pairs.Where(p => styles.For(p.Before) == entry && styles.For(p.After) == entry).Select(p => p.Gap)];
            if (same.Count > 0)
            {
                afterOf[entry] = Median(same);
            }
        }

        foreach (StyleEntry entry in entries)
        {
            List<double> into = [.. pairs.Where(p => styles.For(p.After) == entry && styles.For(p.Before) != entry && afterOf.ContainsKey(styles.For(p.Before)))
                .Select(p => p.Gap - afterOf[styles.For(p.Before)])];
            beforeOf[entry] = into.Count > 0 ? Math.Max(0, Median(into)) : 0;
        }

        foreach (StyleEntry entry in entries.Where(e => !afterOf.ContainsKey(e)))
        {
            List<double> outOf = [.. pairs.Where(p => styles.For(p.Before) == entry && styles.For(p.After) != entry).Select(p => p.Gap - beforeOf[styles.For(p.After)])];
            afterOf[entry] = outOf.Count > 0 ? Math.Max(0, Median(outOf)) : 0;
        }

        foreach (StyleEntry entry in entries)
        {
            entry.SpaceBefore = Round(beforeOf[entry]);
            entry.SpaceAfter = Round(afterOf[entry]);
            ParagraphFormat format = entry.Style.ParagraphFormat;
            format.SpaceBefore = entry.SpaceBefore;
            format.SpaceAfter = entry.SpaceAfter;
            format.ContextualSpacing = false;
        }

        foreach ((BlockDraft before, BlockDraft after, double gap) in pairs)
        {
            StyleEntry above = styles.For(before), below = styles.For(after);
            if (Math.Abs(gap - (above.SpaceAfter + below.SpaceBefore)) > 3)
            {
                spaceBefore[after] = Round(Math.Max(0, gap - above.SpaceAfter));
            }
        }
    }

    private static double Median(List<double> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }

    private static double Round(double space)
    {
        return space < 1.5 ? 0 : Math.Round(space * 2) / 2;
    }
}
