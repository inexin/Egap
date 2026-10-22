namespace Egap.Documents.Layout;

/// <summary>
/// Flex containers (CSS Flexible Box Layout, section 9): items sized from their basis or content, gathered into lines,
/// grown and shrunk by their factors to fill each line, lined up along and across it, and the lines stacked down the page.
/// The page flow breaks between lines; a column container's items are each a line of their own.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>An item placed in its line: offsets from the content box's left and the line's top, and its outer size.</summary>
    private readonly record struct FlexPlaced(FlexItem Item, double X, double Y, double Width, double Height);

    /// <summary>One line of items: the space before it (gap and alignment), its height, and its items.</summary>
    private sealed record FlexLine(double Before, double Height, List<FlexPlaced> Items);

    /// <summary>A flex container measured at one width: its outer size, its content box's offset and its lines.</summary>
    private sealed record FlexPlan(FlexContainer Container, double Width, double Height, double ContentX, List<FlexLine> Lines);

    /// <summary>Plans made in this pass, by container and width; a nested container is planned when measured and when placed.</summary>
    private readonly Dictionary<(FlexContainer Container, double Width), FlexPlan> flexPlans = [];

    private FlexPlan PlanFlex(FlexContainer container, double available)
    {
        if (!flexPlans.TryGetValue((container, available), out FlexPlan? plan))
        {
            plan = MakeFlexPlan(container, available);
            flexPlans[(container, available)] = plan;
        }

        return plan;
    }

    /// <summary>An item's sizes before the line is resolved.</summary>
    private sealed class FlexSizing(FlexItem item)
    {
        public FlexItem Item { get; } = item;

        public double Base { get; set; }

        public double Min { get; set; }

        public double Max { get; set; }

        public double Hypothetical { get; set; }

        public double Target { get; set; }

        public bool Frozen { get; set; }
    }

    private FlexPlan MakeFlexPlan(FlexContainer container, double available)
    {
        FlexLayout layout = container.Layout;
        BoxStyle? box = container.Box;
        double outer = OuterWidth(box, available);
        double inner = Math.Max(1, outer - (box?.Horizontal ?? 0));
        double minInner = Math.Max(0, (box?.MinHeight ?? 0) - (box?.Vertical ?? 0));
        List<FlexItem> items = [.. container.Items.OrderBy(i => i.Order)];
        List<FlexLine> lines = layout.IsRow ? RowLines(layout, items, inner, minInner) : ColumnLines(layout, items, inner, minInner);
        double content = lines.Sum(l => l.Before + l.Height);
        double height = Math.Max(box?.MinHeight ?? 0, content + (box?.Vertical ?? 0));
        return new FlexPlan(container, outer, height, box?.InsetLeft ?? 0, lines);
    }

    private List<FlexLine> RowLines(FlexLayout layout, List<FlexItem> items, double inner, double minInner)
    {
        // Each item's flex base size and its hypothetical size: the base held to its automatic minimum, the content's
        // narrowest (or its own width, where that is smaller).
        List<FlexSizing> sizings = [];
        foreach (FlexItem item in items)
        {
            (double contentMin, double contentMax) = ItemIntrinsic(item, item.Box);
            double margins = item.Box?.MarginsX ?? 0;
            double? width = item.Box?.Width switch
            {
                { Unit: WidthUnit.Points, Value: > 0 } points => points.Value + margins,
                { Unit: WidthUnit.Percent, Value: > 0 } percent => (inner * percent.Value / 100) + margins,
                _ => null,
            };
            double basis = item.Basis switch
            {
                { Unit: WidthUnit.Points } points => points.Value + margins,
                { Unit: WidthUnit.Percent } percent => (inner * percent.Value / 100) + margins,
                _ => width ?? contentMax,
            };
            double min = width is double given ? Math.Min(contentMin, given) : contentMin;
            min = Math.Max(min, (item.Box?.Horizontal ?? 0) + margins);
            sizings.Add(new FlexSizing(item) { Base = basis, Min = min, Max = double.PositiveInfinity, Hypothetical = Math.Max(basis, min) });
        }

        // Lines: as many items as fit, at least one each; a container that does not wrap has one.
        List<List<FlexSizing>> groups = [];
        List<FlexSizing> current = [];
        double used = 0;
        foreach (FlexSizing sizing in sizings)
        {
            double add = sizing.Hypothetical + (current.Count > 0 ? layout.ColumnGap : 0);
            if (layout.Wrap != FlexWrap.NoWrap && current.Count > 0 && used + add > inner + 0.01)
            {
                groups.Add(current);
                current = [];
                used = 0;
                add = sizing.Hypothetical;
            }

            current.Add(sizing);
            used += add;
        }

        if (current.Count > 0)
        {
            groups.Add(current);
        }

        List<FlexLine> lines = [];
        foreach (List<FlexSizing> group in groups)
        {
            ResolveFlexible(group, inner - (layout.ColumnGap * (group.Count - 1)));

            // Across the line: each item's height at its resolved width; the line is as tall as its tallest.
            double[] heights = [.. group.Select(s => OuterHeight(s.Item, s.Item.Box, s.Target))];
            double lineHeight = heights.Length == 0 ? 0 : heights.Max();
            if (layout.Wrap == FlexWrap.NoWrap && groups.Count == 1)
            {
                // A single-line container's line is as tall as the container.
                lineHeight = Math.Max(lineHeight, minInner);
            }

            lines.Add(new FlexLine(0, lineHeight, PlaceLine(layout, group, heights, inner, lineHeight)));
        }

        if (layout.Wrap == FlexWrap.WrapReverse)
        {
            lines.Reverse();
        }

        return AlignLines(layout, lines, minInner, layout.RowGap);
    }

    /// <summary>
    /// Resolves the items' widths on a line (CSS flexbox 9.7): the free space is shared by grow factors, or taken back by
    /// shrink factors weighted by base size; items held at their minimum are frozen and the rest shared again.
    /// </summary>
    private static void ResolveFlexible(List<FlexSizing> line, double space)
    {
        bool growing = line.Sum(s => s.Hypothetical) < space;
        foreach (FlexSizing s in line)
        {
            s.Target = s.Hypothetical;
            double factor = growing ? s.Item.Grow : s.Item.Shrink;
            s.Frozen = factor == 0 || (growing && s.Base > s.Hypothetical) || (!growing && s.Base < s.Hypothetical);
        }

        double initialFree = space - line.Sum(s => s.Frozen ? s.Target : s.Base);
        for (int round = 0; round < line.Count + 1 && line.Any(s => !s.Frozen); round++)
        {
            List<FlexSizing> open = [.. line.Where(s => !s.Frozen)];
            double free = space - line.Sum(s => s.Frozen ? s.Target : s.Base);
            double factors = open.Sum(s => growing ? s.Item.Grow : s.Item.Shrink);
            if (factors < 1 && Math.Abs(initialFree * factors) < Math.Abs(free))
            {
                free = initialFree * factors;
            }

            if (growing)
            {
                foreach (FlexSizing s in open)
                {
                    s.Target = s.Base + (factors > 0 ? free * s.Item.Grow / factors : 0);
                }
            }
            else
            {
                double scaled = open.Sum(s => s.Item.Shrink * s.Base);
                foreach (FlexSizing s in open)
                {
                    s.Target = s.Base + (scaled > 0 ? free * s.Item.Shrink * s.Base / scaled : 0);
                }
            }

            // Held to the minimum: those under it are frozen there when the total change says so, the rest freeze as they are.
            double violation = 0;
            foreach (FlexSizing s in open)
            {
                double clamped = Math.Max(s.Min, Math.Min(s.Max, s.Target));
                violation += clamped - s.Target;
            }

            foreach (FlexSizing s in open)
            {
                double clamped = Math.Max(s.Min, Math.Min(s.Max, s.Target));
                if (violation == 0 || (violation > 0 && clamped > s.Target) || (violation < 0 && clamped < s.Target))
                {
                    s.Target = clamped;
                    s.Frozen = true;
                }
            }
        }
    }

    /// <summary>Places a row line's items along it by justify-content and across it by their alignment.</summary>
    private static List<FlexPlaced> PlaceLine(FlexLayout layout, List<FlexSizing> group, double[] heights, double inner, double lineHeight)
    {
        double total = group.Sum(s => s.Target) + (layout.ColumnGap * (group.Count - 1));
        (double before, double between) = Distribute(layout.JustifyContent, inner - total, group.Count);
        List<FlexPlaced> placed = [];
        double x = before;
        for (int i = 0; i < group.Count; i++)
        {
            FlexSizing s = group[i];
            LayoutAlignment align = s.Item.AlignSelf ?? layout.AlignItems;
            double height = align == LayoutAlignment.Stretch ? lineHeight : heights[i];
            double y = align == LayoutAlignment.Stretch ? 0 : Align(align, lineHeight, height);
            double left = layout.Direction == FlexDirection.RowReverse ? inner - x - s.Target : x;
            placed.Add(new FlexPlaced(s.Item, left, y, s.Target, height));
            x += s.Target + layout.ColumnGap + between;
        }

        return placed;
    }

    private List<FlexLine> ColumnLines(FlexLayout layout, List<FlexItem> items, double inner, double minInner)
    {
        if (layout.Direction == FlexDirection.ColumnReverse)
        {
            items.Reverse();
        }

        // Each item is a line: across, it stretches to the width or takes its content's; down, its basis or its content.
        List<(FlexItem Item, double Width, double Height)> sized = [];
        foreach (FlexItem item in items)
        {
            LayoutAlignment align = item.AlignSelf ?? layout.AlignItems;
            (double min, double max) = ItemIntrinsic(item, item.Box);
            double margins = item.Box?.MarginsX ?? 0;
            double width = item.Box?.Width switch
            {
                { Unit: WidthUnit.Points, Value: > 0 } points => points.Value + margins,
                { Unit: WidthUnit.Percent, Value: > 0 } percent => (inner * percent.Value / 100) + margins,
                _ => align == LayoutAlignment.Stretch ? inner : Math.Clamp(max, Math.Min(min, inner), inner),
            };
            double height = OuterHeight(item, item.Box, width);
            if (item.Basis is { Unit: WidthUnit.Points } basis)
            {
                height = Math.Max(height, basis.Value + (item.Box?.MarginsY ?? 0));
            }

            sized.Add((item, width, height));
        }

        // A container taller than its items: the grow factors share the rest, or justify-content places the items in it.
        double free = minInner - sized.Sum(s => s.Height) - (layout.RowGap * Math.Max(0, sized.Count - 1));
        double grow = sized.Sum(s => s.Item.Grow);
        if (free > 0 && grow > 0)
        {
            for (int i = 0; i < sized.Count; i++)
            {
                sized[i] = sized[i] with { Height = sized[i].Height + (free * sized[i].Item.Grow / grow) };
            }

            free = 0;
        }

        (double before, double between) = Distribute(layout.JustifyContent, free, sized.Count);
        List<FlexLine> lines = [];
        for (int i = 0; i < sized.Count; i++)
        {
            (FlexItem item, double width, double height) = sized[i];
            LayoutAlignment align = item.AlignSelf ?? layout.AlignItems;
            double x = Align(align, inner, width);
            double space = i == 0 ? before : layout.RowGap + between;
            lines.Add(new FlexLine(space, height, [new FlexPlaced(item, x, 0, width, height)]));
        }

        return lines;
    }

    /// <summary>
    /// The lines down the container with the gap between them; a container taller than its lines shares the rest by
    /// align-content, stretching the lines and their stretched items for stretch.
    /// </summary>
    private static List<FlexLine> AlignLines(FlexLayout layout, List<FlexLine> lines, double minInner, double gap)
    {
        double free = minInner - lines.Sum(l => l.Height) - (gap * Math.Max(0, lines.Count - 1));
        if (lines.Count > 1 && free > 0 && layout.AlignContent == LayoutAlignment.Stretch)
        {
            double extra = free / lines.Count;
            lines = [.. lines.Select(l => l with
            {
                Height = l.Height + extra,
                Items = [.. l.Items.Select(p => (p.Item.AlignSelf ?? layout.AlignItems) == LayoutAlignment.Stretch ? p with { Height = p.Height + extra } : p)],
            })];
            free = 0;
        }

        (double before, double between) = lines.Count > 1 ? Distribute(layout.AlignContent, free, lines.Count) : (0, 0);
        List<FlexLine> result = [];
        for (int i = 0; i < lines.Count; i++)
        {
            result.Add(lines[i] with { Before = i == 0 ? before : gap + between });
        }

        return result;
    }

    /// <summary>A flex container placed whole at <paramref name="top"/>, without page breaks.</summary>
    private ContainerBox PlaceFlexWhole(FlexPlan plan, double x, double top)
    {
        BoxStyle? box = plan.Container.Box;
        List<LayoutBox> items = [];
        double at = top + (box?.InsetTop ?? 0);
        foreach (FlexLine line in plan.Lines)
        {
            at += line.Before;
            PlaceFlexLine(plan, line, x, at, items);
            at += line.Height;
        }

        return new ContainerBox(plan.Container, new LayoutRect(x, top, plan.Width, plan.Height), box, items, false, false);
    }

    private void PlaceFlexLine(FlexPlan plan, FlexLine line, double x, double top, List<LayoutBox> into)
    {
        foreach (FlexPlaced placed in line.Items)
        {
            into.Add(LayItem(placed.Item, placed.Item.Box, Inside(placed.Item.Box, new LayoutRect(x + plan.ContentX + placed.X, top + placed.Y, placed.Width, placed.Height))));
        }
    }

    /// <summary>Flows a flex container down columns and pages, breaking between its lines.</summary>
    private void FlowFlex(FlexContainer container)
    {
        BoxStyle? box = container.Box;
        double marginLeft = box?.MarginLeft ?? 0;
        y += TopMargin(box);
        FlexPlan plan = PlanFlex(container, Math.Max(1, Column.Width - (box?.MarginsX ?? 0)));
        ColumnGeometry geometry = Shifted(Column, marginLeft);
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

        foreach (FlexLine line in plan.Lines)
        {
            double before = items.Count == 0 && continued ? 0 : line.Before;
            bool overflows = at + before + line.Height > ColumnBottom + 0.01;

            // A line that does not fit splits where the room ends, its items going on in the next part.
            if (overflows && (ColumnBottom - at - before >= MinSplitRoom || (items.Count == 0 && partAtTop)))
            {
                at += before;
                double left = geometry.X + plan.ContentX;
                List<SplitItem> split = [.. line.Items.Select(p =>
                {
                    LayoutRect rect = Inside(p.Item.Box, new LayoutRect(left + p.X, at, p.Width, p.Height));
                    return new SplitItem(p.Item, p.Item.Box, rect.X, rect.Width);
                })];
                at = SplitBand(split, at, () => items, EndPart);
                partAtTop = false;
                continue;
            }

            if (overflows && !(items.Count == 0 && partAtTop))
            {
                if (items.Count == 0)
                {
                    // Not even the first line fits: the whole container moves to the next column or page.
                    NextColumn();
                    geometry = Shifted(Column, marginLeft);
                    partTop = y;
                    partAtTop = true;
                    at = y + (box?.InsetTop ?? 0);
                }
                else
                {
                    // The line starts the container's next part, in the next column or on the next page.
                    EndPart();
                    partAtTop = true;
                    at = y;
                    before = 0;
                }
            }

            at += before;
            PlaceFlexLine(plan, line, geometry.X, at, items);
            at += line.Height;
        }

        at += box?.InsetBottom ?? 0;
        if (!continued)
        {
            at = Math.Max(at, partTop + (box?.MinHeight ?? 0));
        }

        page!.Add(column, new ContainerBox(container, new LayoutRect(geometry.X, partTop, plan.Width, at - partTop), box, items, continued, false));
        y = at + (box?.MarginBottom ?? 0);
    }

    /// <summary>A column moved right by a box's left margin.</summary>
    private static ColumnGeometry Shifted(ColumnGeometry geometry, double by)
    {
        return by == 0 ? geometry : new ColumnGeometry(geometry.X + by, Math.Max(1, geometry.Width - by));
    }

    /// <summary>An item's outer height at an outer width: its box's height with its margins down.</summary>
    private double OuterHeight(BlockContainer item, BoxStyle? box, double outerWidth)
    {
        return ItemHeight(item, box, Math.Max(1, outerWidth - (box?.MarginsX ?? 0))) + (box?.MarginsY ?? 0);
    }

    /// <summary>An item's box within its outer rectangle: the rectangle less its margins.</summary>
    private static LayoutRect Inside(BoxStyle? box, LayoutRect outer)
    {
        return box is null ? outer : new LayoutRect(outer.X + box.MarginLeft, outer.Y + box.MarginTop, Math.Max(0, outer.Width - box.MarginsX), Math.Max(0, outer.Height - box.MarginsY));
    }
}
