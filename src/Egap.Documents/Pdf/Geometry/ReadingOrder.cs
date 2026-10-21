using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>
/// Reading order by recursive XY cut (design Q3, Build Plan task 4): a region is cut across where a band of whitespace runs
/// the whole width and read top to bottom, or cut down a column gutter running its whole height and read left to right,
/// recursively. When both cuts exist the gutter wins unless the band is wider than one and a half lines, so paragraph
/// breaks that happen to line up across columns do not slice the columns. An item that alone keeps a gutter from running
/// through (a pull quote between columns) is set aside and placed by its content order. The result is then checked against
/// the content order, which most generators already write in reading order: when the two disagree badly and the content
/// order reads down the page and across columns, the content order wins.
/// </summary>
internal static class ReadingOrder
{
    /// <summary>The indices of <paramref name="boxes"/> in reading order; <paramref name="content"/> is each item's place in the content stream.</summary>
    public static List<int> Order(IReadOnlyList<PageBox> boxes, IReadOnlyList<int> content, double size, LayoutThresholds thresholds)
    {
        List<int> geometric = [];
        List<int> floating = [];
        Cut([.. Enumerable.Range(0, boxes.Count)], boxes, size, thresholds, geometric, floating);

        // Set-aside items go before the first item that comes after them in the content.
        foreach (int item in floating.OrderBy(i => content[i]))
        {
            int at = geometric.FindIndex(i => content[i] > content[item]);
            geometric.Insert(at < 0 ? geometric.Count : at, item);
        }

        List<int> byContent = [.. Enumerable.Range(0, boxes.Count).OrderBy(i => content[i])];
        if (boxes.Count > 2 && Agreement(geometric, content) < thresholds.OrderAgreement && Plausible(byContent, boxes, size))
        {
            return byContent;
        }

        return geometric;
    }

    /// <summary>The share of item pairs the order puts in the same order as the content does.</summary>
    internal static double Agreement(List<int> order, IReadOnlyList<int> content)
    {
        long same = 0, pairs = 0;
        for (int i = 0; i < order.Count; i++)
        {
            for (int j = i + 1; j < order.Count; j++)
            {
                pairs++;
                same += content[order[i]] < content[order[j]] ? 1 : 0;
            }
        }

        return pairs == 0 ? 1 : same / (double)pairs;
    }

    /// <summary>Whether an order reads down the page and across columns: at most one step in five goes back up within a column.</summary>
    internal static bool Plausible(List<int> order, IReadOnlyList<PageBox> boxes, double size)
    {
        int back = 0;
        for (int k = 1; k < order.Count; k++)
        {
            PageBox a = boxes[order[k - 1]], b = boxes[order[k]];
            bool sameColumn = Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left);
            back += sameColumn && b.Top < a.Top - size ? 1 : 0;
        }

        return back * 5 <= order.Count - 1;
    }

    private static void Cut(List<int> items, IReadOnlyList<PageBox> boxes, double size, LayoutThresholds thresholds, List<int> output, List<int> floating,
        bool columnsFirst = false)
    {
        if (items.Count <= 1)
        {
            output.AddRange(items);
            return;
        }

        List<(List<int> Group, double Gap)> columns = Split(items, boxes, horizontal: false, thresholds.ColumnGap);
        if (columnsFirst && columns.Count > 1)
        {
            foreach ((List<int> group, _) in columns)
            {
                Cut(group, boxes, size, thresholds, output, floating);
            }

            return;
        }

        HashSet<List<int>> around = new(ReferenceEqualityComparer.Instance);
        List<(List<int> Group, double Gap)> bands = Merge(Split(items, boxes, horizontal: true, thresholds.BandGap), boxes, size, thresholds, floating, around);
        if (bands.Count == 1 && around.Contains(bands[0].Group))
        {
            Cut(bands[0].Group, boxes, size, thresholds, output, floating, columnsFirst: true);
            return;
        }

        bool useBands = bands.Count > 1 && (columns.Count <= 1 || bands.Skip(1).Max(b => b.Gap) > size * 1.5);
        if (useBands || columns.Count > 1)
        {
            foreach ((List<int> group, _) in useBands ? bands : columns)
            {
                Cut(group, boxes, size, thresholds, output, floating, around.Contains(group));
            }

            return;
        }

        if (SetAside(items, boxes, thresholds) is (List<int> rest, List<int> aside))
        {
            floating.AddRange(aside);
            Cut(rest, boxes, size, thresholds, output, floating);
            return;
        }

        output.AddRange(items.OrderBy(i => boxes[i].Top).ThenBy(i => boxes[i].Left));
    }

    /// <summary>
    /// Bands put back together where a gap does not really cross the columns: two neighbouring bands joined when the gap
    /// between them is under one and a half lines and the two together still split into columns (one column ended before
    /// the other); and a band of one or two narrow items between two such bands (a pull quote the columns flow around) set
    /// aside, so the bands around it join.
    /// </summary>
    private static List<(List<int> Group, double Gap)> Merge(List<(List<int> Group, double Gap)> bands, IReadOnlyList<PageBox> boxes, double size,
        LayoutThresholds thresholds, List<int> floating, HashSet<List<int>> around)
    {
        for (int i = 1; i < bands.Count; i++)
        {
            List<int> pair = [.. bands[i - 1].Group, .. bands[i].Group];
            if (bands[i].Gap <= size * 1.5 && Split(pair, boxes, horizontal: false, thresholds.ColumnGap).Count > 1)
            {
                bands[i - 1] = (pair, bands[i - 1].Gap);
                bands.RemoveAt(i);
                i = 0;
                continue;
            }

            if (i + 1 < bands.Count && bands[i].Group.Count <= 2 && bands[i].Gap <= size * 3 && bands[i + 1].Gap <= size * 3)
            {
                double width = bands.SelectMany(b => b.Group).Max(k => boxes[k].Right) - bands.SelectMany(b => b.Group).Min(k => boxes[k].Left);
                List<int> joined = [.. bands[i - 1].Group, .. bands[i + 1].Group];
                if (bands[i].Group.All(k => boxes[k].Width <= width * 2 / 3) && Split(bands[i - 1].Group, boxes, false, thresholds.ColumnGap).Count > 1
                    && Split(joined, boxes, horizontal: false, thresholds.ColumnGap).Count > 1)
                {
                    // The bands around the set-aside items are read column by column.
                    floating.AddRange(bands[i].Group);
                    around.Add(joined);
                    bands[i - 1] = (joined, bands[i - 1].Gap);
                    bands.RemoveRange(i, 2);
                    i = 0;
                }
            }
        }

        return bands;
    }

    /// <summary>
    /// Items grouped by the gaps between them along one axis (top to bottom when <paramref name="horizontal"/>, else left to
    /// right), each group with the gap before it; one group when no gap is at least <paramref name="least"/> wide.
    /// </summary>
    private static List<(List<int> Group, double Gap)> Split(List<int> items, IReadOnlyList<PageBox> boxes, bool horizontal, double least)
    {
        List<int> sorted = [.. items.OrderBy(i => horizontal ? boxes[i].Top : boxes[i].Left)];
        List<(List<int>, double)> groups = [([sorted[0]], 0)];
        double end = horizontal ? boxes[sorted[0]].Bottom : boxes[sorted[0]].Right;
        foreach (int i in sorted.Skip(1))
        {
            double start = horizontal ? boxes[i].Top : boxes[i].Left;
            if (start - end >= least)
            {
                groups.Add(([i], start - end));
            }
            else
            {
                groups[^1].Item1.Add(i);
            }

            end = Math.Max(end, horizontal ? boxes[i].Bottom : boxes[i].Right);
        }

        return groups;
    }

    /// <summary>
    /// A column gutter that runs through the region once a few narrow items crossing it are set aside: at most two items, or
    /// one in ten, none wider than two thirds of the region, with content on both sides. Null when there is no such gutter.
    /// </summary>
    private static (List<int> Remaining, List<int> Aside)? SetAside(List<int> items, IReadOnlyList<PageBox> boxes, LayoutThresholds thresholds)
    {
        double left = items.Min(i => boxes[i].Left), right = items.Max(i => boxes[i].Right);
        int allowed = Math.Max(2, items.Count / 10);
        (List<int> Remaining, List<int> Aside)? best = null;
        foreach (double x in items.SelectMany(i => new[] { boxes[i].Left - (thresholds.ColumnGap / 2), boxes[i].Right + (thresholds.ColumnGap / 2) }).Distinct())
        {
            if (x <= left || x >= right)
            {
                continue;
            }

            List<int> crossing = [.. items.Where(i => boxes[i].Left < x + (thresholds.ColumnGap / 2) && boxes[i].Right > x - (thresholds.ColumnGap / 2))];
            if (crossing.Count == 0 || crossing.Count > allowed || crossing.Any(i => boxes[i].Width > (right - left) * 2 / 3))
            {
                continue;
            }

            List<int> rest = [.. items.Except(crossing)];
            if (!rest.Any(i => boxes[i].Right <= x) || !rest.Any(i => boxes[i].Left >= x))
            {
                continue;
            }

            if (best is null || crossing.Count < best.Value.Aside.Count)
            {
                best = (rest, crossing);
            }
        }

        return best;
    }
}
