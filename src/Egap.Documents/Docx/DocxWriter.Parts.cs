using System.Globalization;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>The parts around the body: notes, comments, styles, numbering, settings, theme, properties and fonts.</summary>
internal sealed partial class DocxWriter
{
    private const string WordType = "application/vnd.openxmlformats-officedocument.wordprocessingml.";

    /// <summary>The original part related to the main part by <paramref name="type"/>, or null.</summary>
    private OpcPart? OriginalPart(string type)
    {
        return data?.Package.GetPart(mainName) is { } main ? data.Package.GetRelatedPart(main.Relationships, type) : null;
    }

    /// <summary>The name of the part of <paramref name="type"/>: the original one, or a new one related from the main part.</summary>
    private string PartFor(string type, string defaultName)
    {
        if (OriginalPart(type) is { } original)
        {
            return original.Name;
        }

        string name = defaultName;
        if (!writtenParts.Contains(name))
        {
            package.AddRelationship(mainName, package.NextRelationshipId(mainName), type, RelationshipTarget(mainName, name));
        }

        return name;
    }

    private static XDocument Xml(XElement root)
    {
        return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root);
    }

    private void WriteNotes(NoteKind kind)
    {
        bool endnote = kind == NoteKind.Endnote;
        string type = endnote ? OpcRelationshipTypes.Endnotes : OpcRelationshipTypes.Footnotes;
        string element = endnote ? "endnote" : "footnote";
        List<Footnote> notes = [.. noteIds.Keys.Where(n => n.Kind == kind).OrderBy(n => noteIds[n])];
        OpcPart? original = OriginalPart(type);
        if (notes.Count == 0 && original is null)
        {
            return;
        }

        string name = original?.Name ?? (endnote ? "/word/endnotes.xml" : "/word/footnotes.xml");
        if (!regenerate && original is not null && NotesUnchanged(notes, original, element))
        {
            writtenParts.Add(name);
            return;
        }

        name = PartFor(type, name);
        string previous = currentPart;
        currentPart = name;
        XElement root = original?.LoadXml().Root is { } originalRoot ? new XElement(originalRoot.Name, originalRoot.Attributes()) : NewRoot(endnote ? "endnotes" : "footnotes");
        List<XElement> special = [.. data?.SpecialNotes.Where(n => n.Name.LocalName == element) ?? []];
        if (special.Count == 0 && original is null)
        {
            special.Add(new XElement(Ns.W + element, new XAttribute(Ns.W + "type", "separator"), new XAttribute(Ns.W + "id", -1),
                new XElement(Ns.W + "p", new XElement(Ns.W + "pPr", new XElement(Ns.W + "spacing", new XAttribute(Ns.W + "after", 0), new XAttribute(Ns.W + "line", 240), new XAttribute(Ns.W + "lineRule", "auto"))),
                    new XElement(Ns.W + "r", new XElement(Ns.W + "separator")))));
            special.Add(new XElement(Ns.W + element, new XAttribute(Ns.W + "type", "continuationSeparator"), new XAttribute(Ns.W + "id", 0),
                new XElement(Ns.W + "p", new XElement(Ns.W + "pPr", new XElement(Ns.W + "spacing", new XAttribute(Ns.W + "after", 0), new XAttribute(Ns.W + "line", 240), new XAttribute(Ns.W + "lineRule", "auto"))),
                    new XElement(Ns.W + "r", new XElement(Ns.W + "continuationSeparator")))));
        }

        root.Add(special.Select(n => new XElement(n)));
        root.Add(data?.Unreferenced.Where(n => n.Name.LocalName == element).Select(n => new XElement(n)));
        foreach (Footnote note in notes)
        {
            XElement e = Kept(note.Story)?.Original is { } keptNote ? new XElement(keptNote.Name, keptNote.Attributes()) : new XElement(Ns.W + element);
            e.SetAttributeValue(Ns.W + "id", noteIds[note]);
            List<XElement> blocks = [.. WriteBlocks(note.Story.Blocks, ensureParagraph: Kept(note.Story) is null)];
            EnsureMarker(blocks, endnote ? "endnoteRef" : "footnoteRef", endnote ? "EndnoteReference" : "FootnoteReference");
            e.Add(blocks);
            root.Add(e);
        }

        currentPart = previous;
        DeclarePrefixes(root);
        package.AddXmlPart(name, WordType + (endnote ? "endnotes+xml" : "footnotes+xml"), Xml(root));
        writtenParts.Add(name);
    }

    private bool NotesUnchanged(List<Footnote> notes, OpcPart original, string noteName)
    {
        int originalCount = original.LoadXml().Root?.Elements().Count(e => e.W("type") is null or "normal") ?? 0;
        if (originalCount != notes.Count + (data?.Unreferenced.Count(n => n.Name.LocalName == noteName) ?? 0))
        {
            return false;
        }

        foreach (Footnote note in notes)
        {
            if (Kept(note.Story) is not { Original: { } element } || DocxUnits.Int(element.W("id")) != noteIds[note] || !StoryUnchanged(note.Story, original.Name))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The first paragraph of a note or comment starts with its reference mark, which the reader dropped.</summary>
    private static void EnsureMarker(List<XElement> blocks, string marker, string style)
    {
        XElement? first = blocks.Find(b => b.Name == Ns.W + "p");
        if (first is null || first.Descendants(Ns.W + marker).Any())
        {
            return;
        }

        XElement run = new(Ns.W + "r", new XElement(Ns.W + "rPr", DocxPropertyWriter.Val("rStyle", style)), new XElement(Ns.W + marker));
        if (first.Element(Ns.W + "pPr") is { } pPr)
        {
            pPr.AddAfterSelf(run);
        }
        else
        {
            first.AddFirst(run);
        }
    }

    private void WriteComments()
    {
        // Comments in the order the file had them, new ones after in document order.
        List<Comment> comments = [.. document.Descendants<Comment>().Select((c, i) => (c, i))
            .OrderBy(x => Kept(x.c.Story)?.Original is { } e && e.Parent is { } parent ? e.ElementsBeforeSelf().Count() : int.MaxValue).ThenBy(x => x.i).Select(x => x.c)];
        OpcPart? original = OriginalPart(OpcRelationshipTypes.Comments);
        OpcPart? originalExtended = OriginalPart(OpcRelationshipTypes.CommentsExtended);
        if (comments.Count == 0 && original is null)
        {
            return;
        }

        string name = original?.Name ?? "/word/comments.xml";
        if (!regenerate && original is not null && CommentsUnchanged(comments, original))
        {
            writtenParts.Add(name);
            return;
        }

        name = PartFor(OpcRelationshipTypes.Comments, name);
        string previous = currentPart;
        currentPart = name;
        XElement root = original?.LoadXml().Root is { } originalRoot ? new XElement(originalRoot.Name, originalRoot.Attributes()) : NewRoot("comments");
        List<(int Index, XElement Element)> written = [];
        Dictionary<Comment, string> paraIds = [];
        bool extendedNeeded = comments.Exists(c => c.Done || c.ParentId is not null) || originalExtended is not null;
        foreach (Comment comment in comments)
        {
            XElement e = Kept(comment.Story)?.Original is { } keptComment
                ? new XElement(keptComment.Name, keptComment.Attributes().Where(a => a.Name.Namespace != Ns.W || a.Name.LocalName is not ("author" or "date" or "initials" or "id")))
                : new XElement(Ns.W + "comment");
            e.SetAttributeValue(Ns.W + "id", comment.Id);
            e.SetAttributeValue(Ns.W + "author", comment.Author);
            if (comment.Date is { } date)
            {
                e.SetAttributeValue(Ns.W + "date", date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            }

            e.SetAttributeValue(Ns.W + "initials", comment.Initials);
            List<XElement> blocks = [.. WriteBlocks(comment.Story.Blocks, ensureParagraph: Kept(comment.Story) is null)];
            EnsureMarker(blocks, "annotationRef", "CommentReference");
            if (extendedNeeded && blocks.LastOrDefault(b => b.Name == Ns.W + "p") is { } last)
            {
                string paraId = (string?)last.Attribute(Ns.W14 + "paraId") ?? NextParaId();
                last.SetAttributeValue(Ns.W14 + "paraId", paraId);
                paraIds[comment] = paraId;
            }

            e.Add(blocks);
            written.Add((Kept(comment.Story)?.Original is { Parent: not null } keptElement ? keptElement.ElementsBeforeSelf().Count() : int.MaxValue, e));
        }

        // Comments the text does not name go back where the file had them.
        written.AddRange(data?.Unreferenced.Where(n => n.Name.LocalName == "comment").Select(n => (n.Parent is null ? int.MaxValue : n.ElementsBeforeSelf().Count(), new XElement(n))) ?? []);
        root.Add(written.Select((w, i) => (w, i)).OrderBy(x => x.w.Index).ThenBy(x => x.i).Select(x => x.w.Element));
        currentPart = previous;
        DeclarePrefixes(root);
        package.AddXmlPart(name, WordType + "comments+xml", Xml(root));
        writtenParts.Add(name);

        if (extendedNeeded)
        {
            string extendedName = PartFor(OpcRelationshipTypes.CommentsExtended, originalExtended?.Name ?? "/word/commentsExtended.xml");
            XElement extended = originalExtended?.LoadXml().Root is { } extendedRoot
                ? new XElement(extendedRoot.Name, extendedRoot.Attributes())
                : new XElement(Ns.W15 + "commentsEx", new XAttribute(XNamespace.Xmlns + "w15", Ns.W15.NamespaceName), new XAttribute(XNamespace.Xmlns + "mc", MarkupCompatibility.Mc.NamespaceName),
                    new XAttribute(MarkupCompatibility.Mc + "Ignorable", "w15"));
            foreach (Comment comment in comments)
            {
                if (!paraIds.TryGetValue(comment, out string? paraId))
                {
                    continue;
                }

                XNamespace w15 = extended.Name.Namespace;
                XElement entry = new(w15 + "commentEx", new XAttribute(w15 + "paraId", paraId));
                if (comment.ParentComment is { } parent && paraIds.TryGetValue(parent, out string? parentId))
                {
                    entry.SetAttributeValue(w15 + "paraIdParent", parentId);
                }

                entry.SetAttributeValue(w15 + "done", comment.Done ? "1" : "0");
                extended.Add(entry);
            }

            package.AddXmlPart(extendedName, WordType + "commentsExtended+xml", Xml(extended));
            writtenParts.Add(extendedName);
        }
    }

    private bool CommentsUnchanged(List<Comment> comments, OpcPart original)
    {
        if ((original.LoadXml().Root?.Elements(Ns.W + "comment").Count() ?? 0) != comments.Count + (data?.Unreferenced.Count(n => n.Name.LocalName == "comment") ?? 0))
        {
            return false;
        }

        foreach (Comment comment in comments)
        {
            if (Kept(comment.Story) is not { Original: { } element } || DocxUnits.Int(element.W("id")) != comment.Id || element.W("author") != comment.Author
                || element.W("initials") != comment.Initials || !StoryUnchanged(comment.Story, original.Name))
            {
                return false;
            }
        }

        // Done and replies live in commentsExtended, written again only when they changed.
        return !comments.Exists(c => c.Done != WasDone(c) || c.ParentId != WasParent(c));

        bool WasDone(Comment comment)
        {
            return ExtendedOf(comment)?.Done ?? false;
        }

        int? WasParent(Comment comment)
        {
            string? parentParaId = ExtendedOf(comment)?.Parent;
            return parentParaId is null ? null : comments.Find(c => LastParaId(c) == parentParaId)?.Id;
        }

        (string? Parent, bool Done)? ExtendedOf(Comment comment)
        {
            if (LastParaId(comment) is not { } paraId || OriginalPart(OpcRelationshipTypes.CommentsExtended)?.LoadXml().Root is not { } root)
            {
                return null;
            }

            XNamespace w15 = root.Name.Namespace;
            XElement? entry = root.Elements(w15 + "commentEx").FirstOrDefault(e => (string?)e.Attribute(w15 + "paraId") == paraId);
            return entry is null ? null : ((string?)entry.Attribute(w15 + "paraIdParent"), (string?)entry.Attribute(w15 + "done") is "1" or "true");
        }

        string? LastParaId(Comment comment)
        {
            return (string?)Kept(comment.Story)?.Original?.Elements(Ns.W + "p").LastOrDefault()?.Attribute(Ns.W14 + "paraId");
        }
    }

    private bool FormattingUnchanged => !regenerate && data is not null && document.FormattingVersion == data.FormattingVersion;

    /// <summary>A file read without a styles, numbering or theme part gets none unless the formatting was edited.</summary>
    private bool KeepAbsent(OpcPart? original)
    {
        return original is null && data is not null && document.FormattingVersion == data.FormattingVersion;
    }

    private void WriteStyles()
    {
        OpcPart? original = OriginalPart(OpcRelationshipTypes.Styles);
        if (KeepAbsent(original))
        {
            return;
        }

        if (original is not null && FormattingUnchanged)
        {
            writtenParts.Add(original.Name);
            return;
        }

        string name = PartFor(OpcRelationshipTypes.Styles, original?.Name ?? "/word/styles.xml");
        XElement? originalRoot = original?.LoadXml().Root;
        XElement root = originalRoot is not null ? new XElement(originalRoot.Name, originalRoot.Attributes()) : NewRoot("styles");
        XElement? defaults = originalRoot?.Element(Ns.W + "docDefaults");
        List<XElement> keptRun = [], keptParagraph = [];
        DocxProperties.ReadRun(defaults?.Element(Ns.W + "rPrDefault")?.Element(Ns.W + "rPr"), new RunFormat(), keptRun.Add);
        DocxProperties.ReadParagraph(defaults?.Element(Ns.W + "pPrDefault")?.Element(Ns.W + "pPr"), new ParagraphFormat(), keptParagraph.Add);
        root.Add(new XElement(
            Ns.W + "docDefaults",
            new XElement(Ns.W + "rPrDefault", DocxPropertyWriter.Run(document.Styles.DefaultRunFormat, null, keptRun) ?? new XElement(Ns.W + "rPr")),
            new XElement(Ns.W + "pPrDefault", DocxPropertyWriter.Paragraph(document.Styles.DefaultParagraphFormat, null, keptParagraph) ?? new XElement(Ns.W + "pPr"))));
        if (originalRoot?.Element(Ns.W + "latentStyles") is { } latent)
        {
            root.Add(new XElement(latent));
        }

        foreach (Style style in document.Styles)
        {
            root.Add(WriteStyle(style, data?.Styles.GetValueOrDefault(style.Id)));
        }

        package.AddXmlPart(name, WordType + "styles+xml", Xml(root));
        writtenParts.Add(name);
    }

    private static readonly string[] StyleOrder =
    [
        "name", "aliases", "basedOn", "next", "link", "autoRedefine", "hidden", "uiPriority", "semiHidden", "unhideWhenUsed", "qFormat", "locked",
        "personal", "personalCompose", "personalReply", "rsid", "pPr", "rPr", "tblPr", "trPr", "tcPr", "tblStylePr",
    ];

    private static XElement WriteStyle(Style style, XElement? original)
    {
        XElement e = original is not null
            ? new XElement(original.Name, original.Attributes().Where(a => a.Name.Namespace != Ns.W || a.Name.LocalName is not ("type" or "default" or "styleId")))
            : new XElement(Ns.W + "style");
        e.SetAttributeValue(Ns.W + "type", style.Type switch
        {
            StyleType.Character => "character",
            StyleType.Table => "table",
            StyleType.List => "numbering",
            _ => "paragraph",
        });
        e.SetAttributeValue(Ns.W + "default", style.IsDefault ? "1" : null);
        e.SetAttributeValue(Ns.W + "styleId", style.Id);
        List<XElement?> c = [DocxPropertyWriter.Val("name", style.Name)];
        if (style.BasedOn is { } basedOn)
        {
            c.Add(DocxPropertyWriter.Val("basedOn", basedOn.Id));
        }

        if (style is ParagraphStyle { Next: { } next })
        {
            c.Add(DocxPropertyWriter.Val("next", next.Id));
        }

        if (style is ParagraphStyle { Linked: { } linkedCharacter })
        {
            c.Add(DocxPropertyWriter.Val("link", linkedCharacter.Id));
        }
        else if (style is CharacterStyle { Linked: { } linkedParagraph })
        {
            c.Add(DocxPropertyWriter.Val("link", linkedParagraph.Id));
        }

        if (style.Hidden)
        {
            c.Add(new XElement(Ns.W + (original?.Element(Ns.W + "hidden") is not null ? "hidden" : "semiHidden")));
        }

        if (style.Priority is int priority)
        {
            c.Add(DocxPropertyWriter.Val("uiPriority", priority.ToString(CultureInfo.InvariantCulture)));
        }

        if (style.IsPrimary)
        {
            c.Add(new XElement(Ns.W + "qFormat"));
        }

        // Children of the original element the model does not map (aliases, locked, rsid and the like).
        foreach (XElement extra in original?.Elements() ?? [])
        {
            if (extra.Name.Namespace != Ns.W || extra.Name.LocalName is not ("name" or "basedOn" or "next" or "link" or "hidden" or "semiHidden" or "uiPriority" or "qFormat"
                or "pPr" or "rPr" or "tblPr" or "trPr" or "tcPr" or "tblStylePr"))
            {
                c.Add(new XElement(extra));
            }
        }

        XElement? pPr = original?.Element(Ns.W + "pPr");
        XElement? rPr = original?.Element(Ns.W + "rPr");
        switch (style)
        {
            case ParagraphStyle paragraph:
                c.Add(DocxPropertyWriter.Paragraph(paragraph.ParagraphFormat, null, KeptParagraph(pPr)));
                c.Add(DocxPropertyWriter.Run(paragraph.RunFormat, null, KeptRun(rPr)));
                break;
            case CharacterStyle character:
                c.Add(DocxPropertyWriter.Run(character.RunFormat, null, KeptRun(rPr)));
                break;
            case TableStyle table:
                c.Add(DocxPropertyWriter.Paragraph(table.ParagraphFormat, null, KeptParagraph(pPr)));
                c.Add(DocxPropertyWriter.Run(table.RunFormat, null, KeptRun(rPr)));
                c.Add(DocxPropertyWriter.Table(table.TableFormat, null, KeptTable(original?.Element(Ns.W + "tblPr"))));
                c.Add(DocxPropertyWriter.Row(table.RowFormat, null));
                c.Add(DocxPropertyWriter.Cell(table.CellFormat, null));
                foreach ((TableStyleRegion region, string type) in Regions)
                {
                    TableStyleConditional conditional = table[region];
                    if (conditional.IsEmpty)
                    {
                        continue;
                    }

                    c.Add(new XElement(Ns.W + "tblStylePr", new XAttribute(Ns.W + "type", type),
                        DocxPropertyWriter.Paragraph(conditional.ParagraphFormat, null, null),
                        DocxPropertyWriter.Run(conditional.RunFormat, null, null),
                        DocxPropertyWriter.Table(conditional.TableFormat, null, null),
                        DocxPropertyWriter.Row(conditional.RowFormat, null),
                        DocxPropertyWriter.Cell(conditional.CellFormat, null)));
                }

                break;
            case ListStyle list:
                if (list.List is { } styleList)
                {
                    c.Add(new XElement(Ns.W + "pPr", new XElement(Ns.W + "numPr", DocxPropertyWriter.Val("numId", styleList.Id.ToString(CultureInfo.InvariantCulture)))));
                }

                break;
        }

        e.Add(c.OfType<XElement>().Select((x, i) => (x, i)).OrderBy(x => Array.IndexOf(StyleOrder, x.x.Name.LocalName) is int at && at >= 0 ? at : StyleOrder.Length).ThenBy(x => x.i).Select(x => x.x));
        return e;
    }

    private static readonly (TableStyleRegion Region, string Type)[] Regions =
    [
        (TableStyleRegion.FirstRow, "firstRow"), (TableStyleRegion.LastRow, "lastRow"), (TableStyleRegion.FirstColumn, "firstCol"), (TableStyleRegion.LastColumn, "lastCol"),
        (TableStyleRegion.OddRowBand, "band1Horz"), (TableStyleRegion.EvenRowBand, "band2Horz"), (TableStyleRegion.OddColumnBand, "band1Vert"),
        (TableStyleRegion.EvenColumnBand, "band2Vert"), (TableStyleRegion.TopRightCell, "neCell"), (TableStyleRegion.TopLeftCell, "nwCell"),
        (TableStyleRegion.BottomRightCell, "seCell"), (TableStyleRegion.BottomLeftCell, "swCell"),
    ];

    private static List<XElement> KeptRun(XElement? rPr)
    {
        List<XElement> kept = [];
        DocxProperties.ReadRun(rPr, new RunFormat(), kept.Add);
        return kept;
    }

    private static List<XElement> KeptParagraph(XElement? pPr)
    {
        List<XElement> kept = [];
        DocxProperties.ReadParagraph(pPr, new ParagraphFormat(), kept.Add);
        return kept;
    }

    private static List<XElement> KeptTable(XElement? tblPr)
    {
        List<XElement> kept = [];
        DocxProperties.ReadTable(tblPr, new TableFormat(), kept.Add);
        return kept;
    }

    private void WriteNumbering()
    {
        OpcPart? original = OriginalPart(OpcRelationshipTypes.Numbering);
        if (KeepAbsent(original))
        {
            return;
        }

        if (original is not null && FormattingUnchanged)
        {
            writtenParts.Add(original.Name);
            return;
        }

        if (original is null && document.Lists.Count == 0)
        {
            return;
        }

        string name = PartFor(OpcRelationshipTypes.Numbering, original?.Name ?? "/word/numbering.xml");
        XElement? originalRoot = original?.LoadXml().Root;
        XElement root = originalRoot is not null ? new XElement(originalRoot.Name, originalRoot.Attributes()) : NewRoot("numbering");
        root.Add(originalRoot?.Elements(Ns.W + "numPicBullet").Select(e => new XElement(e)));
        Dictionary<int, XElement> originalAbstracts = [];
        foreach (XElement abstractNum in originalRoot?.Elements(Ns.W + "abstractNum") ?? [])
        {
            if (DocxUnits.Int(abstractNum.W("abstractNumId")) is int id)
            {
                originalAbstracts.TryAdd(id, abstractNum);
            }
        }

        foreach (ListDefinition definition in document.Lists.Definitions)
        {
            XElement? source = originalAbstracts.GetValueOrDefault(definition.Id);
            XElement e = source is not null ? new XElement(source.Name, source.Attributes()) : new XElement(Ns.W + "abstractNum");
            e.SetAttributeValue(Ns.W + "abstractNumId", definition.Id);
            e.Add(source?.Element(Ns.W + "nsid") is { } nsid ? new XElement(nsid) : null);
            e.Add(DocxPropertyWriter.Val("multiLevelType", definition.Kind switch
            {
                ListKind.SingleLevel => "singleLevel",
                ListKind.Multilevel => "multilevel",
                _ => "hybridMultilevel",
            }));
            e.Add(source?.Element(Ns.W + "tmpl") is { } tmpl ? new XElement(tmpl) : null);
            if (definition.Name is { } listName)
            {
                e.Add(DocxPropertyWriter.Val("name", listName));
            }

            e.Add(source?.Element(Ns.W + "styleLink") is { } styleLink ? new XElement(styleLink) : null);
            e.Add(source?.Element(Ns.W + "numStyleLink") is { } numStyleLink ? new XElement(numStyleLink) : null);
            foreach (ListLevel level in definition.Levels)
            {
                XElement? sourceLevel = source?.Elements(Ns.W + "lvl").FirstOrDefault(l => DocxUnits.Int(l.W("ilvl")) == level.Index);
                e.Add(WriteLevel(level, sourceLevel));
            }

            root.Add(e);
        }

        Dictionary<int, XElement> originalNums = [];
        foreach (XElement num in originalRoot?.Elements(Ns.W + "num") ?? [])
        {
            if (DocxUnits.Int(num.W("numId")) is int id)
            {
                originalNums.TryAdd(id, num);
            }
        }

        foreach (List list in document.Lists)
        {
            XElement? source = originalNums.GetValueOrDefault(list.Id);
            XElement num = source is not null ? new XElement(source.Name, source.Attributes()) : new XElement(Ns.W + "num");
            num.SetAttributeValue(Ns.W + "numId", list.Id);
            num.Add(DocxPropertyWriter.Val("abstractNumId", list.Definition.Id.ToString(CultureInfo.InvariantCulture)));
            foreach (ListLevelOverride levelOverride in list.Overrides)
            {
                if (levelOverride.StartAt is null && levelOverride.Level is null)
                {
                    continue;
                }

                XElement o = new(Ns.W + "lvlOverride", new XAttribute(Ns.W + "ilvl", levelOverride.Index));
                if (levelOverride.StartAt is int start)
                {
                    o.Add(DocxPropertyWriter.Val("startOverride", start.ToString(CultureInfo.InvariantCulture)));
                }

                if (levelOverride.Level is { } level)
                {
                    XElement? sourceLevel = source?.Elements(Ns.W + "lvlOverride").FirstOrDefault(x => DocxUnits.Int(x.W("ilvl")) == levelOverride.Index)?.Element(Ns.W + "lvl");
                    o.Add(WriteLevel(level, sourceLevel));
                }

                num.Add(o);
            }

            root.Add(num);
        }

        package.AddXmlPart(name, WordType + "numbering+xml", Xml(root));
        writtenParts.Add(name);
    }

    private static XElement WriteLevel(ListLevel level, XElement? source)
    {
        XElement e = source is not null ? new XElement(source.Name, source.Attributes()) : new XElement(Ns.W + "lvl");
        e.SetAttributeValue(Ns.W + "ilvl", level.Index);
        e.Add(DocxPropertyWriter.Val("start", level.StartAt.ToString(CultureInfo.InvariantCulture)));
        e.Add(DocxPropertyWriter.Val("numFmt", DocxPropertyWriter.NumberFormatName(level.NumberStyle)));
        if (level.RestartAfterLevel is int restart)
        {
            e.Add(DocxPropertyWriter.Val("lvlRestart", restart.ToString(CultureInfo.InvariantCulture)));
        }

        if (level.LinkedStyle is { } style)
        {
            e.Add(DocxPropertyWriter.Val("pStyle", style.Id));
        }

        if (level.IsLegal)
        {
            e.Add(new XElement(Ns.W + "isLgl"));
        }

        if (level.Suffix != ListLabelSuffix.Tab)
        {
            e.Add(DocxPropertyWriter.Val("suff", level.Suffix == ListLabelSuffix.Space ? "space" : "nothing"));
        }

        e.Add(DocxPropertyWriter.Val("lvlText", level.LevelText));
        e.Add(source?.Element(Ns.W + "lvlPicBulletId") is { } picture ? new XElement(picture) : null);
        e.Add(source?.Element(Ns.W + "legacy") is { } legacy ? new XElement(legacy) : null);
        e.Add(DocxPropertyWriter.Val("lvlJc", level.Alignment switch
        {
            ParagraphAlignment.Center => "center",
            ParagraphAlignment.Right => "right",
            _ => "left",
        }));
        e.Add(DocxPropertyWriter.Paragraph(level.ParagraphFormat, null, KeptParagraph(source?.Element(Ns.W + "pPr"))));
        e.Add(DocxPropertyWriter.Run(level.RunFormat, null, KeptRun(source?.Element(Ns.W + "rPr"))));
        return e;
    }
}
