namespace Egap.Documents.Layout;

/// <summary>
/// Floating tables (Word's <c>w:tblpPr</c>): placed by their anchors like floating pictures, kept clear of text by square
/// wrap on the side with more room, and, when they do not fit, split between rows with the rest continuing as a float at the
/// top of the next page's body, header rows repeated, as Word lays them out in every compatibility mode. Word's
/// <c>doNotBreakWrappedTables</c> keeps a floating table whole. Floating tables in cells, headers, footers, notes and text
/// boxes are laid out in the text.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>The floating tables that go on at the top of the next page: the row (and where in it) to go on from and the x they were planned at.</summary>
    private readonly List<(Table Table, TablePosition Position, int Row, (int Block, int Line)[]? Resume, double PlanX)> carriedTables = [];

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

        // A table that may break is placed by its first row: the rest goes on on the next page instead of pushing it up.
        double height = probe.Rows.Sum(r => r.Height);
        if (MaySplit(position) && probe.Rows.Count > 0 && placement.VerticalAlignment is FloatingAlignment.None or FloatingAlignment.Start or FloatingAlignment.Inside)
        {
            height = probe.Rows[0].Height;
        }

        LayoutRect bounds = FloatBounds(placement, probe.Width, height, y);
        bounds = ClearOtherTables(bounds, position);

        // The position puts the table's edge, or its first cell's text where Word measures the indent there (mode below 15).
        double legacy = LegacyIndent(table, table.GetEffectiveFormat());
        bool split = PlaceFloatingRows(table, position, bounds.X - probe.Left - legacy, bounds.Y, 0);

        // Word 2013 moves the text after a table that breaks to the page where the table ends, unless
        // allowTextAfterFloatingTableBreak lets it follow the first part.
        if (split && !document.Settings.WordCompatibility.AllowTextAfterFloatingTableBreak)
        {
            while (carriedTables.Exists(c => c.Table == table))
            {
                NewPage();
            }
        }
    }

    /// <summary>
    /// Places a floating table's rows from <paramref name="first"/> (and its part from <paramref name="resume"/>) at
    /// <paramref name="top"/>, as many as fit above the body's foot when it may split, a row that may split breaking between
    /// its lines; the rest is carried to the next page. A table placed on the page keeps its first row where it is put; one
    /// placed by the text moves whole to the next page when not even part of that fits. Returns whether some rows were carried.
    /// </summary>
    private bool PlaceFloatingRows(Table table, TablePosition position, double planX, double top, int first, (int Block, int Line)[]? resume = null)
    {
        TablePlan plan = Plan(table, planX, Column.Width);
        double bottom = page!.Geometry.BodyBottom;
        bool maySplit = MaySplit(position);
        bool mayMove = position.VerticalAnchor == VerticalAnchor.Paragraph && top > page.Geometry.BodyTop + 0.01;
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
        while (r < plan.Rows.Count)
        {
            RowPlan row = plan.Rows[r];
            double needed = resume is null ? row.Height : PartHeight(plan, row, resume, double.PositiveInfinity);
            bool mayBreak = row.Format.CantSplit != true && !row.HasMerges && row.Format.HeightRule != RowHeightRule.Exactly;
            if (!maySplit || at + needed <= bottom + 0.01 || (placed == 0 && !mayMove && !mayBreak))
            {
                rows.Add(PlaceRow(plan, row, at, needed, resume, out _, repeated: false));
                at += needed;
                resume = null;
                r++;
                placed++;
                continue;
            }

            if (mayBreak && bottom - at > 0.01)
            {
                (int Block, int Line)[] start = resume ?? [.. row.Cells.Select(_ => (0, 0))];
                RowBox part = PlaceRow(plan, row, at, bottom - at, start, out (int, int)[]? next, repeated: false);
                if (next is not null && part.Bounds.Height > 0.01 && Progressed(start, next))
                {
                    rows.Add(part);
                    at += part.Bounds.Height;
                    resume = next;
                    placed++;
                }
            }

            if (placed == 0 && !mayMove)
            {
                // Nothing of it fits even at the top of a page: placed whole anyway, so layout moves on.
                rows.Add(PlaceRow(plan, row, at, needed, resume, out _, repeated: false));
                at += needed;
                resume = null;
                r++;
                placed++;
            }

            break;
        }

        if (placed > 0)
        {
            LayoutRect bounds = new(plan.Left, top, plan.Width, at - top);
            page.Floats.Add(new PlacedFloat(PlacementOf(position), bounds, new TableBox(table, bounds, rows)) { Table = position });
        }

        if (r < plan.Rows.Count)
        {
            carriedTables.Add((table, position, r, resume, planX));
            return true;
        }

        return false;
    }

    /// <summary>
    /// A floating table breaks across pages, in every compatibility mode, unless <c>doNotBreakWrappedTables</c>; one centred
    /// or aligned to the bottom of the page or margins stays where it is put, margins included.
    /// </summary>
    private bool MaySplit(TablePosition position)
    {
        return !document.Settings.WordCompatibility.DoNotBreakWrappedTables
            && (position.VerticalAnchor == VerticalAnchor.Paragraph || position.VerticalAlignment is FloatingAlignment.None or FloatingAlignment.Start or FloatingAlignment.Inside);
    }

    /// <summary>Places the rows carried from the page before at the top of this page's body.</summary>
    private void PlaceCarriedTables()
    {
        if (carriedTables.Count == 0)
        {
            return;
        }

        List<(Table Table, TablePosition Position, int Row, (int Block, int Line)[]? Resume, double PlanX)> carried = [.. carriedTables];
        carriedTables.Clear();
        foreach ((Table table, TablePosition position, int row, (int Block, int Line)[]? resume, double planX) in carried)
        {
            PlaceFloatingRows(table, position, planX, page!.Geometry.BodyTop, row, resume);
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
