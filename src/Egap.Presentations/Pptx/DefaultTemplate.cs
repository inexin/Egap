using System.Globalization;
using System.Text;
using System.Xml.Linq;

using Egap.Documents.Docx;

namespace Egap.Presentations.Pptx;

/// <summary>
/// The parts of a presentation made in code, written from ECMA-376 Part 1's PresentationML and DrawingML schemas: Egap's own
/// theme, one slide master with the title, body, date, footer and slide number placeholders, the eleven standard layouts,
/// and on demand a notes master. Positions are Egap's own grid on a 16:9 slide (13.333 by 7.5 inches).
/// </summary>
internal static class DefaultTemplate
{
    public const string Ct = "application/vnd.openxmlformats-officedocument.presentationml.";
    public const string ThemeContentType = "application/vnd.openxmlformats-officedocument.theme+xml";
    public const string Namespaces = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\"";

    // Egap's grid, in EMUs: half-inch margins, a 12.333-inch text column.
    private const long Left = 457200, Width = 11277600, TitleTop = 304800, TitleHeight = 1371600, BodyTop = 1828800, BodyHeight = 4419600;
    private const long FooterTop = 6400800, FooterHeight = 304800;

    private static readonly int[] LineWidths = [6350, 12700, 19050];

    private static readonly (string Type, string Name, Func<string> Shapes)[] Layouts =
    [
        ("title", "Title Slide", () =>
            Placeholder(2, "Title 1", "type=\"ctrTitle\"", Box(1524000, 1143000, 9144000, 2362200), "<a:bodyPr anchor=\"b\"/>", "<a:lvl1pPr algn=\"ctr\"><a:defRPr sz=\"5400\"/></a:lvl1pPr>", "Click to add title")
            + Placeholder(3, "Subtitle 2", "type=\"subTitle\" idx=\"1\"", Box(1524000, 3657600, 9144000, 1600200), "<a:bodyPr/>", "<a:lvl1pPr marL=\"0\" indent=\"0\" algn=\"ctr\"><a:buNone/><a:defRPr sz=\"2400\"/></a:lvl1pPr>", "Click to add subtitle")),
        ("obj", "Title and Content", () =>
            Placeholder(2, "Title 1", "type=\"title\"", "", null, null, "Click to add title")
            + Placeholder(3, "Content Placeholder 2", "idx=\"1\"", "", null, null, "Click to add text")),
        ("secHead", "Section Header", () =>
            Placeholder(2, "Title 1", "type=\"title\"", Box(Left, 1600200, Width, 2895600), "<a:bodyPr anchor=\"b\"/>", "<a:lvl1pPr><a:defRPr sz=\"5400\"/></a:lvl1pPr>", "Click to add section title")
            + Placeholder(3, "Text Placeholder 2", "type=\"body\" idx=\"1\"", Box(Left, 4572000, Width, 1524000), "<a:bodyPr/>", "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"2400\"><a:solidFill><a:schemeClr val=\"tx1\"><a:tint val=\"75000\"/></a:schemeClr></a:solidFill></a:defRPr></a:lvl1pPr>", "Click to add text")),
        ("twoObj", "Two Content", () =>
            Placeholder(2, "Title 1", "type=\"title\"", "", null, null, "Click to add title")
            + Placeholder(3, "Content Placeholder 2", "sz=\"half\" idx=\"1\"", Box(Left, BodyTop, 5486400, BodyHeight), null, null, "Click to add text")
            + Placeholder(4, "Content Placeholder 3", "sz=\"half\" idx=\"2\"", Box(6248400, BodyTop, 5486400, BodyHeight), null, null, "Click to add text")),
        ("twoTxTwoObj", "Comparison", () =>
            Placeholder(2, "Title 1", "type=\"title\"", "", null, null, "Click to add title")
            + Placeholder(3, "Text Placeholder 2", "type=\"body\" idx=\"1\"", Box(Left, BodyTop, 5486400, 609600), "<a:bodyPr anchor=\"b\"/>", "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"2400\" b=\"1\"/></a:lvl1pPr>", "Click to add text")
            + Placeholder(4, "Content Placeholder 3", "sz=\"half\" idx=\"2\"", Box(Left, 2438400, 5486400, 3810000), null, null, "Click to add text")
            + Placeholder(5, "Text Placeholder 4", "type=\"body\" sz=\"quarter\" idx=\"3\"", Box(6248400, BodyTop, 5486400, 609600), "<a:bodyPr anchor=\"b\"/>", "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"2400\" b=\"1\"/></a:lvl1pPr>", "Click to add text")
            + Placeholder(6, "Content Placeholder 5", "sz=\"quarter\" idx=\"4\"", Box(6248400, 2438400, 5486400, 3810000), null, null, "Click to add text")),
        ("titleOnly", "Title Only", () => Placeholder(2, "Title 1", "type=\"title\"", "", null, null, "Click to add title")),
        ("blank", "Blank", () => ""),
        ("objTx", "Content with Caption", () =>
            Placeholder(2, "Title 1", "type=\"title\"", Box(Left, 457200, 3962400, 1600200), "<a:bodyPr anchor=\"b\"/>", "<a:lvl1pPr><a:defRPr sz=\"3200\"/></a:lvl1pPr>", "Click to add title")
            + Placeholder(3, "Content Placeholder 2", "idx=\"1\"", Box(4876800, 457200, 6858000, 5791200), null, null, "Click to add text")
            + Placeholder(4, "Text Placeholder 3", "type=\"body\" sz=\"half\" idx=\"2\"", Box(Left, 2057400, 3962400, 4191000), "<a:bodyPr/>", "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"1600\"/></a:lvl1pPr>", "Click to add text")),
        ("picTx", "Picture with Caption", () =>
            Placeholder(2, "Title 1", "type=\"title\"", Box(Left, 457200, 3962400, 1600200), "<a:bodyPr anchor=\"b\"/>", "<a:lvl1pPr><a:defRPr sz=\"3200\"/></a:lvl1pPr>", "Click to add title")
            + Placeholder(3, "Picture Placeholder 2", "type=\"pic\" idx=\"1\"", Box(4876800, 457200, 6858000, 5791200), "<a:bodyPr/>", "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"3200\"/></a:lvl1pPr>", "Click icon to add picture")
            + Placeholder(4, "Text Placeholder 3", "type=\"body\" sz=\"half\" idx=\"2\"", Box(Left, 2057400, 3962400, 4191000), "<a:bodyPr/>", "<a:lvl1pPr marL=\"0\" indent=\"0\"><a:buNone/><a:defRPr sz=\"1600\"/></a:lvl1pPr>", "Click to add text")),
        ("vertTx", "Title and Vertical Text", () =>
            Placeholder(2, "Title 1", "type=\"title\"", "", null, null, "Click to add title")
            + Placeholder(3, "Vertical Text Placeholder 2", "type=\"body\" orient=\"vert\" idx=\"1\"", "", "<a:bodyPr vert=\"eaVert\"/>", null, "Click to add text")),
        ("vertTitleAndTx", "Vertical Title and Text", () =>
            Placeholder(2, "Vertical Title 1", "type=\"title\" orient=\"vert\"", Box(8839200, TitleTop, 2895600, 5943600), "<a:bodyPr vert=\"eaVert\"/>", null, "Click to add title")
            + Placeholder(3, "Vertical Text Placeholder 2", "type=\"body\" orient=\"vert\" idx=\"1\"", Box(Left, TitleTop, 8229600, 5943600), "<a:bodyPr vert=\"eaVert\"/>", null, "Click to add text")),
    ];

    /// <summary>Adds the parts of a new, empty presentation to <paramref name="package"/>; returns the presentation part.</summary>
    public static XmlPart Build(PptxPackage package)
    {
        PackagePart main = package.AddXmlPart("/ppt/presentation.xml", Ct + "presentation.main+xml", Parse(
            $"<p:presentation {Namespaces} saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>"
            + "<p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/>"
            + $"<p:defaultTextStyle>{Levels(i => $"<a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr>", 457200, 0, withDefault: true)}</p:defaultTextStyle></p:presentation>"));
        package.AddRelationship("/", OpcRelationshipTypes.OfficeDocument, main.Name);
        package.AddXmlPart("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml", Parse(
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">"
            + "<dc:title>Presentation</dc:title><dcterms:created xsi:type=\"dcterms:W3CDTF\">" + DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "</dcterms:created></cp:coreProperties>"));
        package.AddRelationship("/", OpcRelationshipTypes.CoreProperties, "/docProps/core.xml");
        package.AddXmlPart("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml", Parse(
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Egap.Presentations</Application></Properties>"));
        package.AddRelationship("/", OpcRelationshipTypes.ExtendedProperties, "/docProps/app.xml");

        PackagePart master = package.AddXmlPart("/ppt/slideMasters/slideMaster1.xml", Ct + "slideMaster+xml", new XDocument());
        package.AddRelationship(main.Name, PptxNames.SlideMasterRelationship, master.Name);
        StringBuilder layoutIds = new();
        for (int i = 0; i < Layouts.Length; i++)
        {
            (string type, string name, Func<string> shapes) = Layouts[i];
            string layoutName = $"/ppt/slideLayouts/slideLayout{i + 1}.xml";
            package.AddXmlPart(layoutName, Ct + "slideLayout+xml", Parse(
                $"<p:sldLayout {Namespaces} type=\"{type}\" preserve=\"1\"><p:cSld name=\"{name}\">{Tree(shapes() + Footers(10))}</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>"));
            package.AddRelationship(layoutName, PptxNames.SlideMasterRelationship, master.Name);
            string rid = package.AddRelationship(master.Name, PptxNames.SlideLayoutRelationship, layoutName);
            layoutIds.Append(CultureInfo.InvariantCulture, $"<p:sldLayoutId id=\"{2147483649L + i}\" r:id=\"{rid}\"/>");
        }

        PackagePart theme = package.AddXmlPart("/ppt/theme/theme1.xml", ThemeContentType, Theme());
        package.AddRelationship(master.Name, PptxNames.ThemeRelationship, theme.Name);
        package.AddRelationship(main.Name, PptxNames.ThemeRelationship, theme.Name);
        master.Xml!.Document.Add(Parse(
            $"<p:sldMaster {Namespaces}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>{Tree(MasterShapes())}</p:cSld>"
            + $"<p:clrMap {StandardColorMap}/><p:sldLayoutIdLst>{layoutIds}</p:sldLayoutIdLst>{TextStyles()}</p:sldMaster>").Root);

        package.AddRelationship(main.Name, PptxNames.Office + "presProps", package.AddXmlPart("/ppt/presProps.xml", Ct + "presProps+xml", Parse($"<p:presentationPr {Namespaces}/>")).Name);
        package.AddRelationship(main.Name, PptxNames.Office + "viewProps", package.AddXmlPart("/ppt/viewProps.xml", Ct + "viewProps+xml",
            Parse($"<p:viewPr {Namespaces}><p:normalViewPr><p:restoredLeft sz=\"15620\"/><p:restoredTop sz=\"94660\"/></p:normalViewPr><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>")).Name);
        package.AddRelationship(main.Name, PptxNames.Office + "tableStyles", package.AddXmlPart("/ppt/tableStyles.xml", "application/vnd.openxmlformats-officedocument.drawingml.tableStyles+xml",
            Parse("<a:tblStyleLst xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" def=\"{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}\"/>")).Name);
        return main.Xml!;
    }

    /// <summary>Adds a notes master, with its own copy of the theme, to a presentation that has none; returns its part.</summary>
    public static PackagePart AddNotesMaster(Presentation presentation)
    {
        PptxPackage package = presentation.Package;
        PackagePart theme = package.AddXmlPart(package.UniqueName("/ppt/theme/theme", ".xml"), ThemeContentType, Theme());
        string shapes =
            Placeholder(2, "Header Placeholder 1", "type=\"hdr\" sz=\"quarter\"", Box(0, 0, 2971800, 458788), "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"/>", "<a:lvl1pPr algn=\"l\"><a:defRPr sz=\"1200\"/></a:lvl1pPr>", null)
            + Placeholder(3, "Date Placeholder 2", "type=\"dt\" idx=\"1\"", Box(3884613, 0, 2971800, 458788), "<a:bodyPr/>", "<a:lvl1pPr algn=\"r\"><a:defRPr sz=\"1200\"/></a:lvl1pPr>", null)
            + "<p:sp><p:nvSpPr><p:cNvPr id=\"4\" name=\"Slide Image Placeholder 3\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldImg\" idx=\"2\"/></p:nvPr></p:nvSpPr>"
            + "<p:spPr><a:xfrm><a:off x=\"685800\" y=\"1143000\"/><a:ext cx=\"5486400\" cy=\"3086100\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom><a:noFill/><a:ln w=\"12700\"><a:solidFill><a:prstClr val=\"black\"/></a:solidFill></a:ln></p:spPr></p:sp>"
            + Placeholder(5, "Notes Placeholder 4", "type=\"body\" sz=\"quarter\" idx=\"3\"", Box(685800, 4400550, 5486400, 3600450), "<a:bodyPr/>", null, "Click to edit notes")
            + Placeholder(6, "Footer Placeholder 5", "type=\"ftr\" sz=\"quarter\" idx=\"4\"", Box(0, 8685213, 2971800, 458787), "<a:bodyPr anchor=\"b\"/>", "<a:lvl1pPr algn=\"l\"><a:defRPr sz=\"1200\"/></a:lvl1pPr>", null)
            + Placeholder(7, "Slide Number Placeholder 6", "type=\"sldNum\" sz=\"quarter\" idx=\"5\"", Box(3884613, 8685213, 2971800, 458787), "<a:bodyPr anchor=\"b\"/>", "<a:lvl1pPr algn=\"r\"><a:defRPr sz=\"1200\"/></a:lvl1pPr>", null, field: "slidenum");
        PackagePart master = package.AddXmlPart(package.UniqueName("/ppt/notesMasters/notesMaster", ".xml"), Ct + "notesMaster+xml", Parse(
            $"<p:notesMaster {Namespaces}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>{Tree(shapes)}</p:cSld><p:clrMap {StandardColorMap}/>"
            + $"<p:notesStyle>{Levels(i => "<a:defRPr sz=\"1200\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr>", 457200, 0, withDefault: false)}</p:notesStyle></p:notesMaster>"));
        package.AddRelationship(master.Name, PptxNames.ThemeRelationship, theme.Name);
        string rid = package.AddRelationship(presentation.Main.Part.Name, PptxNames.NotesMasterRelationship, master.Name);
        presentation.InsertInOrder(new XElement(PptxNames.P + "notesMasterIdLst", new XElement(PptxNames.P + "notesMasterId", new XAttribute(PptxNames.R + "id", rid))));
        return master;
    }

    /// <summary>The XML of a new notes page: the slide's picture and the notes text.</summary>
    public static XDocument NotesSlide(string text)
    {
        XDocument notes = Parse(
            $"<p:notes {Namespaces}><p:cSld>{Tree(
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Slide Image Placeholder 1\"/><p:cNvSpPr><a:spLocks noGrp=\"1\" noRot=\"1\" noChangeAspect=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"sldImg\"/></p:nvPr></p:nvSpPr><p:spPr/></p:sp>"
                + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Notes Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr><p:spPr/>"
                + "<p:txBody><a:bodyPr/><a:lstStyle/></p:txBody></p:sp>")}</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
        notes.Root!.Descendants(PptxNames.P + "txBody").Single().Add(TextBody.NewParagraphs(text, null, null));
        return notes;
    }

    /// <summary>The XML of a new, empty slide; the caller adds the layout's placeholders.</summary>
    public static XDocument Slide() => Parse($"<p:sld {Namespaces}><p:cSld>{Tree("")}</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sld>");

    public static XDocument Parse(string xml) => XDocument.Parse(xml, LoadOptions.PreserveWhitespace);

    private const string StandardColorMap = "bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"";

    private static string Tree(string shapes) =>
        "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>"
        + shapes + "</p:spTree>";

    private static string Box(long x, long y, long cx, long cy) =>
        string.Create(CultureInfo.InvariantCulture, $"<a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>");

    /// <summary>A placeholder shape: its ph attributes, its own position (empty to take the master's), body and list styles, and prompt text.</summary>
    private static string Placeholder(int id, string name, string ph, string xfrm, string? bodyPr, string? listStyle, string? prompt, string? field = null)
    {
        string paragraph = field is not null
            ? $"<a:p><a:fld id=\"{{{FieldId(field)}}}\" type=\"{field}\"><a:rPr lang=\"en-US\"/><a:t>‹#›</a:t></a:fld><a:endParaRPr lang=\"en-US\"/></a:p>"
            : prompt is null ? "<a:p><a:endParaRPr lang=\"en-US\"/></a:p>" : $"<a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{prompt}</a:t></a:r><a:endParaRPr lang=\"en-US\"/></a:p>";
        return string.Create(CultureInfo.InvariantCulture,
            $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph {ph}/></p:nvPr></p:nvSpPr><p:spPr>{xfrm}</p:spPr>"
            + $"<p:txBody>{bodyPr ?? "<a:bodyPr/>"}<a:lstStyle>{listStyle}</a:lstStyle>{paragraph}</p:txBody></p:sp>");
    }

    /// <summary>The date, footer and slide number placeholders of a layout or the master, with ids from <paramref name="index"/>.</summary>
    private static string Footers(int index, bool master = false)
    {
        string X(long x, long cx) => master ? Box(x, FooterTop, cx, FooterHeight) : "";
        string Style(string align) => master ? $"<a:lvl1pPr algn=\"{align}\"><a:defRPr sz=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"><a:tint val=\"75000\"/></a:schemeClr></a:solidFill></a:defRPr></a:lvl1pPr>" : "";
        string body = master ? "<a:bodyPr anchor=\"ctr\"/>" : "<a:bodyPr/>";
        return Placeholder(index + 90, "Date Placeholder", $"type=\"dt\" sz=\"half\" idx=\"{index}\"", X(Left, 2743200), body, Style("l"), null, field: "datetimeFigureOut")
            + Placeholder(index + 91, "Footer Placeholder", $"type=\"ftr\" sz=\"quarter\" idx=\"{index + 1}\"", X(4038600, 4114800), body, Style("ctr"), null)
            + Placeholder(index + 92, "Slide Number Placeholder", $"type=\"sldNum\" sz=\"quarter\" idx=\"{index + 2}\"", X(8991600, 2743200), body, Style("r"), null, field: "slidenum");
    }

    private static string MasterShapes()
    {
        string[] levels = ["Click to edit text", "Second level", "Third level", "Fourth level", "Fifth level"];
        StringBuilder body = new();
        for (int i = 0; i < levels.Length; i++)
        {
            body.Append(CultureInfo.InvariantCulture, $"<a:p><a:pPr lvl=\"{i}\"/><a:r><a:rPr lang=\"en-US\"/><a:t>{levels[i]}</a:t></a:r></a:p>");
        }

        return Placeholder(2, "Title Placeholder 1", "type=\"title\"", Box(Left, TitleTop, Width, TitleHeight), "<a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\" anchor=\"ctr\"><a:normAutofit/></a:bodyPr>", null, "Click to edit title")
            + "<p:sp><p:nvSpPr><p:cNvPr id=\"3\" name=\"Text Placeholder 2\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr><p:ph type=\"body\" idx=\"1\"/></p:nvPr></p:nvSpPr>"
            + $"<p:spPr>{Box(Left, BodyTop, Width, BodyHeight)}</p:spPr><p:txBody><a:bodyPr vert=\"horz\" lIns=\"91440\" tIns=\"45720\" rIns=\"91440\" bIns=\"45720\" rtlCol=\"0\"><a:normAutofit/></a:bodyPr><a:lstStyle/>{body}</p:txBody></p:sp>"
            + Footers(2, master: true);
    }

    private static string TextStyles()
    {
        int[] sizes = [2800, 2400, 2000, 1800, 1800, 1800, 1800, 1800, 1800];
        string body = Levels(i => $"<a:defRPr sz=\"{sizes[i]}\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr>",
            457200, 228600, withDefault: false, bullet: "<a:spcBef><a:spcPts val=\"1000\"/></a:spcBef><a:buFont typeface=\"Arial\" panose=\"020B0604020202020204\" pitchFamily=\"34\" charset=\"0\"/><a:buChar char=\"•\"/>");
        string other = Levels(i => "<a:defRPr sz=\"1800\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mn-lt\"/><a:ea typeface=\"+mn-ea\"/><a:cs typeface=\"+mn-cs\"/></a:defRPr>", 457200, 0, withDefault: true);
        return "<p:txStyles><p:titleStyle><a:lvl1pPr algn=\"l\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\"><a:lnSpc><a:spcPct val=\"90000\"/></a:lnSpc><a:spcBef><a:spcPct val=\"0\"/></a:spcBef><a:buNone/>"
            + "<a:defRPr sz=\"4400\" kern=\"1200\"><a:solidFill><a:schemeClr val=\"tx1\"/></a:solidFill><a:latin typeface=\"+mj-lt\"/><a:ea typeface=\"+mj-ea\"/><a:cs typeface=\"+mj-cs\"/></a:defRPr></a:lvl1pPr></p:titleStyle>"
            + $"<p:bodyStyle>{body}</p:bodyStyle><p:otherStyle>{other}</p:otherStyle></p:txStyles>";
    }

    /// <summary>Nine list levels (a:lvl1pPr to a:lvl9pPr), indented <paramref name="step"/> EMUs a level.</summary>
    private static string Levels(Func<int, string> run, long step, long hanging, bool withDefault, string bullet = "")
    {
        StringBuilder levels = new(withDefault ? "<a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr>" : "");
        for (int i = 0; i < 9; i++)
        {
            long margin = (i * step) + hanging;
            string indent = hanging == 0 ? "0" : (-hanging).ToString(CultureInfo.InvariantCulture);
            levels.Append(CultureInfo.InvariantCulture, $"<a:lvl{i + 1}pPr marL=\"{margin}\" indent=\"{indent}\" algn=\"l\" defTabSz=\"914400\" rtl=\"0\" eaLnBrk=\"1\" latinLnBrk=\"0\" hangingPunct=\"1\">{bullet}{run(i)}</a:lvl{i + 1}pPr>");
        }

        return levels.ToString();
    }

    private static XDocument Theme()
    {
        static string Color(string slot, string hex) => $"<a:{slot}><a:srgbClr val=\"{hex}\"/></a:{slot}>";
        static string Fill(string inner) => $"<a:solidFill><a:schemeClr val=\"phClr\">{inner}</a:schemeClr></a:solidFill>";
        static string Fonts(string latin) => $"<a:latin typeface=\"{latin}\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/>";
        return Parse(
            "<a:theme xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" name=\"Egap\"><a:themeElements><a:clrScheme name=\"Egap\">"
            + "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>"
            + Color("dk2", "1F2A44") + Color("lt2", "E9EBEF") + Color("accent1", "3366B0") + Color("accent2", "E2772E") + Color("accent3", "9B9B9B")
            + Color("accent4", "F0B323") + Color("accent5", "4C9BD6") + Color("accent6", "5EA34F") + Color("hlink", "1A62C4") + Color("folHlink", "7B4C93")
            + $"</a:clrScheme><a:fontScheme name=\"Egap\"><a:majorFont>{Fonts("Calibri Light")}</a:majorFont><a:minorFont>{Fonts("Calibri")}</a:minorFont></a:fontScheme>"
            + "<a:fmtScheme name=\"Egap\"><a:fillStyleLst>" + Fill("") + Fill("<a:tint val=\"70000\"/>") + Fill("<a:shade val=\"85000\"/>") + "</a:fillStyleLst>"
            + "<a:lnStyleLst>" + string.Concat(LineWidths.Select(w => $"<a:ln w=\"{w}\" cap=\"flat\" cmpd=\"sng\" algn=\"ctr\">{Fill("")}<a:prstDash val=\"solid\"/><a:miter lim=\"800000\"/></a:ln>")) + "</a:lnStyleLst>"
            + "<a:effectStyleLst><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle><a:effectStyle><a:effectLst/></a:effectStyle></a:effectStyleLst>"
            + "<a:bgFillStyleLst>" + Fill("") + Fill("<a:tint val=\"95000\"/>") + Fill("<a:shade val=\"90000\"/>") + "</a:bgFillStyleLst></a:fmtScheme>"
            + "</a:themeElements><a:objectDefaults/><a:extraClrSchemeLst/></a:theme>");
    }

    private static string FieldId(string field) => field == "slidenum" ? "6A3B9E21-4C0D-4F7A-9B8E-2D5C1F0A7E31" : "B2E5C8D4-7A1F-4E39-8C6B-0F3D9A2E5B17";
}
