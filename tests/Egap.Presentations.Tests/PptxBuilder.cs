using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Egap.Presentations.Tests;

/// <summary>
/// Builds small PowerPoint packages in code, from ECMA-376 Part 1's PresentationML schema: one master with a theme, its
/// layouts and the slides, with the parts PowerPoint writes alongside (presentation, view and table style properties). The
/// XML of each slide's shape tree is given by the test.
/// </summary>
internal sealed class PptxBuilder
{
    public const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private const string Ct = "application/vnd.openxmlformats-officedocument.presentationml.";

    /// <summary>The shape XML on each slide, inside its p:spTree after the group properties.</summary>
    public List<string> Slides { get; } = [];

    /// <summary>The layout each slide follows, by index into <see cref="Layouts"/>; slides past the list use layout 0.</summary>
    public List<int> SlideLayouts { get; } = [];

    /// <summary>The layouts: their type, name and shape XML.</summary>
    public List<(string Type, string Name, string Shapes)> Layouts { get; } =
    [
        ("title", "Title Slide", Placeholder(2, "Title 1", "ctrTitle", null, 1524000, 1122363, 9144000, 2387600)
            + Placeholder(3, "Subtitle 2", "subTitle", 1, 1524000, 3602038, 9144000, 1655762)),
        ("obj", "Title and Content", Placeholder(2, "Title 1", "title", null, null, null, null, null)
            + Placeholder(3, "Content Placeholder 2", null, 1, null, null, null, null)),
    ];

    /// <summary>The master's shapes.</summary>
    public string MasterShapes { get; set; } = Placeholder(2, "Title Placeholder 1", "title", null, 838200, 365125, 10515600, 1325563, "Click to edit Master title style")
        + Placeholder(3, "Text Placeholder 2", "body", 1, 838200, 1825625, 10515600, 4351338, "Click to edit Master text styles");

    /// <summary>The master's p:clrMap attributes.</summary>
    public string MasterColorMap { get; set; } = "bg1=\"lt1\" tx1=\"dk1\" bg2=\"lt2\" tx2=\"dk2\" accent1=\"accent1\" accent2=\"accent2\" accent3=\"accent3\" accent4=\"accent4\" accent5=\"accent5\" accent6=\"accent6\" hlink=\"hlink\" folHlink=\"folHlink\"";

    /// <summary>What follows each slide's p:cSld: a colour map override, extensions.</summary>
    public string SlideTail { get; set; } = "<p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr>";

    /// <summary>XML added at the end of the presentation part, such as an extension list.</summary>
    public string PresentationTail { get; set; } = "";

    /// <summary>Whether to write ISO/IEC 29500 Strict namespaces and relationship types.</summary>
    public bool Strict { get; set; }

    /// <summary>Whether the first slide has a notes page (and so the presentation a notes master).</summary>
    public bool Notes { get; set; }

    /// <summary>Whether the first slide relates an image as rId2, for pictures.</summary>
    public bool Image { get; set; }

    /// <summary>The main part's content type.</summary>
    public string MainContentType { get; set; } = Ct + "presentation.main+xml";

    /// <summary>Parts the model knows nothing of, related from the presentation part with a made-up type.</summary>
    public List<(string Name, string ContentType, byte[] Data)> UnknownParts { get; } = [];

    /// <summary>Attributes on each slide's root element (show="0").</summary>
    public string SlideAttributes { get; set; } = "";

    private string P => Strict ? "http://purl.oclc.org/ooxml/presentationml/main" : "http://schemas.openxmlformats.org/presentationml/2006/main";

    private string A => Strict ? "http://purl.oclc.org/ooxml/drawingml/main" : "http://schemas.openxmlformats.org/drawingml/2006/main";

    private string R => Strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships" : "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private string Ns => $"xmlns:a=\"{A}\" xmlns:r=\"{R}\" xmlns:p=\"{P}\"";

    /// <summary>A placeholder shape (p:sp with p:ph), with a position when given and text when given.</summary>
    public static string Placeholder(int id, string name, string? type, int? index, long? x, long? y, long? cx, long? cy, string? text = null)
    {
        string ph = "<p:ph" + (type is null ? "" : $" type=\"{type}\"") + (index is null ? "" : $" idx=\"{index}\"") + "/>";
        string xfrm = x is null ? "" : $"<a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>";
        string body = text is null ? "" : $"<p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{text}</a:t></a:r></a:p></p:txBody>";
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr><a:spLocks noGrp=\"1\"/></p:cNvSpPr><p:nvPr>{ph}</p:nvPr></p:nvSpPr><p:spPr>{xfrm}</p:spPr>{body}</p:sp>";
    }

    /// <summary>A rectangle with a preset geometry and one paragraph of text.</summary>
    public static string Rectangle(int id, string name, long x, long y, long cx, long cy, string text, string geometry = "rect", string extra = "")
    {
        return $"<p:sp><p:nvSpPr><p:cNvPr id=\"{id}\" name=\"{name}\"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr><a:xfrm><a:off x=\"{x}\" y=\"{y}\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm>"
            + $"<a:prstGeom prst=\"{geometry}\"><a:avLst/></a:prstGeom></p:spPr><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang=\"en-US\"/><a:t>{text}</a:t></a:r></a:p></p:txBody>{extra}</p:sp>";
    }

    /// <summary>The package's bytes.</summary>
    public byte[] Build()
    {
        if (Slides.Count == 0)
        {
            Slides.Add(Placeholder(2, "Title 1", "ctrTitle", null, null, null, null, null, "Hello"));
        }

        string rel = Strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships/" : "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
        Dictionary<string, (string ContentType, byte[] Data)> parts = [];
        StringBuilder overrides = new();

        void Xml(string name, string contentType, string xml)
        {
            parts[name] = (contentType, Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n" + xml));
            overrides.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"{name}\" ContentType=\"{contentType}\"/>");
        }

        void Rels(string source, params (string Id, string Type, string Target)[] list)
        {
            int slash = source.LastIndexOf('/');
            string name = source[..(slash + 1)] + "_rels/" + source[(slash + 1)..] + ".rels";
            StringBuilder xml = new("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            foreach ((string id, string type, string target) in list)
            {
                xml.Append(CultureInfo.InvariantCulture, $"<Relationship Id=\"{id}\" Type=\"{(type.StartsWith("http", StringComparison.Ordinal) ? type : rel + type)}\" Target=\"{target}\"/>");
            }

            parts[name] = ("application/vnd.openxmlformats-package.relationships+xml", Encoding.UTF8.GetBytes(xml.Append("</Relationships>").ToString()));
        }

        string Tree(string shapes) => "<p:spTree><p:nvGrpSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvGrpSpPr/><p:nvPr/></p:nvGrpSpPr><p:grpSpPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"0\" cy=\"0\"/><a:chOff x=\"0\" y=\"0\"/><a:chExt cx=\"0\" cy=\"0\"/></a:xfrm></p:grpSpPr>" + shapes + "</p:spTree>";

        // The package and the presentation.
        string packageRels = Strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument" : rel + "officeDocument";
        Rels("/", ("rId1", packageRels, "ppt/presentation.xml"), ("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml"),
            ("rId3", Strict ? "http://purl.oclc.org/ooxml/officeDocument/relationships/extendedProperties" : rel + "extended-properties", "docProps/app.xml"));
        List<(string, string, string)> presentationRels = [("rId1", "slideMaster", "slideMasters/slideMaster1.xml")];
        StringBuilder slideIds = new();
        for (int i = 0; i < Slides.Count; i++)
        {
            presentationRels.Add(($"rId{i + 2}", "slide", $"slides/slide{i + 1}.xml"));
            slideIds.Append(CultureInfo.InvariantCulture, $"<p:sldId id=\"{256 + i}\" r:id=\"rId{i + 2}\"/>");
        }

        int next = Slides.Count + 2;
        presentationRels.Add(($"rId{next++}", "presProps", "presProps.xml"));
        presentationRels.Add(($"rId{next++}", "viewProps", "viewProps.xml"));
        presentationRels.Add(($"rId{next++}", "theme", "theme/theme1.xml"));
        presentationRels.Add(($"rId{next++}", "tableStyles", "tableStyles.xml"));
        string notesMasterList = "";
        if (Notes)
        {
            presentationRels.Add(($"rId{next}", "notesMaster", "notesMasters/notesMaster1.xml"));
            notesMasterList = $"<p:notesMasterIdLst><p:notesMasterId r:id=\"rId{next++}\"/></p:notesMasterIdLst>";
        }

        foreach ((string name, string contentType, byte[] data) in UnknownParts)
        {
            presentationRels.Add(($"rId{next++}", "http://example.com/relationships/egap-test", ".." + name));
            parts[name] = (contentType, data);
            overrides.Append(CultureInfo.InvariantCulture, $"<Override PartName=\"{name}\" ContentType=\"{contentType}\"/>");
        }

        Rels("/ppt/presentation.xml", [.. presentationRels]);
        Xml("/ppt/presentation.xml", MainContentType,
            $"<p:presentation {Ns} saveSubsetFonts=\"1\"><p:sldMasterIdLst><p:sldMasterId id=\"2147483648\" r:id=\"rId1\"/></p:sldMasterIdLst>{notesMasterList}<p:sldIdLst>{slideIds}</p:sldIdLst>"
            + $"<p:sldSz cx=\"12192000\" cy=\"6858000\"/><p:notesSz cx=\"6858000\" cy=\"9144000\"/><p:defaultTextStyle><a:defPPr><a:defRPr lang=\"en-US\"/></a:defPPr></p:defaultTextStyle>{PresentationTail}</p:presentation>");
        Xml("/ppt/presProps.xml", Ct + "presProps+xml", $"<p:presentationPr {Ns}/>");
        Xml("/ppt/viewProps.xml", Ct + "viewProps+xml", $"<p:viewPr {Ns}><p:gridSpacing cx=\"76200\" cy=\"76200\"/></p:viewPr>");
        Xml("/ppt/tableStyles.xml", "application/vnd.openxmlformats-officedocument.drawingml.tableStyles+xml", $"<a:tblStyleLst xmlns:a=\"{A}\" def=\"{{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}}\"/>");
        Xml("/ppt/theme/theme1.xml", "application/vnd.openxmlformats-officedocument.theme+xml", Theme("Egap Test Theme"));

        // The master and its layouts.
        StringBuilder layoutIds = new();
        List<(string, string, string)> masterRels = [];
        for (int i = 0; i < Layouts.Count; i++)
        {
            masterRels.Add(($"rId{i + 1}", "slideLayout", $"../slideLayouts/slideLayout{i + 1}.xml"));
            layoutIds.Append(CultureInfo.InvariantCulture, $"<p:sldLayoutId id=\"{2147483649L + i}\" r:id=\"rId{i + 1}\"/>");
            (string type, string name, string shapes) = Layouts[i];
            Rels($"/ppt/slideLayouts/slideLayout{i + 1}.xml", ("rId1", "slideMaster", "../slideMasters/slideMaster1.xml"));
            Xml($"/ppt/slideLayouts/slideLayout{i + 1}.xml", Ct + "slideLayout+xml",
                $"<p:sldLayout {Ns} type=\"{type}\" preserve=\"1\"><p:cSld name=\"{name}\">{Tree(shapes)}</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:sldLayout>");
        }

        masterRels.Add(($"rId{Layouts.Count + 1}", "theme", "../theme/theme1.xml"));
        Rels("/ppt/slideMasters/slideMaster1.xml", [.. masterRels]);
        Xml("/ppt/slideMasters/slideMaster1.xml", Ct + "slideMaster+xml",
            $"<p:sldMaster {Ns}><p:cSld><p:bg><p:bgRef idx=\"1001\"><a:schemeClr val=\"bg1\"/></p:bgRef></p:bg>{Tree(MasterShapes)}</p:cSld><p:clrMap {MasterColorMap}/><p:sldLayoutIdLst>{layoutIds}</p:sldLayoutIdLst>"
            + "<p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz=\"4400\"/></a:lvl1pPr></p:titleStyle><p:bodyStyle><a:lvl1pPr><a:defRPr sz=\"2800\"/></a:lvl1pPr></p:bodyStyle><p:otherStyle><a:lvl1pPr><a:defRPr/></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>");

        // The slides.
        for (int i = 0; i < Slides.Count; i++)
        {
            int layout = i < SlideLayouts.Count ? SlideLayouts[i] : 0;
            List<(string, string, string)> slideRels = [("rId1", "slideLayout", $"../slideLayouts/slideLayout{layout + 1}.xml")];
            if (i == 0 && Image)
            {
                slideRels.Add(("rId2", "image", "../media/image1.png"));
            }

            if (i == 0 && Notes)
            {
                slideRels.Add(("rId3", "notesSlide", "../notesSlides/notesSlide1.xml"));
            }

            Rels($"/ppt/slides/slide{i + 1}.xml", [.. slideRels]);
            Xml($"/ppt/slides/slide{i + 1}.xml", Ct + "slide+xml", $"<p:sld {Ns}{SlideAttributes}><p:cSld>{Tree(Slides[i])}</p:cSld>{SlideTail}</p:sld>");
        }

        if (Image)
        {
            parts["/ppt/media/image1.png"] = ("image/png", Convert.FromBase64String(PngBase64));
        }

        if (Notes)
        {
            Rels("/ppt/notesMasters/notesMaster1.xml", ("rId1", "theme", "../theme/theme2.xml"));
            Xml("/ppt/notesMasters/notesMaster1.xml", Ct + "notesMaster+xml",
                $"<p:notesMaster {Ns}><p:cSld>{Tree("")}</p:cSld><p:clrMap {MasterColorMap}/></p:notesMaster>");
            Xml("/ppt/theme/theme2.xml", "application/vnd.openxmlformats-officedocument.theme+xml", Theme("Notes Theme"));
            Rels("/ppt/notesSlides/notesSlide1.xml", ("rId1", "notesMaster", "../notesMasters/notesMaster1.xml"), ("rId2", "slide", "../slides/slide1.xml"));
            Xml("/ppt/notesSlides/notesSlide1.xml", Ct + "notesSlide+xml",
                $"<p:notes {Ns}><p:cSld>{Tree(Placeholder(2, "Notes Placeholder 1", "body", 1, null, null, null, null, "Speaker notes"))}</p:cSld><p:clrMapOvr><a:masterClrMapping/></p:clrMapOvr></p:notes>");
        }

        Xml("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml",
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Egap test deck</dc:title></cp:coreProperties>");
        Xml("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml",
            $"<Properties xmlns=\"{(Strict ? "http://purl.oclc.org/ooxml/officeDocument/extendedProperties" : "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties")}\"><Application>Egap tests</Application></Properties>");

        string types = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\r\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
            + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>"
            + "<Default Extension=\"png\" ContentType=\"image/png\"/>" + overrides + "</Types>";
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "[Content_Types].xml", Encoding.UTF8.GetBytes(types));
            foreach ((string name, (_, byte[] data)) in parts)
            {
                Write(zip, name.TrimStart('/'), data);
            }
        }

        return output.ToArray();
    }

    /// <summary>The bytes of every entry of a ZIP package, by name, in order.</summary>
    public static List<(string Name, byte[] Data)> Entries(byte[] package)
    {
        using ZipArchive zip = new(new MemoryStream(package), ZipArchiveMode.Read);
        List<(string, byte[])> entries = [];
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            using Stream input = entry.Open();
            using MemoryStream data = new();
            input.CopyTo(data);
            entries.Add((entry.FullName, data.ToArray()));
        }

        return entries;
    }

    /// <summary>The text of one entry.</summary>
    public static string Entry(byte[] package, string name) => Encoding.UTF8.GetString(Entries(package).Single(e => e.Name == name).Data);

    /// <summary>A package with one entry replaced or added.</summary>
    public static byte[] WithEntry(byte[] package, string name, byte[]? data)
    {
        using MemoryStream output = new();
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            bool written = false;
            foreach ((string entry, byte[] bytes) in Entries(package))
            {
                if (entry == name)
                {
                    written = true;
                    if (data is not null)
                    {
                        Write(zip, entry, data);
                    }
                }
                else
                {
                    Write(zip, entry, bytes);
                }
            }

            if (!written && data is not null)
            {
                Write(zip, name, data);
            }
        }

        return output.ToArray();
    }

    private static void Write(ZipArchive zip, string name, byte[] data)
    {
        using Stream stream = zip.CreateEntry(name).Open();
        stream.Write(data);
    }

    private string Theme(string name)
    {
        static string Solid(int count) => string.Concat(Enumerable.Repeat("<a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill>", count));
        return $"<a:theme xmlns:a=\"{A}\" name=\"{name}\"><a:themeElements><a:clrScheme name=\"Egap\">"
            + "<a:dk1><a:sysClr val=\"windowText\" lastClr=\"000000\"/></a:dk1><a:lt1><a:sysClr val=\"window\" lastClr=\"FFFFFF\"/></a:lt1>"
            + "<a:dk2><a:srgbClr val=\"1F3864\"/></a:dk2><a:lt2><a:srgbClr val=\"E7E6E6\"/></a:lt2><a:accent1><a:srgbClr val=\"C00000\"/></a:accent1>"
            + "<a:accent2><a:srgbClr val=\"ED7D31\"/></a:accent2><a:accent3><a:srgbClr val=\"A5A5A5\"/></a:accent3><a:accent4><a:srgbClr val=\"FFC000\"/></a:accent4>"
            + "<a:accent5><a:srgbClr val=\"5B9BD5\"/></a:accent5><a:accent6><a:srgbClr val=\"70AD47\"/></a:accent6><a:hlink><a:srgbClr val=\"0563C1\"/></a:hlink>"
            + "<a:folHlink><a:srgbClr val=\"954F72\"/></a:folHlink></a:clrScheme>"
            + "<a:fontScheme name=\"Egap\"><a:majorFont><a:latin typeface=\"Georgia\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:majorFont>"
            + "<a:minorFont><a:latin typeface=\"Verdana\"/><a:ea typeface=\"\"/><a:cs typeface=\"\"/></a:minorFont></a:fontScheme>"
            + $"<a:fmtScheme name=\"Egap\"><a:fillStyleLst>{Solid(3)}</a:fillStyleLst><a:lnStyleLst>"
            + string.Concat(Enumerable.Repeat("<a:ln w=\"6350\"><a:solidFill><a:schemeClr val=\"phClr\"/></a:solidFill></a:ln>", 3))
            + "</a:lnStyleLst><a:effectStyleLst>" + string.Concat(Enumerable.Repeat("<a:effectStyle><a:effectLst/></a:effectStyle>", 3))
            + $"</a:effectStyleLst><a:bgFillStyleLst>{Solid(3)}</a:bgFillStyleLst></a:fmtScheme></a:themeElements></a:theme>";
    }
}
