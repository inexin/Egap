using System.Globalization;

namespace Egap.Documents;

/// <summary>How a mail merge fills a template.</summary>
public sealed record MailMergeOptions
{
    /// <summary>
    /// Gets the culture numbers and dates are written in where a field gives no format; null (the default) for the
    /// invariant culture, so the output is the same on every machine.
    /// </summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>Gets whether a paragraph left empty because its fields had no value is removed, as Word's "suppress blank lines". Defaults to false.</summary>
    public bool RemoveEmptyParagraphs { get; init; }

    /// <summary>
    /// Gets how <see cref="Document.MailMergeAll(IEnumerable{IMergeData}, MailMergeOptions?)"/> starts each record after the
    /// first: on a new page (the default), continuously, or on the next odd or even page.
    /// </summary>
    public SectionStart Separator { get; init; } = SectionStart.NewPage;

    /// <summary>Gets what is called with each warning of a merge of many records, such as a field a record has no value for; null for none.</summary>
    public Action<ConversionWarning>? OnWarning { get; init; }
}

/// <summary>
/// Fills a document's merge fields from records (Phase 9 design Q11 and Q12): repeated regions first, between
/// TableStart:Name and TableEnd:Name or in a table row of Name.Field fields, then MERGEFIELD with its switches, then IF.
/// </summary>
internal sealed class MailMerger(Document document, MailMergeOptions options)
{
    /// <summary>How deep regions may nest, against hostile templates.</summary>
    private const int MaxDepth = 16;

    private readonly CultureInfo culture = options.Culture ?? CultureInfo.InvariantCulture;
    private readonly WarningCollector warnings = new();

    public IReadOnlyList<ConversionWarning> Warnings => warnings.ToList();

    /// <summary>Fills every merge field in the document from <paramref name="record"/>.</summary>
    public void Fill(IMergeData record)
    {
        FillScope([document], record, 0);
    }

    /// <summary>Fills the regions and fields inside <paramref name="roots"/> from <paramref name="record"/>.</summary>
    private void FillScope(List<Node> roots, IMergeData record, int depth)
    {
        // Regions by markers, outermost first; each pass removes one region's template, so the scan starts again.
        while (FirstMarker(roots) is { } start)
        {
            string name = MarkerName(start)!;
            Field? end = MatchingEnd(roots, start, name);
            List<Node>? units = end is null ? null : Units(start, end);
            if (units is null)
            {
                warnings.Add(ConversionWarningKind.MergeFieldsRemoved, $"Region {name} has no TableEnd:{name} in the same story or table");
                Replace(start, "");
                if (end is not null)
                {
                    Replace(end, "");
                }

                continue;
            }

            MergeRegion(roots, units, name, record, depth, [start, end!]);
        }

        // Regions by table rows whose fields all name one region (Lines.Name, Lines.Price); a repeated row itself is not one again.
        foreach (Row row in roots.SelectMany(r => r.Descendants<Row>()).ToList())
        {
            if (row.Parent is null || RowRegion(row) is not { } name || record.GetRegion(name) is null)
            {
                continue;
            }

            MergeRegion(roots, [row], name, record, depth, [], prefix: name + ".");
        }

        FillFields(roots, record);
    }

    /// <summary>Repeats a region's units once per record of the region, each filled, in place of the template.</summary>
    private void MergeRegion(List<Node> roots, List<Node> units, string name, IMergeData record, int depth, List<Field> markers, string? prefix = null)
    {
        foreach (Field marker in markers)
        {
            Replace(marker, "");
        }

        IEnumerable<IMergeData>? records = depth < MaxDepth ? record.GetRegion(name) : null;
        if (records is null)
        {
            warnings.Add(ConversionWarningKind.MergeFieldsRemoved, $"Region {name} has no records in the data");
        }
        else
        {
            foreach (IMergeData item in records)
            {
                List<Node> copies = [];
                foreach (Node unit in units)
                {
                    Node copy = unit.Clone();
                    InsertBefore(units[0], copy);
                    copies.Add(copy);
                }

                FillScope(copies, new ScopedRecord(item, record, prefix), depth + 1);
            }
        }

        foreach (Node unit in units)
        {
            roots.Remove(unit);
            unit.Remove();
        }
    }

    /// <summary>The nodes a region repeats: the rows from its start to its end in one table, else the blocks in one story.</summary>
    private static List<Node>? Units(Field start, Field end)
    {
        if (start.Start.GetAncestor<Row>() is { } startRow && end.Start.GetAncestor<Row>() is { } endRow && startRow.Parent == endRow.Parent && startRow.Parent is Table table)
        {
            int from = startRow.IndexInParent, to = endRow.IndexInParent;
            return from <= to ? [.. table.Rows.Skip(from).Take(to - from + 1)] : null;
        }

        // The blocks of the innermost container holding both markers.
        HashSet<Node> endChain = [];
        for (Node? node = end.Start.Paragraph; node is not null; node = node.Parent)
        {
            endChain.Add(node);
        }

        for (Node? node = start.Start.Paragraph; node is not null; node = node.Parent)
        {
            if (node is not Block first || node.Parent is not (BlockContainer or ContentControl) || !endChain.Contains(node.Parent))
            {
                continue;
            }

            Block? last = endChain.OfType<Block>().FirstOrDefault(b => b.Parent == first.Parent);
            if (last is null)
            {
                return null;
            }

            int from = first.IndexInParent, to = last.IndexInParent;
            IReadOnlyList<Block> blocks = first.Parent is BlockContainer container ? container.Blocks : ((ContentControl)first.Parent!).Blocks;
            return from <= to ? [.. blocks.Skip(from).Take(to - from + 1)] : null;
        }

        return null;
    }

    private static void InsertBefore(Node anchor, Node node)
    {
        switch (anchor.Parent, node)
        {
            case (Table table, Row row):
                table.Rows.Insert(anchor.IndexInParent, row);
                break;
            case (BlockContainer container, Block block):
                container.Blocks.Insert(anchor.IndexInParent, block);
                break;
            case (ContentControl control, Block block):
                control.Blocks.Insert(anchor.IndexInParent, block);
                break;
        }
    }

    /// <summary>The first TableStart marker in the scope, in document order.</summary>
    private static Field? FirstMarker(List<Node> roots)
    {
        return FieldsIn(roots).FirstOrDefault(f => MarkerName(f) is not null && IsStart(f));
    }

    /// <summary>The TableEnd marker that closes <paramref name="start"/>, with regions of the same name nested inside it counted.</summary>
    private static Field? MatchingEnd(List<Node> roots, Field start, string name)
    {
        int depth = 0;
        bool after = false;
        foreach (Field field in FieldsIn(roots))
        {
            if (field.Start == start.Start)
            {
                after = true;
                continue;
            }

            if (!after || !string.Equals(MarkerName(field), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsStart(field))
            {
                depth++;
            }
            else if (depth-- == 0)
            {
                return field;
            }
        }

        return null;
    }

    /// <summary>The region a row's merge fields all name with a prefix, such as Lines in Lines.Name; null for any other row.</summary>
    private static string? RowRegion(Row row)
    {
        string? region = null;
        foreach (Field field in row.Descendants<FieldStart>().Select(s => s.GetField()).OfType<Field>().Where(f => f.Kind == FieldKind.MergeField))
        {
            if (FieldCode.Parse(field.Code).Arguments is not [string name, ..] || IsRegionMarker(name) || name.IndexOf('.', StringComparison.Ordinal) is not (> 0 and int dot))
            {
                return null;
            }

            string prefix = name[..dot];
            if (region is not null && !string.Equals(region, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            region = prefix;
        }

        return region;
    }

    private static IEnumerable<Field> FieldsIn(List<Node> roots)
    {
        return roots.SelectMany(r => r.Descendants<FieldStart>()).Select(s => s.GetField()).OfType<Field>().Where(f => f.Kind == FieldKind.MergeField);
    }

    /// <summary>The region a TableStart:Name or TableEnd:Name marker names, or null for another field.</summary>
    private static string? MarkerName(Field field)
    {
        return FieldCode.Parse(field.Code).Arguments is [string name, ..] && IsRegionMarker(name) ? name[(name.IndexOf(':', StringComparison.Ordinal) + 1)..] : null;
    }

    private static bool IsStart(Field field) => FieldCode.Parse(field.Code).Arguments[0].StartsWith("TableStart:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Fills the merge fields inside <paramref name="roots"/>, innermost first, then the IF fields holding them.</summary>
    private void FillFields(List<Node> roots, IMergeData record)
    {
        HashSet<Paragraph> emptied = [];
        bool merged = false;
        foreach (Field field in FieldsIn(roots).Reverse().ToList())
        {
            if (field.Start.Parent is null || field.Start.GetField() is not { } current)
            {
                continue;
            }

            FieldCode code = FieldCode.Parse(current.Code);
            if (code.Arguments.Count == 0)
            {
                continue;
            }

            string name = code.Arguments[0];
            string text;
            if (IsRegionMarker(name))
            {
                text = "";
            }
            else if (MergeData.TryGetPath(record, name, out object? value))
            {
                text = Format(value, code);
            }
            else
            {
                warnings.Add(ConversionWarningKind.MergeFieldsRemoved, $"Merge field {name} has no value in the data");
                text = "";
            }

            Paragraph? paragraph = current.Start.Paragraph;
            Replace(current, text);
            merged = true;
            if (text.Length == 0 && paragraph is not null)
            {
                emptied.Add(paragraph);
            }
        }

        if (!merged)
        {
            return;
        }

        // IF fields compare the values just written into their codes, and are left as their results, as merged fields are.
        foreach (Field field in roots.SelectMany(r => r.Descendants<FieldStart>()).Select(s => s.GetField()).OfType<Field>().Where(f => f.Kind == FieldKind.If).Reverse().ToList())
        {
            if (field.Start.GetField() is { } current && current.Update() && current.Start.GetField() is { } updated)
            {
                Replace(updated, updated.Result);
            }
        }

        if (options.RemoveEmptyParagraphs)
        {
            foreach (Paragraph paragraph in emptied)
            {
                if (paragraph.Parent is BlockContainer { Blocks.Count: > 1 } or ContentControl { Blocks.Count: > 1 } && FieldUpdater.VisibleText(paragraph).Trim().Length == 0
                    && !paragraph.Descendants().Any(n => n is Picture or Shape or FieldStart))
                {
                    paragraph.Remove();
                }
            }
        }
    }

    /// <summary>TableStart:Name and TableEnd:Name fields, which mark repeated regions rather than take a value.</summary>
    internal static bool IsRegionMarker(string name)
    {
        return name.StartsWith("TableStart:", StringComparison.OrdinalIgnoreCase) || name.StartsWith("TableEnd:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A region's record, with what it lacks read from the record holding the region, so a line can show the order's
    /// number; a row region's fields drop the region's name before the dot.
    /// </summary>
    private sealed class ScopedRecord(IMergeData inner, IMergeData outer, string? prefix) : IMergeData
    {
        public bool TryGetValue(string name, out object? value)
        {
            if (prefix is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && MergeData.TryGetPath(inner, name[prefix.Length..], out value))
            {
                return true;
            }

            return MergeData.TryGetPath(inner, name, out value) || MergeData.TryGetPath(outer, name, out value);
        }

        public IEnumerable<IMergeData>? GetRegion(string name) => inner.GetRegion(name) ?? outer.GetRegion(name);
    }

    /// <summary>A value as the field's switches write it: \# and \@ pictures, \* formats, \b and \f text around it.</summary>
    private string Format(object? value, FieldCode code)
    {
        string text;
        if (code.Switch("\\#") is { Length: > 0 } picture && Number(value) is double number)
        {
            text = FieldCode.FormatNumber(number, picture);
        }
        else if (code.Switch("\\@") is { Length: > 0 } datePicture && Date(value) is DateTimeOffset date)
        {
            text = FieldCode.FormatDate(date, datePicture);
        }
        else
        {
            text = value switch
            {
                null => "",
                string s => s,
                DateTimeOffset d => d.ToString("d", culture),
                DateTime d => d.ToString("d", culture),
                DateOnly d => d.ToString("d", culture),
                IFormattable f => f.ToString(null, culture),
                _ => value.ToString() ?? "",
            };
        }

        text = code.ApplyGeneralFormat(text);
        if (text.Length > 0)
        {
            text = (code.Switch("\\B") ?? "") + text + (code.Switch("\\F") ?? "");
        }

        return text;
    }

    private double? Number(object? value) => value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        int or long or short or byte or uint or ulong or ushort or sbyte => Convert.ToDouble(value, CultureInfo.InvariantCulture),
        string s when double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, culture, out double parsed) => parsed,
        _ => null,
    };

    private DateTimeOffset? Date(object? value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Unspecified), TimeSpan.Zero),
        DateOnly d => new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
        string s when DateTimeOffset.TryParse(s, culture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) => parsed,
        _ => null,
    };

    /// <summary>
    /// Puts <paramref name="text"/> in place of the whole field, in the field's own formatting, as Word leaves a merged
    /// field. A field whose parts are in different paragraphs or links keeps its place and shows the text as its result.
    /// </summary>
    internal static void Replace(Field field, string text)
    {
        FieldStart start = field.Start;
        if (start.Parent is null || start.Parent != field.End.Parent)
        {
            FieldUpdater.SetResult(field, text);
            return;
        }

        Run? format = field.ResultNodes.OfType<Run>().FirstOrDefault() ?? field.CodeNodes.OfType<Run>().FirstOrDefault();
        if (text.Length > 0)
        {
            Run run = new(start.Document, text) { Style = format?.Style };
            if (format is not null)
            {
                run.Format.CopyRaw(format.Format);
            }

            if (start.PreviousSibling is Inline before)
            {
                FieldUpdater.Insert(before, run);
            }
            else
            {
                switch (start.Parent)
                {
                    case Paragraph paragraph:
                        paragraph.Inlines.Insert(0, run);
                        break;
                    case CompositeInline composite:
                        composite.Inlines.Insert(0, run);
                        break;
                }
            }
        }

        FieldEnd end = field.End;
        for (Node? node = start; node is Inline inline;)
        {
            Node? next = inline.NextSibling;
            inline.Remove();
            if (inline == end)
            {
                break;
            }

            node = next;
        }
    }
}
