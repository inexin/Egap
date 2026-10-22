namespace Egap.Documents.Layout;

/// <summary>An item's place in a grid: its first row and column (from 0) and the tracks it spans.</summary>
internal readonly record struct GridSlot(GridItem Item, int Row, int Column, int RowSpan, int ColumnSpan);

/// <summary>Where a grid's items go, and how many columns and rows that makes, explicit and added.</summary>
internal sealed class GridPlacement(GridLayout layout, List<GridSlot> slots, int columns, int rows)
{
    public List<GridSlot> Slots { get; } = slots;

    public int Columns { get; } = columns;

    public int Rows { get; } = rows;

    public GridTrack ColumnTrack(int index) => index < layout.Columns.Count ? layout.Columns[index] : layout.AutoColumns;

    public GridTrack RowTrack(int index) => index < layout.Rows.Count ? layout.Rows[index] : layout.AutoRows;
}

/// <summary>
/// Grid containers (CSS Grid Layout): items placed by line, span and named area, the rest by the automatic placement
/// algorithm; column tracks sized from their sizes and content, fractions sharing what is left; rows as tall as their items.
/// The page flow breaks between rows, keeping rows that an item spans together.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>The most tracks a grid can have each way: a hostile placement cannot make layout run away.</summary>
    private const int MaxGridTracks = 1000;

    /// <summary>The most cells automatic placement looks at for one grid.</summary>
    private const long MaxPlacementWork = 50_000_000;

    /// <summary>A grid measured at one width: its outer size, the tracks' offsets and sizes, and its items' slots.</summary>
    private sealed record GridPlan(GridContainer Container, double Width, double Height, double ContentX, double[] ColumnX, double[] ColumnWidth,
        double[] RowY, double[] RowHeight, List<GridSlot> Slots);

    private readonly Dictionary<(GridContainer Container, double Width), GridPlan> gridPlans = [];

    private GridPlan PlanGrid(GridContainer container, double available)
    {
        if (!gridPlans.TryGetValue((container, available), out GridPlan? plan))
        {
            plan = MakeGridPlan(container, available);
            gridPlans[(container, available)] = plan;
        }

        return plan;
    }

    /// <summary>Places the items: named areas and explicit lines first, then the rest in order by the automatic placement.</summary>
    internal static GridPlacement PlaceGridItems(GridContainer container)
    {
        GridLayout layout = container.Layout;

        // Named areas: each name's rectangle of cells.
        Dictionary<string, (int Row, int Column, int RowSpan, int ColumnSpan)> areas = new(StringComparer.Ordinal);
        int areaColumns = 0;
        for (int r = 0; r < layout.Areas.Count && r < MaxGridTracks; r++)
        {
            string[] names = layout.Areas[r].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            areaColumns = Math.Max(areaColumns, Math.Min(names.Length, MaxGridTracks));
            for (int c = 0; c < names.Length && c < MaxGridTracks; c++)
            {
                if (names[c].All(ch => ch == '.'))
                {
                    continue;
                }

                if (areas.TryGetValue(names[c], out var area))
                {
                    int top = Math.Min(area.Row, r), left = Math.Min(area.Column, c);
                    int bottom = Math.Max(area.Row + area.RowSpan, r + 1), right = Math.Max(area.Column + area.ColumnSpan, c + 1);
                    areas[names[c]] = (top, left, bottom - top, right - left);
                }
                else
                {
                    areas[names[c]] = (r, c, 1, 1);
                }
            }
        }

        int explicitColumns = Math.Max(layout.Columns.Count, areaColumns);
        int explicitRows = Math.Max(layout.Rows.Count, layout.Areas.Count);
        bool byColumn = layout.AutoFlow is GridAutoFlow.Column or GridAutoFlow.ColumnDense;
        bool dense = layout.AutoFlow is GridAutoFlow.RowDense or GridAutoFlow.ColumnDense;

        // Across is the axis automatic placement fills first; down is the one it adds tracks to.
        int acrossCount = Math.Max(1, byColumn ? explicitRows : explicitColumns);
        HashSet<(int Down, int Across)> taken = [];

        // A budget of cells looked at: a hostile grid of huge spans in thousands of tracks could take hours to place; past
        // it, the items left are placed where the search stands, over others.
        long budget = MaxPlacementWork;
        List<GridSlot> slots = [];
        List<(GridItem Item, int? Down, int DownSpan, int? Across, int AcrossSpan)> pending = [];
        foreach (GridItem item in container.Items)
        {
            int? row = Line(item.Row, explicitRows), column = Line(item.Column, explicitColumns);
            int rowSpan = Math.Min(item.RowSpan, MaxGridTracks), columnSpan = Math.Min(item.ColumnSpan, MaxGridTracks);
            if (item.Area is { } name && areas.TryGetValue(name, out var area))
            {
                (row, column, rowSpan, columnSpan) = area;
            }

            (int? down, int downSpan, int? across, int acrossSpan) = byColumn ? (column, columnSpan, row, rowSpan) : (row, rowSpan, column, columnSpan);
            acrossCount = Math.Min(MaxGridTracks, Math.Max(acrossCount, (across ?? 0) + acrossSpan));
            if (down is int d && across is int a)
            {
                Take(d, a, downSpan, acrossSpan);
                slots.Add(Slot(item, d, a, downSpan, acrossSpan));
            }
            else
            {
                pending.Add((item, down, downSpan, across, acrossSpan));
            }
        }

        // Items locked to a row (or column, flowing by column) go in the first free place along it.
        foreach ((GridItem item, int? down, int downSpan, int? across, int acrossSpan) in pending.Where(p => p.Down is not null).ToList())
        {
            int d = down!.Value;
            int a = 0;
            while (a + acrossSpan <= acrossCount && !Free(d, a, downSpan, acrossSpan))
            {
                a++;
            }

            if (a + acrossSpan > acrossCount)
            {
                a = acrossCount;
                acrossCount = Math.Min(MaxGridTracks, acrossCount + acrossSpan);
            }

            Take(d, a, downSpan, acrossSpan);
            slots.Add(Slot(item, d, a, downSpan, acrossSpan));
        }

        // The rest in order: a cursor moves along each track and on to the next; dense starts from the beginning each time.
        int cursorDown = 0, cursorAcross = 0;
        foreach ((GridItem item, _, int downSpan, int? across, int acrossSpan) in pending.Where(p => p.Down is null))
        {
            int span = Math.Min(acrossSpan, acrossCount);
            int d = dense ? 0 : cursorDown;
            int a = across ?? (dense ? 0 : cursorAcross);
            if (across is int fixedAcross && !dense && fixedAcross < cursorAcross)
            {
                d++;
            }

            while (d < MaxGridTracks)
            {
                if (a + span <= acrossCount && Free(d, a, downSpan, span))
                {
                    break;
                }

                if (across is not null || a + span >= acrossCount)
                {
                    d++;
                    a = across ?? 0;
                }
                else
                {
                    a++;
                }
            }

            Take(d, a, downSpan, span);
            slots.Add(Slot(item, d, a, downSpan, span));
            cursorDown = d;
            cursorAcross = a + span;
        }

        int columns = Math.Max(explicitColumns, slots.Count == 0 ? 0 : slots.Max(s => s.Column + s.ColumnSpan));
        int rows = Math.Max(explicitRows, slots.Count == 0 ? 0 : slots.Max(s => s.Row + s.RowSpan));
        slots.Sort((x, y) => x.Row != y.Row ? x.Row.CompareTo(y.Row) : x.Column.CompareTo(y.Column));
        return new GridPlacement(layout, slots, Math.Clamp(columns, 1, MaxGridTracks), Math.Min(rows, MaxGridTracks));

        GridSlot Slot(GridItem item, int down, int across, int downSpan, int acrossSpan)
        {
            down = Math.Min(down, MaxGridTracks - 1);
            across = Math.Min(across, MaxGridTracks - 1);
            downSpan = Math.Min(downSpan, MaxGridTracks - down);
            acrossSpan = Math.Min(acrossSpan, MaxGridTracks - across);
            return byColumn ? new GridSlot(item, across, down, acrossSpan, downSpan) : new GridSlot(item, down, across, downSpan, acrossSpan);
        }

        bool Free(int down, int across, int downSpan, int acrossSpan)
        {
            for (int i = down; i < down + downSpan; i++)
            {
                for (int j = across; j < across + acrossSpan; j++)
                {
                    if (--budget < 0)
                    {
                        return true;
                    }

                    if (taken.Contains((i, j)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        void Take(int down, int across, int downSpan, int acrossSpan)
        {
            budget -= (long)downSpan * acrossSpan;
            if (budget < 0)
            {
                return;
            }

            for (int i = down; i < Math.Min(down + downSpan, MaxGridTracks); i++)
            {
                for (int j = across; j < Math.Min(across + acrossSpan, MaxGridTracks); j++)
                {
                    taken.Add((i, j));
                }
            }
        }
    }

    /// <summary>A line number as a track index from 0: 1 is the first line, -1 the last line of the explicit grid.</summary>
    private static int? Line(int? line, int explicitTracks)
    {
        return line switch
        {
            null => null,
            > 0 => Math.Min(line.Value - 1, MaxGridTracks - 1),
            _ => Math.Max(0, explicitTracks + 1 + line.Value),
        };
    }

    private GridPlan MakeGridPlan(GridContainer container, double available)
    {
        GridLayout layout = container.Layout;
        BoxStyle? box = container.Box;
        double outer = OuterWidth(box, available);
        double inner = Math.Max(1, outer - (box?.Horizontal ?? 0));
        GridPlacement placement = PlaceGridItems(container);
        int columns = placement.Columns, rows = placement.Rows;

        Dictionary<GridItem, (double Min, double Max)> intrinsic = [];
        foreach (GridSlot slot in placement.Slots)
        {
            intrinsic[slot.Item] = ItemIntrinsic(slot.Item, slot.Item.Box);
        }

        double[] widths = SizeColumns(layout, placement, intrinsic, inner);
        double columnGaps = layout.ColumnGap * (columns - 1);
        (double startX, double betweenX) = layout.JustifyContent == LayoutAlignment.Stretch ? (0, 0) : Distribute(layout.JustifyContent, inner - widths.Sum() - columnGaps, columns);
        double[] columnX = new double[columns];
        double x = startX;
        for (int c = 0; c < columns; c++)
        {
            columnX[c] = x;
            x += widths[c] + layout.ColumnGap + betweenX;
        }

        double AreaWidth(GridSlot slot) => columnX[slot.Column + slot.ColumnSpan - 1] + widths[slot.Column + slot.ColumnSpan - 1] - columnX[slot.Column];

        // Each item's width in its area, then its height at that width.
        Dictionary<GridItem, (double Width, double Height)> sizes = [];
        foreach (GridSlot slot in placement.Slots)
        {
            double width = OuterWidthIn(slot.Item, AreaWidth(slot), slot.Item.JustifySelf ?? layout.JustifyItems, intrinsic[slot.Item]);
            sizes[slot.Item] = (width, OuterHeight(slot.Item, slot.Item.Box, width));
        }

        double minInner = Math.Max(0, (box?.MinHeight ?? 0) - (box?.Vertical ?? 0));
        double[] heights = SizeRows(layout, placement, sizes, minInner);
        double[] rowY = new double[rows];
        double y = 0;
        for (int r = 0; r < rows; r++)
        {
            rowY[r] = y;
            y += heights[r] + (r + 1 < rows ? layout.RowGap : 0);
        }

        double contentHeight = rows == 0 ? 0 : y;
        double height = Math.Max(box?.MinHeight ?? 0, contentHeight + (box?.Vertical ?? 0));
        return new GridPlan(container, outer, height, box?.InsetLeft ?? 0, columnX, widths, rowY, heights, placement.Slots);
    }

    /// <summary>
    /// The columns' widths (CSS grid's track sizing, simplified): fixed sizes as given; content-sized tracks from their items'
    /// narrowest widths, then grown toward their widest while there is room; fractions share what is left, never below their
    /// content; auto tracks share any width still left when the grid stretches.
    /// </summary>
    private static double[] SizeColumns(GridLayout layout, GridPlacement placement, Dictionary<GridItem, (double Min, double Max)> intrinsic, double inner)
    {
        int count = placement.Columns;
        double[] baseSize = new double[count];
        double[] limit = new double[count];
        bool[] flexible = new bool[count];
        double[] fractions = new double[count];
        for (int c = 0; c < count; c++)
        {
            GridTrack track = placement.ColumnTrack(c);
            baseSize[c] = Fixed(track.Min, inner) ?? 0;
            limit[c] = track.Max.Unit == GridTrackUnit.Fraction ? double.PositiveInfinity : Fixed(track.Max, inner) ?? 0;
            flexible[c] = track.Max.Unit == GridTrackUnit.Fraction;
            fractions[c] = flexible[c] ? track.Max.Value : 0;
        }

        // Items in one column size its content-sized limits; items spanning several add what their columns lack.
        foreach (GridSlot slot in placement.Slots.OrderBy(s => s.ColumnSpan))
        {
            (double min, double max) = intrinsic[slot.Item];
            if (slot.ColumnSpan == 1)
            {
                GridTrack track = placement.ColumnTrack(slot.Column);
                baseSize[slot.Column] = Math.Max(baseSize[slot.Column], Content(track.Min, min, max, greatest: false) ?? 0);
                if (!flexible[slot.Column])
                {
                    limit[slot.Column] = Math.Max(limit[slot.Column], Content(track.Max, min, max, greatest: true) ?? 0);
                }

                continue;
            }

            int[] spanned = [.. Enumerable.Range(slot.Column, slot.ColumnSpan).Where(c => c < count)];
            int[] growable = [.. spanned.Where(c => placement.ColumnTrack(c).Min.Unit is not (GridTrackUnit.Points or GridTrackUnit.Percent))];
            double gaps = layout.ColumnGap * (spanned.Length - 1);
            double lacking = min - spanned.Sum(c => baseSize[c]) - gaps;
            if (lacking > 0 && growable.Length > 0)
            {
                foreach (int c in growable)
                {
                    baseSize[c] += lacking / growable.Length;
                }
            }

            double wanting = max - spanned.Sum(c => Math.Max(baseSize[c], limit[c])) - gaps;
            int[] limited = [.. growable.Where(c => !flexible[c])];
            if (wanting > 0 && limited.Length > 0)
            {
                foreach (int c in limited)
                {
                    limit[c] = Math.Max(limit[c], baseSize[c]) + (wanting / limited.Length);
                }
            }
        }

        for (int c = 0; c < count; c++)
        {
            limit[c] = Math.Max(limit[c], baseSize[c]);
        }

        double gapsAll = layout.ColumnGap * (count - 1);
        double[] widths = [.. baseSize];

        // Grow the content-sized tracks toward their limits, equally, while width is left.
        double free = inner - widths.Sum() - gapsAll;
        for (int round = 0; round < count && free > 0.01; round++)
        {
            int[] growing = [.. Enumerable.Range(0, count).Where(c => !flexible[c] && widths[c] < limit[c] - 0.001)];
            if (growing.Length == 0)
            {
                break;
            }

            double share = free / growing.Length;
            foreach (int c in growing)
            {
                double add = Math.Min(share, limit[c] - widths[c]);
                widths[c] += add;
                free -= add;
            }
        }

        // Fractions share what is left; a track whose content needs more than its share keeps its content's width.
        if (flexible.Any(f => f))
        {
            bool[] inflexible = new bool[count];
            for (int round = 0; round <= count; round++)
            {
                double left = inner - gapsAll - Enumerable.Range(0, count).Where(c => !flexible[c] || inflexible[c]).Sum(c => widths[c]);
                double sum = Enumerable.Range(0, count).Where(c => flexible[c] && !inflexible[c]).Sum(c => fractions[c]);
                double unit = Math.Max(0, left) / Math.Max(1, sum);
                bool changed = false;
                for (int c = 0; c < count; c++)
                {
                    if (flexible[c] && !inflexible[c] && baseSize[c] > unit * fractions[c] + 0.001)
                    {
                        inflexible[c] = true;
                        widths[c] = baseSize[c];
                        changed = true;
                    }
                }

                if (!changed)
                {
                    for (int c = 0; c < count; c++)
                    {
                        if (flexible[c] && !inflexible[c])
                        {
                            widths[c] = unit * fractions[c];
                        }
                    }

                    break;
                }
            }
        }
        else if (layout.JustifyContent == LayoutAlignment.Stretch)
        {
            // Width still left goes to the auto tracks.
            free = inner - widths.Sum() - gapsAll;
            int[] autos = [.. Enumerable.Range(0, count).Where(c => placement.ColumnTrack(c).Max.Unit == GridTrackUnit.Auto)];
            if (free > 0.01 && autos.Length > 0)
            {
                foreach (int c in autos)
                {
                    widths[c] += free / autos.Length;
                }
            }
        }

        return widths;
    }

    /// <summary>
    /// The rows' heights: fixed sizes as given; the rest as tall as their items, items spanning rows adding what their rows lack;
    /// a container taller than its rows gives the rest to its fractions, or else to its auto rows.
    /// </summary>
    private static double[] SizeRows(GridLayout layout, GridPlacement placement, Dictionary<GridItem, (double Width, double Height)> sizes, double minInner)
    {
        int count = placement.Rows;
        double[] heights = new double[count];
        bool[] fixedRow = new bool[count];
        for (int r = 0; r < count; r++)
        {
            GridTrack track = placement.RowTrack(r);
            if (track.Min.Unit == GridTrackUnit.Points)
            {
                heights[r] = track.Min.Value;
                fixedRow[r] = track.Max.Unit == GridTrackUnit.Points;
            }
        }

        foreach (GridSlot slot in placement.Slots.OrderBy(s => s.RowSpan))
        {
            double height = sizes[slot.Item].Height;
            int end = Math.Min(count, slot.Row + slot.RowSpan);
            if (slot.RowSpan == 1)
            {
                if (!fixedRow[slot.Row])
                {
                    heights[slot.Row] = Math.Max(heights[slot.Row], height);
                }

                continue;
            }

            int[] spanned = [.. Enumerable.Range(slot.Row, end - slot.Row)];
            int[] growable = [.. spanned.Where(r => !fixedRow[r])];
            double lacking = height - spanned.Sum(r => heights[r]) - (layout.RowGap * (spanned.Length - 1));
            if (lacking > 0 && growable.Length > 0)
            {
                foreach (int r in growable)
                {
                    heights[r] += lacking / growable.Length;
                }
            }
        }

        double free = minInner - heights.Sum() - (layout.RowGap * Math.Max(0, count - 1));
        if (free > 0.01 && count > 0)
        {
            int[] fractions = [.. Enumerable.Range(0, count).Where(r => placement.RowTrack(r).Max.Unit == GridTrackUnit.Fraction)];
            if (fractions.Length > 0)
            {
                double sum = fractions.Sum(r => placement.RowTrack(r).Max.Value);
                foreach (int r in fractions)
                {
                    heights[r] += sum > 0 ? free * placement.RowTrack(r).Max.Value / sum : free / fractions.Length;
                }
            }
            else if (Enumerable.Range(0, count).Where(r => placement.RowTrack(r).Max.Unit == GridTrackUnit.Auto).ToArray() is { Length: > 0 } autos)
            {
                foreach (int r in autos)
                {
                    heights[r] += free / autos.Length;
                }
            }
        }

        return heights;
    }

    /// <summary>A track size that needs no content: points, or per cent of <paramref name="inner"/>; null for the rest.</summary>
    private static double? Fixed(GridTrackSize size, double inner)
    {
        return size.Unit switch
        {
            GridTrackUnit.Points => size.Value,
            GridTrackUnit.Percent => inner * size.Value / 100,
            _ => null,
        };
    }

    /// <summary>
    /// A content-sized track size for an item's narrowest and widest: auto is the narrowest as a least size and the widest as
    /// a greatest; null for the fixed sizes and fractions.
    /// </summary>
    private static double? Content(GridTrackSize size, double min, double max, bool greatest)
    {
        return size.Unit switch
        {
            GridTrackUnit.Auto => greatest ? max : min,
            GridTrackUnit.MinContent => min,
            GridTrackUnit.MaxContent => max,
            _ => null,
        };
    }

    /// <summary>An item's box in its area, by its justify and align settings.</summary>
    private LayoutRect ItemRect(GridPlan plan, GridSlot slot, double left, double top)
    {
        GridLayout layout = plan.Container.Layout;
        int lastColumn = slot.Column + slot.ColumnSpan - 1, lastRow = Math.Min(plan.RowY.Length, slot.Row + slot.RowSpan) - 1;
        double areaX = plan.ColumnX[slot.Column];
        double areaWidth = plan.ColumnX[lastColumn] + plan.ColumnWidth[lastColumn] - areaX;
        double areaY = plan.RowY[slot.Row];
        double areaHeight = plan.RowY[lastRow] + plan.RowHeight[lastRow] - areaY;
        LayoutAlignment justify = slot.Item.JustifySelf ?? layout.JustifyItems;
        LayoutAlignment align = slot.Item.AlignSelf ?? layout.AlignItems;
        double width = OuterWidthIn(slot.Item, areaWidth, justify, ItemIntrinsic(slot.Item, slot.Item.Box));
        double height = align == LayoutAlignment.Stretch ? areaHeight : Math.Min(areaHeight, OuterHeight(slot.Item, slot.Item.Box, width));
        LayoutRect outer = new(left + plan.ContentX + areaX + Align(justify, areaWidth, width), top + areaY + (align == LayoutAlignment.Stretch ? 0 : Align(align, areaHeight, height)), width, height);
        return Inside(slot.Item.Box, outer);
    }

    /// <summary>An item's outer width in its area: its own width with its margins, or the area's, or its content's when it does not stretch.</summary>
    private static double OuterWidthIn(GridItem item, double area, LayoutAlignment justify, (double Min, double Max) intrinsic)
    {
        double margins = item.Box?.MarginsX ?? 0;
        return item.Box?.Width switch
        {
            { Unit: WidthUnit.Points, Value: > 0 } points => Math.Min(points.Value + margins, area),
            { Unit: WidthUnit.Percent, Value: > 0 } percent => (area * percent.Value / 100) + margins,
            _ => justify == LayoutAlignment.Stretch ? area : Math.Clamp(intrinsic.Max, Math.Min(intrinsic.Min, area), area),
        };
    }

    /// <summary>A grid container placed whole at <paramref name="top"/>, without page breaks.</summary>
    private ContainerBox PlaceGridWhole(GridPlan plan, double x, double top)
    {
        BoxStyle? box = plan.Container.Box;
        double contentTop = top + (box?.InsetTop ?? 0);
        List<LayoutBox> items = [.. plan.Slots.Select(slot => (LayoutBox)LayItem(slot.Item, slot.Item.Box, ItemRect(plan, slot, x, contentTop)))];
        return new ContainerBox(plan.Container, new LayoutRect(x, top, plan.Width, plan.Height), box, items, false, false);
    }

    /// <summary>The grid's rows in bands that break as one: rows an item spans stay together.</summary>
    private static List<(int First, int Last)> GridBands(GridPlan plan)
    {
        List<(int, int)> bands = [];
        int rows = plan.RowY.Length;
        int r = 0;
        while (r < rows)
        {
            int last = r;
            for (int i = r; i <= last; i++)
            {
                foreach (GridSlot slot in plan.Slots)
                {
                    if (slot.Row == i)
                    {
                        last = Math.Max(last, Math.Min(rows, slot.Row + slot.RowSpan) - 1);
                    }
                }
            }

            bands.Add((r, last));
            r = last + 1;
        }

        return bands;
    }

    /// <summary>Flows a grid container down columns and pages, breaking between its rows.</summary>
    private void FlowGrid(GridContainer container)
    {
        BoxStyle? box = container.Box;
        double marginLeft = box?.MarginLeft ?? 0;
        y += TopMargin(box);
        GridPlan plan = PlanGrid(container, Math.Max(1, Column.Width - (box?.MarginsX ?? 0)));
        ColumnGeometry geometry = Shifted(Column, marginLeft);
        double gap = container.Layout.RowGap;
        bool continued = false;
        double partTop = y;
        bool partAtTop = AtColumnTop;
        List<LayoutBox> items = [];
        double at = y + (box?.InsetTop ?? 0);
        void EndPart()
        {
            page!.Add(column, new ContainerBox(container, new LayoutRect(geometry.X, partTop, plan.Width, Math.Max(0, ColumnBottom - partTop)), box, items, continued, true));
            NextColumn();
            geometry = Shifted(Column, marginLeft);
            continued = true;
            partTop = y;
            items = [];
        }

        foreach ((int first, int last) in GridBands(plan))
        {
            double bandHeight = plan.RowY[last] + plan.RowHeight[last] - plan.RowY[first];
            double before = first == 0 || (items.Count == 0 && continued) ? 0 : gap;
            bool overflows = at + before + bandHeight > ColumnBottom + 0.01;

            // A row of items one row tall that does not fit splits where the room ends, its items going on in the next part.
            if (overflows && first == last && plan.Slots.Where(s => s.Row == first).All(s => s.RowSpan == 1)
                && (ColumnBottom - at - before >= MinSplitRoom || (items.Count == 0 && partAtTop)))
            {
                at += before;
                double rowShift = at - plan.RowY[first];
                List<SplitItem> split = [.. plan.Slots.Where(s => s.Row == first).Select(s =>
                {
                    LayoutRect rect = ItemRect(plan, s, geometry.X, rowShift);
                    return new SplitItem(s.Item, s.Item.Box, rect.X, rect.Width);
                })];
                at = SplitBand(split, at, () => items, EndPart);
                partAtTop = false;
                continue;
            }

            if (overflows && !(items.Count == 0 && partAtTop))
            {
                if (items.Count == 0 && !continued)
                {
                    // Not even the first row fits: the whole grid moves to the next column or page.
                    NextColumn();
                    geometry = Shifted(Column, marginLeft);
                    partTop = y;
                    at = y + (box?.InsetTop ?? 0);
                }
                else
                {
                    EndPart();
                    at = y;
                    before = 0;
                }

                partAtTop = true;
            }

            at += before;

            // The band's rows are placed with the grid's own offsets, moved to where the band starts.
            double shift = at - plan.RowY[first];
            foreach (GridSlot slot in plan.Slots.Where(s => s.Row >= first && s.Row <= last))
            {
                items.Add(LayItem(slot.Item, slot.Item.Box, ItemRect(plan, slot, geometry.X, shift)));
            }

            at += bandHeight;
        }

        at += box?.InsetBottom ?? 0;
        if (!continued)
        {
            at = Math.Max(at, partTop + (box?.MinHeight ?? 0));
        }

        page!.Add(column, new ContainerBox(container, new LayoutRect(geometry.X, partTop, plan.Width, at - partTop), box, items, continued, false));
        y = at + (box?.MarginBottom ?? 0);
    }
}
