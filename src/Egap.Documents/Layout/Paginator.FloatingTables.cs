namespace Egap.Documents.Layout;

/// <summary>
/// Floating tables (Word's <c>w:tblpPr</c>): placed by their anchors like floating pictures, kept clear of text by square
/// wrap on the side with more room, and, when they do not fit, split between rows with the rest continuing as a float at the
/// top of the next page's body, header rows repeated (Word 2013's rules). Word 2010's rules and Word's
/// <c>doNotBreakWrappedTables</c> keep a floating table whole. Floating tables in cells, headers, footers, notes and text
/// boxes are laid out in the text.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>The floating tables that go on at the top of the next page: the rows still to place and the x they were planned at.</summary>
    private readonly List<(Table Table, TablePosition Position, int Row, double PlanX)> carriedTables = [];

    /// <summary>Whether <paramref name="table"/> floats: it has a position and sits in a section's body, outside every cell.</summary>
    private bool Floats(Table table, out TablePosition position)
    {
        position = null!;
        if (measuring > 0 || page is null || table.GetEffectiveFormat().Position is not { } p)
        {
            return false;
        }

        for (Node? node = table.Parent; node is not null; node = node.Parent)
        {
            if (node is Cell)
            {
                return false;
            }

            if (node is Story)
            {
                position = p;
                return node is Body;
            }
        }

        return false;
    }

    private void FlowFloatingTable(Table table, TablePosition position)
    {
        // Planned once at x 0 to find the width and where the table's edge falls; planned again so that edge lands where the
        // position puts it.
        TablePlan probe = Plan(table, 0, Column.Width);
        FloatingPlacement placement = PlacementOf(position);
        LayoutRect bounds = FloatBounds(placement, probe.Width, probe.Rows.Sum(r => r.Height), y);
        bounds = ClearOtherTables(bounds, position);
        bool split = PlaceFloatingRows(table, position, bounds.X - probe.Left, bounds.Y, 0);

        // Word 2013 moves the text after a table that breaks to the next page, unless allowTextAfterFloatingTableBreak.
        if (split && !document.Settings.WordCompatibility.AllowTextAfterFloatingTableBreak)
        {
            NewPage();
        }
    }

    /// <summary>
    /// Places a floating table's rows from <paramref name="first"/> at <paramref name="top"/>, as many whole rows as fit when it
    /// may split; the rest is carried to the next page. Returns whether some rows were carried.
    /// </summary>
    private bool PlaceFloatingRows(Table table, TablePosition position, double planX, double top, int first)
    {
        TablePlan plan = Plan(table, planX, Column.Width);
        WordCompatibility compat = document.Settings.WordCompatibility;
        bool maySplit = !compat.DoNotBreakWrappedTables && document.Settings.CompatibilityMode >= 15;
        double bottom = page!.Geometry.BodyBottom;
        List<LayoutBox> rows = [];
        double at = top;
        if (first > 0)
        {
            for (int h = 0; h < plan.HeaderRows && h < first; h++)
            {
                rows.Add(PlaceRow(plan, plan.Rows[h], at, plan.Rows[h].Height, null, out _, repeated: true));
                at += plan.Rows[h].Height;
            }
        }

        int r = first;
        int placed = 0;
        for (; r < plan.Rows.Count; r++)
        {
            RowPlan row = plan.Rows[r];
            if (maySplit && at + row.Height > bottom + 0.01 && (placed > 0 || top > page.Geometry.BodyTop + 0.01))
            {
                break;
            }

            rows.Add(PlaceRow(plan, row, at, row.Height, null, out _, repeated: false));
            at += row.Height;
            placed++;
        }

        if (placed > 0)
        {
            LayoutRect bounds = new(plan.Left, top, plan.Width, at - top);
            page.Floats.Add(new PlacedFloat(PlacementOf(position), bounds, new TableBox(table, bounds, rows)) { Table = position });
        }

        if (r < plan.Rows.Count)
        {
            carriedTables.Add((table, position, r, planX));
            return true;
        }

        return false;
    }

    /// <summary>Places the rows carried from the page before at the top of this page's body.</summary>
    private void PlaceCarriedTables()
    {
        if (carriedTables.Count == 0)
        {
            return;
        }

        List<(Table Table, TablePosition Position, int Row, double PlanX)> carried = [.. carriedTables];
        carriedTables.Clear();
        foreach ((Table table, TablePosition position, int row, double planX) in carried)
        {
            PlaceFloatingRows(table, position, planX, page!.Geometry.BodyTop, row);
        }
    }

    /// <summary>A table that may not overlap moves down until it clears the floating tables already on the page (<c>w:tblOverlap never</c>).</summary>
    private LayoutRect ClearOtherTables(LayoutRect bounds, TablePosition position)
    {
        if (position.AllowOverlap)
        {
            return bounds;
        }

        for (int pass = 0; pass < 64; pass++)
        {
            PlacedFloat? hit = page!.Floats.FirstOrDefault(f => f.Table is not null && Overlaps(f.Exclusion, bounds));
            if (hit is null)
            {
                break;
            }

            bounds = bounds with { Y = hit.Exclusion.Bottom + position.DistanceTop };
        }

        return bounds;

        static bool Overlaps(LayoutRect a, LayoutRect b)
        {
            return a.X < b.Right - 0.01 && b.X < a.Right - 0.01 && a.Y < b.Bottom - 0.01 && b.Y < a.Bottom - 0.01;
        }
    }

    private static FloatingPlacement PlacementOf(TablePosition p)
    {
        return new FloatingPlacement
        {
            HorizontalAnchor = p.HorizontalAnchor,
            HorizontalAlignment = p.HorizontalAlignment,
            HorizontalOffset = p.HorizontalOffset,
            VerticalAnchor = p.VerticalAnchor,
            VerticalAlignment = p.VerticalAlignment,
            VerticalOffset = p.VerticalOffset,
            Wrap = WrapMode.Square,
            WrapSide = WrapSide.Largest,
            DistanceLeft = p.DistanceLeft,
            DistanceRight = p.DistanceRight,
            DistanceTop = p.DistanceTop,
            DistanceBottom = p.DistanceBottom,
        };
    }
}
