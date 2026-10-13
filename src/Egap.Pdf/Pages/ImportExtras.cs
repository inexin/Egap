using Egap.Pdf.Writing;

namespace Egap.Pdf.Pages;

/// <summary>
/// What imported pages bring besides themselves (Phase 2 Q5, Q6): their form fields, named destinations and layers, page
/// labels for their range, and, the first time a source is imported from, its attachments and its outline with the items
/// that lead to pages not imported left out.
/// </summary>
internal static class ImportExtras
{
    public static void Bring(PdfDocument target, ObjectCopier copier, IReadOnlyList<PdfPage> from, IReadOnlyList<PdfPage> added, List<(PdfPageLabelStyle, string?, int)>? labelsBefore, PdfImportOptions options)
    {
        PdfDocument source = copier.Source;
        if (ReferenceEquals(source, target) || added.Count == 0)
        {
            return;
        }

        Fields(target, copier, added, options);
        NamedDestinations(target, copier, added);
        Layers(target, copier);
        if (labelsBefore is not null)
        {
            Labels(target, source, from, added, labelsBefore);
        }

        if (!copier.ExtrasBrought)
        {
            copier.ExtrasBrought = true;
            Attachments(target, copier);
            Outline(target, copier, source.Outline, target.Outline);
        }
    }

    /// <summary>Each page's label as style, prefix and number, or null when the document has no page labels.</summary>
    public static List<(PdfPageLabelStyle, string?, int)>? LabelsOf(PdfDocument document)
    {
        IReadOnlyList<PdfPageLabelRange> ranges = document.Pages.GetLabelRanges();
        if (ranges.Count == 0)
        {
            return null;
        }

        List<(PdfPageLabelStyle, string?, int)> labels = new(document.Pages.Count);
        for (int i = 0; i < document.Pages.Count; i++)
        {
            labels.Add(LabelAt(ranges, i));
        }

        return labels;
    }

    private static (PdfPageLabelStyle, string?, int) LabelAt(IReadOnlyList<PdfPageLabelRange> ranges, int index)
    {
        PdfPageLabelRange? range = ranges.LastOrDefault(r => r.StartIndex <= index);
        return range is null ? (PdfPageLabelStyle.Arabic, null, index + 1) : (range.Style, range.Prefix, range.Start + index - range.StartIndex);
    }

    /// <summary>
    /// The form fields of the imported widgets: their top-level fields join the target's <c>/Fields</c>, renamed with a
    /// suffix when the name is taken (Q5), or joined to the field of that name when asked and the two can share widgets.
    /// The source form's default resources, default appearance and flags come along; an XFA form does not.
    /// </summary>
    private static void Fields(PdfDocument target, ObjectCopier copier, IReadOnlyList<PdfPage> added, PdfImportOptions options)
    {
        List<(PdfDictionary Root, PdfReference Reference)> roots = [];
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        foreach (PdfAnnotation widget in added.SelectMany(p => p.Annotations).Where(a => a is PdfWidgetAnnotation))
        {
            PdfDictionary node = widget.Dictionary;
            PdfReference? reference = widget.Reference;
            HashSet<PdfDictionary> path = new(ReferenceEqualityComparer.Instance);
            while (node.Get<PdfDictionary>("Parent") is PdfDictionary parent && path.Add(node) && path.Count < PdfForm.MaxDepth)
            {
                reference = node["Parent"] as PdfReference;
                node = parent;
            }

            if (reference is not null && (node.Get("FT") is not null || node.Get("T") is not null || node.Get("Kids") is not null) && seen.Add(node))
            {
                roots.Add((node, reference));
            }
        }

        if (roots.Count == 0)
        {
            return;
        }

        PdfDictionary? sourceForm = copier.Source.Catalog.Get<PdfDictionary>("AcroForm");
        if (target.Catalog.Get<PdfDictionary>("AcroForm") is not PdfDictionary form)
        {
            form = new PdfDictionary { ["Fields"] = new PdfArray() };
            target.Catalog["AcroForm"] = target.Objects.Add(form);
            form = target.Catalog.Get<PdfDictionary>("AcroForm")!;
        }

        if (form.Get<PdfArray>("Fields") is not PdfArray fields)
        {
            fields = [];
            form["Fields"] = fields;
        }

        if (sourceForm is not null)
        {
            MergeForm(target, copier, sourceForm, form);
        }

        HashSet<string> taken = [.. target.Form.Select(f => f.PartialName)];
        foreach ((PdfDictionary root, PdfReference reference) in roots)
        {
            if (Enumerable.Range(0, fields.Count).Any(i => ReferenceEquals(fields.Get(i), root)))
            {
                continue;
            }

            string name = root.GetText("T") ?? "";
            if (name.Length > 0 && taken.Contains(name))
            {
                if (options.JoinFields && Join(target, root, name))
                {
                    continue;
                }

                string renamed = Enumerable.Range(2, int.MaxValue - 2).Select(n => $"{name}_{n}").First(n => !taken.Contains(n));
                root["T"] = PdfString.FromText(renamed);
                target.Log.Add(PdfWarningCode.FieldRenamed, $"Imported field {name} was renamed {renamed}, since the document has a field of that name.");
                name = renamed;
            }

            taken.Add(name);
            fields.Add(reference);
        }
    }

    /// <summary>The source form's fonts and other resources the target lacks, its default appearance, and its flags.</summary>
    private static void MergeForm(PdfDocument target, ObjectCopier copier, PdfDictionary sourceForm, PdfDictionary form)
    {
        if (sourceForm.Get<PdfDictionary>("DR") is PdfDictionary sourceResources)
        {
            if (form.Get<PdfDictionary>("DR") is not PdfDictionary resources)
            {
                resources = [];
                form["DR"] = resources;
            }

            foreach ((PdfName category, PdfObject value) in sourceResources.Entries)
            {
                if (value.Resolve() is not PdfDictionary entries)
                {
                    continue;
                }

                if (resources.Get<PdfDictionary>(category) is not PdfDictionary into)
                {
                    into = [];
                    resources[category] = into;
                }

                foreach ((PdfName name, PdfObject entry) in entries.Entries)
                {
                    if (into.Get(name) is null)
                    {
                        into[name] = copier.Copy(entry);
                    }
                }
            }
        }

        if (form.Get("DA") is null && sourceForm.Get<PdfString>("DA") is PdfString appearance)
        {
            form["DA"] = appearance;
        }

        if (sourceForm.GetBool("NeedAppearances") == true)
        {
            form["NeedAppearances"] = PdfBoolean.True;
        }

        if ((sourceForm.GetInt("SigFlags") ?? 0) is long flags and not 0)
        {
            form["SigFlags"] = new PdfInteger((form.GetInt("SigFlags") ?? 0) | flags);
        }

        if (sourceForm.Get("XFA") is not null)
        {
            target.Log.Add(PdfWarningCode.NotImported, "The source's XFA form was not brought along with its pages; their AcroForm fields were.");
        }
    }

    /// <summary>
    /// Joins an imported terminal field to the target's top-level field of the same name and type (Q5 option): its widgets
    /// become the target field's, sharing its value. False when either is not a terminal field with separate widgets.
    /// </summary>
    private static bool Join(PdfDocument target, PdfDictionary incoming, string name)
    {
        PdfField? existing = target.Form.FirstOrDefault(f => f.PartialName == name);
        if (existing is null || !existing.IsTerminal || existing.Reference is null || existing.Dictionary.GetName("Subtype")?.Value == "Widget"
            || incoming.GetName("Subtype")?.Value == "Widget" || incoming.Get<PdfArray>("Kids") is not PdfArray kids
            || existing.FieldType != incoming.GetName("FT")?.Value)
        {
            return false;
        }

        if (existing.Dictionary.Get<PdfArray>("Kids") is not PdfArray into)
        {
            into = [];
            existing.Dictionary["Kids"] = into;
        }

        for (int i = 0; i < kids.Count; i++)
        {
            if (kids.Get(i) is PdfDictionary widget && widget.Get("T") is null && kids[i] is PdfReference reference)
            {
                widget["Parent"] = existing.Reference;
                into.Add(reference);
            }
        }

        return true;
    }

    /// <summary>
    /// The named destinations the imported links use, when their pages came along; a name the target already gives another
    /// destination is renamed with a suffix in the links too.
    /// </summary>
    private static void NamedDestinations(PdfDocument target, ObjectCopier copier, IReadOnlyList<PdfPage> added)
    {
        foreach (PdfAnnotation annotation in added.SelectMany(p => p.Annotations))
        {
            (PdfDictionary holder, PdfName key) = annotation.Dictionary.Get<PdfDictionary>("A") is PdfDictionary action && action.GetName("S")?.Value == "GoTo"
                ? (action, (PdfName)"D")
                : (annotation.Dictionary, (PdfName)"Dest");
            string? name = holder.Get(key) switch
            {
                PdfString text => text.ToText(),
                PdfName n => n.Value,
                _ => null,
            };
            if (name is null || !copier.Source.NamedDestinations.TryGet(name, out PdfDestination destination)
                || PdfDestination.FromArray(copier.Copy(destination.ToArray()) as PdfArray) is not PdfDestination copied)
            {
                continue;
            }

            string used = name;
            for (int n = 2; target.NamedDestinations.TryGet(used, out PdfDestination existing) && existing != copied; n++)
            {
                used = $"{name}_{n}";
            }

            target.NamedDestinations.Set(used, copied);
            if (used != name)
            {
                holder[key] = PdfString.FromText(used);
            }
        }
    }

    /// <summary>The source's layers the imported pages use: added to the target's, with their default visibility.</summary>
    private static void Layers(PdfDocument target, ObjectCopier copier)
    {
        if (copier.Source.Catalog.Get<PdfDictionary>("OCProperties") is not PdfDictionary sourceProperties || sourceProperties.Get<PdfArray>("OCGs") is not PdfArray sourceGroups)
        {
            return;
        }

        PdfDictionary? sourceDefaults = sourceProperties.Get<PdfDictionary>("D");
        bool offByDefault = sourceDefaults?.GetName("BaseState")?.Value == "OFF";
        HashSet<PdfDictionary> listedOff = Listed(sourceDefaults?.Get<PdfArray>("OFF"));
        HashSet<PdfDictionary> listedOn = Listed(sourceDefaults?.Get<PdfArray>("ON"));
        for (int i = 0; i < sourceGroups.Count; i++)
        {
            if (sourceGroups[i] is not PdfReference group || copier.Mapped(group) is not PdfReference copy || group.Resolve() is not PdfDictionary dictionary)
            {
                continue;
            }

            if (target.Catalog.Get<PdfDictionary>("OCProperties") is not PdfDictionary properties)
            {
                properties = new PdfDictionary { ["OCGs"] = new PdfArray(), ["D"] = new PdfDictionary { ["Order"] = new PdfArray() } };
                target.Catalog["OCProperties"] = properties;
            }

            PdfArray groups = properties.Get<PdfArray>("OCGs") ?? Put(properties, "OCGs");
            if (Enumerable.Range(0, groups.Count).Any(j => ReferenceEquals(groups.Get(j), copy.Resolve())))
            {
                continue;
            }

            groups.Add(copy);
            PdfDictionary defaults = properties.Get<PdfDictionary>("D") ?? PutDictionary(properties, "D");
            (defaults.Get<PdfArray>("Order") ?? Put(defaults, "Order")).Add(copy);
            if (listedOff.Contains(dictionary) || (offByDefault && !listedOn.Contains(dictionary)))
            {
                (defaults.Get<PdfArray>("OFF") ?? Put(defaults, "OFF")).Add(copy);
            }
        }
    }

    private static HashSet<PdfDictionary> Listed(PdfArray? array)
    {
        return array is null ? new(ReferenceEqualityComparer.Instance) : new(Enumerable.Range(0, array.Count).Select(array.Get<PdfDictionary>).OfType<PdfDictionary>(), ReferenceEqualityComparer.Instance);
    }

    private static PdfArray Put(PdfDictionary dictionary, PdfName key)
    {
        PdfArray array = [];
        dictionary[key] = array;
        return array;
    }

    private static PdfDictionary PutDictionary(PdfDictionary dictionary, PdfName key)
    {
        PdfDictionary made = [];
        dictionary[key] = made;
        return made;
    }

    /// <summary>
    /// Page labels: the target's pages keep the labels they had, the imported pages take their labels in the source (1, 2,
    /// 3... when it has none), and the label ranges are written again to match.
    /// </summary>
    private static void Labels(PdfDocument target, PdfDocument source, IReadOnlyList<PdfPage> from, IReadOnlyList<PdfPage> added, List<(PdfPageLabelStyle, string?, int)> before)
    {
        IReadOnlyList<PdfPageLabelRange> sourceRanges = source.Pages.GetLabelRanges();
        Dictionary<PdfPage, (PdfPageLabelStyle, string?, int)> imported = [];
        for (int i = 0; i < added.Count; i++)
        {
            imported[added[i]] = LabelAt(sourceRanges, source.Pages.IndexOf(from[i]));
        }

        List<(PdfPageLabelStyle Style, string? Prefix, int Number)> labels = new(target.Pages.Count);
        int old = 0;
        foreach (PdfPage page in target.Pages)
        {
            labels.Add(imported.TryGetValue(page, out (PdfPageLabelStyle, string?, int) label) ? label : old < before.Count ? before[old++] : (PdfPageLabelStyle.Arabic, null, labels.Count + 1));
        }

        foreach (PdfPageLabelRange range in target.Pages.GetLabelRanges())
        {
            target.Pages.RemoveLabelRange(range.StartIndex);
        }

        for (int i = 0; i < labels.Count; i++)
        {
            bool continues = i > 0 && labels[i].Style == labels[i - 1].Style && labels[i].Prefix == labels[i - 1].Prefix && labels[i].Number == labels[i - 1].Number + 1;
            if (!continues)
            {
                target.Pages.SetLabelRange(new PdfPageLabelRange(i, labels[i].Style, labels[i].Prefix, Math.Max(1, labels[i].Number)));
            }
        }
    }

    /// <summary>The source's embedded files, each under its name, or the name with a suffix when the target has it (Q6).</summary>
    private static void Attachments(PdfDocument target, ObjectCopier copier)
    {
        foreach (PdfAttachment attachment in copier.Source.Attachments)
        {
            string extension = Path.GetExtension(attachment.Name);
            string stem = attachment.Name[..^extension.Length];
            string name = attachment.Name;
            for (int n = 2; target.Attachments.TryGet(name, out _); n++)
            {
                name = $"{stem}_{n}{extension}";
            }

            target.Attachments.AddCopy(name, copier.Copy(attachment.Reference ?? (PdfObject)attachment.FileSpecification));
        }
    }

    /// <summary>
    /// The source's outline appended at the top level: an item comes along when it leads to an imported page or does
    /// something else than go to a page, or when one of its children comes along.
    /// </summary>
    private static void Outline(PdfDocument target, ObjectCopier copier, PdfOutlineNode from, PdfOutlineNode into)
    {
        foreach (PdfOutlineItem item in from.Children)
        {
            PdfDestination? destination = item.Destination is PdfDestination d && d.PageReference is not null
                ? PdfDestination.FromArray(copier.Copy(d.ToArray()) as PdfArray)
                : null;
            PdfAction? action = destination is null && item.Action is PdfAction a && a is not PdfGoToAction
                ? PdfAction.FromDictionary((PdfDictionary)copier.Copy(a.Dictionary))
                : null;
            PdfOutlineItem made = into.Add(item.Title, destination);
            if (action is not null)
            {
                made.Action = action;
            }

            made.Color = item.Color;
            made.IsBold = item.IsBold;
            made.IsItalic = item.IsItalic;
            Outline(target, copier, item, made);
            made.IsOpen = item.IsOpen;
            if (destination is null && action is null && made.Children.Count == 0)
            {
                made.Remove();
            }
        }
    }
}
