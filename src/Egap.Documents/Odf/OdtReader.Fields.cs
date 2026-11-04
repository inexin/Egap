using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>Fields (ODF 1.3 Part 3, 7) as Word's field codes, and tracked changes (5.5) as revisions.</summary>
internal sealed partial class OdtReader
{
    private readonly Dictionary<string, (Revision? Revision, XElement Change)> changes = new(StringComparer.Ordinal);
    private readonly List<(string Id, Revision Revision)> insertions = [];
    private Revision? deletion;

    // Fields shown as their value: ODF has them, Word's fields have nothing like them.
    private static readonly HashSet<string> ValueFields = new(StringComparer.Ordinal)
    {
        "variable-get", "variable-set", "variable-input", "user-field-get", "user-field-input", "expression", "page-variable-get",
        "page-variable-set", "execute-macro", "dde-connection", "paragraph-count", "table-count", "image-count", "object-count",
        "editing-cycles", "editing-duration", "printed-by", "print-time", "modification-time", "sender-firstname",
        "sender-lastname", "sender-initials", "sender-title", "sender-position", "sender-email", "sender-phone-private",
        "sender-fax", "sender-company", "sender-phone-work", "sender-street", "sender-city", "sender-postal-code",
        "sender-country", "sender-state-or-province", "script", "drop-down", "database-name", "database-row-number",
        "sheet-name", "measure", "table-formula", "template-name", "creator", "author-initials",
    };

    private void ReadChanges(XElement body)
    {
        if (body.Element(TextNs + "tracked-changes") is not { } tracked)
        {
            return;
        }

        document.Settings.TrackRevisions = tracked.Attr(TextNs, "track-changes") != "false";
        foreach (XElement region in tracked.Elements(TextNs + "changed-region"))
        {
            string? id = region.Attr(TextNs, "id") ?? region.Attr(XmlNs, "id");
            XElement? change = region.Elements().FirstOrDefault();
            if (id is null || change is null || changes.Count >= MaxEntries * 10)
            {
                continue;
            }

            XElement? info = change.Element(OfficeNs + "change-info");
            string author = info?.Element(DcNs + "creator")?.Value ?? "";
            DateTimeOffset? date = OdfUnits.Date(info?.Element(DcNs + "date")?.Value);
            Revision? revision = change.Name.LocalName switch
            {
                "insertion" => new Revision(RevisionKind.Insertion, author, date),
                "deletion" => new Revision(RevisionKind.Deletion, author, date),
                _ => null,
            };
            changes.TryAdd(id, (revision, change));
        }
    }

    /// <summary>The revision text read now is part of: a deletion being read, else the innermost insertion.</summary>
    private Revision? CurrentRevision() => deletion ?? (insertions.Count > 0 ? insertions[^1].Revision : null);

    private bool StartOrEndChange(XElement element)
    {
        string? id = element.Attr(TextNs, "change-id");
        if (id is null || !changes.TryGetValue(id, out (Revision? Revision, XElement Change) change))
        {
            return true;
        }

        switch (element.Name.LocalName)
        {
            case "change-start":
                if (change.Revision is { Kind: RevisionKind.Insertion } inserted)
                {
                    insertions.Add((id, inserted));
                }
                else if (change.Revision is null)
                {
                    Warn(ConversionWarningKind.ChangesAccepted, "Tracked formatting changes are read as accepted.");
                }

                return true;
            case "change-end":
                insertions.RemoveAll(i => i.Id == id);
                return true;
        }

        return change.Revision is not { Kind: RevisionKind.Deletion };
    }

    private void ReadInlineChange(XElement element, InlineContext context, RunFormat format, CharacterStyle? characterStyle, double size)
    {
        if (StartOrEndChange(element))
        {
            return;
        }

        // A deletion's text is kept in the changed region and marked by a point in the text (ODF 1.3 Part 3, 5.5.7). Each
        // deleted paragraph end splits the paragraph, as Word holds a deleted paragraph mark.
        (Revision? revision, XElement change) = changes[element.Attr(TextNs, "change-id")!];
        Revision? saved = deletion;
        deletion = revision;
        try
        {
            bool first = true;
            foreach (XElement content in change.Elements())
            {
                if (content.Name != TextNs + "p" && content.Name != TextNs + "h")
                {
                    continue;
                }

                if (!first && context.TopLevel)
                {
                    Flush(context);
                    context.Paragraph.MarkRevision = revision;
                    Paragraph next = new(document) { Style = context.Paragraph.Style };
                    next.Format.CopyRaw(context.Paragraph.Format);
                    context.Blocks.Blocks.AddTrusted(next);
                    context.Paragraph = next;
                    context.Inlines = next.Inlines;
                }

                first = false;
                ReadInlines(content, context, format, characterStyle, size);
            }
        }
        finally
        {
            deletion = saved;
        }
    }

    private void ReadBlockChange(XElement element, BlockContext context, ListState? list)
    {
        if (StartOrEndChange(element))
        {
            return;
        }

        (Revision? revision, XElement change) = changes[element.Attr(TextNs, "change-id")!];
        Revision? saved = deletion;
        deletion = revision;
        try
        {
            foreach (XElement content in change.Elements().Where(e => e.Name.Namespace != OfficeNs))
            {
                ReadBlock(content, context, list);
            }
        }
        finally
        {
            deletion = saved;
        }
    }

    /// <summary>Reads a text field as a Word field (code and result); false when the element is no field.</summary>
    private bool ReadField(XElement element, InlineContext context, RunFormat format, CharacterStyle? characterStyle)
    {
        string name = element.Name.LocalName;
        string result = element.Value;
        if (name == "hidden-paragraph")
        {
            return true;
        }

        if (ValueFields.Contains(name))
        {
            AddText(context, result, format, characterStyle, collapse: false);
            return true;
        }

        string? code = FieldCode(element);
        if (code is null)
        {
            return false;
        }

        string? bookmark = name == "sequence" ? element.Attr(TextNs, "ref-name") : null;
        if (bookmark is not null)
        {
            AddInline(context, new BookmarkStart(document, bookmark), null, null);
        }

        FieldStart start = AddInline(context, new FieldStart(document), null, null);
        start.IsLocked = element.Attr(TextNs, "fixed") == "true";
        AddInline(context, new Run(document, code), format, characterStyle);
        AddInline(context, new FieldSeparator(document), null, null);
        if (result.Length > 0)
        {
            AddInline(context, new Run(document, result), format, characterStyle);
        }

        AddInline(context, new FieldEnd(document), null, null);
        if (bookmark is not null)
        {
            AddInline(context, new BookmarkEnd(document, bookmark), null, null);
        }

        return true;
    }

    private static string? FieldCode(XElement element)
    {
        string? format = NumberSwitch(element.Attr(StyleNs, "num-format"));
        switch (element.Name.LocalName)
        {
            case "page-number":
                return "PAGE" + format;
            case "page-count":
                return "NUMPAGES" + format;
            case "date":
                return "DATE";
            case "time":
                return "TIME \\@ \"HH:mm:ss\"";
            case "creation-date":
                return "CREATEDATE";
            case "creation-time":
                return "CREATEDATE \\@ \"HH:mm:ss\"";
            case "modification-date":
                return "SAVEDATE";
            case "print-date":
                return "PRINTDATE";
            case "title":
                return "TITLE";
            case "subject":
                return "SUBJECT";
            case "description":
                return "COMMENTS";
            case "keywords":
                return "KEYWORDS";
            case "initial-creator" or "author-name":
                return "AUTHOR";
            case "file-name":
                return element.Attr(TextNs, "display") is "full" or "path" ? "FILENAME \\p" : "FILENAME";
            case "word-count":
                return "NUMWORDS";
            case "character-count":
                return "NUMCHARS";
            case "chapter":
                {
                    int level = Math.Clamp(OdfUnits.Int(element.Attr(TextNs, "outline-level")) ?? 1, 1, 9);
                    return element.Attr(TextNs, "display") == "number" ? $"STYLEREF {level} \\n" : $"STYLEREF {level}";
                }

            case "sequence":
                return element.Attr(TextNs, "name") is { Length: > 0 } sequence ? $"SEQ {Quote(sequence)}{format ?? " \\* ARABIC"}" : null;
            case "bookmark-ref" or "reference-ref" or "sequence-ref":
                {
                    if (element.Attr(TextNs, "ref-name") is not { Length: > 0 } target)
                    {
                        return null;
                    }

                    return element.Attr(TextNs, "reference-format") switch
                    {
                        "page" => $"PAGEREF {Quote(target)} \\h",
                        "direction" => $"REF {Quote(target)} \\p \\h",
                        "number" or "number-all-superior" or "number-no-superior" or "chapter" => $"REF {Quote(target)} \\r \\h",
                        _ => $"REF {Quote(target)} \\h",
                    };
                }

            case "note-ref":
                {
                    if (element.Attr(TextNs, "ref-name") is not { Length: > 0 } note)
                    {
                        return null;
                    }

                    return element.Attr(TextNs, "reference-format") == "page" ? $"PAGEREF {Quote(note)} \\h" : $"NOTEREF {Quote(note)} \\h";
                }

            case "user-defined":
                return element.Attr(TextNs, "name") is { Length: > 0 } property ? $"DOCPROPERTY {Quote(property)}" : null;
            case "database-display":
                return element.Attr(TextNs, "column-name") is { Length: > 0 } column ? $"MERGEFIELD {Quote(column)}" : null;
            default:
                return null;
        }
    }

    private static string? NumberSwitch(string? numFormat) => numFormat switch
    {
        "i" => " \\* roman",
        "I" => " \\* ROMAN",
        "a" => " \\* alphabetic",
        "A" => " \\* ALPHABETIC",
        "1" => " \\* ARABIC",
        _ => null,
    };

    private static string Quote(string name)
    {
        return name.AsSpan().ContainsAny(" \t\"\\") ? "\"" + name.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : name;
    }
}
