using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// Writes the model as a transitional DOCX package (Phase 6 Build Plan task 12). A document read from DOCX keeps its
/// package: parts the model does not map are copied with their relationships, unchanged paragraphs, tables and stories are
/// written as they were read, and edited ones are written from the model with the markup the reader kept merged back.
/// </summary>
internal sealed partial class DocxWriter
{
    private const string MainContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

    private readonly Document document;
    private readonly DocxPackageData? data;
    private readonly OpcPackageWriter package = new();
    private readonly string mainName;
    private readonly bool regenerate;
    private string mainContentType = MainContentType;
    private readonly Dictionary<Footnote, int> noteIds = [];
    private readonly Dictionary<string, int> bookmarkIds = new(StringComparer.Ordinal);
    private readonly HashSet<int> usedBookmarkIds = [];
    private readonly WarningCollector warnings = new();
    private readonly HashSet<string> writtenParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ImageData, string> media = [];
    private readonly Dictionary<string, string> rebased = new(StringComparer.OrdinalIgnoreCase);
    private int nextBookmarkId;
    private int nextRevisionId = 1;
    private int nextDrawingId = 1;
    private int nextParaId = 0x10000000;

    // The part being written, whose relationships new hyperlinks and images go into.
    private string currentPart;

    private DocxWriter(Document document, bool regenerate)
    {
        this.document = document;
        this.regenerate = regenerate;
        data = DocxPackageData.Of(document);
        mainName = data?.MainPartName ?? "/word/document.xml";
        currentPart = mainName;
    }

    /// <param name="document">The document.</param>
    /// <param name="stream">Where the package goes.</param>
    /// <param name="regenerate">For tests: write every story, table and paragraph from the model, as if all were edited.</param>
    /// <param name="kind">The kind to write, or null to keep the kind read.</param>
    /// <returns>What DOCX could not hold.</returns>
    public static IReadOnlyList<ConversionWarning> Write(Document document, Stream stream, bool regenerate = false, DocxKind? kind = null)
    {
        DocxWriter writer = new(document, regenerate);
        writer.mainContentType = kind is { } k ? ContentTypeOf(k) : writer.data?.MainContentType ?? MainContentType;
        writer.WriteAll();
        writer.package.Save(stream);
        return writer.warnings.ToList();
    }

    private void WriteAll()
    {
        CopyOriginal();
        if (data is null)
        {
            package.AddRelationship("/", "rId1", OpcRelationshipTypes.OfficeDocument, "word/document.xml");
        }

        AssignIds();
        WriteMain();
        WriteNotes(NoteKind.Footnote);
        WriteNotes(NoteKind.Endnote);
        WriteComments();
        WriteStyles();
        WriteNumbering();
        WriteSettings();
        WriteTheme();
        WriteProperties();
        WriteFonts();
    }

    /// <summary>Copies the package as read: every part and every relationship; mapped parts are replaced as they are written.</summary>
    private void CopyOriginal()
    {
        if (data is null)
        {
            return;
        }

        OpcPackage original = data.Package;
        foreach (OpcRelationship relationship in original.Relationships)
        {
            package.AddRelationship("/", relationship.Id, relationship.Type, relationship.Target, relationship.External);
        }

        foreach (OpcPart part in original.Parts)
        {
            if (part.IsRelationships || part.Name.Equals("/[Content_Types].xml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            package.AddPart(part.Name, part.ContentType, part.Data);
            foreach (OpcRelationship relationship in part.Relationships)
            {
                package.AddRelationship(part.Name, relationship.Id, relationship.Type, relationship.Target, relationship.External);
            }
        }
    }

    /// <summary>Note ids kept from the file where they are free, so unchanged paragraphs that name them stay right.</summary>
    private void AssignIds()
    {
        HashSet<int> used = [];
        foreach (XElement note in (data?.SpecialNotes ?? []).Concat(data?.Unreferenced ?? []))
        {
            if (note.Name.LocalName is "footnote" or "endnote" && DocxUnits.Int(note.W("id")) is int noteId)
            {
                used.Add(noteId + (note.Name.LocalName == "endnote" ? 1 << 30 : 0));
            }
        }

        List<Footnote> fresh = [];
        foreach (Footnote note in document.Descendants<Footnote>())
        {
            if (Kept(note) is { Original: { } reference } && DocxUnits.Int(reference.W("id")) is int id && id >= 0 && used.Add(id + (note.Kind == NoteKind.Endnote ? 1 << 30 : 0)))
            {
                noteIds[note] = id;
            }
            else
            {
                fresh.Add(note);
            }
        }

        int footnote = 1, endnote = 1;
        foreach (Footnote note in fresh)
        {
            int offset = note.Kind == NoteKind.Endnote ? 1 << 30 : 0;
            ref int next = ref note.Kind == NoteKind.Endnote ? ref endnote : ref footnote;
            while (!used.Add(next + offset))
            {
                next++;
            }

            noteIds[note] = next;
        }

        // Drawing ids (wp:docPr) must be unique: new drawings number after the ones kept from the file.
        int maxDrawingId = 0;
        foreach (Node node in document.Descendants())
        {
            if (node is Picture or Shape or Island && Kept(node)?.Original is { } drawing)
            {
                foreach (XElement docPr in drawing.Descendants(Ns.Wp + "docPr"))
                {
                    maxDrawingId = Math.Max(maxDrawingId, DocxUnits.Int((string?)docPr.Attribute("id")) ?? 0);
                }
            }
        }

        nextDrawingId = maxDrawingId + 1;
        nextRevisionId = (data?.MaxMarkupId ?? 0) + 1;

        foreach (BookmarkStart start in document.Descendants<BookmarkStart>())
        {
            if (Kept(start) is { Original: { } element } && DocxUnits.Int(element.W("id")) is int id && id >= 0)
            {
                usedBookmarkIds.Add(id);
            }
        }
    }

    /// <summary>The reader's data for a node, when it came from this document's reading.</summary>
    private DocxExtension? Kept(Node node)
    {
        return data is not null && node.Extensions.Get<DocxExtension>(DocxExtension.Format) is { } extension && extension.Source == data ? extension : null;
    }

    private void WriteMain()
    {
        XElement root = data?.MainRoot is { } original ? new XElement(original) : NewRoot("document");
        XElement body = new(Ns.W + "body");
        root.RemoveNodes();
        if (data?.Background is { } background)
        {
            root.Add(new XElement(background));
        }

        root.Add(body);
        root.Add(data?.RootExtras.Select(e => new XElement(e)));
        currentPart = mainName;
        for (int i = 0; i < document.Sections.Count; i++)
        {
            Section section = document.Sections[i];
            List<XElement> blocks = [.. WriteBlocks(section.Body.Blocks)];
            XElement sectPr = SectionProperties(section);

            // A section break belongs only on the last paragraph of its section: drop the ones unchanged paragraphs bring along.
            for (int b = 0; b < blocks.Count - (i == document.Sections.Count - 1 ? 0 : 1); b++)
            {
                blocks[b].Element(Ns.W + "pPr")?.Element(Ns.W + "sectPr")?.Remove();
            }

            if (i == document.Sections.Count - 1)
            {
                body.Add(blocks);
                if (sectPr.Attribute("omit") is null)
                {
                    body.Add(sectPr);
                }
            }
            else
            {
                // An unchanged content control or table can hold its section's break in its last paragraph, as read.
                if (blocks.Count > 0 && blocks[^1].Name != Ns.W + "p" && blocks[^1].Descendants(Ns.W + "sectPr").LastOrDefault() is { } inside && XNode.DeepEquals(inside, sectPr))
                {
                    body.Add(blocks);
                    continue;
                }

                // A content control ending a section carries the break in its own last paragraph, as Word writes it.
                XElement? carrier = blocks.Count > 0 ? blocks[^1] : null;
                while (carrier?.Name == Ns.W + "sdt")
                {
                    carrier = carrier.Element(Ns.W + "sdtContent")?.Elements().LastOrDefault();
                }

                if (carrier?.Name != Ns.W + "p")
                {
                    carrier = new XElement(Ns.W + "p");
                    blocks.Add(carrier);
                }

                SetSectionBreak(carrier, sectPr);
                body.Add(blocks);
            }
        }

        DeclarePrefixes(root);
        package.AddXmlPart(mainName, mainContentType, new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root));
        if (KindOf(mainContentType) is DocxKind.Document or DocxKind.Template)
        {
            DropMacros();
        }
        writtenParts.Add(mainName);
    }

    /// <summary>The content type of the main part for a kind.</summary>
    internal static string ContentTypeOf(DocxKind kind)
    {
        return kind switch
        {
            DocxKind.Template => "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml",
            DocxKind.MacroEnabledDocument => "application/vnd.ms-word.document.macroEnabled.main+xml",
            DocxKind.MacroEnabledTemplate => "application/vnd.ms-word.template.macroEnabledTemplate.main+xml",
            _ => MainContentType,
        };
    }

    /// <summary>The kind a main part's content type names.</summary>
    internal static DocxKind KindOf(string contentType)
    {
        return contentType.ToUpperInvariant() switch
        {
            "APPLICATION/VND.OPENXMLFORMATS-OFFICEDOCUMENT.WORDPROCESSINGML.TEMPLATE.MAIN+XML" => DocxKind.Template,
            "APPLICATION/VND.MS-WORD.DOCUMENT.MACROENABLED.MAIN+XML" => DocxKind.MacroEnabledDocument,
            "APPLICATION/VND.MS-WORD.TEMPLATE.MACROENABLEDTEMPLATE.MAIN+XML" => DocxKind.MacroEnabledTemplate,
            _ => DocxKind.Document,
        };
    }

    /// <summary>
    /// A kind without macros keeps none: the VBA project, its data and its signature go, with the
    /// relationships that name them.
    /// </summary>
    private void DropMacros()
    {
        HashSet<string> macroTypes = new(StringComparer.Ordinal)
        {
            OpcRelationshipTypes.VbaProject,
            "http://schemas.microsoft.com/office/2006/relationships/wordVbaData",
            "http://schemas.microsoft.com/office/2006/relationships/vbaProjectSignature",
        };
        foreach (OpcRelationship relationship in package.RelationshipsOf(mainName).Where(r => !r.External && macroTypes.Contains(r.Type)).ToList())
        {
            string part = OpcPackage.ResolveTarget(mainName, relationship.Target);
            foreach (OpcRelationship inner in package.RelationshipsOf(part).Where(r => !r.External).ToList())
            {
                package.RemovePart(OpcPackage.ResolveTarget(part, inner.Target));
            }

            package.RemovePart(part);
        }

        package.RemoveRelationships(mainName, r => macroTypes.Contains(r.Type));
    }

    private static void SetSectionBreak(XElement paragraph, XElement sectPr)
    {
        XElement? pPr = paragraph.Element(Ns.W + "pPr");
        if (pPr is null)
        {
            pPr = new XElement(Ns.W + "pPr");
            paragraph.AddFirst(pPr);
        }

        if (pPr.Element(Ns.W + "sectPr") is { } existing)
        {
            if (XNode.DeepEquals(existing, sectPr))
            {
                return;
            }

            existing.Remove();
        }

        if (pPr.Element(Ns.W + "pPrChange") is { } change)
        {
            change.AddBeforeSelf(sectPr);
        }
        else
        {
            pPr.Add(sectPr);
        }
    }

    private XElement SectionProperties(Section section)
    {
        DocxExtension? kept = Kept(section);
        List<XElement> references = [];
        bool storiesUnchanged = true;
        bool last = section == document.Sections[^1];
        PageSetup setup = section.PageSetup;
        foreach (HeaderFooter story in StoriesOf(section, ref setup))
        {
            (XElement reference, bool reused) = WriteHeaderFooter(story);
            references.Add(reference);
            storiesUnchanged &= reused;
        }

        if (!regenerate && kept is { Original: { } original, Snapshot: PageSetup snapshot } && storiesUnchanged && snapshot.RawEquals(section.PageSetup)
            && original.Elements().Count(e => e.Name == Ns.W + "headerReference" || e.Name == Ns.W + "footerReference") == references.Count)
        {
            return new XElement(original);
        }

        if (last && data is not null && kept is null && references.Count == 0 && section.PageSetup.IsEmpty)
        {
            // The file's body had no final sectPr and nothing gave the section one since.
            return new XElement(Ns.W + "sectPr", new XAttribute("omit", "1"));
        }

        XElement written = DocxPropertyWriter.Section(setup, kept?.UnknownProperties?.GetValueOrDefault("sectPr"), references);
        if (kept?.Original is { } read)
        {
            written.Add(read.Attributes().Select(a => new XAttribute(a)));
        }

        return written;
    }

    /// <summary>
    /// The headers and footers to write for a section. Word has no margin boxes, so those along the top and the bottom are
    /// added to the header or footer as a borderless table of three cells (left, middle and right); a first-page box makes
    /// the section's first page different. Corner and side boxes are left out.
    /// </summary>
    private List<HeaderFooter> StoriesOf(Section section, ref PageSetup setup)
    {
        MarginBoxCollection boxes = section.MarginBoxes;
        if (!boxes.Any(b => b.Blocks.Count > 0 || b.FirstPage))
        {
            return [.. section.HeadersFooters];
        }

        if (boxes.Any(b => b is { IsTop: false, IsBottom: false } || b.Position is MarginBoxPosition.TopLeftCorner or MarginBoxPosition.TopRightCorner
            or MarginBoxPosition.BottomLeftCorner or MarginBoxPosition.BottomRightCorner))
        {
            warnings.Add(ConversionWarningKind.PageLayoutDropped, "margin boxes in the corners and at the sides of the page");
        }

        bool firstBoxes = boxes.Any(b => b.FirstPage);
        bool firstBefore = setup.DifferentFirstPage == true;
        if (firstBoxes && !firstBefore)
        {
            PageSetup copy = new();
            copy.CopyRaw(setup);
            copy.DifferentFirstPage = true;
            setup = copy;
        }

        List<HeaderFooter> stories = [];
        foreach ((HeaderFooterKind kind, MarginBoxPosition start) in (ReadOnlySpan<(HeaderFooterKind, MarginBoxPosition)>)
            [
                (HeaderFooterKind.HeaderPrimary, MarginBoxPosition.TopLeft), (HeaderFooterKind.HeaderFirst, MarginBoxPosition.TopLeft), (HeaderFooterKind.HeaderEven, MarginBoxPosition.TopLeft),
                (HeaderFooterKind.FooterPrimary, MarginBoxPosition.BottomLeft), (HeaderFooterKind.FooterFirst, MarginBoxPosition.BottomLeft), (HeaderFooterKind.FooterEven, MarginBoxPosition.BottomLeft),
            ])
        {
            bool first = kind is HeaderFooterKind.HeaderFirst or HeaderFooterKind.FooterFirst;
            HeaderFooter? own = section.HeadersFooters[kind];
            if (first && own is null && !firstBefore && firstBoxes)
            {
                // The first page showed the primary header or footer until now; it keeps it.
                own = section.HeadersFooters[kind == HeaderFooterKind.HeaderFirst ? HeaderFooterKind.HeaderPrimary : HeaderFooterKind.FooterPrimary];
            }

            bool wanted = section.HeadersFooters[kind] is not null || kind is HeaderFooterKind.HeaderPrimary or HeaderFooterKind.FooterPrimary || (first && firstBoxes);
            MarginBox?[] parts = start == MarginBoxPosition.TopLeft
                ? [boxes.For(MarginBoxPosition.TopLeft, first), boxes.For(MarginBoxPosition.TopCenter, first), boxes.For(MarginBoxPosition.TopRight, first)]
                : [boxes.For(MarginBoxPosition.BottomLeft, first), boxes.For(MarginBoxPosition.BottomCenter, first), boxes.For(MarginBoxPosition.BottomRight, first)];
            if (!wanted || parts.All(p => p is not { Blocks.Count: > 0 }))
            {
                if (section.HeadersFooters[kind] is { } unchanged)
                {
                    stories.Add(unchanged);
                }
                else if (wanted && own is not null)
                {
                    stories.Add(Copy(own, kind));
                }

                continue;
            }

            HeaderFooter story = own is null ? new HeaderFooter(document, kind) : Copy(own, kind);
            double width = Math.Max(0, (setup.PageWidth ?? 612) - (setup.MarginLeft ?? 72) - (setup.MarginRight ?? 72) - (setup.Gutter ?? 0));
            Table table = new(document) { Grid = [width / 3, width / 3, width / 3] };
            table.Format.Layout = TableLayoutKind.Fixed;
            table.Format.CellMarginLeft = table.Format.CellMarginRight = 0;
            Row row = table.AddRow(3);
            for (int i = 0; i < 3; i++)
            {
                Cell cell = row.Cells[i];
                if (parts[i] is { Blocks.Count: > 0 } part)
                {
                    cell.Blocks[0].Remove();
                    foreach (Block block in part.Blocks)
                    {
                        cell.Blocks.Add((Block)block.Clone());
                    }
                }
            }

            story.Blocks.Add(table);
            stories.Add(story);
        }

        return stories;

        HeaderFooter Copy(HeaderFooter source, HeaderFooterKind kind)
        {
            HeaderFooter copy = new(document, kind);
            foreach (Block block in source.Blocks)
            {
                copy.Blocks.Add((Block)block.Clone());
            }

            return copy;
        }
    }

    /// <summary>Writes a header or footer part (or keeps the one read) and returns the reference to it.</summary>
    private (XElement Reference, bool Reused) WriteHeaderFooter(HeaderFooter story)
    {
        bool header = story.IsHeader;
        string type = story.Kind switch
        {
            HeaderFooterKind.HeaderFirst or HeaderFooterKind.FooterFirst => "first",
            HeaderFooterKind.HeaderEven or HeaderFooterKind.FooterEven => "even",
            _ => "default",
        };
        string relationshipType = header ? OpcRelationshipTypes.Header : OpcRelationshipTypes.Footer;
        DocxExtension? kept = Kept(story);
        string? id = null;
        bool reused = false;
        if (kept is { PartName: { } partName } && StoryUnchanged(story, partName)
            && data!.Package.GetPart(mainName)?.Relationships.FirstOrDefault(r => !r.External && r.TargetName.Equals(partName, StringComparison.OrdinalIgnoreCase)) is { } relationship)
        {
            // The part was copied with the package; the same story read into several sections writes nothing new.
            id = relationship.Id;
            reused = true;
            writtenParts.Add(partName);
        }
        else
        {
            string kind = header ? "header" : "footer";
            string name = UniquePartName($"/word/{kind}", ".xml");
            string previous = currentPart;
            currentPart = name;
            XElement root = kept is { Original: { } original } ? new XElement(original.Name, original.Attributes()) : NewRoot(header ? "hdr" : "ftr");
            root.Add(WriteBlocks(story.Blocks, ensureParagraph: kept is null));
            DeclarePrefixes(root);
            currentPart = previous;
            package.AddXmlPart(name, $"application/vnd.openxmlformats-officedocument.wordprocessingml.{kind}+xml", new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root));
            writtenParts.Add(name);
            id = package.NextRelationshipId(mainName);
            package.AddRelationship(mainName, id, relationshipType, name[(name.LastIndexOf('/') + 1)..]);
        }

        return (new XElement(Ns.W + (header ? "headerReference" : "footerReference"), new XAttribute(Ns.W + "type", type), new XAttribute(Ns.R + "id", id)), reused);
    }

    /// <summary>Whether a story read from a part is as it was read: its version and every paragraph in it unchanged.</summary>
    private bool StoryUnchanged(Story story, string partName)
    {
        DocxExtension? kept = Kept(story);
        if (regenerate || kept is null || story.Version != kept.Version || kept.PartName is not null && !kept.PartName.Equals(partName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (Paragraph paragraph in story.Descendants<Paragraph>())
        {
            if (!ParagraphUnchanged(paragraph, partName))
            {
                return false;
            }
        }

        return true;
    }

    private bool ParagraphUnchanged(Paragraph paragraph, string partName)
    {
        if (regenerate || Kept(paragraph) is not { Original: not null } kept || paragraph.Version != kept.Version
            || !string.Equals(kept.PartName, partName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Text boxes are written inside their paragraph: their stories must be unchanged too.
        foreach (Shape shape in paragraph.Descendants<Shape>())
        {
            if (shape.TextBox is { } box && (Kept(box) is not { } boxKept ? box.Blocks.Count > 0 : box.Version != boxKept.Version))
            {
                return false;
            }
        }

        foreach (Paragraph inner in paragraph.Descendants<Paragraph>())
        {
            if (inner.GetAncestor<Shape>() is not null && !ParagraphUnchanged(inner, partName))
            {
                return false;
            }
        }

        return true;
    }

    private string UniquePartName(string stem, string extension)
    {
        for (int i = 1; ; i++)
        {
            string name = stem + i.ToString(CultureInfo.InvariantCulture) + extension;
            if (!package.HasPart(name) && !writtenParts.Contains(name))
            {
                return name;
            }
        }
    }

    /// <summary>Declares on a part's root the prefixes Egap writes with, where the file did not, so drawings stay short.</summary>
    private static void DeclarePrefixes(XElement root)
    {
        HashSet<XNamespace> used = [];
        foreach (XElement element in root.DescendantsAndSelf())
        {
            used.Add(element.Name.Namespace);
            foreach (XAttribute attribute in element.Attributes())
            {
                used.Add(attribute.Name.Namespace);
            }
        }

        foreach ((string prefix, XNamespace ns) in (ReadOnlySpan<(string, XNamespace)>)
            [("r", Ns.R), ("wp", Ns.Wp), ("a", Ns.A), ("pic", Ns.Pic), ("wps", Ns.Wps), ("w14", Ns.W14), ("w15", Ns.W15), ("mc", MarkupCompatibility.Mc)])
        {
            if (used.Contains(ns) && root.GetPrefixOfNamespace(ns) is null && root.GetNamespaceOfPrefix(prefix) is null)
            {
                root.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName);
            }
        }
    }

    private static XElement NewRoot(string name)
    {
        return new XElement(
            Ns.W + name,
            new XAttribute(XNamespace.Xmlns + "wpc", "http://schemas.microsoft.com/office/word/2010/wordprocessingCanvas"),
            new XAttribute(XNamespace.Xmlns + "mc", MarkupCompatibility.Mc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "o", Ns.O.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "r", Ns.R.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "m", Ns.M.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "v", Ns.V.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wp14", Ns.Wp14.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wp", Ns.Wp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "w10", Ns.W10.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "w", Ns.W.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "w14", Ns.W14.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "w15", Ns.W15.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wpg", Ns.Wpg.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "wps", Ns.Wps.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "a", Ns.A.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "pic", Ns.Pic.NamespaceName),
            new XAttribute(MarkupCompatibility.Mc + "Ignorable", "w14 w15 wp14"));
    }

    /// <param name="blocks">The blocks.</param>
    /// <param name="ensureParagraph">Write an empty paragraph when there are no blocks, as Word wants in a story it makes.</param>
    private IEnumerable<XElement> WriteBlocks(IEnumerable<Block> blocks, bool ensureParagraph = false)
    {
        bool any = false;
        foreach (Block block in blocks)
        {
            any = true;
            DocxExtension? kept = Kept(block);
            foreach (XElement before in kept?.Before ?? [])
            {
                yield return Rebase(before, kept!.PartName);
            }

            yield return kept?.Wrapper is { } wrapper && BlockUnchanged(block) ? new XElement(wrapper) : WriteBlock(block);
            foreach (XElement after in kept?.After ?? [])
            {
                yield return Rebase(after, kept!.PartName);
            }
        }

        if (ensureParagraph && !any)
        {
            yield return new XElement(Ns.W + "p");
        }
    }

    private XElement WriteBlock(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                return WriteParagraph(paragraph);
            case Table table:
                return WriteTable(table);
            case FlexContainer or GridContainer:
                // Word has no such containers: a borderless table (Phase 9 design Q8).
                return WriteTable(ContainerTables.For(block, ContainerTables.TextWidth(block))!);
            case ContentControl control:
                {
                    DocxExtension? kept = Kept(control);
                    if (kept?.Original is { } original && ControlUnchanged(control, kept))
                    {
                        return new XElement(original);
                    }

                    List<XElement> content = control.Box is null ? [.. WriteBlocks(control.Blocks, ensureParagraph: kept is null)]
                        : [WriteTable(ContainerTables.For(control, ContainerTables.TextWidth(control))!), new XElement(Ns.W + "p")];
                    return new XElement(Ns.W + "sdt", SdtProperties(control.Properties, kept, kept?.Original), SdtEnd(kept), new XElement(Ns.W + "sdtContent", content));
                }

            case BlockIsland island when island.Format == DocxExtension.Format:
                return Parse(island.Markup) is { } markup ? Rebase(markup, Kept(island)?.PartName, copy: false) : new XElement(Ns.W + "p");
            default:
                // An island of another format has no place in DOCX: an empty paragraph keeps the block count.
                return new XElement(Ns.W + "p");
        }
    }

    private XElement WriteParagraph(Paragraph paragraph)
    {
        DocxExtension? kept = Kept(paragraph);
        if (kept?.Original is { } original && ParagraphUnchanged(paragraph, currentPart))
        {
            XElement copy = new(original);
            foreach (XElement table in copy.Elements(Ns.W + "tbl").ToList())
            {
                table.Remove();
            }

            NoteBookmarks(paragraph);
            return copy;
        }

        XElement p = new(Ns.W + "p");
        if (kept?.Attributes is { } attributes)
        {
            p.Add(attributes.Select(a => new XAttribute(a)));
        }

        XElement? markProperties = DocxPropertyWriter.Run(paragraph.MarkFormat, null, kept?.UnknownProperties?.GetValueOrDefault("pPr/rPr"), RevisionElement(paragraph.MarkRevision, mark: true));
        XElement? change = null;
        if (paragraph.FormatRevision is { OldFormat: ParagraphFormat old } revision)
        {
            change = new XElement(Ns.W + "pPrChange", new XAttribute(Ns.W + "id", nextRevisionId++), RevisionAttributes(revision.Author, revision.Date),
                DocxPropertyWriter.Paragraph(old, null, null) ?? new XElement(Ns.W + "pPr"));
        }

        if (DocxPropertyWriter.Paragraph(paragraph.Format, paragraph.Style, kept?.UnknownProperties?.GetValueOrDefault("pPr"), markProperties, change) is { } pPr)
        {
            p.Add(pPr);
        }

        if (kept?.Children is { Count: > 0 } children)
        {
            // Range markup the reader kept goes back between the inlines it sat between.
            bool fieldCode = false;
            int from = 0;
            foreach ((int index, XElement element) in children)
            {
                int to = Math.Min(index, paragraph.Inlines.Count);
                if (to > from)
                {
                    fieldCode = WriteInlines(paragraph.Inlines.Skip(from).Take(to - from), p, fieldCode);
                    from = to;
                }

                p.Add(new XElement(element));
            }

            WriteInlines(paragraph.Inlines.Skip(from), p, fieldCode);
        }
        else
        {
            WriteInlines(paragraph.Inlines, p, fieldCode: false);
        }

        return p;
    }

    /// <summary>Records the bookmark ids an unchanged paragraph keeps, so ends written later find them.</summary>
    private void NoteBookmarks(Paragraph paragraph)
    {
        foreach (BookmarkStart start in paragraph.Descendants<BookmarkStart>())
        {
            if (Kept(start) is { Original: { } element } && DocxUnits.Int(element.W("id")) is int id)
            {
                bookmarkIds[start.Name] = id;
            }
        }
    }

    private static XElement? SdtEnd(DocxExtension? kept)
    {
        return kept?.UnknownProperties?.GetValueOrDefault("sdtEndPr") is [{ } end, ..] ? new XElement(end) : null;
    }

    /// <summary>Whether a block is as it was read, with everything in it, so it can be written as read.</summary>
    private bool BlockUnchanged(Block block)
    {
        return block switch
        {
            Paragraph paragraph => ParagraphUnchanged(paragraph, currentPart),
            Table table => Kept(table) is { } kept && TableUnchanged(table, kept),
            ContentControl control => Kept(control) is { } kept && ControlUnchanged(control, kept),
            BlockIsland island => Kept(island) is { Original: not null } kept && string.Equals(kept.PartName, currentPart, StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private bool ControlUnchanged(ContentControl control, DocxExtension kept)
    {
        return !regenerate && kept.Snapshot is ControlState state && state.Matches(control.Properties, control.Blocks)
            && string.Equals(kept.PartName, currentPart, StringComparison.OrdinalIgnoreCase) && control.Blocks.All(BlockUnchanged);
    }

    private bool TableUnchanged(Table table, DocxExtension kept)
    {
        if (regenerate || kept.Original is null || kept.Version != table.Stamp || !string.Equals(kept.PartName, currentPart, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (Row row in table.Rows)
        {
            foreach (Cell cell in row.Cells)
            {
                if (Kept(cell) is not { } cellKept || cellKept.Snapshot is not Node[] blocks || !blocks.SequenceEqual(cell.Blocks) || !cell.Blocks.All(BlockUnchanged))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private XElement WriteTable(Table table)
    {
        DocxExtension? kept = Kept(table);
        if (kept is not null && TableUnchanged(table, kept))
        {
            return new XElement(kept.Original!);
        }

        XElement tbl = new(Ns.W + "tbl");
        bool stampSame = !regenerate && kept is not null && kept.Version == table.Stamp;
        XElement? tblPr = stampSame && kept!.Original?.Element(Ns.W + "tblPr") is { } originalPr
            ? new XElement(originalPr)
            : DocxPropertyWriter.Table(table.Format, table.Style, kept?.UnknownProperties?.GetValueOrDefault("tblPr"));
        tbl.Add(tblPr ?? new XElement(Ns.W + "tblPr"));
        XElement grid = stampSame && kept!.Original?.Element(Ns.W + "tblGrid") is { } originalGrid
            ? new XElement(originalGrid)
            : new XElement(Ns.W + "tblGrid", (table.Grid ?? (kept is null ? GuessGrid(table) : [])).Select(w => new XElement(Ns.W + "gridCol", new XAttribute(Ns.W + "w", DocxUnits.Twips(w)))),
                kept?.UnknownProperties?.GetValueOrDefault("tblGrid")?.Select(e => new XElement(e)));
        tbl.Add(grid);
        foreach (Row row in table.Rows)
        {
            DocxExtension? rowKept = Kept(row);
            XElement tr = new(Ns.W + "tr");
            if (rowKept?.Attributes is { } attributes)
            {
                tr.Add(attributes.Select(a => new XAttribute(a)));
            }

            if (rowKept?.UnknownProperties?.GetValueOrDefault("tr") is { } exceptions)
            {
                tr.Add(exceptions.Select(e => new XElement(e)));
            }

            XElement? trPr = stampSame && rowKept?.Original?.Element(Ns.W + "trPr") is { } originalTrPr
                ? new XElement(originalTrPr)
                : DocxPropertyWriter.Row(row.Format, rowKept?.UnknownProperties?.GetValueOrDefault("trPr"), RevisionElement(row.Revision, mark: true));
            if (trPr is not null)
            {
                tr.Add(trPr);
            }

            foreach (Cell cell in row.Cells)
            {
                DocxExtension? cellKept = Kept(cell);
                XElement tc = new(Ns.W + "tc");
                if (cellKept?.Attributes is { } cellAttributes)
                {
                    tc.Add(cellAttributes.Select(a => new XAttribute(a)));
                }

                XElement? tcPr = stampSame && cellKept?.Original?.Element(Ns.W + "tcPr") is { } originalTcPr
                    ? new XElement(originalTcPr)
                    : DocxPropertyWriter.Cell(cell.Format, cellKept?.UnknownProperties?.GetValueOrDefault("tcPr"));
                if (tcPr is not null)
                {
                    tc.Add(tcPr);
                }

                List<XElement> content = [.. WriteBlocks(cell.Blocks)];
                if (cellKept is null && (content.Count == 0 || content[^1].Name == Ns.W + "tbl"))
                {
                    // Word wants a cell to end with a paragraph; a cell read from a file keeps what it had.
                    content.Add(new XElement(Ns.W + "p"));
                }

                tc.Add(content);
                tr.Add(tc);
            }

            tbl.Add(tr);
        }

        return tbl;
    }

    /// <summary>
    /// Kept markup moved to another part than the one it was read from: each relationship it names (r:id, r:embed and the
    /// like) is added to the part being written, with the same target, and the markup renamed to it.
    /// </summary>
    private XElement Rebase(XElement markup, string? sourcePart, bool copy = true)
    {
        XElement result = copy ? new XElement(markup) : markup;
        if (data is null || sourcePart is null || string.Equals(sourcePart, currentPart, StringComparison.OrdinalIgnoreCase)
            || data.Package.GetPart(sourcePart) is not { } source)
        {
            return result;
        }

        foreach (XAttribute attribute in result.DescendantsAndSelf().SelectMany(e => e.Attributes()).Where(a => a.Name.Namespace == Ns.R).ToList())
        {
            if (source.GetRelationship(attribute.Value) is not { } relationship)
            {
                continue;
            }

            string target = relationship.External ? relationship.Target : RelationshipTarget(currentPart, relationship.TargetName);
            string key = currentPart + "|" + relationship.Type + "|" + target;
            if (!rebased.TryGetValue(key, out string? id))
            {
                id = package.NextRelationshipId(currentPart);
                package.AddRelationship(currentPart, id, relationship.Type, target, relationship.External);
                rebased[key] = id;
            }

            attribute.Value = id;
        }

        return result;
    }

    private static List<double> GuessGrid(Table table)
    {
        int columns = table.Rows.Count == 0 ? 1 : table.Rows.Max(r => r.Cells.Sum(c => c.Format.GridSpan ?? 1));
        return [.. Enumerable.Repeat(468.0 / Math.Max(1, columns), Math.Max(1, columns))];
    }

    private static XElement? Parse(string markup)
    {
        try
        {
            return XElement.Parse(markup, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private XElement? RevisionElement(Revision? revision, bool mark)
    {
        if (revision is null)
        {
            return null;
        }

        string name = revision.Kind switch
        {
            RevisionKind.Deletion => "del",
            RevisionKind.MoveFrom => "moveFrom",
            RevisionKind.MoveTo => "moveTo",
            _ => "ins",
        };
        _ = mark;
        return new XElement(Ns.W + name, new XAttribute(Ns.W + "id", nextRevisionId++), RevisionAttributes(revision.Author, revision.Date));
    }

    private static IEnumerable<XAttribute> RevisionAttributes(string author, DateTimeOffset? date)
    {
        yield return new XAttribute(Ns.W + "author", author);
        if (date is { } value)
        {
            yield return new XAttribute(Ns.W + "date", value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
    }

    private string NextParaId()
    {
        return (nextParaId++).ToString("X8", CultureInfo.InvariantCulture);
    }

    private static string RelationshipTarget(string sourcePart, string targetPart)
    {
        string directory = sourcePart[..(sourcePart.LastIndexOf('/') + 1)];
        return targetPart.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ? targetPart[directory.Length..] : targetPart;
    }

    private static string Text(string value)
    {
        StringBuilder text = new(value.Length);
        foreach (char c in value)
        {
            // XML 1.0 has no place for most control characters.
            if (c is '\t' or '\n' or '\r' or >= ' ' and not ('\uFFFE' or '\uFFFF'))
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }
}
