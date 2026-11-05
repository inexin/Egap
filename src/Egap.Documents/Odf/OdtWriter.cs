using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using static Egap.Documents.Odf.OdfNames;

namespace Egap.Documents.Odf;

/// <summary>
/// Writes the model as OpenDocument Text (ODF 1.3 Part 3, with 1.2 and 1.4 as options), as LibreOffice Writer writes it:
/// common styles, list styles, page layouts and master pages in <c>styles.xml</c>; the body with its automatic styles in
/// <c>content.xml</c>; <c>meta.xml</c>, <c>settings.xml</c>, pictures under <c>Pictures/</c> and the manifest. Styles become
/// common styles and direct formatting automatic styles; sections become master pages (and column sections text sections).
/// What the ODT reader kept (see <see cref="OdtExtension"/>) is written back where it sat, with the package files it uses.
/// </summary>
internal sealed partial class OdtWriter
{
    private readonly Document document;
    private readonly OdtSaveOptions options;
    private readonly OdtKind kind;
    private readonly string version;
    private readonly WarningCollector warnings = new();
    private readonly OdfPackage? source;

    /// <summary>The package's files beyond the four XML files and the manifest: pictures and kept objects, by path.</summary>
    private readonly Dictionary<string, (byte[] Data, string MediaType)> files = new(StringComparer.Ordinal);

    /// <summary>Directories the manifest lists with a media type of their own (embedded objects).</summary>
    private readonly Dictionary<string, string> directories = new(StringComparer.Ordinal);

    private OdtWriter(Document document, OdtSaveOptions options, OdtKind kind)
    {
        this.document = document;
        this.options = options;
        this.kind = kind;
        version = options.Version switch
        {
            OdfVersion.Odf12 => "1.2",
            OdfVersion.Odf14 => "1.4",
            _ => "1.3",
        };
        source = options.KeepUnknownMarkup ? document.Extensions.Get<OdtExtension>(OdtExtension.Format)?.Package : null;
        scope = contentStyles;
    }

    private bool IsFlat => kind is OdtKind.FlatDocument or OdtKind.FlatTemplate;

    private bool IsTemplate => kind is OdtKind.Template or OdtKind.FlatTemplate;

    /// <summary>Whether ODF 1.3's additions (first-page headers, contextual spacing, comment initials) may be written.</summary>
    private bool Odf13 => options.Version != OdfVersion.Odf12;

    private string MediaType => IsTemplate ? TemplateMediaType : TextMediaType;

    public static IReadOnlyList<ConversionWarning> Write(Document document, Stream stream, OdtSaveOptions options, OdtKind kind)
    {
        OdtWriter writer = new(document, options, kind);
        writer.WriteDocument(stream);
        return writer.warnings.ToList();
    }

    private void WriteDocument(Stream stream)
    {
        // The body first: writing it finds the fonts, styles, lists and pictures the other parts list.
        PrepareStyles();
        WriteMasterPages();
        XElement text = WriteBody();
        XElement commonStyles = WriteCommonStyles();
        XElement meta = WriteMeta();
        XElement settings = WriteSettings();

        if (IsFlat)
        {
            XElement root = Root("document", meta, settings, FontFaces(), commonStyles,
                new XElement(OfficeNs + "automatic-styles", masterStyles.Elements, contentStyles.Elements),
                new XElement(OfficeNs + "master-styles", masterPages), new XElement(OfficeNs + "body", text));
            root.SetAttributeValue(OfficeNs + "mimetype", MediaType);
            InlinePackageFiles(root);
            Save(root, stream);
            return;
        }

        XElement content = Root("document-content", FontFaces(), new XElement(OfficeNs + "automatic-styles", contentStyles.Elements),
            new XElement(OfficeNs + "body", text));
        XElement styles = Root("document-styles", FontFaces(), commonStyles, new XElement(OfficeNs + "automatic-styles", masterStyles.Elements),
            new XElement(OfficeNs + "master-styles", masterPages));
        XElement metaRoot = Root("document-meta", meta);
        XElement settingsRoot = Root("document-settings", settings);

        if (string.IsNullOrEmpty(options.Password))
        {
            WritePackage(stream, content, styles, metaRoot, settingsRoot);
            return;
        }

        using MemoryStream plain = new();
        WritePackage(plain, content, styles, metaRoot, settingsRoot);
        OdfEncryption.Encrypt(plain.ToArray(), options.Password, options.Encryption == OdtEncryption.AesGcm ? OdfEncryptionMethod.AesGcmArgon2id : OdfEncryptionMethod.AesCbc, stream);
    }

    /// <summary>The elements that may hold their file as <c>office:binary-data</c> instead of a link (ODF 1.3 Part 3, 19.4.3).</summary>
    private static readonly HashSet<XName> BinaryHolders =
        [DrawNs + "image", DrawNs + "fill-image", DrawNs + "object-ole", StyleNs + "background-image", TextNs + "list-level-style-image"];

    /// <summary>
    /// A flat file has no package, so the files its elements link to go into the XML: pictures and OLE objects as base64
    /// (<c>office:binary-data</c>) and embedded documents (charts, formulas) as the <c>draw:object</c>'s own child (ODF 1.3
    /// Part 3, 10.4.6.2). An object whose document is not in the package is left out; its frame keeps its picture.
    /// </summary>
    private void InlinePackageFiles(XElement root)
    {
        foreach (XElement element in root.Descendants().Where(e => e.Attribute(XLinkNs + "href") is not null).ToList())
        {
            string href = element.Attr(XLinkNs, "href")!;
            if (href.Length == 0 || href[0] == '#' || href.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            string path = OdfPackage.NormalizePath(href);
            if (BinaryHolders.Contains(element.Name) && files.TryGetValue(path, out (byte[] Data, string) file))
            {
                RemoveLink(element);
                element.Add(new XElement(OfficeNs + "binary-data", Convert.ToBase64String(file.Data)));
            }
            else if (element.Name == DrawNs + "object" && EmbeddedDocument(path) is { } embedded)
            {
                RemoveLink(element);
                element.Add(embedded);
            }
            else if (element.Name == DrawNs + "object" || element.Name == DrawNs + "object-ole")
            {
                warnings.Add(ConversionWarningKind.DrawingDropped, "An embedded object whose document is not in the package was written as its picture.");
                element.Remove();
            }
        }

        static void RemoveLink(XElement element)
        {
            foreach (string name in (string[])["href", "type", "show", "actuate"])
            {
                element.Attribute(XLinkNs + name)?.Remove();
            }
        }
    }

    /// <summary>A sub-document of the package as one element: its content as an <c>office:document</c>, or a formula's MathML.</summary>
    private XElement? EmbeddedDocument(string path)
    {
        if (!files.TryGetValue(path + "/content.xml", out (byte[] Data, string) content) || ParseFile(content.Data) is not { } root)
        {
            return null;
        }

        if (root.Name != OfficeNs + "document-content")
        {
            return root;
        }

        XElement document = new(OfficeNs + "document", root.Attributes().Where(a => a.IsNamespaceDeclaration));
        document.Add(new XAttribute(OfficeNs + "version", version));
        document.Add(new XAttribute(OfficeNs + "mimetype", directories.GetValueOrDefault(path) ?? "application/vnd.oasis.opendocument.chart"));
        XElement? styles = files.TryGetValue(path + "/styles.xml", out (byte[] Data, string) part) ? ParseFile(part.Data) : null;
        document.Add(root.Element(OfficeNs + "font-face-decls"), styles?.Element(OfficeNs + "styles"), root.Element(OfficeNs + "automatic-styles"),
            root.Element(OfficeNs + "body"));
        return document;
    }

    private static XElement? ParseFile(byte[] data)
    {
        try
        {
            using XmlReader reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XElement.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private XElement Root(string name, params object?[] content)
    {
        XElement root = new(OfficeNs + name,
            new XAttribute(XNamespace.Xmlns + "office", OfficeNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "style", StyleNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "text", TextNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "table", TableNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "draw", DrawNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "fo", FoNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "xlink", XLinkNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", DcNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "meta", MetaNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "number", NumberNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "svg", SvgNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "config", ConfigNs.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "loext", LoExtNs.NamespaceName),
            new XAttribute(OfficeNs + "version", version));
        root.Add(content);
        return root;
    }

    private void WritePackage(Stream stream, XElement content, XElement styles, XElement meta, XElement settings)
    {
        using ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true);

        // The mimetype comes first, stored, so the file's first bytes name its type (ODF 1.3 Part 2, 3.3).
        ZipArchiveEntry mimetype = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);
        using (Stream entry = mimetype.Open())
        {
            entry.Write(Encoding.ASCII.GetBytes(MediaType));
        }

        Entry(zip, "content.xml", content);
        Entry(zip, "styles.xml", styles);
        Entry(zip, "meta.xml", meta);
        Entry(zip, "settings.xml", settings);
        foreach ((string path, (byte[] data, string _)) in files)
        {
            ZipArchiveEntry entry = zip.CreateEntry(path, IsCompressed(path) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            using Stream output = entry.Open();
            output.Write(data);
        }

        Entry(zip, "META-INF/manifest.xml", Manifest());
    }

    // Pictures that are compressed already gain nothing from deflate.
    private static bool IsCompressed(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp";
    }

    private static void Entry(ZipArchive zip, string path, XElement root)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        Save(root, output);
    }

    private static void Save(XElement root, Stream stream)
    {
        XmlWriterSettings settings = new() { Encoding = new UTF8Encoding(false), Indent = false, CloseOutput = false };
        using XmlWriter writer = XmlWriter.Create(stream, settings);
        new XDocument(new XDeclaration("1.0", "UTF-8", null), root).Save(writer);
    }

    private XElement Manifest()
    {
        XNamespace m = ManifestNs;
        XElement manifest = new(m + "manifest", new XAttribute(XNamespace.Xmlns + "manifest", m.NamespaceName), new XAttribute(m + "version", version),
            new XElement(m + "file-entry", new XAttribute(m + "full-path", "/"), new XAttribute(m + "version", version), new XAttribute(m + "media-type", MediaType)));
        foreach ((string path, string mediaType) in directories)
        {
            manifest.Add(new XElement(m + "file-entry", new XAttribute(m + "full-path", path + "/"), new XAttribute(m + "media-type", mediaType)));
        }

        foreach (string name in new[] { "content.xml", "styles.xml", "meta.xml", "settings.xml" })
        {
            manifest.Add(new XElement(m + "file-entry", new XAttribute(m + "full-path", name), new XAttribute(m + "media-type", "text/xml")));
        }

        foreach ((string path, (byte[] _, string mediaType)) in files)
        {
            manifest.Add(new XElement(m + "file-entry", new XAttribute(m + "full-path", path), new XAttribute(m + "media-type", mediaType)));
        }

        return manifest;
    }

    // ---- Meta and settings ----

    private XElement WriteMeta()
    {
        DocumentProperties p = document.Properties;
        XElement meta = new(OfficeNs + "meta", new XElement(MetaNs + "generator", Generator));
        Add(meta, DcNs + "title", p.Title);
        Add(meta, DcNs + "subject", p.Subject);
        Add(meta, DcNs + "description", p.Comments);
        Add(meta, MetaNs + "initial-creator", p.Author);
        Add(meta, DcNs + "creator", p.LastSavedBy);
        Add(meta, MetaNs + "creation-date", Date(p.Created));
        Add(meta, DcNs + "date", Date(p.Saved));
        Add(meta, MetaNs + "print-date", Date(p.Printed));
        if (p.Keywords is { Length: > 0 } keywords)
        {
            foreach (string keyword in keywords.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                meta.Add(new XElement(MetaNs + "keyword", Clean(keyword)));
            }
        }

        foreach (string name in p.CustomNames)
        {
            if (p.GetCustom(name) is { } value)
            {
                meta.Add(new XElement(MetaNs + "user-defined", new XAttribute(MetaNs + "name", Clean(name)), Clean(value)));
            }
        }

        return meta;
    }

    private static readonly string Generator = "Egap.Documents/" + (typeof(OdtWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0");

    private static void Add(XElement parent, XName name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            parent.Add(new XElement(name, Clean(value)));
        }
    }

    /// <summary>A date as ODF writes it (xsd:dateTime, in UTC with no zone, as LibreOffice reads it).</summary>
    private static string? Date(DateTimeOffset? date)
    {
        return date?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// settings.xml: LibreOffice's settings kept from the file read (with the ones the writer depends on set), the editing
    /// restriction as LibreOffice gives it, and Egap's own record of Word's restriction and password hash.
    /// </summary>
    private XElement WriteSettings()
    {
        XElement settings = source?.Settings?.Element(OfficeNs + "settings") is { } kept ? new XElement(kept) : new XElement(OfficeNs + "settings");
        settings.Elements(ConfigNs + "config-item-set").Where(s => s.Attr(ConfigNs, "name") == ProtectionSet).Remove();
        XElement? configuration = settings.Elements(ConfigNs + "config-item-set").FirstOrDefault(s => s.Attr(ConfigNs, "name") == "ooo:configuration-settings");
        if (configuration is null)
        {
            configuration = new XElement(ConfigNs + "config-item-set", new XAttribute(ConfigNs + "name", "ooo:configuration-settings"));
            settings.Add(configuration);
        }

        // Tab positions are written from the paragraph's indent, as ODF describes them.
        SetItem(configuration, "TabsRelativeToIndent", "boolean", "true");
        DocumentProtection protection = document.Protection;
        SetItem(configuration, "LoadReadonly", "boolean", protection.IsEnforced && protection.Kind == ProtectionKind.ReadOnly ? "true" : "false");
        SetItem(configuration, "ProtectForm", "boolean", protection.IsEnforced && protection.Kind == ProtectionKind.Forms ? "true" : "false");
        if (protection.Kind != ProtectionKind.None)
        {
            settings.Add(ProtectionSettings(protection));
        }

        return settings;
    }

    private static void SetItem(XElement set, string name, string type, string value)
    {
        XElement? item = set.Elements(ConfigNs + "config-item").FirstOrDefault(i => i.Attr(ConfigNs, "name") == name);
        if (item is null)
        {
            set.Add(new XElement(ConfigNs + "config-item", new XAttribute(ConfigNs + "name", name), new XAttribute(ConfigNs + "type", type), value));
        }
        else
        {
            item.SetAttributeValue(ConfigNs + "type", type);
            item.Value = value;
        }
    }

    /// <summary>The settings Egap keeps Word's editing restriction in, which ODF and LibreOffice have no place for.</summary>
    internal const string ProtectionSet = "egap:document-protection";

    private static XElement ProtectionSettings(DocumentProtection protection)
    {
        XElement set = new(ConfigNs + "config-item-set", new XAttribute(ConfigNs + "name", ProtectionSet));
        void Item(string name, string type, string value) => set.Add(new XElement(ConfigNs + "config-item",
            new XAttribute(ConfigNs + "name", name), new XAttribute(ConfigNs + "type", type), value));
        Item("Kind", "string", protection.Kind.ToString());
        Item("Enforced", "boolean", protection.IsEnforced ? "true" : "false");
        if (protection.HasPassword && protection.HashAlgorithm is { } algorithm)
        {
            Item("HashAlgorithm", "string", algorithm);
            Item("Hash", "base64Binary", Convert.ToBase64String(protection.Hash.Span));
            Item("Salt", "base64Binary", Convert.ToBase64String(protection.Salt.Span));
            Item("SpinCount", "int", protection.SpinCount.ToString(CultureInfo.InvariantCulture));
        }

        return set;
    }

    // ---- Values ----

    /// <summary>A length in points as ODF writes it: "12.5pt".</summary>
    private static string Pt(double points)
    {
        return (double.IsFinite(points) ? Math.Round(points, 3) : 0).ToString("0.###", CultureInfo.InvariantCulture) + "pt";
    }

    private static string Percent(double percent)
    {
        return (double.IsFinite(percent) ? Math.Round(percent, 3) : 100).ToString("0.###", CultureInfo.InvariantCulture) + "%";
    }

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Hex(Egami.Pixels.Rgba8 rgb) => string.Create(CultureInfo.InvariantCulture, $"#{rgb.R:x2}{rgb.G:x2}{rgb.B:x2}");

    /// <summary>A colour as #rrggbb, theme colours resolved through the document's theme; null for automatic.</summary>
    private string? Color(DocumentColor color)
    {
        return color.Kind == DocumentColorKind.Auto ? null : Hex(color.Resolve(document.Theme, new Egami.Pixels.Rgba8(0, 0, 0, 255)));
    }

    /// <summary>Text with the characters XML cannot hold left out, and half a surrogate pair as U+FFFD.</summary>
    private static string Clean(string text)
    {
        int i = 0;
        while (i < text.Length && IsXmlChar(text, i))
        {
            i += char.IsHighSurrogate(text[i]) ? 2 : 1;
        }

        if (i >= text.Length)
        {
            return text;
        }

        StringBuilder clean = new(text.Length);
        clean.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                clean.Append(c).Append(text[++i]);
            }
            else if (char.IsSurrogate(c))
            {
                clean.Append('�');
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                clean.Append(c);
            }
        }

        return clean.ToString();
    }

    private static bool IsXmlChar(string text, int i)
    {
        char c = text[i];
        if (char.IsHighSurrogate(c))
        {
            return i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
        }

        return !char.IsSurrogate(c) && XmlConvert.IsXmlChar(c);
    }
}
