using System.Globalization;

namespace Egap.Pdf.Signatures;

/// <summary>
/// Compares each revision after a signature with the one before it and judges every changed object against the
/// signature's permissions (Phase 5 design Q8; ISO 32000-2, 12.8.2.2 to 12.8.2.4): long-term validation data (a DSS, document
/// timestamps) always; form filling and signing from DocMDP 2; annotations from DocMDP 3; nothing that changes page content,
/// resources, the page tree or a locked field. An object's role comes from where the catalog reaches it in each revision, so
/// an object rewritten under page content is judged as page content wherever else it is used (the hide-and-replace attacks).
/// </summary>
internal static class ChangeAnalyzer
{
    private const int LongTerm = 0;
    private const int FormFilling = 2;
    private const int Annotating = 3;
    private const int Suspicious = 100;
    private const int Disallowed = 200;

    /// <summary>Field keys whose change is filling the form or drawing its appearance.</summary>
    private static readonly HashSet<string> FillingKeys = ["V", "AS", "AP", "RV", "MK", "DA", "Q", "DS"];

    /// <summary>Form keys a filled or newly signed form may change.</summary>
    private static readonly HashSet<string> FormKeys = ["Fields", "SigFlags", "NeedAppearances", "DR", "DA"];

    [Flags]
    private enum Role
    {
        None = 0,
        Catalog = 1,
        Info = 1 << 1,
        Metadata = 1 << 2,
        Pages = 1 << 3,
        Page = 1 << 4,
        Content = 1 << 5,
        Resource = 1 << 6,
        Annotation = 1 << 7,
        Widget = 1 << 8,
        Field = 1 << 9,
        Appearance = 1 << 10,
        Form = 1 << 11,
        FormResource = 1 << 12,
        Signature = 1 << 13,
        Dss = 1 << 14,
        Other = 1 << 15,
    }

    /// <summary>What is judged at most this level for a signature: its DocMDP, or a certification's before it, else annotations.</summary>
    public static List<PdfSignatureChange> Analyze(PdfDocument document, PdfSignature signature, out bool complete)
    {
        List<PdfSignatureChange> changes = [];
        complete = true;
        if (signature.Revision is not { } signed || signed.IsLatest)
        {
            return changes;
        }

        int permission = Permission(document, signature);
        List<PdfFieldLock> locks = [];
        if (signature.FieldLock is { } own)
        {
            locks.Add(own);
        }

        IReadOnlyList<PdfRevision> revisions = document.Revisions;
        PdfDocument before;
        try
        {
            before = revisions[signed.Index].Open();
        }
        catch (PdfException e)
        {
            complete = false;
            changes.Add(new PdfSignatureChange(signed.Index, 0, PdfChangeVerdict.Suspicious, $"The signed revision could not be opened: {e.Message}"));
            return changes;
        }

        // Each revision is compared with the one before it; the last is the document itself.
        for (int index = signed.Index + 1; index < revisions.Count; index++)
        {
            PdfDocument after;
            try
            {
                after = revisions[index].IsLatest ? document : revisions[index].Open();
            }
            catch (PdfException e)
            {
                complete = false;
                changes.Add(new PdfSignatureChange(index, 0, PdfChangeVerdict.Suspicious, $"The revision could not be opened: {e.Message}"));
                break;
            }

            try
            {
                Compare(before, after, index, signature.Name, permission, locks, changes);
            }
            catch (PdfException e)
            {
                complete = false;
                changes.Add(new PdfSignatureChange(index, 0, PdfChangeVerdict.Suspicious, $"The revision could not be compared: {e.Message}"));
            }

            if (!ReferenceEquals(before, document))
            {
                before.Dispose();
            }

            before = after;
        }

        if (!ReferenceEquals(before, document))
        {
            before.Dispose();
        }

        return changes;
    }

    /// <summary>The verdict for the changes: invalid for any disallowed, indeterminate for any suspicious, else valid.</summary>
    public static PdfVerdict Verdict(IReadOnlyList<PdfSignatureChange> changes)
    {
        return changes.Any(c => c.Verdict == PdfChangeVerdict.Disallowed) ? PdfVerdict.Invalid
            : changes.Any(c => c.Verdict == PdfChangeVerdict.Suspicious) ? PdfVerdict.Indeterminate
            : PdfVerdict.Valid;
    }

    /// <summary>
    /// The DocMDP level later revisions are held to: the signature's own, its lock's (PDF 2.0), and that of a certification
    /// signature at or before it, the strictest winning; an approval signature in a document without one allows annotations.
    /// </summary>
    private static int Permission(PdfDocument document, PdfSignature signature)
    {
        int permission = Annotating;
        foreach (PdfSignature other in document.Signatures)
        {
            bool atOrBefore = other.Revision is { } r && signature.Revision is { } s && r.Index <= s.Index;
            if (ReferenceEquals(other.Dictionary, signature.Dictionary) || (atOrBefore && other.Kind == PdfSignatureKind.Certification))
            {
                if (other.Certification is { } level)
                {
                    permission = Math.Min(permission, (int)level);
                }
            }
        }

        if (signature.FieldLock?.Certification is { } locked)
        {
            permission = Math.Min(permission, (int)locked);
        }

        return permission;
    }

    private static void Compare(PdfDocument before, PdfDocument after, int revision, string signedField, int permission, List<PdfFieldLock> locks, List<PdfSignatureChange> changes)
    {
        Dictionary<int, Role> oldRoles = Roles(before);
        Dictionary<int, Role> newRoles = Roles(after);
        HashSet<int> numbers = [.. before.Xref.Entries.Select(e => e.Key), .. after.Xref.Entries.Select(e => e.Key)];
        foreach (int number in numbers.Order())
        {
            bool hadEntry = before.Xref.TryGet(number, out Parsing.XrefEntry oldEntry) && oldEntry.Kind != Parsing.XrefEntryKind.Free;
            bool hasEntry = after.Xref.TryGet(number, out Parsing.XrefEntry newEntry) && newEntry.Kind != Parsing.XrefEntryKind.Free;
            if (hadEntry == hasEntry && (!hasEntry || oldEntry == newEntry))
            {
                continue;
            }

            PdfObject oldValue = hadEntry ? before.Objects.Get(number) : PdfNull.Instance;
            PdfObject newValue = hasEntry ? after.Objects.Get(number) : PdfNull.Instance;
            if (hadEntry && hasEntry && Same(oldValue, newValue, 0))
            {
                continue;
            }

            if (IsStructural(newValue) || IsStructural(oldValue))
            {
                continue;
            }

            Role roles = oldRoles.GetValueOrDefault(number) | newRoles.GetValueOrDefault(number);
            (int level, string description) = Judge(number, roles, oldValue, newValue, hadEntry, hasEntry, after, signedField, locks);
            changes.Add(new PdfSignatureChange(revision, number, Verdict(level, permission), description));
        }

        // The trailer's own entries: /Info replaced, /Root replaced, /Encrypt added or removed.
        foreach (string key in (string[])["Root", "Info", "Encrypt"])
        {
            if (!Same(before.Trailer[key] ?? PdfNull.Instance, after.Trailer[key] ?? PdfNull.Instance, 0))
            {
                int level = key == "Info" ? FormFilling : Disallowed;
                changes.Add(new PdfSignatureChange(revision, 0, Verdict(level, permission), $"The trailer's /{key} changed."));
            }
        }
    }

    private static PdfChangeVerdict Verdict(int level, int permission)
    {
        return level switch
        {
            Disallowed => PdfChangeVerdict.Disallowed,
            Suspicious => PdfChangeVerdict.Suspicious,
            _ when level == LongTerm || level <= permission => PdfChangeVerdict.Allowed,
            _ => PdfChangeVerdict.Disallowed,
        };
    }

    /// <summary>The level one changed object needs, by the roles it plays in either revision, and a sentence on it.</summary>
    private static (int Level, string Description) Judge(int number, Role roles, PdfObject oldValue, PdfObject newValue, bool hadEntry, bool hasEntry, PdfDocument after, string signedField, List<PdfFieldLock> locks)
    {
        string what = string.Create(CultureInfo.InvariantCulture, $"Object {number}");
        if (roles == Role.None)
        {
            return hadEntry ? (Suspicious, $"{what}, used by neither revision, was changed.") : (Suspicious, $"{what} was added, but nothing uses it.");
        }

        if (!hasEntry)
        {
            return roles is Role.Annotation or Role.Appearance ? (Annotating, $"{what}, an annotation, was removed.") : (Disallowed, $"{what} ({roles}) was removed.");
        }

        // Anything that draws the page: its content, resources and tree, and the catalog's other parts.
        const Role Drawing = Role.Pages | Role.Content | Role.Resource | Role.Other;
        if ((roles & Drawing) != 0)
        {
            return (Disallowed, $"{what} ({roles & Drawing}) changed: it is part of what the signed pages show.");
        }

        // An annotation or field list held in an object of its own: judged by what was added and removed.
        if (newValue is PdfArray list && (roles & (Role.Annotation | Role.Widget | Role.Field)) != 0)
        {
            return JudgeList(what, oldValue as PdfArray ?? [], list);
        }

        if ((roles & Role.Page) != 0)
        {
            return JudgePage(what, oldValue, newValue, after);
        }

        if ((roles & Role.Catalog) != 0)
        {
            return JudgeCatalog(what, oldValue, newValue, after);
        }

        if ((roles & (Role.Widget | Role.Field)) != 0)
        {
            return JudgeField(what, oldValue, newValue, hadEntry, after, signedField, locks);
        }

        if ((roles & Role.Form) != 0)
        {
            return JudgeKeys(what, "the form", oldValue, newValue, FormKeys, OnlyTimestampFieldsAdded(oldValue, newValue, after) ? LongTerm : FormFilling);
        }

        if ((roles & Role.Signature) != 0)
        {
            return hadEntry ? (Disallowed, $"{what}, a signature dictionary, was changed after signing.")
                : (IsDocumentTimestamp(newValue) ? LongTerm : FormFilling, $"{what}, a new signature, was added.");
        }

        if ((roles & Role.Dss) != 0)
        {
            return (LongTerm, $"{what}, long-term validation data, was added or updated.");
        }

        if ((roles & Role.Annotation) != 0)
        {
            return (Annotating, hadEntry ? $"{what}, an annotation, was changed." : $"{what}, an annotation, was added.");
        }

        if ((roles & (Role.Appearance | Role.FormResource)) != 0)
        {
            return (FormFilling, $"{what}, a field's appearance or the form's resources, was {(hadEntry ? "changed" : "added")}.");
        }

        if ((roles & (Role.Info | Role.Metadata)) != 0)
        {
            return (FormFilling, $"{what}, the document's metadata, was changed.");
        }

        return (Suspicious, $"{what} ({roles}) changed.");
    }

    private static (int Level, string Description) JudgePage(string what, PdfObject oldValue, PdfObject newValue, PdfDocument after)
    {
        if (oldValue is not PdfDictionary oldPage || newValue is not PdfDictionary newPage)
        {
            return (Disallowed, $"{what}, a page, was replaced.");
        }

        List<string> keys = ChangedKeys(oldPage, newPage);
        if (keys.Any(k => k != "Annots"))
        {
            return (Disallowed, $"{what}, a page, changed its {string.Join(", ", keys.Select(k => "/" + k))}.");
        }

        // Only /Annots: judged by what was added and removed.
        (int level, _) = JudgeList(what, Items(oldPage["Annots"]), Items(newPage["Annots"]));
        return (level, $"{what}, a page, changed its annotations.");
    }

    /// <summary>
    /// A list of annotations or fields: each added item is judged by what it is (a document timestamp's widget is long-term
    /// data, a signature field's is signing, any other is an annotation); removing a widget is never allowed, any other
    /// annotation from DocMDP 3.
    /// </summary>
    private static (int Level, string Description) JudgeList(string what, PdfArray older, PdfArray newer)
    {
        HashSet<(int, int)> before = [.. older.OfType<PdfReference>().Select(r => (r.ObjectNumber, r.Generation))];
        HashSet<(int, int)> now = [.. newer.OfType<PdfReference>().Select(r => (r.ObjectNumber, r.Generation))];
        int level = LongTerm;
        foreach (PdfReference added in newer.OfType<PdfReference>().Where(r => !before.Contains((r.ObjectNumber, r.Generation))))
        {
            PdfDictionary? item = added.Resolve() as PdfDictionary;
            PdfDictionary? field = item?.ContainsKey("FT") == true ? item : item?.Get("Parent")?.Resolve() as PdfDictionary;
            bool signature = field?.GetName("FT")?.Value == "Sig";
            int needed = !signature ? Annotating
                : IsDocumentTimestamp(field!.Get("V")?.Resolve() ?? item?.Get("V")?.Resolve() ?? PdfNull.Instance) ? LongTerm
                : FormFilling;
            level = Math.Max(level, needed);
        }

        foreach (PdfReference removed in older.OfType<PdfReference>().Where(r => !now.Contains((r.ObjectNumber, r.Generation))))
        {
            level = Math.Max(level, (removed.Resolve() as PdfDictionary)?.GetName("Subtype")?.Value == "Widget" ? Disallowed : Annotating);
        }

        // Direct items cannot be told apart by reference: any change among them is judged as an annotation's.
        if (older.Count(i => i is not PdfReference) != newer.Count(i => i is not PdfReference))
        {
            level = Math.Max(level, Annotating);
        }

        return (level, $"{what}, a list of annotations or fields, changed.");
    }

    private static (int Level, string Description) JudgeCatalog(string what, PdfObject oldValue, PdfObject newValue, PdfDocument after)
    {
        if (oldValue is not PdfDictionary oldCatalog || newValue is not PdfDictionary newCatalog)
        {
            return (Disallowed, $"{what}, the catalog, was replaced.");
        }

        int level = LongTerm;
        List<string> keys = ChangedKeys(oldCatalog, newCatalog);
        foreach (string key in keys)
        {
            level = Math.Max(level, key switch
            {
                "DSS" or "Extensions" or "Version" => LongTerm,
                "AcroForm" => OnlyTimestampFieldsAdded(oldCatalog.Get("AcroForm")?.Resolve() ?? PdfNull.Instance, newCatalog.Get("AcroForm")?.Resolve() ?? PdfNull.Instance, after) ? LongTerm : FormFilling,
                "Metadata" => FormFilling,
                _ => Disallowed,
            });
        }

        return (level, $"{what}, the catalog, changed its {string.Join(", ", keys.Select(k => "/" + k))}.");
    }

    private static (int Level, string Description) JudgeField(string what, PdfObject oldValue, PdfObject newValue, bool hadEntry, PdfDocument after, string signedField, List<PdfFieldLock> locks)
    {
        if (newValue is not PdfDictionary field)
        {
            return (Disallowed, $"{what}, a form field, was replaced by something else.");
        }

        string name = FullName(field);
        if (!hadEntry)
        {
            // A new field: a new signature field (with its widget) is signing, other fields need form filling at least.
            bool signature = (field.GetName("FT")?.Value ?? (field.Get("Parent")?.Resolve() as PdfDictionary)?.GetName("FT")?.Value) == "Sig";
            int level = signature && IsDocumentTimestamp(field.Get("V")?.Resolve() ?? PdfNull.Instance) ? LongTerm : FormFilling;
            return (signature ? level : Annotating, $"{what}, the new field '{name}', was added.");
        }

        if (oldValue is not PdfDictionary old)
        {
            return (Disallowed, $"{what}, the field '{name}', was replaced.");
        }

        List<string> keys = ChangedKeys(old, field);
        if (name == signedField || (old.GetName("FT")?.Value == "Sig" && old.Get("V") is not null && keys.Contains("V")))
        {
            return (Disallowed, $"{what}, the signed field '{name}', was changed after signing.");
        }

        if (keys.Any(k => FillingKeys.Contains(k)) && locks.Any(l => l.Covers(name)))
        {
            return (Disallowed, $"{what}, the field '{name}', is locked by the signature but was filled.");
        }

        // Kids may grow by new widgets; any other key (its name, type, flags, place) is not filling.
        List<string> other = [.. keys.Where(k => !FillingKeys.Contains(k) && !(k == "Kids" && Appended(old["Kids"], field["Kids"])))];
        if (other.Count > 0)
        {
            return (Disallowed, $"{what}, the field '{name}', changed its {string.Join(", ", other.Select(k => "/" + k))}.");
        }

        return (FormFilling, $"{what}, the field '{name}', was filled.");
    }

    /// <summary>A dictionary whose changed keys are all in <paramref name="allowed"/> needs <paramref name="level"/>; any other key is disallowed.</summary>
    private static (int Level, string Description) JudgeKeys(string what, string role, PdfObject oldValue, PdfObject newValue, HashSet<string> allowed, int level)
    {
        if (oldValue is not PdfDictionary old || newValue is not PdfDictionary now)
        {
            return (newValue is PdfDictionary ? level : Disallowed, $"{what}, {role}, was {(oldValue is PdfNull ? "added" : "replaced")}.");
        }

        List<string> keys = ChangedKeys(old, now);
        List<string> other = [.. keys.Where(k => !allowed.Contains(k) || (k == "Fields" && !Appended(old["Fields"], now["Fields"])))];
        return other.Count > 0 ? (Disallowed, $"{what}, {role}, changed its {string.Join(", ", other.Select(k => "/" + k))}.") : (level, $"{what}, {role}, changed its {string.Join(", ", keys.Select(k => "/" + k))}.");
    }

    /// <summary>Whether the form only gained fields that are document timestamps (B-LTA's update), or nothing that matters.</summary>
    private static bool OnlyTimestampFieldsAdded(PdfObject oldForm, PdfObject newForm, PdfDocument after)
    {
        HashSet<PdfObject> before = [.. Items((oldForm as PdfDictionary)?["Fields"])];
        List<PdfObject> added = [.. Items((newForm as PdfDictionary)?["Fields"]).Where(f => !before.Contains(f))];
        if (oldForm is PdfDictionary old && newForm is PdfDictionary now && ChangedKeys(old, now).Any(k => k is not ("Fields" or "SigFlags")))
        {
            return false;
        }

        return added.All(f => f.Resolve() is PdfDictionary field && IsDocumentTimestamp(field.Get("V")?.Resolve() ?? PdfNull.Instance));
    }

    private static bool IsDocumentTimestamp(PdfObject value)
    {
        return value is PdfDictionary dictionary && (dictionary.GetName("Type")?.Value == "DocTimeStamp" || dictionary.GetName("SubFilter")?.Value == "ETSI.RFC3161");
    }

    private static bool IsStructural(PdfObject value)
    {
        return value is PdfStream stream && stream.Dictionary.GetName("Type")?.Value is "XRef" or "ObjStm";
    }

    /// <summary>Whether <paramref name="newer"/> is <paramref name="older"/> with items added at the end.</summary>
    private static bool Appended(PdfObject? older, PdfObject? newer)
    {
        List<PdfObject> a = [.. Items(older)], b = [.. Items(newer)];
        return b.Count >= a.Count && a.Zip(b).All(p => Same(p.First, p.Second, 0));
    }

    private static PdfArray Items(PdfObject? value)
    {
        return value?.Resolve() as PdfArray ?? [];
    }

    private static List<string> ChangedKeys(PdfDictionary a, PdfDictionary b)
    {
        HashSet<string> keys = [.. a.Entries.Select(e => e.Key.Value), .. b.Entries.Select(e => e.Key.Value)];
        return [.. keys.Where(k => !Same(a[k] ?? PdfNull.Instance, b[k] ?? PdfNull.Instance, 0)).Order(StringComparer.Ordinal)];
    }

    private static string FullName(PdfDictionary field)
    {
        List<string> names = [];
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        for (PdfDictionary? node = field; node is not null && seen.Add(node); node = node.Get("Parent")?.Resolve() as PdfDictionary)
        {
            if (node.GetText("T") is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }

        names.Reverse();
        return string.Join('.', names);
    }

    /// <summary>Whether two values are the same as written: references by number, streams by dictionary and data.</summary>
    private static bool Same(PdfObject a, PdfObject b, int depth)
    {
        if (depth > 64)
        {
            return false;
        }

        switch (a, b)
        {
            case (PdfReference x, PdfReference y):
                return x.ObjectNumber == y.ObjectNumber && x.Generation == y.Generation;
            case (PdfDictionary x, PdfDictionary y):
                {
                    HashSet<string> keys = [.. x.Entries.Select(e => e.Key.Value), .. y.Entries.Select(e => e.Key.Value)];
                    return keys.All(k => Same(x[k] ?? PdfNull.Instance, y[k] ?? PdfNull.Instance, depth + 1));
                }

            case (PdfArray x, PdfArray y):
                return x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second, depth + 1));
            case (PdfStream x, PdfStream y):
                {
                    if (!Same(Without(x.Dictionary, "Length"), Without(y.Dictionary, "Length"), depth + 1))
                    {
                        return false;
                    }

                    try
                    {
                        return x.GetDecodedBytes().AsSpan().SequenceEqual(y.GetDecodedBytes());
                    }
                    catch (PdfException)
                    {
                        return false;
                    }
                }

            default:
                return a.GetType() == b.GetType() && a.Equals(b);
        }
    }

    private static PdfDictionary Without(PdfDictionary dictionary, string key)
    {
        PdfDictionary copy = dictionary.Clone();
        copy.Remove(key);
        return copy;
    }

    /// <summary>The role of every indirect object the catalog and trailer reach in a revision.</summary>
    private static Dictionary<int, Role> Roles(PdfDocument document)
    {
        Dictionary<int, Role> roles = [];
        Queue<(PdfObject Value, Role Role)> queue = [];
        Visit(document.Trailer["Root"], Role.Catalog);
        Visit(document.Trailer["Info"], Role.Info);
        int steps = 0;
        while (queue.Count > 0 && steps++ < 2_000_000)
        {
            (PdfObject value, Role role) = queue.Dequeue();
            switch (value)
            {
                case PdfDictionary dictionary:
                    foreach ((PdfName key, PdfObject child) in dictionary.Entries)
                    {
                        if (ChildRole(role, key.Value, dictionary, child) is Role childRole and not Role.None)
                        {
                            Visit(child, childRole);
                        }
                    }

                    break;
                case PdfArray array:
                    foreach (PdfObject item in array)
                    {
                        Visit(item, ItemRole(role, item));
                    }

                    break;
                case PdfStream stream:
                    queue.Enqueue((stream.Dictionary, role));
                    break;
            }
        }

        return roles;

        void Visit(PdfObject? value, Role role)
        {
            if (value is null)
            {
                return;
            }

            if (value is PdfReference reference)
            {
                Role had = roles.GetValueOrDefault(reference.ObjectNumber);
                if ((had & role) == role)
                {
                    return;
                }

                PdfObject resolved;
                try
                {
                    resolved = document.Objects[reference];
                }
                catch (PdfException)
                {
                    return;
                }

                // Outlines, structure elements and actions point at pages and annotations, which keep the roles the page tree
                // gives them, and a signature reference's /Data points back at the catalog.
                if (role is Role.Other or Role.Signature && resolved is PdfDictionary target
                    && (target.GetName("Type")?.Value is "Page" or "Pages" or "Annot" or "Catalog" || (target.ContainsKey("Subtype") && target.ContainsKey("Rect"))))
                {
                    return;
                }

                roles[reference.ObjectNumber] = had | role;

                queue.Enqueue((resolved, role));
                return;
            }

            queue.Enqueue((value, role));
        }
    }

    /// <summary>The role of an array item: the kids of a page tree node and the annotations of a page are told apart by type.</summary>
    private static Role ItemRole(Role role, PdfObject item)
    {
        if (role is Role.Pages)
        {
            return (item.Resolve() as PdfDictionary)?.GetName("Type")?.Value == "Pages" ? Role.Pages : Role.Page;
        }

        if (role is Role.Annotation or Role.Widget or Role.Field)
        {
            PdfDictionary? dictionary = item.Resolve() as PdfDictionary;
            return dictionary?.GetName("Subtype")?.Value == "Widget" ? Role.Widget
                : dictionary?.ContainsKey("FT") == true || dictionary?.ContainsKey("T") == true || dictionary?.ContainsKey("Kids") == true ? Role.Field
                : Role.Annotation;
        }

        return role;
    }

    /// <summary>The role a dictionary entry gives the value it holds; <see cref="Role.None"/> for links back up, which are not followed.</summary>
    private static Role ChildRole(Role role, string key, PdfDictionary owner, PdfObject child)
    {
        if (key is "Parent" && role is not (Role.Widget or Role.Field))
        {
            return Role.None;
        }

        if (key is "P" or "IRT" or "Dest" or "D" && role is Role.Annotation or Role.Widget or Role.Field)
        {
            return Role.None;
        }

        // A signature reference's /Data points back at the catalog it signs.
        if (key is "Data" && role is Role.Signature)
        {
            return Role.None;
        }

        return role switch
        {
            Role.Catalog => key switch
            {
                "Pages" => Role.Pages,
                "AcroForm" => Role.Form,
                "Metadata" => Role.Metadata,
                "DSS" => Role.Dss,
                "Extensions" => Role.Dss,
                "Perms" => Role.Signature,
                _ => Role.Other,
            },
            Role.Pages => key switch
            {
                "Kids" => Role.Pages,
                _ => Role.Resource,
            },
            Role.Page => key switch
            {
                "Contents" => Role.Content,
                "Annots" => Role.Annotation,
                "Metadata" => Role.Metadata,
                _ => Role.Resource,
            },
            Role.Annotation or Role.Widget or Role.Field => key switch
            {
                "AP" => Role.Appearance,
                "Kids" => Role.Field,
                "Parent" => Role.Field,
                "Popup" => Role.Annotation,
                "V" when (owner.GetName("FT")?.Value ?? "") == "Sig" || child.Resolve() is PdfDictionary { } v && v.ContainsKey("ByteRange") => Role.Signature,
                _ => role == Role.Annotation ? Role.Annotation : Role.Field,
            },
            Role.Form => key switch
            {
                "Fields" => Role.Field,
                "DR" => Role.FormResource,
                _ => Role.Form,
            },
            _ => role,
        };
    }
}
