using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Egap.Documents.Docx;

/// <summary>Inlines: runs, field characters, marks, hyperlinks, content controls, notes, comments, drawings and islands.</summary>
internal sealed partial class DocxWriter
{
    /// <summary>
    /// Writes <paramref name="inlines"/> into <paramref name="parent"/>, wrapping runs of inlines that share a tracked change
    /// in one w:ins or w:del. Field code runs (between a field start and its separator) are written as instrText.
    /// </summary>
    private bool WriteInlines(IEnumerable<Inline> inlines, XElement parent, bool fieldCode)
    {
        Revision? open = null;
        XElement target = parent;
        foreach (Inline inline in inlines)
        {
            if (!ReferenceEquals(inline.Revision, open))
            {
                open = inline.Revision;
                target = parent;
                if (open is not null && RevisionElement(open, mark: false) is { } wrapper)
                {
                    parent.Add(wrapper);
                    target = wrapper;
                }
            }

            fieldCode = WriteInline(inline, target, fieldCode);
        }

        return fieldCode;
    }

    private bool WriteInline(Inline inline, XElement target, bool fieldCode)
    {
        DocxExtension? kept = Kept(inline);
        if (kept?.Hoisted == true)
        {
            // Written from between the blocks, where the reader found it.
            if (inline is BookmarkStart hoisted && kept.Original is { } element && DocxUnits.Int(element.W("id")) is int hoistedId)
            {
                bookmarkIds[hoisted.Name] = hoistedId;
            }

            return fieldCode;
        }

        // Moved-from text stays w:t inside w:moveFrom; only a deletion uses w:delText.
        bool deleted = inline.Revision?.Kind == RevisionKind.Deletion;
        switch (inline)
        {
            case Run run:
                WriteRun(run, kept, target, fieldCode, deleted);
                break;
            case Tab:
                target.Add(RunWith(kept, kept?.Original?.Name == Ns.W + "ptab" ? new XElement(kept.Original) : new XElement(Ns.W + "tab")));
                break;
            case Break breakNode:
                {
                    XElement br = kept?.Original is { } original && original.Name == Ns.W + "br" ? new XElement(original) : new XElement(Ns.W + "br");
                    br.SetAttributeValue(Ns.W + "type", breakNode.Kind switch
                    {
                        BreakKind.Page => "page",
                        BreakKind.Column => "column",
                        _ => null,
                    });
                    target.Add(RunWith(kept, br));
                    break;
                }

            case FieldStart start:
                {
                    XElement fldChar = kept?.Original is { } original && original.Name == Ns.W + "fldChar" ? new XElement(original) : new XElement(Ns.W + "fldChar");
                    fldChar.SetAttributeValue(Ns.W + "fldCharType", "begin");
                    fldChar.SetAttributeValue(Ns.W + "fldLock", start.IsLocked ? "1" : null);
                    fldChar.SetAttributeValue(Ns.W + "dirty", start.IsDirty ? "1" : null);
                    target.Add(RunWith(kept, fldChar));
                    return true;
                }

            case FieldSeparator:
                target.Add(RunWith(kept, new XElement(Ns.W + "fldChar", new XAttribute(Ns.W + "fldCharType", "separate"))));
                return false;
            case FieldEnd:
                target.Add(RunWith(kept, new XElement(Ns.W + "fldChar", new XAttribute(Ns.W + "fldCharType", "end"))));
                return false;
            case BookmarkStart start:
                {
                    int id = BookmarkId(start, kept);
                    XElement element = kept?.Original is { } original ? new XElement(original) : new XElement(Ns.W + "bookmarkStart");
                    element.SetAttributeValue(Ns.W + "id", id);
                    element.SetAttributeValue(Ns.W + "name", start.Name);
                    target.Add(element);
                    break;
                }

            case BookmarkEnd end:
                if (bookmarkIds.TryGetValue(end.Name, out int endId) || (kept?.Original is { } endElement && DocxUnits.Int(endElement.W("id")) is int keptEndId && (endId = keptEndId) >= 0))
                {
                    XElement element = kept?.Original is { } original ? new XElement(original) : new XElement(Ns.W + "bookmarkEnd");
                    element.SetAttributeValue(Ns.W + "id", endId);
                    target.Add(element);
                }

                break;
            case CommentRangeStart rangeStart:
                target.Add(new XElement(Ns.W + "commentRangeStart", new XAttribute(Ns.W + "id", rangeStart.CommentId)));
                break;
            case CommentRangeEnd rangeEnd:
                target.Add(new XElement(Ns.W + "commentRangeEnd", new XAttribute(Ns.W + "id", rangeEnd.CommentId)));
                break;
            case Hyperlink link:
                WriteHyperlink(link, kept, target);
                break;
            case InlineContentControl control:
                {
                    XElement content = new(Ns.W + "sdtContent");
                    fieldCode = WriteInlines(control.Inlines, content, fieldCode);
                    target.Add(new XElement(Ns.W + "sdt", SdtProperties(control.Properties, kept, kept?.Original), SdtEnd(kept), content));
                    break;
                }

            case Footnote note:
                WriteNoteReference(note, kept, target);
                break;
            case Comment comment:
                target.Add(RunWith(kept, new XElement(Ns.W + "commentReference", new XAttribute(Ns.W + "id", comment.Id))));
                break;
            case Picture picture:
                target.Add(RunWith(kept, WritePicture(picture, kept)));
                break;
            case Shape shape:
                target.Add(RunWith(kept, WriteShape(shape, kept)));
                break;
            case Island island:
                WriteIsland(island, kept, target);
                break;
        }

        return fieldCode;
    }

    /// <summary>A w:r holding <paramref name="content"/> with the run properties the reader kept for it.</summary>
    private static XElement RunWith(DocxExtension? kept, XElement content)
    {
        XElement run = new(Ns.W + "r");
        if (kept?.RunProperties is { } rPr)
        {
            run.Add(new XElement(rPr));
        }

        run.Add(content);
        return run;
    }

    private void WriteRun(Run run, DocxExtension? kept, XElement target, bool fieldCode, bool deleted)
    {
        XElement r = new(Ns.W + "r");
        if (kept?.Attributes is { } attributes)
        {
            r.Add(attributes.Select(a => new XAttribute(a)));
        }

        XElement? change = null;
        if (run.FormatRevision is { OldFormat: RunFormat old } revision)
        {
            change = new XElement(Ns.W + "rPrChange", new XAttribute(Ns.W + "id", nextRevisionId++), RevisionAttributes(revision.Author, revision.Date),
                DocxPropertyWriter.Run(old, null, null) ?? new XElement(Ns.W + "rPr"));
        }

        if (DocxPropertyWriter.Run(run.Format, run.Style, kept?.UnknownProperties?.GetValueOrDefault("rPr"), change) is { } rPr)
        {
            r.Add(rPr);
        }

        if (kept?.Original is { } symbol && symbol.Name == Ns.W + "sym" && run.Text.Length == 1)
        {
            r.Add(new XElement(symbol));
            target.Add(r);
            return;
        }

        string textName = (fieldCode, deleted) switch
        {
            (true, true) => "delInstrText",
            (true, false) => "instrText",
            (false, true) => "delText",
            _ => "t",
        };
        StringBuilder pending = new();
        foreach (char c in Text(run.Text))
        {
            // Tabs and line breaks inside text stay text, as Word reads them from w:t; breaks of the model are Break nodes.
            XElement? special = c switch
            {
                '\u2011' => new XElement(Ns.W + "noBreakHyphen"),
                '\u00AD' => new XElement(Ns.W + "softHyphen"),
                _ => null,
            };
            if (special is null)
            {
                pending.Append(c);
                continue;
            }

            Flush();
            r.Add(special);
        }

        Flush();
        target.Add(r);

        void Flush()
        {
            if (pending.Length == 0)
            {
                return;
            }

            string value = pending.ToString();
            XElement t = new(Ns.W + textName, value);
            if (value[0] is ' ' or '\t' or '\n' or '\r' || value[^1] is ' ' or '\t' or '\n' or '\r' || value.Contains("  ", StringComparison.Ordinal))
            {
                t.Add(new XAttribute(XNamespace.Xml + "space", "preserve"));
            }

            r.Add(t);
            pending.Clear();
        }
    }

    private int BookmarkId(BookmarkStart start, DocxExtension? kept)
    {
        int id;
        if (kept?.Original is { } original && DocxUnits.Int(original.W("id")) is int keptId && !bookmarkIds.ContainsValue(keptId))
        {
            id = keptId;
        }
        else
        {
            while (usedBookmarkIds.Contains(nextBookmarkId))
            {
                nextBookmarkId++;
            }

            id = nextBookmarkId++;
            usedBookmarkIds.Add(id);
        }

        bookmarkIds[start.Name] = id;
        return id;
    }

    private void WriteHyperlink(Hyperlink link, DocxExtension? kept, XElement target)
    {
        XElement element = new(Ns.W + "hyperlink");
        if (kept?.Original is { } original)
        {
            element.Add(original.Attributes().Where(a => a.Name.Namespace != Ns.R && a.Name.LocalName is not ("anchor" or "tooltip" or "tgtFrame")).Select(a => new XAttribute(a)));
        }

        if (link.Address is { Length: > 0 } address)
        {
            string? id = null;
            if (kept?.Original is { } keptOriginal && (string?)keptOriginal.Attribute(Ns.R + "id") is { } keptId
                && string.Equals(kept.PartName, currentPart, StringComparison.OrdinalIgnoreCase)
                && data!.Package.GetPart(currentPart)?.GetRelationship(keptId) is { External: true } relationship && relationship.Target == address)
            {
                id = keptId;
            }

            if (id is null)
            {
                id = package.NextRelationshipId(currentPart);
                package.AddRelationship(currentPart, id, OpcRelationshipTypes.Hyperlink, address, external: true);
            }

            element.SetAttributeValue(Ns.R + "id", id);
        }

        element.SetAttributeValue(Ns.W + "anchor", link.Anchor);
        element.SetAttributeValue(Ns.W + "tooltip", link.Tooltip);
        element.SetAttributeValue(Ns.W + "tgtFrame", link.Target);
        WriteInlines(link.Inlines, element, fieldCode: false);
        target.Add(element);
    }

    /// <summary>A drop-down list's or combo box's items as <c>w:listItem</c> elements, each its own display text and value.</summary>
    private static IEnumerable<XElement> ListItems(ContentControlProperties properties)
    {
        return (properties.Items ?? []).Select(i => new XElement(Ns.W + "listItem", new XAttribute(Ns.W + "displayText", i), new XAttribute(Ns.W + "value", i)));
    }

    private static XElement SdtProperties(ContentControlProperties properties, DocxExtension? kept, XElement? original)
    {
        List<XElement> children = [];
        if (properties.Title is { } title)
        {
            children.Add(DocxPropertyWriter.Val("alias", title));
        }

        if (properties.Tag is { } tag)
        {
            children.Add(DocxPropertyWriter.Val("tag", tag));
        }

        if (properties.LockContents || properties.LockControl)
        {
            children.Add(DocxPropertyWriter.Val("lock", (properties.LockControl, properties.LockContents) switch
            {
                (true, true) => "sdtContentLocked",
                (true, false) => "sdtLocked",
                _ => "contentLocked",
            }));
        }

        List<XElement> extras = kept?.UnknownProperties?.GetValueOrDefault("sdtPr") ?? [];
        if (properties.ShowingPlaceholder)
        {
            children.Add(new XElement(Ns.W + "showingPlcHdr"));
        }

        bool kindKept = extras.Exists(e => e.Name.LocalName is "date" or "dropDownList" or "comboBox" or "docPartObj" or "docPartList" or "checkbox");
        if (!kindKept)
        {
            switch (properties.Kind)
            {
                case ContentControlKind.PlainText:
                    children.Add(new XElement(Ns.W + "text"));
                    break;
                case ContentControlKind.Date:
                    children.Add(new XElement(Ns.W + "date"));
                    break;
                case ContentControlKind.DropDownList:
                    children.Add(new XElement(Ns.W + "dropDownList", ListItems(properties)));
                    break;
                case ContentControlKind.ComboBox:
                    children.Add(new XElement(Ns.W + "comboBox", ListItems(properties)));
                    break;
                case ContentControlKind.Picture:
                    children.Add(new XElement(Ns.W + "picture"));
                    break;
                case ContentControlKind.Group:
                    children.Add(new XElement(Ns.W + "group"));
                    break;
                case ContentControlKind.BuildingBlockGallery:
                    children.Add(new XElement(Ns.W + "docPartObj"));
                    break;
                case ContentControlKind.Citation:
                    children.Add(new XElement(Ns.W + "citation"));
                    break;
                case ContentControlKind.Bibliography:
                    children.Add(new XElement(Ns.W + "bibliography"));
                    break;
                case ContentControlKind.Equation:
                    children.Add(new XElement(Ns.W + "equation"));
                    break;
                case ContentControlKind.CheckBox:
                    children.Add(new XElement(Ns.W14 + "checkbox", new XElement(Ns.W14 + "checked", new XAttribute(Ns.W14 + "val", properties.IsChecked == true ? "1" : "0"))));
                    break;
                case ContentControlKind.RepeatingSection:
                    children.Add(new XElement(Ns.W15 + "repeatingSection"));
                    break;
            }
        }
        else if (properties.Kind == ContentControlKind.CheckBox && extras.Find(e => e.Name == Ns.W14 + "checkbox") is { } checkbox)
        {
            XElement copy = new(checkbox);
            extras = [.. extras.Where(e => e != checkbox)];
            copy.Element(Ns.W14 + "checked")?.SetAttributeValue(Ns.W14 + "val", properties.IsChecked == true ? "1" : "0");
            children.Add(copy);
        }

        // sdtPr has no schema sequence that matters to Word beyond rPr first; keep the original order where it can be found.
        children.AddRange(extras.Select(e => new XElement(e)));
        if (original?.Element(Ns.W + "sdtPr") is { } originalPr)
        {
            List<XName> order = [.. originalPr.Elements().Select(e => e.Name)];
            children = [.. children.Select((e, i) => (e, i)).OrderBy(x => order.IndexOf(x.e.Name) is int at && at >= 0 ? at : order.Count + x.i).Select(x => x.e)];
        }

        return new XElement(Ns.W + "sdtPr", children);
    }

    private void WriteNoteReference(Footnote note, DocxExtension? kept, XElement target)
    {
        bool endnote = note.Kind == NoteKind.Endnote;
        XElement reference = new(Ns.W + (endnote ? "endnoteReference" : "footnoteReference"), new XAttribute(Ns.W + "id", noteIds[note]));
        XElement run = new(Ns.W + "r");
        run.Add(kept?.RunProperties is { } rPr
            ? new XElement(rPr)
            : new XElement(Ns.W + "rPr", DocxPropertyWriter.Val("rStyle", endnote ? "EndnoteReference" : "FootnoteReference")));
        run.Add(reference);
        if (note.CustomMark is { } mark)
        {
            reference.SetAttributeValue(Ns.W + "customMarkFollows", "1");
            run.Add(new XElement(Ns.W + "t", mark));
        }

        target.Add(run);
    }

    private void WriteIsland(Island island, DocxExtension? kept, XElement target)
    {
        if (island.Format != DocxExtension.Format || Parse(island.Markup) is not { } markup)
        {
            if (island.Text.Length > 0)
            {
                target.Add(new XElement(Ns.W + "r", new XElement(Ns.W + "t", Text(island.Text))));
            }

            return;
        }

        bool runLevel = kept?.InRun ?? markup.Name.Namespace == Ns.W && markup.Name.LocalName is "drawing" or "pict" or "object" or "contentPart" or "ruby";

        // An island read from a markup compatibility choice goes back with every branch.
        XElement content = kept?.Original is { } original && MarkupCompatibility.IsAlternateContent(original) ? new XElement(original) : markup;
        content = Rebase(content, kept?.PartName, copy: false);
        target.Add(runLevel ? RunWith(kept, content) : content);
    }

    private XElement WritePicture(Picture picture, DocxExtension? kept)
    {
        if (!regenerate && kept?.Original is { } original && kept.Snapshot is PictureState state && state.Matches(picture)
            && string.Equals(kept.PartName, currentPart, StringComparison.OrdinalIgnoreCase))
        {
            return new XElement(original);
        }

        long cx = (long)Math.Round(picture.Width * 12700);
        long cy = (long)Math.Round(picture.Height * 12700);
        int id = nextDrawingId++;
        XElement blip = new(Ns.A + "blip");
        if (picture.Image.Bytes.Length > 0 && picture.Image.ContentType == "image/svg+xml" && picture.Fallback is { Bytes.Length: > 0 } fallback)
        {
            // Word's form of an SVG picture: the raster stand-in as the blip, the SVG in an extension beside it.
            blip.SetAttributeValue(Ns.R + "embed", MediaRelationship(fallback));
            blip.Add(new XElement(Ns.A + "extLst", new XElement(
                Ns.A + "ext",
                new XAttribute("uri", "{96DAC541-7B7A-43D3-8B79-37D633B846F1}"),
                new XElement(Ns.Asvg + "svgBlip", new XAttribute(XNamespace.Xmlns + "asvg", Ns.Asvg.NamespaceName), new XAttribute(Ns.R + "embed", MediaRelationship(picture.Image))))));
        }
        else if (picture.Image.Bytes.Length > 0)
        {
            blip.SetAttributeValue(Ns.R + "embed", MediaRelationship(picture.Image));
        }
        else if (picture.SourceUrl is { } url)
        {
            string linkId = package.NextRelationshipId(currentPart);
            package.AddRelationship(currentPart, linkId, OpcRelationshipTypes.Image, url, external: true);
            blip.SetAttributeValue(Ns.R + "link", linkId);
        }

        XElement blipFill = new(Ns.Pic + "blipFill", blip);
        (double left, double top, double right, double bottom) = picture.Crop;
        if (left != 0 || top != 0 || right != 0 || bottom != 0)
        {
            XElement rect = new(Ns.A + "srcRect");
            foreach ((string name, double value) in (ReadOnlySpan<(string, double)>)[("l", left), ("t", top), ("r", right), ("b", bottom)])
            {
                if (value != 0)
                {
                    rect.SetAttributeValue(name, Math.Round(value * 100000).ToString(CultureInfo.InvariantCulture));
                }
            }

            blipFill.Add(rect);
        }

        blipFill.Add(new XElement(Ns.A + "stretch", new XElement(Ns.A + "fillRect")));
        XElement pic = new(
            Ns.Pic + "pic",
            new XElement(Ns.Pic + "nvPicPr", new XElement(Ns.Pic + "cNvPr", new XAttribute("id", id), new XAttribute("name", "Picture " + id)), new XElement(Ns.Pic + "cNvPicPr")),
            blipFill,
            new XElement(Ns.Pic + "spPr", new XElement(Ns.A + "xfrm", new XElement(Ns.A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(Ns.A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                new XElement(Ns.A + "prstGeom", new XAttribute("prst", "rect"), new XElement(Ns.A + "avLst"))));
        XElement graphic = new(Ns.A + "graphic", new XElement(Ns.A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"), pic));
        return new XElement(Ns.W + "drawing", Frame(picture.Floating, cx, cy, id, "Picture " + id, picture.Title, picture.Description, graphic));
    }

    private XElement WriteShape(Shape shape, DocxExtension? kept)
    {
        if (!regenerate && kept?.Original is { } original && ShapeUnchanged(shape, kept))
        {
            return new XElement(original);
        }

        // A shape whose own settings are as read keeps its markup (VML or DrawingML, every branch); only its text is new.
        if (kept is { Original: { } read, Snapshot: ShapeState state } && state == ShapeState.Of(shape)
            && string.Equals(kept.PartName, currentPart, StringComparison.OrdinalIgnoreCase))
        {
            XElement copy = new(read);
            List<XElement> contents = [.. copy.Descendants(Ns.W + "txbxContent").Where(c => !c.Ancestors(Ns.W + "txbxContent").Any())];
            if (shape.TextBox is { } textBox && contents.Count > 0)
            {
                foreach (XElement content in contents)
                {
                    content.ReplaceNodes(WriteBlocks(textBox.Blocks, ensureParagraph: Kept(textBox) is null));
                }

                return copy;
            }
        }

        long cx = (long)Math.Round(shape.Width * 12700);
        long cy = (long)Math.Round(shape.Height * 12700);
        int id = nextDrawingId++;
        XElement spPr = new(Ns.Wps + "spPr", new XElement(Ns.A + "xfrm", shape.Rotation != 0 ? new XAttribute("rot", Math.Round(shape.Rotation * 60000).ToString(CultureInfo.InvariantCulture)) : null,
            new XElement(Ns.A + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(Ns.A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
            Geometry(shape, kept));
        spPr.Add(shape.Fill is { } fill ? new XElement(Ns.A + "solidFill", DrawingColor(fill)) : new XElement(Ns.A + "noFill"));
        spPr.Add(shape.Outline is { Style: not BorderStyle.None } outline
            ? new XElement(Ns.A + "ln", new XAttribute("w", Math.Round(outline.Width * 12700).ToString(CultureInfo.InvariantCulture)), new XElement(Ns.A + "solidFill", DrawingColor(outline.Color)))
            : new XElement(Ns.A + "ln", new XElement(Ns.A + "noFill")));
        XElement wsp = new(Ns.Wps + "wsp", new XElement(Ns.Wps + "cNvSpPr"), spPr);
        if (shape.TextBox is { } box)
        {
            string previous = currentPart;
            wsp.Add(new XElement(Ns.Wps + "txbx", new XElement(Ns.W + "txbxContent", WriteBlocks(box.Blocks, ensureParagraph: Kept(box) is null))));
            currentPart = previous;
        }

        wsp.Add(new XElement(Ns.Wps + "bodyPr"));
        XElement graphic = new(Ns.A + "graphic", new XElement(Ns.A + "graphicData", new XAttribute("uri", "http://schemas.microsoft.com/office/word/2010/wordprocessingShape"), wsp));
        return new XElement(Ns.W + "drawing", Frame(shape.Floating, cx, cy, id, "Shape " + id, shape.Title, shape.Description, graphic));
    }

    /// <summary>The shape's geometry: the file's own for a kind the model does not name, else the preset for its kind.</summary>
    private static XElement Geometry(Shape shape, DocxExtension? kept)
    {
        if (shape.Kind == ShapeKind.Other && kept?.Original?.Descendants(Ns.Wps + "spPr").FirstOrDefault() is { } spPr
            && (spPr.Element(Ns.A + "custGeom") ?? spPr.Element(Ns.A + "prstGeom")) is { } geometry)
        {
            return new XElement(geometry);
        }

        return new XElement(Ns.A + "prstGeom", new XAttribute("prst", shape.Kind switch
        {
            ShapeKind.RoundedRectangle => "roundRect",
            ShapeKind.Ellipse => "ellipse",
            ShapeKind.Line => "line",
            ShapeKind.Triangle => "triangle",
            ShapeKind.RightArrow => "rightArrow",
            ShapeKind.Other => "rect",
            _ => "rect",
        }), new XElement(Ns.A + "avLst"));
    }

    private bool ShapeUnchanged(Shape shape, DocxExtension kept)
    {
        if (!string.Equals(kept.PartName, currentPart, StringComparison.OrdinalIgnoreCase) || shape.Paragraph is not { } paragraph
            || Kept(paragraph) is not { } paragraphKept || paragraph.Version != paragraphKept.Version)
        {
            return false;
        }

        return shape.TextBox is not { } box || (Kept(box) is { } boxKept && box.Version == boxKept.Version
            && box.Descendants<Paragraph>().All(p => ParagraphUnchanged(p, currentPart)));
    }

    private static XElement DrawingColor(DocumentColor color)
    {
        return color.Kind switch
        {
            DocumentColorKind.Theme => new XElement(Ns.A + "schemeClr", new XAttribute("val", color.Theme switch
            {
                ThemeColor.Dark1 => "tx1",
                ThemeColor.Light1 => "bg1",
                ThemeColor.Dark2 => "tx2",
                ThemeColor.Light2 => "bg2",
                ThemeColor.Hyperlink => "hlink",
                ThemeColor.FollowedHyperlink => "folHlink",
                _ => DocxPropertyWriter.ThemeColorName(color.Theme),
            })),
            DocumentColorKind.Rgb => new XElement(Ns.A + "srgbClr", new XAttribute("val", DocxPropertyWriter.Hex(color.Rgb))),
            _ => new XElement(Ns.A + "srgbClr", new XAttribute("val", "000000")),
        };
    }

    /// <summary>The wp:inline or wp:anchor frame around a drawing's graphic.</summary>
    private static XElement Frame(FloatingPlacement? floating, long cx, long cy, int id, string name, string? title, string? description, XElement graphic)
    {
        XElement docPr = new(Ns.Wp + "docPr", new XAttribute("id", id), new XAttribute("name", name));
        if (description is not null)
        {
            docPr.SetAttributeValue("descr", description);
        }

        if (title is not null)
        {
            docPr.SetAttributeValue("title", title);
        }

        XElement extent = new(Ns.Wp + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy));
        if (floating is null)
        {
            return new XElement(Ns.Wp + "inline", new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                extent, docPr, new XElement(Ns.Wp + "cNvGraphicFramePr"), graphic);
        }

        static long Emu(double points) => (long)Math.Round(points * 12700);
        XElement anchor = new(
            Ns.Wp + "anchor",
            new XAttribute("distT", Emu(floating.DistanceTop)),
            new XAttribute("distB", Emu(floating.DistanceBottom)),
            new XAttribute("distL", Emu(floating.DistanceLeft)),
            new XAttribute("distR", Emu(floating.DistanceRight)),
            new XAttribute("simplePos", 0),
            new XAttribute("relativeHeight", Math.Clamp(floating.ZOrder, 0, uint.MaxValue)),
            new XAttribute("behindDoc", floating.Wrap == WrapMode.BehindText ? 1 : 0),
            new XAttribute("locked", floating.Locked ? 1 : 0),
            new XAttribute("layoutInCell", floating.LayoutInCell ? 1 : 0),
            new XAttribute("allowOverlap", floating.AllowOverlap ? 1 : 0),
            new XElement(Ns.Wp + "simplePos", new XAttribute("x", 0), new XAttribute("y", 0)),
            Position("positionH", floating.HorizontalAnchor switch
            {
                HorizontalAnchor.Page => "page",
                HorizontalAnchor.Margin => "margin",
                HorizontalAnchor.Character => "character",
                HorizontalAnchor.LeftMargin => "leftMargin",
                HorizontalAnchor.RightMargin => "rightMargin",
                HorizontalAnchor.InsideMargin => "insideMargin",
                HorizontalAnchor.OutsideMargin => "outsideMargin",
                _ => "column",
            }, floating.HorizontalAlignment, floating.HorizontalOffset, horizontal: true),
            Position("positionV", floating.VerticalAnchor switch
            {
                VerticalAnchor.Page => "page",
                VerticalAnchor.Margin => "margin",
                VerticalAnchor.Line => "line",
                VerticalAnchor.TopMargin => "topMargin",
                VerticalAnchor.BottomMargin => "bottomMargin",
                VerticalAnchor.InsideMargin => "insideMargin",
                VerticalAnchor.OutsideMargin => "outsideMargin",
                _ => "paragraph",
            }, floating.VerticalAlignment, floating.VerticalOffset, horizontal: false),
            extent,
            new XElement(Ns.Wp + "effectExtent", new XAttribute("l", 0), new XAttribute("t", 0), new XAttribute("r", 0), new XAttribute("b", 0)));
        string side = floating.WrapSide switch
        {
            WrapSide.Left => "left",
            WrapSide.Right => "right",
            WrapSide.Largest => "largest",
            _ => "bothSides",
        };
        anchor.Add(floating.Wrap switch
        {
            WrapMode.BehindText or WrapMode.InFrontOfText => new XElement(Ns.Wp + "wrapNone"),
            WrapMode.TopAndBottom => new XElement(Ns.Wp + "wrapTopAndBottom"),
            WrapMode.Tight or WrapMode.Through => new XElement(Ns.Wp + (floating.Wrap == WrapMode.Tight ? "wrapTight" : "wrapThrough"), new XAttribute("wrapText", side), Polygon(floating.WrapPolygon)),
            _ => new XElement(Ns.Wp + "wrapSquare", new XAttribute("wrapText", side)),
        });
        anchor.Add(docPr, new XElement(Ns.Wp + "cNvGraphicFramePr"), graphic);
        return anchor;

        static XElement Position(string name, string relativeFrom, FloatingAlignment alignment, double offset, bool horizontal)
        {
            XElement position = new(Ns.Wp + name, new XAttribute("relativeFrom", relativeFrom));
            if (alignment == FloatingAlignment.None)
            {
                position.Add(new XElement(Ns.Wp + "posOffset", Emu(offset)));
            }
            else
            {
                position.Add(new XElement(Ns.Wp + "align", alignment switch
                {
                    FloatingAlignment.Start => horizontal ? "left" : "top",
                    FloatingAlignment.Center => "center",
                    FloatingAlignment.End => horizontal ? "right" : "bottom",
                    FloatingAlignment.Inside => "inside",
                    _ => "outside",
                }));
            }

            return position;
        }

        static XElement Polygon(IReadOnlyList<(double X, double Y)>? points)
        {
            IReadOnlyList<(double X, double Y)> list = points is { Count: >= 3 } ? points : [(0, 0), (0, 1), (1, 1), (1, 0), (0, 0)];
            XElement polygon = new(Ns.Wp + "wrapPolygon", new XAttribute("edited", 0));
            for (int i = 0; i < list.Count; i++)
            {
                polygon.Add(new XElement(Ns.Wp + (i == 0 ? "start" : "lineTo"), new XAttribute("x", Math.Round(list[i].X * 21600)), new XAttribute("y", Math.Round(list[i].Y * 21600))));
            }

            return polygon;
        }
    }

    /// <summary>The relationship id, from the part being written, of a media part holding <paramref name="image"/>.</summary>
    private string MediaRelationship(ImageData image)
    {
        if (!media.TryGetValue(image, out string? partName))
        {
            string extension = image.ContentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpeg",
                "image/gif" => ".gif",
                "image/bmp" => ".bmp",
                "image/tiff" => ".tiff",
                "image/x-emf" => ".emf",
                "image/x-wmf" => ".wmf",
                "image/svg+xml" => ".svg",
                "image/webp" => ".webp",
                _ => ".bin",
            };
            partName = UniquePartName("/word/media/image", extension);
            package.AddPart(partName, image.ContentType, image.Bytes.ToArray());
            writtenParts.Add(partName);
            media[image] = partName;
        }

        string id = package.NextRelationshipId(currentPart);
        package.AddRelationship(currentPart, id, OpcRelationshipTypes.Image, RelationshipTarget(currentPart, partName));
        return id;
    }
}
