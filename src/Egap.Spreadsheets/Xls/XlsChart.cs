using System.Buffers.Binary;
using System.Text;

using Egap.Documents.Charts;

namespace Egap.Spreadsheets.Xls;

/// <summary>
/// A chart substream ([MS-XLS] 2.1.7.20.1): the records between a chart's BOF and EOF, read into the chart model. It takes
/// what a chart is about: its chart groups (what each draws, stacked or not, 3D or not, on which axes), its series (the
/// cells of their names, values, categories and bubble sizes) and its title and legend. The formatting stays behind.
/// </summary>
internal static class XlsChart
{
    private const int Series = 0x1003, SeriesText = 0x100D, ChartFormat = 0x1014, Legend = 0x1015, Bar = 0x1017, Line = 0x1018,
        Pie = 0x1019, Area = 0x101A, Scatter = 0x101B, CrtLine = 0x101C, Text = 0x1025, ObjectLink = 0x1027, Begin = 0x1033,
        End = 0x1034, Chart3D = 0x103A, Radar = 0x103E, Surface = 0x103F, RadarArea = 0x1040, AxisParent = 0x1041,
        SerToCrt = 0x1045, SerParent = 0x104A, Brai = 0x1051, BopPop = 0x1061, SIIndex = 0x1065, Number = 0x0203, Label = 0x0204;

    private sealed class Group
    {
        public ChartType Type = ChartType.Unknown;
        public int Order;
        public bool ThreeD;
        public bool Cluster = true;
        public bool Stacked;
        public bool Percent;
        public bool Varied;
        public bool Secondary;
        public bool Legend;
        public bool HighLow;
        public int? Gap;
        public int? Overlap;
        public int? FirstSlice;
        public int? Hole;
        public int? BubbleScale;
        public bool BubbleWidth;
        public bool Filled;
        public bool Wireframe;
    }

    private sealed class Item
    {
        public string? Name;
        public string? NameReference;
        public string? Values;
        public string? Categories;
        public string? Sizes;
        public int Group;
        public bool Auxiliary;
        public bool LiteralValues;
        public bool LiteralCategories;
    }

    /// <summary>Reads a chart from its records; null when it has nothing the model can show.</summary>
    /// <param name="records">The records of the substream, after its BOF and before its EOF.</param>
    /// <param name="reference">Turns a formula's tokens into the text of a reference, such as <c>Sheet1!$B$2:$B$9</c>.</param>
    public static Chart? Read(IReadOnlyList<(int Type, byte[] Body)> records, Func<byte[], string?> reference)
    {
        List<Group> groups = [];
        List<Item> series = [];
        Stack<int> open = [];
        int last = 0;
        bool secondary = false;
        Item? item = null;
        Group? group = null;
        string? title = null;
        bool titled = false;
        int? textLink = null;
        string? text = null;

        // The values a series keeps in the chart itself: after an SIIndex record, NUMBER and LABEL records whose row is the
        // point and whose column the series.
        int cache = 0;
        Dictionary<(int Kind, int Series), SortedDictionary<int, (double? Number, string? Text)>> cached = [];

        foreach ((int type, byte[] body) in records)
        {
            ReadOnlySpan<byte> data = body;
            switch (type)
            {
                case Begin:
                    open.Push(last);
                    break;
                case End:
                    if (open.Count == 0)
                    {
                        break;
                    }

                    int closed = open.Pop();
                    if (closed == Series && item is not null)
                    {
                        series.Add(item);
                        item = null;
                    }
                    else if (closed == ChartFormat)
                    {
                        group = null;
                    }
                    else if (closed == AxisParent)
                    {
                        secondary = false;
                    }
                    else if (closed == Text)
                    {
                        if (textLink == 1)
                        {
                            titled = true;
                            title = text;
                        }

                        textLink = null;
                        text = null;
                    }

                    break;
                case Series:
                    item = new Item();
                    break;
                case AxisParent when data.Length >= 2:
                    secondary = BinaryPrimitives.ReadUInt16LittleEndian(data) == 1;
                    break;
                case ChartFormat when data.Length >= 20:
                    group = new Group { Secondary = secondary, Varied = (data[16] & 0x01) != 0, Order = BinaryPrimitives.ReadUInt16LittleEndian(data[18..]) };
                    groups.Add(group);
                    break;
                case Bar when group is not null && data.Length >= 6:
                    {
                        int flags = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
                        group.Type = (flags & 0x01) != 0 ? ChartType.Bar : ChartType.Column;
                        group.Overlap = -BinaryPrimitives.ReadInt16LittleEndian(data);
                        group.Gap = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
                        group.Stacked = (flags & 0x02) != 0;
                        group.Percent = (flags & 0x04) != 0;
                        break;
                    }

                case Line or Area when group is not null && data.Length >= 2:
                    {
                        int flags = BinaryPrimitives.ReadUInt16LittleEndian(data);
                        group.Type = type == Line ? ChartType.Line : ChartType.Area;
                        group.Stacked = (flags & 0x01) != 0;
                        group.Percent = (flags & 0x02) != 0;
                        break;
                    }

                case Pie when group is not null && data.Length >= 4:
                    {
                        int hole = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
                        group.Type = hole > 0 ? ChartType.Doughnut : ChartType.Pie;
                        group.FirstSlice = BinaryPrimitives.ReadUInt16LittleEndian(data) % 360;
                        group.Hole = hole > 0 ? Math.Clamp(hole, 10, 90) : null;
                        break;
                    }

                case Scatter when group is not null && data.Length >= 6:
                    {
                        bool bubbles = (BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) & 0x01) != 0;
                        group.Type = bubbles ? ChartType.Bubble : ChartType.Scatter;
                        if (bubbles)
                        {
                            group.BubbleScale = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(data), 0, 300);
                            group.BubbleWidth = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]) == 2;
                        }

                        break;
                    }

                case Radar or RadarArea when group is not null:
                    group.Type = ChartType.Radar;
                    group.Filled = type == RadarArea;
                    break;
                case Surface when group is not null && data.Length >= 2:
                    group.Type = ChartType.Surface;
                    group.Wireframe = (data[0] & 0x01) == 0;
                    break;
                case BopPop when group is not null && data.Length >= 1:
                    group.Type = data[0] == 2 ? ChartType.BarOfPie : ChartType.PieOfPie;
                    break;
                case Chart3D when group is not null && data.Length >= 14:
                    group.ThreeD = true;
                    group.Cluster = (BinaryPrimitives.ReadUInt16LittleEndian(data[12..]) & 0x02) != 0;
                    break;
                case Legend when group is not null:
                    group.Legend = true;
                    break;
                case CrtLine when group is not null && data.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(data) == 1:
                    group.HighLow = true;
                    break;
                case SerToCrt when item is not null && data.Length >= 2:
                    item.Group = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    break;
                case SerParent when item is not null:
                    // A trendline's or error bars' series, which belongs to another.
                    item.Auxiliary = true;
                    break;
                case Text:
                    textLink = null;
                    text = null;
                    break;
                case ObjectLink when data.Length >= 2 && open.Count > 0 && open.Peek() == Text:
                    textLink = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    break;
                case SeriesText when data.Length >= 4:
                    {
                        string value = ShortString(data[2..]);
                        if (open.Count > 0 && open.Peek() == Text)
                        {
                            text = value;
                        }
                        else if (item is not null && open.Count > 0 && open.Peek() == Series)
                        {
                            item.Name = value;
                        }

                        break;
                    }

                case SIIndex when data.Length >= 2:
                    cache = BinaryPrimitives.ReadUInt16LittleEndian(data);
                    break;
                case Number or Label when cache > 0 && data.Length >= 8 && open.Count == 0:
                    {
                        int point = BinaryPrimitives.ReadUInt16LittleEndian(data);
                        int column = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
                        if (!cached.TryGetValue((cache, column), out var points))
                        {
                            cached[(cache, column)] = points = [];
                        }

                        if (points.Count < 32_000)
                        {
                            points[point] = type == Number && data.Length >= 14 ? (BinaryPrimitives.ReadDoubleLittleEndian(data[6..]), null) : (null, LongString(data[6..]));
                        }

                        break;
                    }

                case Brai when data.Length >= 8:
                    {
                        int id = data[0];
                        int kind = data[1];
                        int size = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
                        string? cells = kind == 2 && size > 0 && size <= data.Length - 8 ? reference(data.Slice(8, size).ToArray()) : null;
                        if (open.Count > 0 && open.Peek() == Text)
                        {
                            break;
                        }

                        if (item is not null && open.Count > 0 && open.Peek() == Series)
                        {
                            switch (id)
                            {
                                case 0:
                                    item.NameReference = cells;
                                    break;
                                case 1:
                                    item.Values = cells;
                                    item.LiteralValues = kind == 1;
                                    break;
                                case 2:
                                    item.Categories = cells;
                                    item.LiteralCategories = kind == 1;
                                    break;
                                case 3:
                                    item.Sizes = cells;
                                    break;
                            }
                        }

                        break;
                    }
            }

            if (type is not (Begin or End))
            {
                last = type;
            }
        }

        groups.RemoveAll(g => g.Type == ChartType.Unknown);
        if (groups.Count == 0)
        {
            return null;
        }

        Chart chart;
        List<ChartPlot> plots = [];
        try
        {
            Group first = groups[0];
            chart = Chart.Create(Kind(first), first.ThreeD && first.Type is ChartType.Column or ChartType.Bar or ChartType.Line or ChartType.Area or ChartType.Pie or ChartType.Surface);
            plots.Add(chart.Plots[0]);
            foreach (Group other in groups.Skip(1))
            {
                try
                {
                    plots.Add(chart.AddPlot(Kind(other), other.Secondary));
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    // A plot that cannot share the chart, such as a pie after a column plot or one beside a plot without
                    // axes, goes with the first plot.
                    plots.Add(chart.Plots[0]);
                }
            }
        }
        catch (ArgumentException)
        {
            return null;
        }

        for (int i = 0; i < groups.Count; i++)
        {
            // A group that fell back to the first plot keeps its series there but not its settings, which are not that
            // plot's: a pie group's would reset a stacked column plot.
            if (i == 0 || !ReferenceEquals(plots[i], plots[0]))
            {
                Apply(plots[i], groups[i]);
            }
        }

        for (int number = 0; number < series.Count; number++)
        {
            Item one = series[number];
            if (one.Auxiliary || (one.Values is null && !one.LiteralValues))
            {
                continue;
            }

            // A series names its chart group by the group's drawing order (icrt).
            int index = groups.FindIndex(g => g.Order == one.Group);
            ChartPlot plot = plots[Math.Max(index, 0)];
            if (one.Values is null)
            {
                // Values kept in the chart, with categories kept the same way or none.
                SortedDictionary<int, (double? Number, string? Text)> values = cached.GetValueOrDefault((1, number)) ?? [];
                SortedDictionary<int, (double? Number, string? Text)>? labels = one.LiteralCategories ? cached.GetValueOrDefault((2, number)) : null;
                int count = values.Count == 0 ? 0 : values.Keys.Max() + 1;
                ChartSeries literal = plot.Series.Add(
                    one.NameReference is null ? one.Name : null,
                    labels is null ? null : Enumerable.Range(0, count).Select(i => labels.TryGetValue(i, out var label) ? label.Text ?? (label.Number is { } n ? XlsFormula.Number(n) : null) : null),
                    Enumerable.Range(0, count).Select(i => values.TryGetValue(i, out var value) ? value.Number : null));
                if (one.NameReference is not null)
                {
                    literal.NameReference = one.NameReference;
                }

                continue;
            }

            ChartSeries added = plot.Series.Add(one.Values, plot.Type is ChartType.Bubble or ChartType.Scatter or ChartType.Pie or ChartType.Doughnut || one.Categories is not null ? one.Categories : null, one.NameReference, plot.Type == ChartType.Bubble ? one.Sizes : null);
            if (one.NameReference is null && one.Name is not null)
            {
                added.Name = one.Name;
            }
        }

        chart.HasLegend = groups.Any(g => g.Legend);
        if (titled && !string.IsNullOrEmpty(title))
        {
            chart.Title.Text = title;
        }

        return chart;
    }

    private static ChartType Kind(Group group) => group.Type == ChartType.Line && group.HighLow ? ChartType.Stock : group.Type;

    private static void Apply(ChartPlot plot, Group group)
    {
        switch (plot.Type)
        {
            case ChartType.Column or ChartType.Bar:
                plot.Grouping = group.Percent ? ChartGrouping.PercentStacked : group.Stacked ? ChartGrouping.Stacked : plot.Is3D && !group.Cluster ? ChartGrouping.Standard : ChartGrouping.Clustered;
                plot.GapWidth = Math.Clamp(group.Gap ?? 150, 0, 500);
                if (!plot.Is3D)
                {
                    // A 3D plot has no overlap, also when a damaged group that is not 3D shares one.
                    plot.Overlap = Math.Clamp(group.Stacked || group.Percent ? 100 : group.Overlap ?? 0, -100, 100);
                }

                break;
            case ChartType.Line or ChartType.Area:
                plot.Grouping = group.Percent ? ChartGrouping.PercentStacked : group.Stacked ? ChartGrouping.Stacked : ChartGrouping.Standard;
                break;
            case ChartType.Pie or ChartType.Doughnut:
                if (!plot.Is3D)
                {
                    // A 3D pie turns with the chart's 3D rotation and has no first slice angle of its own.
                    plot.FirstSliceAngle = group.FirstSlice;
                }

                if (plot.Type == ChartType.Doughnut)
                {
                    plot.HoleSize = group.Hole;
                }

                break;
            case ChartType.Bubble:
                plot.BubbleScale = group.BubbleScale;
                plot.BubbleSizeIsWidth = group.BubbleWidth;
                break;
            case ChartType.Radar:
                plot.RadarStyle = group.Filled ? ChartRadarStyle.Filled : ChartRadarStyle.Marker;
                break;
            case ChartType.Surface:
                plot.Wireframe = group.Wireframe;
                break;
        }

        if (plot.Type is not (ChartType.Surface or ChartType.Stock))
        {
            plot.VaryColors = group.Varied;
        }
    }

    /// <summary>An XLUnicodeString: a two-byte count of characters, a byte that says how wide they are, the characters.</summary>
    private static string LongString(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return "";
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(data);
        bool wide = (data[2] & 0x01) != 0;
        data = data[3..];
        count = Math.Min(count, wide ? data.Length / 2 : data.Length);
        return wide ? Encoding.Unicode.GetString(data[..(count * 2)]) : Encoding.Latin1.GetString(data[..count]);
    }

    /// <summary>A ShortXLUnicodeString: a count of characters, a byte that says whether they take one byte or two, the characters.</summary>
    private static string ShortString(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return "";
        }

        int count = data[0];
        bool wide = (data[1] & 0x01) != 0;
        data = data[2..];
        count = Math.Min(count, wide ? data.Length / 2 : data.Length);
        return wide ? Encoding.Unicode.GetString(data[..(count * 2)]) : Encoding.Latin1.GetString(data[..count]);
    }
}
