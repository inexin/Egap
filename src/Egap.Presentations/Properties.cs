using System.Globalization;
using System.Xml.Linq;

using Egap.Documents.Docx;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// A presentation's properties: the core ones every Office file has (title, author, dates; <c>docProps/core.xml</c>), two of
/// the application's (<c>docProps/app.xml</c>), and custom ones by name (<c>docProps/custom.xml</c>). They are read from the
/// file each time and written into it when set, so everything else in those parts stays as it was. The names follow
/// <see cref="Egap.Documents.DocumentProperties"/>.
/// </summary>
public sealed class PresentationProperties
{
    private const string CoreType = "application/vnd.openxmlformats-package.core-properties+xml";
    private const string AppType = "application/vnd.openxmlformats-officedocument.extended-properties+xml";
    private const string CustomType = "application/vnd.openxmlformats-officedocument.custom-properties+xml";
    private const string CustomFormat = "{D5CDD505-2E9C-101B-9397-08002B2CF9AE}";
    private static readonly XNamespace Cp = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DcTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace App = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    private static readonly XNamespace Custom = "http://schemas.openxmlformats.org/officeDocument/2006/custom-properties";
    private static readonly XNamespace Vt = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";

    private readonly PptxPackage package;

    internal PresentationProperties(PptxPackage package)
    {
        this.package = package;
    }

    /// <summary>The title (<c>dc:title</c>).</summary>
    public string? Title { get => CoreText(Dc + "title"); set => SetCoreText(Dc + "title", value); }

    /// <summary>The subject (<c>dc:subject</c>).</summary>
    public string? Subject { get => CoreText(Dc + "subject"); set => SetCoreText(Dc + "subject", value); }

    /// <summary>The author (<c>dc:creator</c>).</summary>
    public string? Author { get => CoreText(Dc + "creator"); set => SetCoreText(Dc + "creator", value); }

    /// <summary>The keywords (<c>cp:keywords</c>).</summary>
    public string? Keywords { get => CoreText(Cp + "keywords"); set => SetCoreText(Cp + "keywords", value); }

    /// <summary>The comments (<c>dc:description</c>).</summary>
    public string? Comments { get => CoreText(Dc + "description"); set => SetCoreText(Dc + "description", value); }

    /// <summary>The category (<c>cp:category</c>).</summary>
    public string? Category { get => CoreText(Cp + "category"); set => SetCoreText(Cp + "category", value); }

    /// <summary>Who saved it last (<c>cp:lastModifiedBy</c>).</summary>
    public string? LastSavedBy { get => CoreText(Cp + "lastModifiedBy"); set => SetCoreText(Cp + "lastModifiedBy", value); }

    /// <summary>When it was made (<c>dcterms:created</c>).</summary>
    public DateTimeOffset? Created { get => CoreDate(DcTerms + "created"); set => SetCoreDate(DcTerms + "created", value); }

    /// <summary>When it was saved last (<c>dcterms:modified</c>); Egap does not set it on a save.</summary>
    public DateTimeOffset? Saved { get => CoreDate(DcTerms + "modified"); set => SetCoreDate(DcTerms + "modified", value); }

    /// <summary>When it was printed last (<c>cp:lastPrinted</c>).</summary>
    public DateTimeOffset? Printed { get => CoreDate(Cp + "lastPrinted"); set => SetCoreDate(Cp + "lastPrinted", value); }

    /// <summary>The company (<c>Company</c> in the application properties).</summary>
    public string? Company { get => AppText("Company"); set => SetAppText("Company", value); }

    /// <summary>The manager (<c>Manager</c> in the application properties).</summary>
    public string? Manager { get => AppText("Manager"); set => SetAppText("Manager", value); }

    /// <summary>The application that wrote the file (<c>Application</c>), such as <c>Microsoft Office PowerPoint</c>.</summary>
    public string? Application => AppText("Application");

    /// <summary>The custom properties' names, in the file's order.</summary>
    public IReadOnlyCollection<string> CustomNames
    {
        get
        {
            List<string> names = [];
            foreach (XElement property in CustomRoot(create: false)?.Elements(Custom + "property") ?? [])
            {
                if ((string?)property.Attribute("name") is { Length: > 0 } name && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }

            return names;
        }
    }

    /// <summary>A custom property's value as text, or null when there is none by that name (names ignore case).</summary>
    /// <exception cref="ArgumentException">The name is null or empty.</exception>
    public string? GetCustom(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return FindCustom(CustomRoot(create: false), name)?.Elements().FirstOrDefault()?.Value;
    }

    /// <summary>Sets a custom property as text (<c>vt:lpwstr</c>), or removes it with null.</summary>
    /// <exception cref="ArgumentException">The name is null or empty.</exception>
    public void SetCustom(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (value is null)
        {
            if (CustomRoot(create: false) is { } existing && FindCustom(existing, name) is { } gone)
            {
                gone.Remove();
                Mark(existing);
            }

            return;
        }

        XElement root = CustomRoot(create: true)!;
        XElement? property = FindCustom(root, name);
        if (property?.Elements().FirstOrDefault() is { Name.LocalName: "lpwstr" } text && text.Value == value)
        {
            return;
        }

        if (property is null)
        {
            int pid = 2;
            foreach (XElement other in root.Elements(Custom + "property"))
            {
                pid = Math.Max(pid, (int)(PptxNames.Long(other, "pid") ?? 1) + 1);
            }

            property = new XElement(Custom + "property", new XAttribute("fmtid", CustomFormat), new XAttribute("pid", pid.ToString(CultureInfo.InvariantCulture)), new XAttribute("name", name));
            root.Add(property);
        }

        property.RemoveNodes();
        property.Add(new XElement(Vt + "lpwstr", value));
        Mark(root);
    }

    private string? CoreText(XName name)
    {
        return CoreRoot(create: false)?.Element(name) is { } element && element.Value.Length > 0 ? element.Value : null;
    }

    private void SetCoreText(XName name, string? value)
    {
        if (CoreText(name) == value)
        {
            return;
        }

        XElement root = CoreRoot(create: true)!;
        XElement? element = root.Element(name);
        if (value is null)
        {
            element?.Remove();
        }
        else if (element is null)
        {
            root.Add(new XElement(name, value));
        }
        else
        {
            element.Value = value;
        }

        Mark(root);
    }

    private DateTimeOffset? CoreDate(XName name)
    {
        return DateTimeOffset.TryParse(CoreText(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date) ? date : null;
    }

    private void SetCoreDate(XName name, DateTimeOffset? value)
    {
        string? text = value?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (CoreText(name) == text)
        {
            return;
        }

        SetCoreText(name, text);
        if (text is not null && name.Namespace == DcTerms && CoreRoot(create: false)?.Element(name) is { } element)
        {
            element.SetAttributeValue(XNamespace.Xmlns + "xsi", Xsi.NamespaceName);
            element.SetAttributeValue(Xsi + "type", "dcterms:W3CDTF");
            if (element.Document?.Root?.GetPrefixOfNamespace(DcTerms) is null)
            {
                element.SetAttributeValue(XNamespace.Xmlns + "dcterms", DcTerms.NamespaceName);
            }
        }
    }

    private string? AppText(string name)
    {
        return Root(OpcRelationshipTypes.ExtendedProperties, create: false)?.Element(App + name) is { } element && element.Value.Length > 0 ? element.Value : null;
    }

    private void SetAppText(string name, string? value)
    {
        if (AppText(name) == value)
        {
            return;
        }

        XElement root = Root(OpcRelationshipTypes.ExtendedProperties, create: true)!;
        XElement? element = root.Element(App + name);
        if (value is null)
        {
            element?.Remove();
        }
        else if (element is null)
        {
            root.Add(new XElement(App + name, value));
        }
        else
        {
            element.Value = value;
        }

        Mark(root);
    }

    private XElement? CoreRoot(bool create) => Root(OpcRelationshipTypes.CoreProperties, create);

    private XElement? CustomRoot(bool create) => Root(OpcRelationshipTypes.CustomProperties, create);

    private static XElement? FindCustom(XElement? root, string name)
    {
        return root?.Elements(Custom + "property").FirstOrDefault(p => string.Equals((string?)p.Attribute("name"), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The root of the properties part the package relates to with <paramref name="type"/>, made when asked and missing.</summary>
    private XElement? Root(string type, bool create)
    {
        if (package.Related("/", type) is { } part)
        {
            return package.Xml(part).Root;
        }

        if (!create)
        {
            return null;
        }

        (string name, string contentType, XElement root) = type switch
        {
            OpcRelationshipTypes.CoreProperties => ("/docProps/core.xml", CoreType, new XElement(Cp + "coreProperties",
                new XAttribute(XNamespace.Xmlns + "cp", Cp.NamespaceName), new XAttribute(XNamespace.Xmlns + "dc", Dc.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "dcterms", DcTerms.NamespaceName), new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName))),
            OpcRelationshipTypes.ExtendedProperties => ("/docProps/app.xml", AppType, new XElement(App + "Properties",
                new XAttribute(XNamespace.Xmlns + "vt", Vt.NamespaceName))),
            _ => ("/docProps/custom.xml", CustomType, new XElement(Custom + "Properties", new XAttribute(XNamespace.Xmlns + "vt", Vt.NamespaceName))),
        };
        if (package.GetPart(name) is not null)
        {
            name = package.UniqueName(name[..^4], ".xml");
        }

        PackagePart made = package.AddXmlPart(name, contentType, new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), root));
        package.AddRelationship("/", type, made.Name);
        return root;
    }

    private void Mark(XElement root)
    {
        foreach (PackagePart part in package.Parts)
        {
            if (part.Xml?.Root == root)
            {
                part.Xml.MarkChanged();
                return;
            }
        }
    }
}

/// <summary>How the slide show runs (<c>p:showPr</c> in <c>ppt/presProps.xml</c>): its type, whether it loops, and whether it keeps to the slides' timings.</summary>
public sealed class SlideShowSettings
{
    private readonly Presentation presentation;

    internal SlideShowSettings(Presentation presentation)
    {
        this.presentation = presentation;
    }

    /// <summary>Who runs the show: a speaker (the default), a reader in a window, or a kiosk.</summary>
    public SlideShowType Type
    {
        get
        {
            XElement? show = Show(create: false);
            return show?.Element(PptxNames.P + "kiosk") is not null ? SlideShowType.Kiosk
                : show?.Element(PptxNames.P + "browse") is not null ? SlideShowType.Browsed
                : SlideShowType.Presented;
        }

        set
        {
            if (value == Type)
            {
                return;
            }

            XElement show = Show(create: true)!;
            show.Elements().Where(e => e.Name == PptxNames.P + "present" || e.Name == PptxNames.P + "browse" || e.Name == PptxNames.P + "kiosk").Remove();
            show.AddFirst(new XElement(PptxNames.P + value switch
            {
                SlideShowType.Kiosk => "kiosk",
                SlideShowType.Browsed => "browse",
                _ => "present",
            }));

            // A kiosk loops by itself; PowerPoint writes the flag with it.
            if (value == SlideShowType.Kiosk)
            {
                show.SetAttributeValue("loop", "1");
            }

            Changed(show);
        }
    }

    /// <summary>Whether the show starts again after the last slide until Esc (<c>loop</c>); always on in a kiosk.</summary>
    public bool Loop
    {
        get => PptxNames.Bool(Show(create: false), "loop");
        set => SetFlag("loop", value, fallback: false);
    }

    /// <summary>Whether the show moves on by the slides' timings (<c>useTimings</c>); true unless the file says otherwise.</summary>
    public bool UseTimings
    {
        get => PptxNames.Bool(Show(create: false), "useTimings", fallback: true);
        set => SetFlag("useTimings", value, fallback: true);
    }

    /// <summary>Whether the show plays recorded narration (<c>showNarration</c>).</summary>
    public bool ShowNarration
    {
        get => PptxNames.Bool(Show(create: false), "showNarration");
        set => SetFlag("showNarration", value, fallback: false);
    }

    /// <summary>Whether the show plays without animations (<c>showAnimation="0"</c>); false unless the file says so.</summary>
    public bool WithoutAnimations
    {
        get => !PptxNames.Bool(Show(create: false), "showAnimation", fallback: true);
        set => SetFlag("showAnimation", !value, fallback: true);
    }

    private void SetFlag(string name, bool value, bool fallback)
    {
        XElement? show = Show(create: false);
        if (PptxNames.Bool(show, name, fallback) == value)
        {
            return;
        }

        show ??= Show(create: true)!;
        show.SetAttributeValue(name, value == fallback ? null : value ? "1" : "0");
        Changed(show);
    }

    private void Changed(XElement show)
    {
        foreach (PackagePart part in presentation.Package.Parts)
        {
            if (part.Xml?.Root == show.Document?.Root)
            {
                part.Xml!.MarkChanged();
                return;
            }
        }
    }

    /// <summary>The <c>p:showPr</c> of the presentation's properties part, made (with the part) when asked and missing.</summary>
    private XElement? Show(bool create)
    {
        PptxPackage package = presentation.Package;
        string main = presentation.Main.Part.Name;
        XElement? root;
        if (package.Related(main, PptxNames.Office + "presProps") is { } part)
        {
            root = package.Xml(part).Root;
        }
        else if (create)
        {
            PackagePart made = package.AddXmlPart(package.GetPart("/ppt/presProps.xml") is null ? "/ppt/presProps.xml" : package.UniqueName("/ppt/presProps", ".xml"),
                DefaultTemplate.Ct + "presProps+xml", new XDocument(new XElement(PptxNames.P + "presentationPr",
                    new XAttribute(XNamespace.Xmlns + "a", PptxNames.A.NamespaceName), new XAttribute(XNamespace.Xmlns + "r", PptxNames.R.NamespaceName),
                    new XAttribute(XNamespace.Xmlns + "p", PptxNames.P.NamespaceName))));
            package.AddRelationship(main, PptxNames.Office + "presProps", made.Name);
            root = made.Xml!.Root;
        }
        else
        {
            return null;
        }

        if (root.Element(PptxNames.P + "showPr") is { } show || !create)
        {
            return root.Element(PptxNames.P + "showPr");
        }

        show = new XElement(PptxNames.P + "showPr");
        XElement? before = root.Elements().FirstOrDefault(e => e.Name == PptxNames.P + "clrMru" || e.Name == PptxNames.P + "extLst");
        if (before is not null)
        {
            before.AddBeforeSelf(show);
        }
        else if (root.Elements().LastOrDefault(e => e.Name == PptxNames.P + "htmlPubPr" || e.Name == PptxNames.P + "webPr" || e.Name == PptxNames.P + "prnPr") is { } after)
        {
            after.AddAfterSelf(show);
        }
        else
        {
            root.AddFirst(show);
        }

        return show;
    }
}

/// <summary>Who runs a slide show.</summary>
public enum SlideShowType
{
    /// <summary>A speaker, full screen (<c>p:present</c>), the default.</summary>
    Presented,

    /// <summary>A reader, in a window (<c>p:browse</c>).</summary>
    Browsed,

    /// <summary>Nobody: a kiosk, full screen, looping (<c>p:kiosk</c>).</summary>
    Kiosk,
}
