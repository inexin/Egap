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
}

/// <summary>Fills a document's merge fields from records (Phase 9 design Q11): MERGEFIELD with its switches, then IF.</summary>
internal sealed class MailMerger(Document document, MailMergeOptions options)
{
    private readonly CultureInfo culture = options.Culture ?? CultureInfo.InvariantCulture;
    private readonly WarningCollector warnings = new();

    public IReadOnlyList<ConversionWarning> Warnings => warnings.ToList();

    /// <summary>Fills every merge field in the document from <paramref name="record"/>.</summary>
    public void Fill(IMergeData record)
    {
        FillFields([.. document.GetFields()], record);
        RemoveUnusedRegionMarkers();
    }

    /// <summary>Fills the merge fields among <paramref name="fields"/>, innermost first, then the IF fields holding them.</summary>
    private void FillFields(List<Field> fields, IMergeData record)
    {
        HashSet<Paragraph> emptied = [];
        bool merged = false;
        foreach (Field field in fields.Where(f => f.Kind == FieldKind.MergeField).Reverse())
        {
            if (field.Start.Parent is null || field.Start.GetField() is not { } current)
            {
                continue;
            }

            FieldCode code = FieldCode.Parse(current.Code);
            if (code.Arguments.Count == 0 || IsRegionMarker(code.Arguments[0]))
            {
                continue;
            }

            string name = code.Arguments[0];
            string text;
            if (MergeData.TryGetPath(record, name, out object? value))
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
        foreach (Field field in document.GetFields().Where(f => f.Kind == FieldKind.If).Reverse().ToList())
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

    /// <summary>Removes region markers left after the regions were merged (or for regions the data did not have).</summary>
    private void RemoveUnusedRegionMarkers()
    {
        foreach (Field field in document.GetFields().Where(f => f.Kind == FieldKind.MergeField).ToList())
        {
            if (FieldCode.Parse(field.Code).Arguments is [string name, ..] && IsRegionMarker(name))
            {
                Replace(field, "");
            }
        }
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
