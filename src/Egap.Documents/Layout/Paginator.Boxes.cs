namespace Egap.Documents.Layout;

/// <summary>
/// Boxes around blocks: a group with a <see cref="ContentControl.Box"/> flows its blocks inside its padding and borders and
/// breaks across columns and pages, drawn open at each break (CSS box-decoration-break: slice); flex and grid containers and
/// boxed groups laid out whole where nothing breaks (cells, headers, text boxes, items); and content widths for sizing.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>A boxed group being flowed: where its part in the current column started.</summary>
    private sealed class OpenBox(ContentControl group, BoxStyle style, double x, double width)
    {
        public ContentControl Group { get; } = group;

        public BoxStyle Style { get; } = style;

        public double X { get; } = x;

        public double Width { get; } = width;

        public List<LayoutBox> Contents { get; set; } = [];

        public int Start { get; set; }

        public double Top { get; set; }

        public bool Continued { get; set; }
    }

    private readonly List<OpenBox> openBoxes = [];

    // The room boxed groups being flowed take from the column on each side.
    private double insetLeft;
    private double insetRight;

    /// <summary>The column being filled, less the room open boxed groups take at the sides.</summary>
    private ColumnGeometry Column
    {
        get
        {
            ColumnGeometry full = page!.Columns[column];
            return insetLeft == 0 && insetRight == 0 ? full : new ColumnGeometry(full.X + insetLeft, Math.Max(1, full.Width - insetLeft - insetRight));
        }
    }

    /// <summary>The outer width a box asks for in <paramref name="available"/>: its width, or all of it, never more.</summary>
    private static double OuterWidth(BoxStyle? box, double available)
    {
        double width = box?.Width switch
        {
            { Unit: WidthUnit.Points, Value: > 0 } points => points.Value,
            { Unit: WidthUnit.Percent, Value: > 0 } percent => available * percent.Value / 100,
            _ => available,
        };
        return Math.Clamp(width, Math.Min(available, box?.Horizontal ?? 0), Math.Max(1, available));
    }

    /// <summary>
    /// Flows a group with a box: its padding and borders narrow the column for its blocks, and each column or page it reaches
    /// gets a <see cref="ContainerBox"/> holding its part.
    /// </summary>
    private void FlowBoxedGroup(ContentControl group, BoxStyle box)
    {
        GroupPaging? paging = group.Paging;
        if (paging is not null && (paging.KeepTogether || paging.ShowEntire || paging.EnsureSpace is not null) && !AtColumnTop)
        {
            // Kept together or asking for room: the whole box moves on when it lacks it, before any of it is placed.
            measuring++;
            LayWhole(group, 0, 0, Column.Width, out double height);
            measuring--;
            double room = ColumnBottom - y;
            double full = ColumnBottom - page!.ColumnTop(column);
            if ((paging.EnsureSpace is double need && room < need) || ((paging.KeepTogether || paging.ShowEntire) && height > room + 0.01 && (height <= full + 0.01 || paging.ShowEntire)))
            {
                NextColumn();
            }
        }

        double top = y + TopMargin(box);
        y = top;
        ColumnGeometry outer = Column;
        double width = OuterWidth(box, Math.Max(1, outer.Width - box.MarginsX));
        OpenBox open = new(group, box, outer.X + box.MarginLeft, width);
        StartPart(open, top, continued: false);
        y += box.InsetTop;
        double left = box.MarginLeft + box.InsetLeft;
        double right = box.InsetRight + (outer.Width - box.MarginLeft - width);
        insetLeft += left;
        insetRight += right;
        openBoxes.Add(open);
        try
        {
            if (paging is not null && (paging.StopPaging || paging.ScaleToFit))
            {
                FlowGroup(group, paging with { KeepTogether = false, ShowEntire = false, EnsureSpace = null });
            }
            else
            {
                FlowBlocks(group.Blocks);
            }
        }
        finally
        {
            openBoxes.Remove(open);
            insetLeft -= left;
            insetRight -= right;
        }

        y += box.InsetBottom;
        if (!open.Continued && y - open.Top < box.MinHeight)
        {
            y = open.Top + box.MinHeight;
        }

        EndPart(open, y, continues: false);
        y += box.MarginBottom;
    }

    /// <summary>A box's top margin where it starts: left out at the top of a column, except where the document starts.</summary>
    private double TopMargin(BoxStyle? box)
    {
        return box is null || box.MarginTop == 0 || (AtColumnTop && (pages.Count > 1 || column > 0)) ? 0 : box.MarginTop;
    }

    private void StartPart(OpenBox open, double top, bool continued)
    {
        open.Contents = page!.ColumnContents(column);
        open.Start = open.Contents.Count;
        open.Top = top;
        open.Continued = continued;
    }

    /// <summary>Wraps what the box's part holds in the column so far in its <see cref="ContainerBox"/>.</summary>
    private static void EndPart(OpenBox open, double bottom, bool continues)
    {
        List<LayoutBox> inside = open.Contents.GetRange(open.Start, open.Contents.Count - open.Start);
        open.Contents.RemoveRange(open.Start, inside.Count);
        open.Contents.Add(new ContainerBox(open.Group, new LayoutRect(open.X, open.Top, open.Width, Math.Max(0, bottom - open.Top)), open.Style, inside, open.Continued, continues));
    }

    /// <summary>Before the flow moves to another column or page: each open box's part ends at the column's foot.</summary>
    private void BreakBoxes()
    {
        if (openBoxes.Count == 0 || page is null)
        {
            return;
        }

        // The innermost first, so an outer box's part holds the inner one's.
        double bottom = page.ColumnBottom(column);
        for (int i = openBoxes.Count - 1; i >= 0; i--)
        {
            EndPart(openBoxes[i], bottom, continues: true);
        }
    }

    /// <summary>After the flow moved to another column or page: each open box starts a new part at its top.</summary>
    private void ResumeBoxes()
    {
        foreach (OpenBox open in openBoxes)
        {
            StartPart(open, y, continued: true);
        }
    }

    /// <summary>
    /// Lays a flex or grid container or a boxed group out whole at <paramref name="top"/>, without page breaks; null for other
    /// blocks. Measuring takes the height from the plan without placing the items.
    /// </summary>
    private ContainerBox? LayWhole(Block block, double x, double top, double width, out double height)
    {
        BoxStyle? margins = block switch
        {
            FlexContainer flex => flex.Box,
            GridContainer grid => grid.Box,
            ContentControl group => group.Box,
            _ => null,
        };
        x += margins?.MarginLeft ?? 0;
        top += margins?.MarginTop ?? 0;
        width = Math.Max(1, width - (margins?.MarginsX ?? 0));
        ContainerBox? result;
        switch (block)
        {
            case FlexContainer flex:
                {
                    FlexPlan plan = PlanFlex(flex, width);
                    height = plan.Height;
                    result = measuring > 0 ? null : PlaceFlexWhole(plan, x, top);
                    break;
                }

            case GridContainer grid:
                {
                    GridPlan plan = PlanGrid(grid, width);
                    height = plan.Height;
                    result = measuring > 0 ? null : PlaceGridWhole(plan, x, top);
                    break;
                }

            case ContentControl { Box: { } box } group:
                {
                    double outer = OuterWidth(box, width);
                    (List<LayoutBox> blocks, double inner) = LayBlocks(group.Blocks, x + box.InsetLeft, top + box.InsetTop, Math.Max(1, outer - box.Horizontal));
                    height = Math.Max(box.MinHeight, inner + box.Vertical);
                    result = measuring > 0 ? null : new ContainerBox(group, new LayoutRect(x, top, outer, height), box, blocks, false, false);
                    break;
                }

            default:
                height = 0;
                return null;
        }

        height += margins?.MarginsY ?? 0;
        return result;
    }

    /// <summary>A container's narrowest and widest content widths, its blocks' widest of each (CSS min-content and max-content).</summary>
    private (double Min, double Max) ContentWidths(BlockContainer container)
    {
        return IntrinsicOf(container.Blocks);
    }

    private (double Min, double Max) IntrinsicOf(IEnumerable<Block> blocks)
    {
        double min = 0;
        double max = 0;
        foreach (Block block in blocks)
        {
            (double blockMin, double blockMax) = IntrinsicOf(block);
            min = Math.Max(min, blockMin);
            max = Math.Max(max, blockMax);
        }

        return (min, max);
    }

    private (double Min, double Max) IntrinsicOf(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                {
                    ParagraphContent content = ContentOf(paragraph);
                    Egami.Fonts.Layout.TextLayout layout = content.Text.Layout(float.PositiveInfinity);
                    double indents = content.Format.LeftIndent + content.Format.RightIndent + Math.Max(0, content.Format.FirstLineIndent);
                    return (layout.MinIntrinsicWidth + indents, layout.MaxIntrinsicWidth + indents);
                }

            case Table nested:
                return (36, nested.Grid?.Sum() ?? 144);

            case ContentControl { Box: { } box } group:
                {
                    (double min, double max) = IntrinsicOf(group.Blocks);
                    return WithMargins(box, Fixed(box, (min + box.Horizontal, max + box.Horizontal)));
                }

            case ContentControl group:
                return IntrinsicOf(group.Blocks);

            case FlexContainer flex:
                {
                    FlexLayout layout = flex.Layout;
                    double min = 0, max = 0;
                    int count = 0;
                    foreach (FlexItem item in flex.Items)
                    {
                        (double itemMin, double itemMax) = ItemIntrinsic(item, item.Box);
                        if (layout.IsRow)
                        {
                            min = layout.Wrap == FlexWrap.NoWrap ? min + itemMin : Math.Max(min, itemMin);
                            max += itemMax;
                        }
                        else
                        {
                            min = Math.Max(min, itemMin);
                            max = Math.Max(max, itemMax);
                        }

                        count++;
                    }

                    if (layout.IsRow && count > 1)
                    {
                        max += layout.ColumnGap * (count - 1);
                        if (layout.Wrap == FlexWrap.NoWrap)
                        {
                            min += layout.ColumnGap * (count - 1);
                        }
                    }

                    double extra = flex.Box?.Horizontal ?? 0;
                    return WithMargins(flex.Box, Fixed(flex.Box, (min + extra, max + extra)));
                }

            case GridContainer grid:
                {
                    // Each column's widest single-column item, summed: what the grid needs with nothing wrapped and with all wrapped.
                    GridPlacement placement = PlaceGridItems(grid);
                    double[] mins = new double[placement.Columns];
                    double[] maxes = new double[placement.Columns];
                    for (int c = 0; c < placement.Columns; c++)
                    {
                        GridTrack track = placement.ColumnTrack(c);
                        if (track.Min.Unit == GridTrackUnit.Points)
                        {
                            mins[c] = maxes[c] = track.Min.Value;
                        }
                    }

                    foreach (GridSlot slot in placement.Slots.Where(s => s.ColumnSpan == 1))
                    {
                        (double itemMin, double itemMax) = ItemIntrinsic(slot.Item, slot.Item.Box);
                        mins[slot.Column] = Math.Max(mins[slot.Column], itemMin);
                        maxes[slot.Column] = Math.Max(maxes[slot.Column], itemMax);
                    }

                    double gaps = grid.Layout.ColumnGap * Math.Max(0, placement.Columns - 1);
                    double extra = (grid.Box?.Horizontal ?? 0) + gaps;
                    return WithMargins(grid.Box, Fixed(grid.Box, (mins.Sum() + extra, maxes.Sum() + extra)));
                }

            default:
                return (0, 0);
        }
    }

    /// <summary>An item's narrowest and widest outer widths: its content's with its borders and padding, or its own width.</summary>
    private (double Min, double Max) ItemIntrinsic(BlockContainer item, BoxStyle? box)
    {
        (double min, double max) = ContentWidths(item);
        double extra = box?.Horizontal ?? 0;
        return WithMargins(box, Fixed(box, (min + extra, max + extra)));
    }

    /// <summary>Widths with the box's margins across added.</summary>
    private static (double Min, double Max) WithMargins(BoxStyle? box, (double Min, double Max) widths)
    {
        double margins = box?.MarginsX ?? 0;
        return (widths.Min + margins, widths.Max + margins);
    }

    /// <summary>A box's own width in points, where it gives one, for both its narrowest and widest.</summary>
    private static (double Min, double Max) Fixed(BoxStyle? box, (double Min, double Max) content)
    {
        return box?.Width is { Unit: WidthUnit.Points, Value: > 0 } width ? (width.Value, width.Value) : content;
    }

    /// <summary>The height an item's blocks take at an outer width, with its borders and padding and its least height.</summary>
    private double ItemHeight(BlockContainer item, BoxStyle? box, double outerWidth)
    {
        double inner = Math.Max(1, outerWidth - (box?.Horizontal ?? 0));
        return Math.Max(box?.MinHeight ?? 0, MeasureBlocks(item.Blocks, inner) + (box?.Vertical ?? 0));
    }

    /// <summary>An item laid out in its box at <paramref name="bounds"/>.</summary>
    private ItemBox LayItem(BlockContainer item, BoxStyle? box, LayoutRect bounds)
    {
        double left = box?.InsetLeft ?? 0, top = box?.InsetTop ?? 0;
        (List<LayoutBox> blocks, _) = LayBlocks(item.Blocks, bounds.X + left, bounds.Y + top, Math.Max(1, bounds.Width - (box?.Horizontal ?? 0)));
        return new ItemBox(item, bounds, box, blocks);
    }

    /// <summary>The offset of the first box and the space between boxes when <paramref name="free"/> is shared out by <paramref name="alignment"/>.</summary>
    private static (double Before, double Between) Distribute(LayoutAlignment alignment, double free, int count)
    {
        if (free <= 0 || count == 0)
        {
            return (0, 0);
        }

        return alignment switch
        {
            LayoutAlignment.End => (free, 0),
            LayoutAlignment.Center => (free / 2, 0),
            LayoutAlignment.SpaceBetween => count > 1 ? (0, free / (count - 1)) : (0, 0),
            LayoutAlignment.SpaceAround => (free / count / 2, free / count),
            LayoutAlignment.SpaceEvenly => (free / (count + 1), free / (count + 1)),
            _ => (0, 0),
        };
    }

    /// <summary>Where a box of <paramref name="size"/> goes in <paramref name="room"/> by <paramref name="alignment"/>.</summary>
    private static double Align(LayoutAlignment alignment, double room, double size)
    {
        return alignment switch
        {
            LayoutAlignment.End => room - size,
            LayoutAlignment.Center => (room - size) / 2,
            _ => 0,
        };
    }
}
