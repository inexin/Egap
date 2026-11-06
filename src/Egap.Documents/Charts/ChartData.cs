using System.Collections;
using System.Globalization;
using System.Xml.Linq;

namespace Egap.Documents.Charts;

/// <summary>
/// One set of a chart's values: a reference to cells, such as <c>Sheet1!$B$2:$B$5</c>, and the values the file keeps for it,
/// which is what a chart draws from when the cells are not at hand. Values are read from the file each time, so hold on to
/// a list you read more than once.
/// </summary>
public sealed class ChartData
{
    /// <summary>Excel's most points in a series; a file that says more is taken at this count.</summary>
    internal const int MaxPoints = 1 << 20;

    private readonly ChartNode holder;
    private readonly bool extended;

    /// <param name="holder">In a c: chart, the element that holds the data (<c>c:val</c>, <c>c:cat</c>); in a cx: chart, the dimension itself.</param>
    /// <param name="extended">Whether this is a cx: chart's dimension.</param>
    internal ChartData(ChartNode holder, bool extended)
    {
        this.holder = holder;
        this.extended = extended;
    }

    /// <summary>The reference to the cells, or null when the values are written in the chart.</summary>
    public string? Reference
    {
        get => extended ? (string?)holder.Find()?.Element(ChartNs.Cx + "f") : (string?)Form()?.Element(ChartNs.C + "f");
        set
        {
            if (extended)
            {
                SetExtendedReference(value);
                return;
            }

            XElement? form = Form();
            if (value is null)
            {
                if (form is not null)
                {
                    bool numeric = form.Name.LocalName is "numRef" or "numLit";
                    form.Element(ChartNs.C + "f")?.Remove();
                    ChartText.Rename(form, ChartNs.C + (numeric ? "numLit" : "strLit"));
                    if (form.Element(ChartNs.C + (numeric ? "numCache" : "strCache")) is { } cache)
                    {
                        cache.Remove();
                        form.Add(cache.Elements());
                    }
                }

                return;
            }

            if (form is null)
            {
                form = new XElement(ChartNs.C + "numRef");
                holder.Ensure().AddFirst(form);
            }
            else if (form.Name.LocalName is "numLit" or "strLit")
            {
                bool numeric = form.Name.LocalName == "numLit";
                XElement cache = new(ChartNs.C + (numeric ? "numCache" : "strCache"), form.Elements());
                form.RemoveNodes();
                form.Add(cache);
                ChartText.Rename(form, ChartNs.C + (numeric ? "numRef" : "strRef"));
            }

            if (form.Element(ChartNs.C + "f") is { } f)
            {
                f.Value = value;
            }
            else
            {
                form.AddFirst(new XElement(ChartNs.C + "f", value));
            }
        }
    }

    /// <summary>Whether the file keeps these values as numbers.</summary>
    public bool IsNumeric => extended
        ? holder.Find()?.Name.LocalName == "numDim"
        : Form()?.Name.LocalName is "numRef" or "numLit";

    /// <summary>The number format of the values, from their cells, or null.</summary>
    public string? FormatCode => extended
        ? (string?)holder.Find()?.Element(ChartNs.Cx + "lvl")?.Attribute("formatCode")
        : (string?)Cache()?.Element(ChartNs.C + "formatCode");

    /// <summary>How many points there are, blanks included; 0 when the file keeps no values.</summary>
    public int Count
    {
        get
        {
            XElement? level = Levels().FirstOrDefault();
            if (level is null)
            {
                return 0;
            }

            return extended
                ? Math.Clamp(ChartText.Int((string?)level.Attribute("ptCount")) ?? Points(level).Length, 0, MaxPoints)
                : Math.Clamp(ChartText.Int((string?)Cache()?.Element(ChartNs.C + "ptCount")?.Attribute("val")) ?? Points(level).Length, 0, MaxPoints);
        }
    }

    /// <summary>The values as numbers, one to a point; null for a blank or for text that is not a number.</summary>
    public IReadOnlyList<double?> Numbers
    {
        get
        {
            XElement? level = Levels().FirstOrDefault();
            (int Index, string Text)[] points = level is null ? [] : Points(level);
            return new PointList<double?>(Count, points.Select(p => p.Index).ToArray(), points.Select(p => ChartText.Double(p.Text)).ToArray());
        }
    }

    /// <summary>The values as the file writes them, one to a point; null for a blank.</summary>
    public IReadOnlyList<string?> Text
    {
        get
        {
            XElement? level = Levels().FirstOrDefault();
            return level is null ? new PointList<string?>(0, [], []) : TextOf(level);
        }
    }

    /// <summary>Labels in levels, innermost first, for categories taken from more than one column; one level for plain data.</summary>
    public IReadOnlyList<IReadOnlyList<string?>> TextLevels => [.. Levels().Select(TextOf)];

    /// <summary>Writes numbers as the values kept, and the data then counts as numeric. The reference stays.</summary>
    public void SetNumbers(IEnumerable<double?> values, string? formatCode = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<XElement> points = [];
        int count = 0;
        foreach (double? value in values)
        {
            if (value is { } number)
            {
                points.Add(Point(count, ChartText.Text(number)));
            }

            count++;
        }

        Write(numeric: true, levels: false, count, formatCode, [points]);
    }

    /// <summary>Writes text as the values kept, and the data then counts as text. The reference stays.</summary>
    public void SetText(IEnumerable<string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        List<XElement> points = [];
        int count = 0;
        foreach (string? value in values)
        {
            if (value is not null)
            {
                points.Add(Point(count, value));
            }

            count++;
        }

        Write(numeric: false, levels: false, count, null, [points]);
    }

    /// <summary>Writes labels in levels, innermost first, for categories taken from more than one column. The reference stays.</summary>
    /// <exception cref="ArgumentException">There are no levels.</exception>
    public void SetTextLevels(IEnumerable<IEnumerable<string?>> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        List<List<XElement>> written = [];
        int count = 0;
        foreach (IEnumerable<string?> level in levels)
        {
            List<XElement> points = [];
            int index = 0;
            foreach (string? value in level)
            {
                if (value is not null)
                {
                    points.Add(Point(index, value));
                }

                index++;
            }

            count = Math.Max(count, index);
            written.Add(points);
        }

        if (written.Count == 0)
        {
            throw new ArgumentException("Labels need at least one level.", nameof(levels));
        }

        Write(numeric: false, levels: true, count, null, written);
    }

    private XElement Point(int index, string text)
    {
        string safe = Docx.OpcPackageWriter.XmlSafe(text);
        return extended
            ? new XElement(ChartNs.Cx + "pt", new XAttribute("idx", index), safe)
            : new XElement(ChartNs.C + "pt", new XAttribute("idx", index), new XElement(ChartNs.C + "v", safe));
    }

    private void Write(bool numeric, bool levels, int count, string? formatCode, List<List<XElement>> written)
    {
        string? reference = Reference;
        string count2 = count.ToString(CultureInfo.InvariantCulture);
        if (extended)
        {
            XElement dimension = holder.Ensure();
            dimension.Elements(ChartNs.Cx + "lvl").Remove();
            ChartText.Rename(dimension, ChartNs.Cx + (numeric ? "numDim" : "strDim"));
            foreach (List<XElement> level in written)
            {
                dimension.Add(new XElement(
                    ChartNs.Cx + "lvl",
                    new XAttribute("ptCount", count2),
                    numeric ? new XAttribute("formatCode", formatCode ?? FormatCode ?? "General") : null,
                    level));
            }

            return;
        }

        string? oldFormat = formatCode ?? FormatCode;
        Form()?.Remove();
        XElement data = holder.Ensure();
        XElement form;
        if (levels)
        {
            form = new XElement(
                ChartNs.C + "multiLvlStrRef",
                new XElement(ChartNs.C + "f", reference ?? ""),
                new XElement(ChartNs.C + "multiLvlStrCache", new XElement(ChartNs.C + "ptCount", new XAttribute("val", count2)), written.Select(l => new XElement(ChartNs.C + "lvl", l))));
        }
        else
        {
            object?[] content =
            [
                numeric ? new XElement(ChartNs.C + "formatCode", oldFormat ?? "General") : null,
                new XElement(ChartNs.C + "ptCount", new XAttribute("val", count2)),
                written[0],
            ];
            form = reference is null
                ? new XElement(ChartNs.C + (numeric ? "numLit" : "strLit"), content)
                : new XElement(ChartNs.C + (numeric ? "numRef" : "strRef"), new XElement(ChartNs.C + "f", reference), new XElement(ChartNs.C + (numeric ? "numCache" : "strCache"), content));
        }

        data.AddFirst(form);
    }

    private void SetExtendedReference(string? value)
    {
        if (value is null)
        {
            holder.Find()?.Element(ChartNs.Cx + "f")?.Remove();
            return;
        }

        XElement dimension = holder.Ensure();
        if (dimension.Element(ChartNs.Cx + "f") is { } f)
        {
            f.Value = value;
        }
        else
        {
            dimension.AddFirst(new XElement(ChartNs.Cx + "f", value));
        }
    }

    /// <summary>In a c: chart, the element the data is written in.</summary>
    private XElement? Form() => holder.Find()?.Elements().FirstOrDefault(e => e.Name.LocalName is "numRef" or "strRef" or "numLit" or "strLit" or "multiLvlStrRef");

    /// <summary>In a c: chart, the element that holds the points or levels.</summary>
    private XElement? Cache()
    {
        XElement? form = Form();
        return form?.Name.LocalName switch
        {
            "numRef" => form.Element(ChartNs.C + "numCache"),
            "strRef" => form.Element(ChartNs.C + "strCache"),
            "multiLvlStrRef" => form.Element(ChartNs.C + "multiLvlStrCache"),
            _ => form,
        };
    }

    private IEnumerable<XElement> Levels()
    {
        if (extended)
        {
            return holder.Find()?.Elements(ChartNs.Cx + "lvl") ?? [];
        }

        XElement? cache = Cache();
        if (cache is null)
        {
            return [];
        }

        return cache.Name.LocalName == "multiLvlStrCache" ? cache.Elements(ChartNs.C + "lvl") : [cache];
    }

    private PointList<string?> TextOf(XElement level)
    {
        (int Index, string Text)[] points = Points(level);
        return new PointList<string?>(Count, points.Select(p => p.Index).ToArray(), points.Select(p => (string?)p.Text).ToArray());
    }

    /// <summary>A level's points in order of index; the last one wins for an index given twice.</summary>
    private (int Index, string Text)[] Points(XElement level)
    {
        SortedDictionary<int, string> points = [];
        foreach (XElement point in level.Elements(level.Name.Namespace + "pt"))
        {
            if (ChartText.Int((string?)point.Attribute("idx")) is { } index and >= 0 and < MaxPoints)
            {
                points[index] = extended ? point.Value : (string?)point.Element(ChartNs.C + "v") ?? "";
            }
        }

        return [.. points.Select(p => (p.Key, p.Value))];
    }

    /// <summary>A list as long as the data says, holding only the points the file has, so a file cannot make it big by saying so.</summary>
    private sealed class PointList<T>(int count, int[] indexes, T[] values) : IReadOnlyList<T>
    {
        public int Count { get; } = Math.Max(count, 0);

        public T this[int index]
        {
            get
            {
                ArgumentOutOfRangeException.ThrowIfNegative(index);
                ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
                int found = Array.BinarySearch(indexes, index);
                return found >= 0 ? values[found] : default!;
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
