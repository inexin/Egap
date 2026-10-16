using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>
/// Reads a DOCX package into the model (Phase 6 Build Plan tasks 10 and 11): the body with its paragraphs, runs, tables,
/// sections, fields, bookmarks, hyperlinks, content controls, notes, comments, drawings and tracked changes, and the parts
/// around it. What the model does not name goes into each node's extension data (<see cref="DocxExtension"/>).
/// </summary>
internal sealed partial class DocxReader
{
    private static readonly HashSet<string> MainContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.template.main+xml",
        "application/vnd.ms-word.document.macroEnabled.main+xml",
        "application/vnd.ms-word.template.macroEnabledTemplate.main+xml",
    };

    private readonly OpcPackage package;
    private readonly Document document;
    private readonly DocxPackageData data;
    private readonly OpcPart main;
    private readonly Dictionary<string, string> bookmarkNames = new(StringComparer.Ordinal);
    private readonly Dictionary<int, XElement> comments = [];
    private readonly HashSet<int> usedComments = [];
    private readonly Dictionary<string, (string? ParentParaId, bool Done)> commentsExtended = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> footnotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> endnotes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, XElement> headerParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Inline> pendingMarks = [];
    private readonly List<Comment> readComments = [];
    private readonly List<(int NumId, string StyleId)> styleLinkedLists = [];
    private readonly Dictionary<int, List> listRedirects = [];
    private Paragraph? lastParagraph;
    private XElement? pendingSectPr;

    // Where reading is: notes, comments and headers may not hold notes; comments may not hold comments.
    private int noteDepth;
    private int commentDepth;
    private int headerDepth;
    private OpcPart currentPart;
    private XElement? runProperties;

    private DocxReader(OpcPackage package, OpcPart main)
    {
        this.package = package;
        this.main = main;
        currentPart = main;
        document = new Document(blank: true);
        data = new DocxPackageData { Package = package, MainPartName = main.Name, MainContentType = main.ContentType };
        document.Extensions.Set(DocxExtension.Format, data);
    }

    public static Document Read(OpcPackage package)
    {
        OpcPart? main = package.GetRelatedPart(package.Relationships, OpcRelationshipTypes.OfficeDocument);
        if (main is null || !MainContentTypes.Contains(main.ContentType))
        {
            throw new DocumentException(main is null
                ? "The package has no main document part."
                : $"The package's main part is {main.ContentType}, not a Word document.");
        }

        DocxReader reader = new(package, main);
        reader.ReadAll();
        return reader.document;
    }

    private void ReadAll()
    {
        data.MappedParts.Add(main.Name);
        ReadTheme();
        ReadStylesAndNumbering();
        ReadSettings();
        ReadProperties();
        ReadFonts();
        ReadNoteParts();
        ReadCommentParts();

        XDocument xml = main.LoadXml();
        XElement? body = xml.Root?.Element(Ns.W + "body");
        DocxExtension.Of(document).Original = xml.Root is { } root ? new XElement(root.Name, root.Attributes()) : null;
        if (xml.Root?.Element(Ns.W + "background") is { } background)
        {
            DocxExtension.Of(document).AddUnknownProperty("document", background);
        }

        ReadBody(body);
        FinishComments();
        Stamp();
    }

    private void ReadBody(XElement? body)
    {
        Section section = document.Sections.AddTrusted(new Section(document));
        if (body is null)
        {
            return;
        }

        foreach (XElement element in body.Elements())
        {
            if (element.Name == Ns.W + "sectPr")
            {
                continue;
            }

            ReadBlock(element, section.Body.Blocks);
            if (pendingSectPr is { } sectPr)
            {
                pendingSectPr = null;
                ApplySection(section, sectPr);
                section = document.Sections.AddTrusted(new Section(document));
            }
        }

        FlushMarksToLast();
        ApplySection(section, body.Element(Ns.W + "sectPr"));
        if (section.Body.Blocks.Count == 0 && document.Sections.Count > 1 && body.Element(Ns.W + "sectPr") is null)
        {
            // A body whose last paragraph closed its section and that has no final sectPr: no empty section after it.
            section.Remove();
        }
    }

    private void ApplySection(Section section, XElement? sectPr)
    {
        if (sectPr is null)
        {
            return;
        }

        DocxExtension extension = DocxExtension.Of(section);
        extension.Original = sectPr;
        DocxProperties.ReadSection(sectPr, section.PageSetup, e => extension.AddUnknownProperty("sectPr", e));
        PageSetup snapshot = new();
        snapshot.CopyRaw(section.PageSetup);
        extension.Snapshot = snapshot;
        foreach (XElement reference in sectPr.Elements())
        {
            bool header = reference.Name == Ns.W + "headerReference";
            if (!header && reference.Name != Ns.W + "footerReference")
            {
                continue;
            }

            HeaderFooterKind kind = (reference.W("type"), header) switch
            {
                ("first", true) => HeaderFooterKind.HeaderFirst,
                ("even", true) => HeaderFooterKind.HeaderEven,
                (_, true) => HeaderFooterKind.HeaderPrimary,
                ("first", false) => HeaderFooterKind.FooterFirst,
                ("even", false) => HeaderFooterKind.FooterEven,
                _ => HeaderFooterKind.FooterPrimary,
            };
            if (section.HeadersFooters[kind] is not null
                || (string?)reference.Attribute(Ns.R + "id") is not { } id
                || main.GetRelationship(id) is not { External: false } relationship
                || package.GetPart(relationship.TargetName) is not { } part)
            {
                continue;
            }

            HeaderFooter story = section.HeadersFooters.AddTrusted(new HeaderFooter(document, kind));
            DocxExtension.Of(story).PartName = part.Name;
            data.MappedParts.Add(part.Name);
            if (!headerParts.TryGetValue(part.Name, out XElement? root))
            {
                root = part.LoadXml().Root ?? new XElement(Ns.W + "hdr");
                headerParts[part.Name] = root;
            }

            DocxExtension.Of(story).Original = new XElement(root.Name, root.Attributes());
            OpcPart previous = currentPart;
            currentPart = part;
            headerDepth++;
            try
            {
                ReadBlocks(root, story.Blocks);
                FlushMarksToLast();
            }
            finally
            {
                headerDepth--;
                currentPart = previous;
            }
        }
    }

    private void ReadBlocks(XElement container, NodeCollection<Block> blocks)
    {
        foreach (XElement element in container.Elements())
        {
            ReadBlock(element, blocks);
        }
    }

    private void ReadBlock(XElement element, NodeCollection<Block> blocks)
    {
        if (MarkupCompatibility.IsAlternateContent(element))
        {
            foreach (XElement chosen in MarkupCompatibility.Choose(element, Ns.Understood))
            {
                ReadBlock(chosen, blocks);
            }

            return;
        }

        if (element.Name.Namespace != Ns.W)
        {
            AddBlockIsland(element, blocks);
            return;
        }

        switch (element.Name.LocalName)
        {
            case "p":
                ReadParagraph(element, blocks);
                break;
            case "tbl":
                ReadTable(element, blocks);
                break;
            case "sdt":
                ReadBlockContentControl(element, blocks);
                break;
            case "customXml" or "ins" or "del" or "moveFrom" or "moveTo" or "smartTag":
                foreach (XElement child in element.Elements())
                {
                    if (child.Name != Ns.W + "customXmlPr")
                    {
                        ReadBlock(child, blocks);
                    }
                }

                break;
            case "bookmarkStart" or "bookmarkEnd" or "commentRangeStart" or "commentRangeEnd":
                if (ReadMark(element) is { } mark)
                {
                    pendingMarks.Add(mark);
                }

                break;
            case "proofErr" or "permStart" or "permEnd" or "moveFromRangeStart" or "moveFromRangeEnd" or "moveToRangeStart" or "moveToRangeEnd"
                or "customXmlInsRangeStart" or "customXmlInsRangeEnd" or "customXmlDelRangeStart" or "customXmlDelRangeEnd"
                or "customXmlMoveFromRangeStart" or "customXmlMoveFromRangeEnd" or "customXmlMoveToRangeStart" or "customXmlMoveToRangeEnd"
                or "sectPr" or "tblGrid" or "tblPr" or "trPr" or "tcPr" or "sdtPr" or "sdtEndPr":
                break;
            default:
                AddBlockIsland(element, blocks);
                break;
        }
    }

    private void AddBlockIsland(XElement element, NodeCollection<Block> blocks)
    {
        BlockIsland island = blocks.AddTrusted(new BlockIsland(document, DocxExtension.Format, element.ToString(SaveOptions.DisableFormatting)));
        DocxExtension.Of(island).Original = element;
        DocxExtension.Of(island).PartName = currentPart.Name;
    }

    private void ReadParagraph(XElement element, NodeCollection<Block> blocks)
    {
        Paragraph paragraph = blocks.AddTrusted(new Paragraph(document));
        DocxExtension extension = DocxExtension.Of(paragraph);
        extension.Original = element;
        extension.PartName = currentPart.Name;
        if (element.HasAttributes)
        {
            extension.Attributes = [.. element.Attributes()];
        }

        if (element.Element(Ns.W + "pPr") is { } pPr)
        {
            ReadParagraphProperties(pPr, paragraph, extension);
        }

        if (pendingMarks.Count > 0)
        {
            foreach (Inline mark in pendingMarks)
            {
                paragraph.Inlines.AddTrusted(mark);
            }

            pendingMarks.Clear();
        }

        ReadInlines(element, paragraph.Inlines, new InlineState());
        lastParagraph = paragraph;

        // A table inside a paragraph is not valid markup, but Word's predecessors and other tools write it: it follows the paragraph.
        foreach (XElement table in element.Elements(Ns.W + "tbl"))
        {
            ReadTable(table, blocks);
        }
    }

    private void ReadParagraphProperties(XElement pPr, Paragraph paragraph, DocxExtension extension)
    {
        if (pPr.Element(Ns.W + "pStyle").Val() is { } styleId && document.Styles[styleId] is ParagraphStyle style)
        {
            paragraph.Style = style;
        }

        ReadNumbering(pPr.Element(Ns.W + "numPr"), paragraph.Format);
        DocxProperties.ReadParagraph(pPr, paragraph.Format, e => extension.AddUnknownProperty("pPr", e));
        if (pPr.Element(Ns.W + "rPr") is { } markProperties)
        {
            DocxProperties.ReadRun(markProperties, paragraph.MarkFormat, e =>
            {
                if (e.Name.Namespace == Ns.W && e.Name.LocalName is "ins" or "del" or "moveFrom" or "moveTo")
                {
                    paragraph.MarkRevision = RevisionOf(e);
                }
                else if (e.Name == Ns.W + "rPrChange")
                {
                    // The mark's own formatting change is kept only as markup.
                    extension.AddUnknownProperty("pPr/rPr", e);
                }
                else
                {
                    extension.AddUnknownProperty("pPr/rPr", e);
                }
            });
        }

        if (pPr.Element(Ns.W + "pPrChange") is { } change)
        {
            ParagraphFormat old = new();
            XElement? oldPPr = change.Element(Ns.W + "pPr");
            DocxProperties.ReadParagraph(oldPPr, old);
            ReadNumbering(oldPPr?.Element(Ns.W + "numPr"), old);
            paragraph.FormatRevision = new FormatRevision(change.W("author") ?? "", DateOf(change), old);
        }

        if (pPr.Element(Ns.W + "sectPr") is { } sectPr)
        {
            pendingSectPr = sectPr;
        }
    }

    private void ReadNumbering(XElement? numPr, ParagraphFormat format)
    {
        if (numPr is null)
        {
            return;
        }

        if (DocxUnits.Int(numPr.Element(Ns.W + "ilvl").Val()) is int level)
        {
            format.ListLevel = Math.Clamp(level, 0, 8);
        }

        if (DocxUnits.Int(numPr.Element(Ns.W + "numId").Val()) is int numId)
        {
            format.List = numId == 0 ? List.None : listRedirects.GetValueOrDefault(numId) ?? document.Lists[numId] ?? List.None;
        }
    }

    private void ReadTable(XElement element, NodeCollection<Block> blocks)
    {
        Table table = blocks.AddTrusted(new Table(document));
        DocxExtension extension = DocxExtension.Of(table);
        extension.Original = element;
        extension.PartName = currentPart.Name;
        if (element.Element(Ns.W + "tblPr") is { } tblPr)
        {
            if (tblPr.Element(Ns.W + "tblStyle").Val() is { } styleId && document.Styles[styleId] is TableStyle style)
            {
                table.Style = style;
            }

            DocxProperties.ReadTable(tblPr, table.Format, e => extension.AddUnknownProperty("tblPr", e));
        }

        if (element.Element(Ns.W + "tblGrid") is { } grid)
        {
            List<double> widths = [.. grid.Elements(Ns.W + "gridCol").Select(c => Math.Max(0, DocxUnits.Points(c.W("w")) ?? 0))];
            if (widths.Count > 0)
            {
                table.Grid = widths;
            }

            if (grid.Element(Ns.W + "tblGridChange") is { } gridChange)
            {
                extension.AddUnknownProperty("tblGrid", gridChange);
            }
        }

        ReadRows(element, table);
        if (table.Rows.Count == 0)
        {
            // A table without rows is not one Word opens; keep it as markup.
            table.Remove();
            AddBlockIsland(element, blocks);
        }
    }

    private void ReadRows(XElement container, Table table)
    {
        foreach (XElement element in container.Elements())
        {
            if (element.Name == Ns.W + "tr")
            {
                ReadRow(element, table);
            }
            else if (element.Name == Ns.W + "sdt" || element.Name == Ns.W + "customXml")
            {
                ReadRows(element.Element(Ns.W + "sdtContent") ?? element, table);
            }
            else if (MarkupCompatibility.IsAlternateContent(element))
            {
                ReadRows(new XElement("choice", MarkupCompatibility.Choose(element, Ns.Understood)), table);
            }
            else if (element.Name.LocalName is "bookmarkStart" or "bookmarkEnd" or "commentRangeStart" or "commentRangeEnd" && ReadMark(element) is { } mark)
            {
                pendingMarks.Add(mark);
            }
        }
    }

    private void ReadRow(XElement element, Table table)
    {
        Row row = table.Rows.AddTrusted(new Row(document));
        DocxExtension extension = DocxExtension.Of(row);
        extension.Original = element;
        extension.PartName = currentPart.Name;
        if (element.HasAttributes)
        {
            extension.Attributes = [.. element.Attributes()];
        }

        if (element.Element(Ns.W + "tblPrEx") is { } exceptions)
        {
            extension.AddUnknownProperty("tr", exceptions);
        }

        if (element.Element(Ns.W + "trPr") is { } trPr)
        {
            DocxProperties.ReadRow(trPr, row.Format, e => extension.AddUnknownProperty("trPr", e));
            if (trPr.Elements().FirstOrDefault(e => e.Name.Namespace == Ns.W && e.Name.LocalName is "ins" or "del") is { } revision)
            {
                row.Revision = RevisionOf(revision);
            }
        }

        ReadCells(element, row);
    }

    private void ReadCells(XElement container, Row row)
    {
        foreach (XElement element in container.Elements())
        {
            if (element.Name == Ns.W + "tc")
            {
                Cell cell = row.Cells.AddTrusted(new Cell(document));
                DocxExtension extension = DocxExtension.Of(cell);
                extension.Original = element;
                extension.PartName = currentPart.Name;
                if (element.HasAttributes)
                {
                    extension.Attributes = [.. element.Attributes()];
                }

                if (element.Element(Ns.W + "tcPr") is { } tcPr)
                {
                    DocxProperties.ReadCell(tcPr, cell.Format, e => extension.AddUnknownProperty("tcPr", e));
                }

                ReadBlocks(element, cell.Blocks);
            }
            else if (element.Name == Ns.W + "sdt" || element.Name == Ns.W + "customXml")
            {
                ReadCells(element.Element(Ns.W + "sdtContent") ?? element, row);
            }
            else if (MarkupCompatibility.IsAlternateContent(element))
            {
                ReadCells(new XElement("choice", MarkupCompatibility.Choose(element, Ns.Understood)), row);
            }
            else if (element.Name.LocalName is "bookmarkStart" or "bookmarkEnd" or "commentRangeStart" or "commentRangeEnd" && ReadMark(element) is { } mark)
            {
                pendingMarks.Add(mark);
            }
        }
    }

    private void ReadBlockContentControl(XElement element, NodeCollection<Block> blocks)
    {
        ContentControl control = blocks.AddTrusted(new ContentControl(document));
        DocxExtension extension = DocxExtension.Of(control);
        extension.Original = element;
        extension.PartName = currentPart.Name;
        ReadContentControlProperties(element, control.Properties, extension);
        if (element.Element(Ns.W + "sdtContent") is { } content)
        {
            ReadBlocks(content, control.Blocks);
        }
    }

    private static void ReadContentControlProperties(XElement sdt, ContentControlProperties properties, DocxExtension extension)
    {
        XElement? sdtPr = sdt.Element(Ns.W + "sdtPr");
        if (sdtPr is null)
        {
            return;
        }

        foreach (XElement e in sdtPr.Elements())
        {
            switch (e.Name.LocalName)
            {
                case "alias" when e.Name.Namespace == Ns.W:
                    properties.Title = e.Val();
                    break;
                case "tag" when e.Name.Namespace == Ns.W:
                    properties.Tag = e.Val();
                    break;
                case "lock" when e.Name.Namespace == Ns.W:
                    properties.LockControl = e.Val() is "sdtLocked" or "sdtContentLocked";
                    properties.LockContents = e.Val() is "contentLocked" or "sdtContentLocked";
                    break;
                case "showingPlcHdr" when e.Name.Namespace == Ns.W:
                    properties.ShowingPlaceholder = DocxUnits.OnOff(e);
                    break;
                case "text" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.PlainText;
                    break;
                case "date" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.Date;
                    extension.AddUnknownProperty("sdtPr", e);
                    break;
                case "dropDownList" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.DropDownList;
                    extension.AddUnknownProperty("sdtPr", e);
                    break;
                case "comboBox" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.ComboBox;
                    extension.AddUnknownProperty("sdtPr", e);
                    break;
                case "picture" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.Picture;
                    break;
                case "group" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.Group;
                    break;
                case "docPartObj" or "docPartList" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.BuildingBlockGallery;
                    extension.AddUnknownProperty("sdtPr", e);
                    break;
                case "citation" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.Citation;
                    break;
                case "bibliography" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.Bibliography;
                    break;
                case "equation" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.Equation;
                    break;
                case "richText" when e.Name.Namespace == Ns.W:
                    properties.Kind = ContentControlKind.RichText;
                    break;
                case "checkbox" when e.Name.Namespace == Ns.W14:
                    properties.Kind = ContentControlKind.CheckBox;
                    properties.IsChecked = e.Element(Ns.W14 + "checked") is { } check && (string?)check.Attribute(Ns.W14 + "val") is "1" or "true";
                    extension.AddUnknownProperty("sdtPr", e);
                    break;
                case "repeatingSection" when e.Name.Namespace == Ns.W15:
                    properties.Kind = ContentControlKind.RepeatingSection;
                    break;
                default:
                    extension.AddUnknownProperty("sdtPr", e);
                    break;
            }
        }
    }

    /// <summary>What applies to the inlines read inside a wrapper: a tracked change, and whether a hyperlink holds them.</summary>
    private readonly record struct InlineState(Revision? Revision = null, bool InHyperlink = false);

    private void ReadInlines(XElement container, NodeCollection<Inline> inlines, InlineState state)
    {
        foreach (XElement element in container.Elements())
        {
            ReadInline(element, inlines, state);
        }
    }

    private void ReadInline(XElement element, NodeCollection<Inline> inlines, InlineState state)
    {
        if (MarkupCompatibility.IsAlternateContent(element))
        {
            foreach (XElement chosen in MarkupCompatibility.Choose(element, Ns.Understood))
            {
                ReadInline(chosen, inlines, state);
            }

            return;
        }

        if (element.Name.Namespace == Ns.M)
        {
            if (element.Name.LocalName is "oMath" or "oMathPara")
            {
                Add(inlines, new Island(document, IslandKind.Math, DocxExtension.Format, element.ToString(SaveOptions.DisableFormatting))
                {
                    Text = string.Concat(element.Descendants(Ns.M + "t").Select(t => t.Value)),
                }, state, element);
            }

            return;
        }

        if (element.Name.Namespace != Ns.W)
        {
            Add(inlines, new Island(document, IslandKind.Other, DocxExtension.Format, element.ToString(SaveOptions.DisableFormatting)), state, element);
            return;
        }

        switch (element.Name.LocalName)
        {
            case "r":
                ReadRun(element, inlines, state);
                break;
            case "hyperlink":
                ReadHyperlink(element, inlines, state);
                break;
            case "fldSimple":
                ReadSimpleField(element, inlines, state);
                break;
            case "sdt":
                {
                    InlineContentControl control = Add(inlines, new InlineContentControl(document), state, element);
                    ReadContentControlProperties(element, control.Properties, DocxExtension.Of(control));
                    if (element.Element(Ns.W + "sdtContent") is { } content)
                    {
                        ReadInlines(content, control.Inlines, state);
                    }

                    break;
                }

            case "ins" or "del" or "moveFrom" or "moveTo":
                ReadInlines(element, inlines, state with { Revision = RevisionOf(element) });
                break;
            case "smartTag" or "customXml" or "dir" or "bdo":
                foreach (XElement child in element.Elements())
                {
                    if (child.Name.LocalName is not ("smartTagPr" or "customXmlPr"))
                    {
                        ReadInline(child, inlines, state);
                    }
                }

                break;
            case "bookmarkStart" or "bookmarkEnd" or "commentRangeStart" or "commentRangeEnd":
                if (ReadMark(element) is { } mark)
                {
                    Add(inlines, mark, state, null);
                }

                break;
            case "pPr" or "tbl" or "proofErr" or "permStart" or "permEnd" or "moveFromRangeStart" or "moveFromRangeEnd" or "moveToRangeStart" or "moveToRangeEnd"
                or "customXmlInsRangeStart" or "customXmlInsRangeEnd" or "customXmlDelRangeStart" or "customXmlDelRangeEnd"
                or "customXmlMoveFromRangeStart" or "customXmlMoveFromRangeEnd" or "customXmlMoveToRangeStart" or "customXmlMoveToRangeEnd":
                break;
            default:
                Add(inlines, new Island(document, IslandKind.Other, DocxExtension.Format, element.ToString(SaveOptions.DisableFormatting)), state, element);
                break;
        }
    }

    private T Add<T>(NodeCollection<Inline> inlines, T inline, InlineState state, XElement? original)
        where T : Inline
    {
        if (state.Revision is { } revision)
        {
            inline.Revision = revision;
        }

        if (original is not null)
        {
            DocxExtension extension = DocxExtension.Of(inline);
            extension.Original = original;
            extension.PartName = currentPart.Name;
            if (inline is Island)
            {
                extension.InRun = original.Parent?.Name == Ns.W + "r" || original.Parent?.Parent?.Parent?.Name == Ns.W + "r";
            }
        }

        if (inline is not Run && runProperties is not null)
        {
            DocxExtension.Of(inline).RunProperties = runProperties;
        }

        return inlines.AddTrusted(inline);
    }

    private void ReadHyperlink(XElement element, NodeCollection<Inline> inlines, InlineState state)
    {
        if (state.InHyperlink)
        {
            ReadInlines(element, inlines, state);
            return;
        }

        Hyperlink link = Add(inlines, new Hyperlink(document), state, element);
        if ((string?)element.Attribute(Ns.R + "id") is { } id && currentPart.GetRelationship(id) is { } relationship)
        {
            link.Address = relationship.Target;
        }

        link.Anchor = element.W("anchor");
        link.Tooltip = element.W("tooltip");
        link.Target = element.W("tgtFrame");
        ReadInlines(element, link.Inlines, state with { InHyperlink = true });
    }

    private void ReadSimpleField(XElement element, NodeCollection<Inline> inlines, InlineState state)
    {
        FieldStart start = Add(inlines, new FieldStart(document), state, element);
        start.IsLocked = element.W("fldLock") is "1" or "true" or "on";
        start.IsDirty = element.W("dirty") is "1" or "true" or "on";
        Add(inlines, new Run(document, element.W("instr") ?? ""), state, null);
        Add(inlines, new FieldSeparator(document), state, null);
        ReadInlines(element, inlines, state);
        Add(inlines, new FieldEnd(document), state, null);
    }

    private Inline? ReadMark(XElement element)
    {
        string? id = element.W("id");
        switch (element.Name.LocalName)
        {
            case "bookmarkStart":
                if (element.W("name") is not { } name || id is null)
                {
                    return null;
                }

                bookmarkNames[id] = name;
                BookmarkStart start = new(document, name);
                DocxExtension.Of(start).Original = element;
                return start;
            case "bookmarkEnd":
                if (id is null || !bookmarkNames.TryGetValue(id, out string? endName))
                {
                    return null;
                }

                BookmarkEnd end = new(document, endName);
                DocxExtension.Of(end).Original = element;
                return end;
            case "commentRangeStart":
                return DocxUnits.Int(id) is int startId ? new CommentRangeStart(document, startId) : null;
            case "commentRangeEnd":
                return DocxUnits.Int(id) is int endId ? new CommentRangeEnd(document, endId) : null;
            default:
                return null;
        }
    }

    private void ReadRun(XElement element, NodeCollection<Inline> inlines, InlineState state)
    {
        XElement? rPr = element.Element(Ns.W + "rPr");
        XElement? outerRunProperties = runProperties;
        runProperties = rPr;
        try
        {
            ReadRunContent(element, rPr, inlines, state);
        }
        finally
        {
            runProperties = outerRunProperties;
        }
    }

    private void ReadRunContent(XElement element, XElement? rPr, NodeCollection<Inline> inlines, InlineState state)
    {
        StringBuilder text = new();
        bool customMarkNext = false;
        Footnote? customMarkNote = null;

        foreach (XElement child in element.Elements())
        {
            ReadRunChild(child);
        }

        Flush();

        void ReadRunChild(XElement child)
        {
            if (MarkupCompatibility.IsAlternateContent(child))
            {
                int before = inlines.Count;
                Flush();
                foreach (XElement chosen in MarkupCompatibility.Choose(child, Ns.Understood))
                {
                    ReadRunChild(chosen);
                }

                Flush();
                if (inlines.Count == before + 1)
                {
                    DocxExtension.Of(inlines[before]).Original = child;
                }

                return;
            }

            if (child.Name.Namespace != Ns.W)
            {
                Flush();
                Add(inlines, new Island(document, IslandKind.Other, DocxExtension.Format, child.ToString(SaveOptions.DisableFormatting)), state, child);
                return;
            }

            switch (child.Name.LocalName)
            {
                case "rPr" or "lastRenderedPageBreak" or "footnoteRef" or "endnoteRef" or "annotationRef" or "separator" or "continuationSeparator"
                    or "dayShort" or "dayLong" or "monthShort" or "monthLong" or "yearShort" or "yearLong":
                    break;
                case "t" or "delText" or "instrText" or "delInstrText":
                    if (customMarkNext && customMarkNote is not null)
                    {
                        customMarkNote.CustomMark = child.Value;
                        customMarkNext = false;
                    }
                    else
                    {
                        text.Append(child.Value);
                    }

                    break;
                case "noBreakHyphen":
                    text.Append('\u2011');
                    break;
                case "softHyphen":
                    text.Append('\u00AD');
                    break;
                case "tab" or "ptab":
                    Flush();
                    Add(inlines, new Tab(document), state, child.Name.LocalName == "ptab" ? child : null);
                    break;
                case "cr":
                    Flush();
                    Add(inlines, new Break(document), state, null);
                    break;
                case "br":
                    Flush();
                    Add(inlines, new Break(document, child.W("type") switch
                    {
                        "page" => BreakKind.Page,
                        "column" => BreakKind.Column,
                        _ => BreakKind.Line,
                    }), state, child.Attribute(Ns.W + "clear") is null ? null : child);
                    break;
                case "sym" when customMarkNext && customMarkNote is not null:
                    customMarkNote.CustomMark = SymbolText(child) ?? "";
                    customMarkNext = false;
                    break;
                case "sym":
                    Flush();
                    ReadSymbol(child);
                    break;
                case "fldChar":
                    Flush();
                    ReadFieldChar(child);
                    break;
                case "pgNum":
                    Flush();
                    Add(inlines, new FieldStart(document), state, child);
                    Add(inlines, new Run(document, " PAGE "), state, null);
                    Add(inlines, new FieldSeparator(document), state, null);
                    Add(inlines, new FieldEnd(document), state, null);
                    break;
                case "footnoteReference" or "endnoteReference":
                    Flush();
                    customMarkNote = ReadNoteReference(child, inlines, state);
                    customMarkNext = customMarkNote is not null && child.W("customMarkFollows") is "1" or "true" or "on";
                    break;
                case "commentReference":
                    Flush();
                    ReadCommentReference(child, inlines, state);
                    break;
                case "drawing":
                    Flush();
                    ReadDrawing(child, inlines, state);
                    break;
                case "pict":
                    Flush();
                    ReadVml(child, inlines, state, IslandKind.Vml);
                    break;
                case "object":
                    Flush();
                    ReadVml(child, inlines, state, IslandKind.OleObject);
                    break;
                case "contentPart":
                    Flush();
                    Add(inlines, new Island(document, IslandKind.Ink, DocxExtension.Format, child.ToString(SaveOptions.DisableFormatting)), state, child);
                    break;
                case "ruby":
                    Flush();
                    Add(inlines, new Island(document, IslandKind.Other, DocxExtension.Format, child.ToString(SaveOptions.DisableFormatting))
                    {
                        Text = string.Concat(child.Element(Ns.W + "rubyBase")?.Descendants(Ns.W + "t").Select(t => t.Value) ?? []),
                    }, state, child);
                    break;
                default:
                    Flush();
                    Add(inlines, new Island(document, IslandKind.Other, DocxExtension.Format, child.ToString(SaveOptions.DisableFormatting)), state, child);
                    break;
            }
        }

        void Flush()
        {
            if (text.Length == 0)
            {
                return;
            }

            NewRun(text.ToString());
            text.Clear();
        }

        Run NewRun(string value)
        {
            Run run = new(document, value);
            DocxExtension extension = DocxExtension.Of(run);
            if (element.HasAttributes)
            {
                extension.Attributes = [.. element.Attributes()];
            }

            if (rPr is not null)
            {
                if (rPr.Element(Ns.W + "rStyle").Val() is { } styleId && document.Styles[styleId] is CharacterStyle style)
                {
                    run.Style = style;
                }

                DocxProperties.ReadRun(rPr, run.Format, e =>
                {
                    if (e.Name == Ns.W + "rPrChange")
                    {
                        RunFormat old = new();
                        DocxProperties.ReadRun(e.Element(Ns.W + "rPr"), old);
                        run.FormatRevision = new FormatRevision(e.W("author") ?? "", DateOf(e), old);
                    }
                    else
                    {
                        extension.AddUnknownProperty("rPr", e);
                    }
                });
            }

            return Add(inlines, run, state, null);
        }

        void ReadSymbol(XElement child)
        {
            if (SymbolText(child) is not { } symbol)
            {
                return;
            }

            Run run = NewRun(symbol);
            if (child.W("font") is { } font)
            {
                run.Format.FontAscii = font;
                run.Format.FontHighAnsi = font;
            }

            DocxExtension.Of(run).Original = child;
        }

        void ReadFieldChar(XElement child)
        {
            switch (child.W("fldCharType"))
            {
                case "begin":
                    FieldStart start = Add(inlines, new FieldStart(document), state, child);
                    start.IsLocked = child.W("fldLock") is "1" or "true" or "on";
                    start.IsDirty = child.W("dirty") is "1" or "true" or "on";
                    break;
                case "separate":
                    Add(inlines, new FieldSeparator(document), state, null);
                    break;
                case "end":
                    Add(inlines, new FieldEnd(document), state, null);
                    break;
            }
        }
    }

    /// <summary>The character a w:sym stands for, or null when its code is not one.</summary>
    private static string? SymbolText(XElement sym)
    {
        return sym.W("char") is { } hex && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code) && code is > 0 and <= 0xFFFF
            && !char.IsSurrogate((char)code) ? ((char)code).ToString() : null;
    }

    private Footnote? ReadNoteReference(XElement reference, NodeCollection<Inline> inlines, InlineState state)
    {
        bool endnote = reference.Name.LocalName == "endnoteReference";
        if (noteDepth > 0 || commentDepth > 0 || headerDepth > 0
            || reference.W("id") is not { } id
            || !(endnote ? endnotes : footnotes).TryGetValue(id, out XElement? content))
        {
            return null;
        }

        Footnote note = Add(inlines, new Footnote(document, endnote ? NoteKind.Endnote : NoteKind.Footnote), state, reference);
        DocxExtension.Of(note.Story).Original = content;
        noteDepth++;
        try
        {
            ReadStory(content, note.Story.Blocks, endnote ? "endnotes" : "footnotes");
        }
        finally
        {
            noteDepth--;
        }

        return note;
    }

    private void ReadCommentReference(XElement reference, NodeCollection<Inline> inlines, InlineState state)
    {
        if (commentDepth > 0 || DocxUnits.Int(reference.W("id")) is not int id || !comments.TryGetValue(id, out XElement? content) || !usedComments.Add(id))
        {
            return;
        }

        Comment comment = Add(inlines, new Comment(document, content.W("author") ?? "", DateOf(content)), state, reference);
        comment.SetIdsSilently(id, null);
        document.ReserveCommentId(id);
        comment.Initials = content.W("initials");
        DocxExtension.Of(comment.Story).Original = content;
        commentDepth++;
        try
        {
            ReadStory(content, comment.Story.Blocks, "comments");
        }
        finally
        {
            commentDepth--;
        }

        readComments.Add(comment);
    }

    /// <summary>Reads a note's or comment's blocks from its part, with relationships resolved against that part.</summary>
    private void ReadStory(XElement content, NodeCollection<Block> blocks, string partKind)
    {
        OpcPart previous = currentPart;
        if (content.Document?.Annotation<OpcPart>() is { } part)
        {
            currentPart = part;
        }

        List<Inline> outerMarks = [.. pendingMarks];
        pendingMarks.Clear();
        Paragraph? outerLast = lastParagraph;
        lastParagraph = null;
        XElement? outerRunProperties = runProperties;
        runProperties = null;
        try
        {
            ReadBlocks(content, blocks);
            FlushMarksToLast();
        }
        finally
        {
            pendingMarks.Clear();
            pendingMarks.AddRange(outerMarks);
            lastParagraph = outerLast;
            currentPart = previous;
            runProperties = outerRunProperties;
        }

        _ = partKind;
    }

    /// <summary>Marks left over at the end of a story go at the end of its last paragraph.</summary>
    private void FlushMarksToLast()
    {
        if (pendingMarks.Count == 0)
        {
            return;
        }

        if (lastParagraph is not null)
        {
            foreach (Inline mark in pendingMarks)
            {
                lastParagraph.Inlines.AddTrusted(mark);
            }
        }

        pendingMarks.Clear();
    }

    private void FinishComments()
    {
        if (commentsExtended.Count == 0)
        {
            return;
        }

        // commentsExtended names comments by the paraId of their last paragraph; replies name their parent the same way.
        Dictionary<string, Comment> byParaId = new(StringComparer.Ordinal);
        foreach (Comment comment in readComments)
        {
            if (DocxExtension.Of(comment.Story).Original is { } content
                && content.Elements(Ns.W + "p").LastOrDefault()?.Attribute(Ns.W14 + "paraId")?.Value is { } paraId)
            {
                byParaId.TryAdd(paraId, comment);
            }
        }

        foreach ((string paraId, Comment comment) in byParaId)
        {
            if (!commentsExtended.TryGetValue(paraId, out (string? ParentParaId, bool Done) extended))
            {
                continue;
            }

            comment.Done = extended.Done;
            if (extended.ParentParaId is { } parentParaId && byParaId.TryGetValue(parentParaId, out Comment? parent) && parent != comment)
            {
                comment.SetIdsSilently(comment.Id, parent.Id);
            }
        }
    }

    /// <summary>Records each node's version as loading ends, so the writer can tell what was edited.</summary>
    private void Stamp()
    {
        data.FormattingVersion = document.FormattingVersion;
        foreach (Node node in document.Descendants())
        {
            if (node.Extensions.Get<DocxExtension>(DocxExtension.Format) is not { } extension)
            {
                continue;
            }

            extension.Source = data;
            extension.Version = node switch
            {
                Paragraph paragraph => paragraph.Version,
                Story story => story.Version,
                Table table => table.Stamp,
                _ => document.Version,
            };
        }
    }

    private static Revision RevisionOf(XElement element)
    {
        RevisionKind kind = element.Name.LocalName switch
        {
            "del" => RevisionKind.Deletion,
            "moveFrom" => RevisionKind.MoveFrom,
            "moveTo" => RevisionKind.MoveTo,
            _ => RevisionKind.Insertion,
        };
        return new Revision(kind, element.W("author") ?? "", DateOf(element));
    }

    private static DateTimeOffset? DateOf(XElement element)
    {
        return element.W("date") is { } date && DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset value) ? value : null;
    }
}
