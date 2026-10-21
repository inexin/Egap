using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>Where a block, table or figure sits: the left and right of the column (or the page's text area) it flows in.</summary>
internal readonly record struct Frame(double Left, double Right)
{
    public double Width => Right - Left;
}

/// <summary>A body item placed on its page: a block, table or figure with the frame it flows in.</summary>
internal sealed record PlacedItem(PageDraft Page, object Item, Frame Frame)
{
    public PageBox Box => Item switch
    {
        BlockDraft b => b.Box,
        TableDraft t => t.Box,
        FigureDraft f => f.Box,
        _ => PageBox.Empty,
    };
}

/// <summary>
/// One section of the document to build: a run of pages of one size and margins, and within them a run of content set in
/// the same columns. A new page size starts a section on a new page; a change of columns on a page starts one in place.
/// </summary>
internal sealed class SectionPlan
{
    public required int Group { get; init; }

    public required bool NewPage { get; init; }

    public required PageDraft FirstPage { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public required double Left { get; init; }

    public required double Right { get; init; }

    public required double Top { get; init; }

    public required double Bottom { get; init; }

    /// <summary>The columns' frames; one for a single column.</summary>
    public required IReadOnlyList<Frame> Columns { get; init; }

    public List<PlacedItem> Items { get; } = [];
}

/// <summary>
/// Plans sections (design Q6): page size and margins from where the body sits on each run of same-sized pages, and section
/// columns from blocks that stand side by side, with a continuous section break where a page changes from one column to
/// several or back.
/// </summary>
internal static class SectionPlanner
{
    /// <summary>Margins documents use, in points: whole and half inches, centimetres from 1.5 to 3.</summary>
    private static readonly double[] CommonMargins = [36, 54, 72, 90, 108, 42.52, 56.69, 70.87, 85.04];

    public static List<SectionPlan> Plan(IReadOnlyList<PageDraft> pages, Func<PageDraft, IReadOnlyList<object>> items)
    {
        List<SectionPlan> sections = [];
        int group = -1;
        for (int start = 0; start < pages.Count;)
        {
            int end = start + 1;
            while (end < pages.Count && Math.Abs(pages[end].Content.Width - pages[start].Content.Width) < 1 && Math.Abs(pages[end].Content.Height - pages[start].Content.Height) < 1)
            {
                end++;
            }

            group++;
            List<PageDraft> run = [.. pages.Skip(start).Take(end - start)];
            Margins(run, items, out double left, out double right, out double top, out double bottom);
            double width = run[0].Content.Width, height = run[0].Content.Height;
            Frame body = new(left, width - right);
            SectionPlan? current = null;
            foreach (PageDraft page in run)
            {
                IReadOnlyList<object> onPage = items(page);
                List<Frame> columns = Columns([.. onPage.OfType<BlockDraft>()], body);
                foreach (object item in onPage)
                {
                    PageBox box = Box(item);
                    Frame? column = columns.Count > 1 ? columns.Cast<Frame?>().FirstOrDefault(c => box.Left >= c!.Value.Left - 4 && box.Right <= c.Value.Right + 4) : null;
                    IReadOnlyList<Frame> set = column is null ? [body] : columns;
                    if (current is null || !Same(current.Columns, set))
                    {
                        current = new SectionPlan
                        {
                            Group = group,
                            NewPage = current is null,
                            FirstPage = page,
                            Width = width,
                            Height = height,
                            Left = left,
                            Right = right,
                            Top = top,
                            Bottom = bottom,
                            Columns = set,
                        };
                        sections.Add(current);
                    }

                    current.Items.Add(new PlacedItem(page, item, column ?? body));
                }
            }

            if (current is null)
            {
                sections.Add(new SectionPlan
                {
                    Group = group,
                    NewPage = true,
                    FirstPage = run[0],
                    Width = width,
                    Height = height,
                    Left = left,
                    Right = right,
                    Top = top,
                    Bottom = bottom,
                    Columns = [body],
                });
            }

            start = end;
        }

        return sections;
    }

    /// <summary>
    /// The columns of a page: the bands of x that narrow blocks fill side by side, two to four of them, each with a few lines
    /// and overlapping the others top to bottom. None (an empty list) for a page set in one column.
    /// </summary>
    internal static List<Frame> Columns(IReadOnlyList<BlockDraft> blocks, Frame body)
    {
        List<BlockDraft> narrow = [.. blocks.Where(b => b.Box.Width < body.Width * 0.6 && b.Role is not (LayoutRole.PageHeader or LayoutRole.PageFooter or LayoutRole.PageNumber))
            .OrderBy(b => b.Box.Left)];
        List<(Frame Frame, double Top, double Bottom, int Lines)> bands = [];
        foreach (BlockDraft block in narrow)
        {
            if (bands.Count > 0 && block.Box.Left <= bands[^1].Frame.Right + 2)
            {
                (Frame f, double t, double b, int n) = bands[^1];
                bands[^1] = (new Frame(f.Left, Math.Max(f.Right, block.Box.Right)), Math.Min(t, block.Box.Top), Math.Max(b, block.Box.Bottom), n + block.Lines.Count);
            }
            else
            {
                bands.Add((new Frame(block.Box.Left, block.Box.Right), block.Box.Top, block.Box.Bottom, block.Lines.Count));
            }
        }

        if (bands.Count is < 2 or > 4 || bands.Any(b => b.Lines < 3) || bands[^1].Frame.Right - bands[0].Frame.Left < body.Width * 0.7)
        {
            return [];
        }

        for (int i = 1; i < bands.Count; i++)
        {
            double overlap = Math.Min(bands[i].Bottom, bands[i - 1].Bottom) - Math.Max(bands[i].Top, bands[i - 1].Top);
            if (overlap < Math.Min(bands[i].Bottom - bands[i].Top, bands[i - 1].Bottom - bands[i - 1].Top) * 0.3)
            {
                return [];
            }
        }

        return [.. bands.Select(b => b.Frame)];
    }

    /// <summary>The margins of a run of same-sized pages: the middle of where each page's body starts and ends, snapped to a common value when near one.</summary>
    private static void Margins(List<PageDraft> run, Func<PageDraft, IReadOnlyList<object>> items, out double left, out double right, out double top, out double bottom)
    {
        List<double> lefts = [], rights = [], tops = [], bottoms = [];
        foreach (PageDraft page in run)
        {
            List<PageBox> boxes = [.. items(page).Select(Box).Concat(page.Blocks.Where(b => b.Role == LayoutRole.Footnote).Select(b => b.Box)).Where(b => !b.IsEmpty)];
            if (boxes.Count == 0)
            {
                continue;
            }

            lefts.Add(boxes.Min(b => b.Left));
            rights.Add(page.Content.Width - boxes.Max(b => b.Right));
            tops.Add(boxes.Min(b => b.Top));
            bottoms.Add(page.Content.Height - boxes.Max(b => b.Bottom));
        }

        left = Snap(Median(lefts));
        right = Snap(Median(rights));
        top = Snap(Median(tops));
        bottom = Snap(Median(bottoms));
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
        {
            return 72;
        }

        values.Sort();
        return Math.Max(0, values[values.Count / 2]);
    }

    /// <summary>A margin rounded to the half point, or a common margin within two points of it.</summary>
    internal static double Snap(double value)
    {
        double nearest = CommonMargins.MinBy(c => Math.Abs(value - c));
        return Math.Abs(value - nearest) <= 2 ? nearest : Math.Round(value * 2) / 2;
    }

    private static bool Same(IReadOnlyList<Frame> a, IReadOnlyList<Frame> b)
    {
        return a.Count == b.Count && a.Zip(b).All(p => Math.Abs(p.First.Left - p.Second.Left) < 8 && Math.Abs(p.First.Right - p.Second.Right) < 8);
    }

    internal static PageBox Box(object item)
    {
        return item switch
        {
            BlockDraft b => b.Box,
            TableDraft t => t.Box,
            FigureDraft f => f.Box,
            _ => PageBox.Empty,
        };
    }
}
