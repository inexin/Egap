using System.Globalization;
using System.Text.RegularExpressions;

namespace Egap.Documents;

/// <summary>
/// Recomputes the fields Egap understands (Phase 6 design Q8): dates and times, document properties, REF, NOTEREF, SEQ,
/// STYLEREF, HYPERLINK, IF on simple comparisons and TOC. Page fields (PAGE, NUMPAGES, SECTIONPAGES, PAGEREF) are left to
/// the layout engine; locked fields and fields Egap does not know keep their stored result.
/// </summary>
internal sealed class FieldUpdater
{
    private readonly Document document;
    private readonly DateTimeOffset now;
    private readonly Dictionary<FieldStart, int> sequenceValues = [];

    private FieldUpdater(Document document, DateTimeOffset now)
    {
        this.document = document;
        this.now = now;
    }

    public static int Update(Document document, DateTimeOffset now)
    {
        return new FieldUpdater(document, now).Run();
    }

    private int Run()
    {
        List<Field> fields = [.. document.GetFields()];
        NumberSequences(fields);
        int updated = 0;

        // In the order the fields end: document order, with nested fields before the field holding them, as Word updates.
        Dictionary<Node, int> order = [];
        foreach (Node node in document.Descendants())
        {
            if (node is FieldEnd)
            {
                order[node] = order.Count;
            }
        }

        fields.Sort((a, b) => order.GetValueOrDefault(a.End).CompareTo(order.GetValueOrDefault(b.End)));
        for (int i = 0; i < fields.Count; i++)
        {
            Field field = fields[i].Start.GetField() ?? fields[i];
            if (field.Start.IsLocked || field.Start.Parent is null)
            {
                continue;
            }

            if (Update(field))
            {
                field.Start.IsDirty = false;
                updated++;
            }
        }

        return updated;
    }

    private bool Update(Field field)
    {
        FieldCode code = FieldCode.Parse(field.Code);
        switch (field.Kind)
        {
            case FieldKind.Toc:
                return TableOfContents.Build(field, code);
            case FieldKind.Page or FieldKind.NumPages or FieldKind.SectionPages or FieldKind.PageRef:
                return false;
        }

        string? result = field.Kind switch
        {
            FieldKind.Date => FormatDate(now, code, "M/d/yyyy"),
            FieldKind.Time => FormatDate(now, code, "h:mm am/pm"),
            FieldKind.CreateDate => document.Properties.Created is { } created ? FormatDate(created, code, "M/d/yyyy h:mm:ss am/pm") : null,
            FieldKind.SaveDate => document.Properties.Saved is { } saved ? FormatDate(saved, code, "M/d/yyyy h:mm:ss am/pm") : null,
            FieldKind.PrintDate => document.Properties.Printed is { } printed ? FormatDate(printed, code, "M/d/yyyy h:mm:ss am/pm") : null,
            FieldKind.Author => document.Properties.Author ?? "",
            FieldKind.Title => document.Properties.Title ?? "",
            FieldKind.Subject => document.Properties.Subject ?? "",
            FieldKind.Keywords => document.Properties.Keywords ?? "",
            FieldKind.Comments => document.Properties.Comments ?? "",
            FieldKind.LastSavedBy => document.Properties.LastSavedBy ?? "",
            FieldKind.FileName => document.FileName is { } name ? (code.Has("\\P") ? name : Path.GetFileName(name)) : null,
            FieldKind.DocProperty => code.Arguments.Count > 0 ? document.Properties.Get(code.Arguments[0]) ?? "" : null,
            FieldKind.Ref => Ref(code),
            FieldKind.NoteRef => NoteRef(code),
            FieldKind.Seq => sequenceValues.TryGetValue(field.Start, out int value) && !code.Has("\\H") ? value.ToString(CultureInfo.InvariantCulture) : code.Has("\\H") ? "" : null,
            FieldKind.StyleRef => StyleRef(field, code),
            FieldKind.If => If(code),
            FieldKind.Hyperlink => field.Result.Length > 0 ? null : code.Arguments.FirstOrDefault(),
            _ => null,
        };
        if (result is null)
        {
            return false;
        }

        if (code.Switch("\\#") is { } picture && double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
        {
            result = FieldCode.FormatNumber(number, picture);
        }

        SetResult(field, code.ApplyGeneralFormat(result));
        return true;
    }

    /// <summary>SEQ values in document order: each name counts up, \r restarts, \c repeats the last value.</summary>
    private void NumberSequences(List<Field> fields)
    {
        Dictionary<string, int> counters = new(StringComparer.OrdinalIgnoreCase);
        foreach (Field field in fields)
        {
            if (field.Kind != FieldKind.Seq)
            {
                continue;
            }

            FieldCode code = FieldCode.Parse(field.Code);
            if (code.Arguments.Count == 0)
            {
                continue;
            }

            string name = code.Arguments[0];
            int value = counters.GetValueOrDefault(name);
            if (code.Switch("\\R") is { } reset && int.TryParse(reset, CultureInfo.InvariantCulture, out int start))
            {
                value = start;
            }
            else if (!code.Has("\\C"))
            {
                value++;
            }

            counters[name] = value;
            sequenceValues[field.Start] = value;
        }
    }

    private string? Ref(FieldCode code)
    {
        return code.Arguments.Count > 0 && document.GetBookmark(code.Arguments[0]) is { } bookmark ? bookmark.Text.Replace(ControlChar.ParagraphBreak, ' ') : null;
    }

    private string? NoteRef(FieldCode code)
    {
        if (code.Arguments.Count == 0 || document.GetBookmark(code.Arguments[0]) is not { } bookmark)
        {
            return null;
        }

        Footnote? note = bookmark.Start.NextSibling as Footnote;
        for (Node? node = bookmark.Start.NextSibling; note is null && node is not null && node != bookmark.End; node = node.NextSibling)
        {
            note = node as Footnote ?? node.Descendants<Footnote>().FirstOrDefault();
        }

        return note?.Mark;
    }

    private string? StyleRef(Field field, FieldCode code)
    {
        if (code.Arguments.Count == 0)
        {
            return null;
        }

        string name = code.Arguments[0];
        Style? style = document.Styles[name] ?? document.Styles.GetByName(name);
        if (style is null || field.Start.Paragraph is not { } here)
        {
            return null;
        }

        List<Paragraph> paragraphs = [.. document.Descendants<Paragraph>().Where(p => p.OwnerStory is Body)];
        int index = paragraphs.IndexOf(here);
        IEnumerable<Paragraph> before = index >= 0 ? paragraphs.Take(index + 1).Reverse() : paragraphs;
        Paragraph? found = before.FirstOrDefault(p => p.Style == style) ?? paragraphs.FirstOrDefault(p => p.Style == style);
        return found is null ? null : VisibleText(found);
    }

    private static string? If(FieldCode code)
    {
        if (code.Arguments.Count < 5)
        {
            return null;
        }

        string left = code.Arguments[0], op = code.Arguments[1], right = code.Arguments[2];
        bool numeric = double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
            & double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out double b);
        int comparison = numeric ? a.CompareTo(b) : string.Compare(left, right, StringComparison.Ordinal);
        bool truth = op switch
        {
            "=" => numeric ? comparison == 0 : Wildcard(left, right),
            "<>" => numeric ? comparison != 0 : !Wildcard(left, right),
            "<" => comparison < 0,
            ">" => comparison > 0,
            "<=" => comparison <= 0,
            ">=" => comparison >= 0,
            _ => false,
        };
        return truth ? code.Arguments[3] : code.Arguments[4];
    }

    private static bool Wildcard(string text, string pattern)
    {
        string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal).Replace("\\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(text, regex, RegexOptions.None, TimeSpan.FromSeconds(1));
    }

    private static string FormatDate(DateTimeOffset value, FieldCode code, string fallback)
    {
        return FieldCode.FormatDate(value, code.Switch("\\@") ?? fallback);
    }

    /// <summary>The text of a paragraph without field codes, for STYLEREF and the table of contents.</summary>
    internal static string VisibleText(Paragraph paragraph)
    {
        System.Text.StringBuilder text = new();
        int depth = 0;
        bool inCode = false;
        Stack<bool> codes = new();
        foreach (Node node in paragraph.Descendants())
        {
            switch (node)
            {
                case FieldStart:
                    codes.Push(inCode);
                    inCode = true;
                    depth++;
                    break;
                case FieldSeparator when depth > 0:
                    inCode = false;
                    break;
                case FieldEnd when depth > 0:
                    depth--;
                    inCode = codes.Pop();
                    break;
                case Run run when !inCode && node.OwnerStory == paragraph.OwnerStory:
                    text.Append(run.Text);
                    break;
                case Tab when !inCode:
                    text.Append(' ');
                    break;
            }
        }

        return text.ToString().Trim();
    }

    /// <summary>
    /// Replaces a field's result with one run of <paramref name="text"/>, formatted as the old result's first run (Word's
    /// MERGEFORMAT keeps the formatting much the same way), adding a separator when the field had none.
    /// </summary>
    internal static void SetResult(Field field, string text)
    {
        RunFormat? format = field.ResultNodes.OfType<Run>().FirstOrDefault()?.Format;
        RunFormat? keep = format is null ? null : CopyOf(format);
        FieldSeparator separator = ClearResult(field);
        Run run = new(field.Start.Document, text);
        if (keep is not null)
        {
            run.Format.Apply(keep);
        }

        Insert(separator, run);
    }

    /// <summary>
    /// Removes everything between the separator and the end (adding a separator when there is none), joining the end's
    /// paragraph to the separator's when the result spanned paragraphs.
    /// </summary>
    internal static FieldSeparator ClearResult(Field field)
    {
        FieldSeparator separator = field.Separator ?? InsertSeparator(field);
        FieldEnd end = field.End;
        if (separator.Parent == end.Parent)
        {
            while (separator.NextSibling is Inline next && next != end)
            {
                next.Remove();
            }

            return separator;
        }

        Paragraph? first = separator.Paragraph;
        Paragraph? last = end.Paragraph;
        if (first is null || last is null || first.Parent != last.Parent || separator.Parent != first || end.Parent != last)
        {
            // Parts in different containers (a hyperlink, a cell): clear what follows the separator in its own container only.
            while (separator.NextSibling is Inline next and not FieldEnd)
            {
                next.Remove();
            }

            return separator;
        }

        while (separator.NextSibling is Inline next)
        {
            next.Remove();
        }

        while (first.NextSibling is { } between && between != last)
        {
            between.Remove();
        }

        while (last.Inlines.Count > 0 && last.Inlines[0] != end)
        {
            last.Inlines.RemoveAt(0);
        }

        List<Inline> rest = [.. last.Inlines];
        last.Remove();
        foreach (Inline inline in rest)
        {
            inline.Remove();
            first.Inlines.Add(inline);
        }

        return separator;
    }

    /// <summary>Puts <paramref name="inline"/> right after <paramref name="after"/> in the same collection.</summary>
    internal static void Insert(Inline after, Inline inline)
    {
        switch (after.Parent)
        {
            case Paragraph paragraph:
                paragraph.Inlines.Insert(after.IndexInParent + 1, inline);
                break;
            case CompositeInline composite:
                composite.Inlines.Insert(after.IndexInParent + 1, inline);
                break;
        }
    }

    private static FieldSeparator InsertSeparator(Field field)
    {
        FieldSeparator separator = new(field.Start.Document);
        switch (field.End.Parent)
        {
            case Paragraph paragraph:
                paragraph.Inlines.Insert(field.End.IndexInParent, separator);
                break;
            case CompositeInline composite:
                composite.Inlines.Insert(field.End.IndexInParent, separator);
                break;
        }

        return separator;
    }

    private static RunFormat CopyOf(RunFormat format)
    {
        RunFormat copy = new();
        copy.CopyRaw(format);
        return copy;
    }
}
