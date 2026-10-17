using System.Globalization;

namespace Egap.Documents;

/// <summary>
/// Builds a TOC field's result as Word does: one paragraph per heading in the TOC n style, the heading's text, a right tab
/// with dots and a PAGEREF field to a _Toc bookmark placed on the heading. The page numbers come from layout (Phase 6
/// design Q8). Switches read: \o (outline levels), \u (paragraph outline levels), \t (styles and levels), \h (links),
/// \n (no page numbers).
/// </summary>
internal static class TableOfContents
{
    public static bool Build(Field field, FieldCode code)
    {
        Document document = field.Start.Document;
        (int from, int to) = Levels(code.Switch("\\O"));
        Dictionary<string, int> styleLevels = StyleLevels(code.Switch("\\T"));
        bool links = code.Has("\\H");
        bool noPages = code.Has("\\N");

        List<(Paragraph Heading, int Level)> headings = [];
        HashSet<Node> inField = [.. field.ResultNodes, .. field.CodeNodes];
        foreach (Paragraph paragraph in document.Descendants<Paragraph>())
        {
            if (paragraph.OwnerStory is not Body || inField.Contains(paragraph) || paragraph.Descendants().Contains(field.Start))
            {
                continue;
            }

            int? level = null;
            if (paragraph.Style is { } style && (styleLevels.TryGetValue(style.Name, out int l) || styleLevels.TryGetValue(style.Id, out l)))
            {
                level = l;
            }
            else if (code.Has("\\O") || code.Has("\\U"))
            {
                int outline = paragraph.GetEffectiveFormat().OutlineLevel;
                if (outline < 9 && outline + 1 >= from && outline + 1 <= to)
                {
                    level = outline + 1;
                }
            }

            if (level is int found && FieldUpdater.VisibleText(paragraph).Length > 0)
            {
                headings.Add((paragraph, found));
            }
        }

        FieldSeparator separator = FieldUpdater.ClearResult(field);
        Paragraph current = separator.Paragraph!;
        List<Inline> tail = [];
        while (separator.NextSibling is Inline next)
        {
            next.Remove();
            tail.Add(next);
        }

        if (headings.Count == 0)
        {
            FieldUpdater.Insert(separator, new Run(document, "No table of contents entries found."));
            foreach (Inline inline in tail)
            {
                current.Inlines.Add(inline);
            }

            return true;
        }

        double tabPosition = TextWidth(document);
        bool fieldAlone = separator.Paragraph!.Inlines[0] == field.Start;
        for (int i = 0; i < headings.Count; i++)
        {
            (Paragraph heading, int level) = headings[i];
            if (i > 0)
            {
                Paragraph next = new(document);
                InsertAfter(current, next);
                current = next;
            }

            if (i > 0 || fieldAlone)
            {
                current.Style = (ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Toc1 + Math.Clamp(level - 1, 0, 8));
                current.Format.TabStops = [new TabStop(tabPosition, TabAlignment.Right, TabLeader.Dot)];
            }

            string bookmark = EnsureBookmark(heading);
            List<Inline> entry = [new Run(document, FieldUpdater.VisibleText(heading))];
            if (!noPages)
            {
                entry.Add(new Tab(document));
                entry.Add(new FieldStart(document) { IsDirty = true });
                entry.Add(new Run(document, $" PAGEREF {bookmark} \\h "));
                entry.Add(new FieldSeparator(document));
                entry.Add(new FieldEnd(document));
            }

            Inline anchor = i == 0 ? separator : null!;
            if (links)
            {
                Hyperlink link = new(document) { Anchor = bookmark };
                foreach (Inline inline in entry)
                {
                    link.Inlines.Add(inline);
                }

                entry = [link];
            }

            foreach (Inline inline in entry)
            {
                if (i == 0)
                {
                    FieldUpdater.Insert(anchor, inline);
                    anchor = inline;
                }
                else
                {
                    current.Inlines.Add(inline);
                }
            }
        }

        foreach (Inline inline in tail)
        {
            current.Inlines.Add(inline);
        }

        return true;
    }

    private static void InsertAfter(Paragraph paragraph, Paragraph next)
    {
        switch (paragraph.Parent)
        {
            case BlockContainer container:
                container.Blocks.Insert(paragraph.IndexInParent + 1, next);
                break;
            case ContentControl control:
                control.Blocks.Insert(paragraph.IndexInParent + 1, next);
                break;
        }
    }

    /// <summary>The heading's _Toc bookmark, added around its text when it has none.</summary>
    private static string EnsureBookmark(Paragraph heading)
    {
        if (heading.Inlines.OfType<BookmarkStart>().FirstOrDefault(b => b.Name.StartsWith("_Toc", StringComparison.Ordinal)) is { } existing)
        {
            return existing.Name;
        }

        Document document = heading.Document;
        HashSet<string> names = [.. document.Descendants<BookmarkStart>().Select(b => b.Name)];
        int number = 1;
        while (names.Contains("_Toc" + number.ToString("D9", CultureInfo.InvariantCulture)))
        {
            number++;
        }

        string name = "_Toc" + number.ToString("D9", CultureInfo.InvariantCulture);
        heading.Inlines.Insert(0, new BookmarkStart(document, name));
        heading.Inlines.Add(new BookmarkEnd(document, name));
        return name;
    }

    private static (int From, int To) Levels(string? range)
    {
        if (range is null)
        {
            return (1, 9);
        }

        string[] parts = range.Split('-', StringSplitOptions.TrimEntries);
        int from = int.TryParse(parts[0], CultureInfo.InvariantCulture, out int f) ? f : 1;
        int to = parts.Length > 1 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out int t) ? t : from;
        return (Math.Clamp(from, 1, 9), Math.Clamp(to, 1, 9));
    }

    /// <summary>The \t switch: "Style name,level,Other style,level", separators comma or semicolon.</summary>
    private static Dictionary<string, int> StyleLevels(string? spec)
    {
        Dictionary<string, int> levels = new(StringComparer.OrdinalIgnoreCase);
        if (spec is null)
        {
            return levels;
        }

        string[] parts = spec.Split([',', ';'], StringSplitOptions.TrimEntries);
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            if (int.TryParse(parts[i + 1], CultureInfo.InvariantCulture, out int level))
            {
                levels[parts[i]] = Math.Clamp(level, 1, 9);
            }
        }

        return levels;
    }

    /// <summary>The first section's text width: page width less margins, Word's Letter defaults when unset.</summary>
    private static double TextWidth(Document document)
    {
        PageSetup? page = document.Sections.Count > 0 ? document.Sections[0].PageSetup : null;
        double width = page?.PageWidth ?? 612;
        return Math.Max(72, width - (page?.MarginLeft ?? 72) - (page?.MarginRight ?? 72) - (document.Settings.GutterAtTop ? 0 : page?.Gutter ?? 0));
    }
}
