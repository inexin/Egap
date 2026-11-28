using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;
using Egap.Documents.Office;
using Egap.Presentations.Pptx;

namespace Egap.Presentations;

/// <summary>
/// A PowerPoint presentation: its slides, the slide layouts they follow and the slide masters those belong to. A loaded
/// presentation keeps its file whole, so a save writes back every part and every element the model does not hold, as it was,
/// and only the parts whose content the model changed are written again.
/// </summary>
public sealed partial class Presentation
{
    private const string MainContentType = "application/vnd.openxmlformats-officedocument.presentationml.";
    private const string MacroContentType = "application/vnd.ms-powerpoint.";

    private readonly XmlPart main;
    private readonly List<SlideMaster> masters = [];
    private NotesMaster? notesMaster;

    private Presentation(PptxPackage package, XmlPart main)
    {
        Package = package;
        this.main = main;
        Format = FormatOf(main.Part.ContentType);
        Slides = new SlideCollection(this);
        Sections = new SlideSectionCollection(this);
        CommentAuthors = new CommentAuthorCollection(this);
        Properties = new PresentationProperties(package);
        SlideShow = new SlideShowSettings(this);
    }

    internal PptxPackage Package { get; }

    /// <summary>Whether the file was written in ISO/IEC 29500 Strict namespaces; a save keeps them.</summary>
    internal bool IsStrict => Package.IsStrict;

    /// <summary>
    /// The kind of file a save to a stream writes: the kind it was loaded from until set. Saving as another kind changes the
    /// main part's content type; saving a deck with macros as a kind without them leaves its VBA project out of the file
    /// (the presentation keeps it, for a later save with macros).
    /// </summary>
    public PresentationFormat Format { get; set; }

    /// <summary>The title, author, dates and custom properties of the file.</summary>
    public PresentationProperties Properties { get; }

    /// <summary>How the slide show runs: speaker, window or kiosk, looping or not.</summary>
    public SlideShowSettings SlideShow { get; }

    /// <summary>
    /// Whether the file has a password to modify it (<c>p:modifyVerifier</c>): PowerPoint opens it read-only without the
    /// password. It is not encryption; anyone can read the file, and other programs may ignore it.
    /// </summary>
    public bool HasModifyPassword => main.Root.Element(PptxNames.P + "modifyVerifier") is not null;

    /// <summary>
    /// The size of every slide, in EMUs with points alongside (<c>p:sldSz</c>). Setting it changes the size the slides are
    /// shown and printed at; their shapes keep their positions.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A side is shorter than 1 inch (914,400 EMUs) or longer than 56 inches (51,206,400 EMUs), as PowerPoint allows.</exception>
    public SlideSize SlideSize
    {
        get => ReadSize(main.Root.Element(PptxNames.P + "sldSz")) ?? new SlideSize(12_192_000, 6_858_000);
        set
        {
            if (value.Width is < 914_400 or > 51_206_400 || value.Height is < 914_400 or > 51_206_400)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Each side of a slide must be from 914,400 to 51,206,400 EMUs (1 to 56 inches).");
            }

            if (value == SlideSize)
            {
                return;
            }

            XElement? size = main.Root.Element(PptxNames.P + "sldSz");
            if (size is null)
            {
                size = new XElement(PptxNames.P + "sldSz");
                XElement? before = main.Root.Element(PptxNames.P + "notesSz");
                if (before is not null)
                {
                    before.AddBeforeSelf(size);
                }
                else
                {
                    main.Root.Add(size);
                }
            }

            size.SetAttributeValue("cx", PptxNames.Text(value.Width));
            size.SetAttributeValue("cy", PptxNames.Text(value.Height));

            // The named size (screen4x3, A4 and the rest) no longer holds.
            size.Attribute("type")?.Remove();
            main.MarkChanged();
        }
    }

    /// <summary>The size of the notes pages, in EMUs with points alongside (<c>p:notesSz</c>).</summary>
    public SlideSize NotesSize => ReadSize(main.Root.Element(PptxNames.P + "notesSz")) ?? new SlideSize(6_858_000, 9_144_000);

    /// <summary>The slide masters, in the order the presentation lists them, each with its layouts and theme.</summary>
    public IReadOnlyList<SlideMaster> SlideMasters => masters;

    /// <summary>The slides, in the order they are shown.</summary>
    public SlideCollection Slides { get; }

    /// <summary>
    /// The name of the notes master's part, such as <c>/ppt/notesMasters/notesMaster1.xml</c>, or null when the presentation
    /// has none. Setting a slide's notes makes one when there is none.
    /// </summary>
    public string? NotesMasterPartName => NotesMasterPart?.Name;

    /// <summary>The notes master, which lays out every notes page, or null when the presentation has none.</summary>
    public NotesMaster? NotesMaster
    {
        get
        {
            PackagePart? part = NotesMasterPart;
            if (part is null)
            {
                return notesMaster = null;
            }

            return notesMaster is not null && notesMaster.Xml.Part == part ? notesMaster : notesMaster = new NotesMaster(this, Package.Xml(part));
        }
    }

    /// <summary>The authors of the presentation's comments, of both kinds; add one to comment on a slide.</summary>
    public CommentAuthorCollection CommentAuthors { get; }

    /// <summary>The sections the slides are grouped in; empty when the presentation has none.</summary>
    public SlideSectionCollection Sections { get; }

    internal PackagePart? NotesMasterPart
    {
        get
        {
            foreach (XElement id in main.Root.Element(PptxNames.P + "notesMasterIdLst")?.Elements(PptxNames.P + "notesMasterId") ?? [])
            {
                if (IdTarget(main.Part.Name, id, PptxNames.NotesMasterRelationship) is { } part)
                {
                    return part;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Loads a PowerPoint file: PPTX, PPTM, POTX, POTM, PPSX or PPSM, transitional or Strict, and encrypted with a password
    /// (<see cref="PresentationLoadOptions.Password"/>) by agile or standard encryption.
    /// </summary>
    /// <exception cref="ArgumentException">The path is null or empty.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and the password is missing or wrong.</exception>
    /// <exception cref="DocumentException">The file is not a presentation Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file goes past one of <see cref="PresentationLoadOptions.Limits"/>.</exception>
    public static Presentation Load(string path, PresentationLoadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using FileStream stream = File.OpenRead(path);
        return Load(stream, options);
    }

    /// <summary>Loads a PowerPoint file from a stream, which is left open.</summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="DocumentPasswordException">The file is encrypted and the password is missing or wrong.</exception>
    /// <exception cref="DocumentException">The stream does not hold a presentation Egap can read.</exception>
    /// <exception cref="DocumentLimitException">The file goes past one of <see cref="PresentationLoadOptions.Limits"/>.</exception>
    public static Presentation Load(Stream stream, PresentationLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            using MemoryStream copy = new();
            stream.CopyTo(copy);
            copy.Position = 0;
            return Load(copy, options);
        }

        DocumentLimits limits = options?.Limits ?? new DocumentLimits();
        if (OfficeEncryption.IsEncryptedPackage(stream))
        {
            CompoundFile file;
            try
            {
                file = CompoundFile.Read(stream, limits);
            }
            catch (InvalidDataException e)
            {
                throw new DocumentException("The encrypted presentation's compound file is damaged: " + e.Message, e);
            }

            if (options?.Password is null)
            {
                throw new DocumentPasswordException("The presentation is encrypted: give its password in the load options.", wrongPassword: false);
            }

            byte[] decrypted = OfficeEncryption.Decrypt(file, options.Password, null, limits);
            return Load(new MemoryStream(decrypted, writable: false), new PresentationLoadOptions { Limits = limits });
        }

        PptxPackage package = PptxPackage.Read(stream, limits);
        PackagePart part = package.Related("/", OpcRelationshipTypes.OfficeDocument)
            ?? throw new DocumentException("The package has no main part: it is not a PowerPoint file.");
        XmlPart main = package.Xml(part);
        if (main.Root.Name != PptxNames.P + "presentation")
        {
            throw new DocumentException($"The main part {part.Name} holds a {main.Root.Name.LocalName} element, not a presentation: the file is not a PowerPoint file.");
        }

        Presentation presentation = new(package, main);
        presentation.Read();
        return presentation;
    }

    /// <summary>
    /// A new presentation with 16:9 slides and no slides yet: one slide master with Egap's own theme (Office-like colours,
    /// Calibri Light and Calibri) and the eleven standard layouts, from Title Slide to Vertical Title and Text. Add slides
    /// with <see cref="SlideCollection.Add(SlideLayout)"/>.
    /// </summary>
    public static Presentation Create()
    {
        PptxPackage package = PptxPackage.Create();
        XmlPart main = DefaultTemplate.Build(package);
        Presentation presentation = new(package, main);
        presentation.Read();
        return presentation;
    }

    /// <summary>
    /// Saves the presentation to a file as the kind its extension names (.pptx, .pptm, .potx, .potm, .ppsx or .ppsm), unless
    /// <see cref="PresentationSaveOptions.Format"/> names one. A deck with macros saved as a kind without them leaves the
    /// macros out. With <see cref="PresentationSaveOptions.Password"/>, the file is encrypted (agile encryption, as
    /// PowerPoint 2010 and later write it).
    /// </summary>
    /// <exception cref="ArgumentException">The path is null or empty, the extension names no PowerPoint kind and the options name none, or the password is longer than 255 characters.</exception>
    public void Save(string path, PresentationSaveOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        PresentationFormat format = options?.Format ?? FormatOfExtension(Path.GetExtension(path))
            ?? throw new ArgumentException($"No PowerPoint kind has the extension \"{Path.GetExtension(path)}\"; name one in the options.", nameof(path));
        using MemoryStream buffer = new();
        Write(buffer, format, options?.Password);
        using FileStream stream = File.Create(path);
        buffer.Position = 0;
        buffer.CopyTo(stream);
    }

    /// <summary>Saves the presentation to a stream, which is left open, as <see cref="PresentationSaveOptions.Format"/> or else <see cref="Format"/>.</summary>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ArgumentException">The password is longer than 255 characters.</exception>
    public void Save(Stream stream, PresentationSaveOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Write(stream, options?.Format ?? Format, options?.Password);
    }

    /// <summary>
    /// Sets the password to modify the file, which PowerPoint asks for before it lets anyone edit (it opens the file
    /// read-only otherwise), or removes it with null. The password is kept as a salted SHA-512 hash, as PowerPoint 2013 and
    /// later write it.
    /// </summary>
    /// <exception cref="ArgumentException">The password is empty or longer than 255 characters.</exception>
    public void SetModifyPassword(string? password)
    {
        if (password is not null)
        {
            ArgumentException.ThrowIfNullOrEmpty(password);
            if (password.Length > OfficeEncryption.MaxPasswordLength)
            {
                throw new ArgumentException("A password can have at most 255 characters.", nameof(password));
            }
        }

        if (main.Root.Element(PptxNames.P + "modifyVerifier") is { } old)
        {
            old.Remove();
            main.MarkChanged();
        }

        if (password is not null)
        {
            InsertInOrder(ModifyVerifier.Create(password));
        }
    }

    /// <summary>
    /// Whether <paramref name="password"/> is the password to modify the file; true when it has none. Both of the forms
    /// PowerPoint writes are checked: the algorithm name of 2013 and later, and the algorithm number of 2007 and 2010.
    /// </summary>
    /// <exception cref="DocumentException">The file's hash asks for more than ten million rounds.</exception>
    public bool VerifyModifyPassword(string? password)
    {
        return ModifyVerifier.Verify(main.Root.Element(PptxNames.P + "modifyVerifier"), password);
    }

    private void Write(Stream stream, PresentationFormat format, string? password)
    {
        if (password is { Length: > OfficeEncryption.MaxPasswordLength })
        {
            throw new ArgumentException("A password can have at most 255 characters.", nameof(password));
        }

        string contentType = ContentTypeOf(format);
        PackageSave save = new(main.Part.Name, contentType == main.Part.ContentType ? null : contentType,
            DropMacros: format is PresentationFormat.Pptx or PresentationFormat.Potx or PresentationFormat.Ppsx);
        if (string.IsNullOrEmpty(password))
        {
            Package.Save(stream, save);
            return;
        }

        // PowerPoint's encrypted files: the package, encrypted, in a compound file.
        using MemoryStream package = new();
        Package.Save(package, save);
        OfficeEncryption.Encrypt(package.GetBuffer().AsSpan(0, (int)package.Length), password, stream);
    }

    internal XmlPart Main => main;

    /// <summary>The part an id list entry's <c>r:id</c> points at, when the relationship has the type expected.</summary>
    internal PackagePart? IdTarget(string sourceName, XElement id, string type)
    {
        if ((string?)id.Attribute(PptxNames.R + "id") is not { } rid || Package.Relationship(sourceName, rid) is not { External: false } relationship
            || !OpcRelationshipTypes.Same(relationship.Type, type))
        {
            return null;
        }

        return Package.GetPart(relationship.TargetName);
    }

    /// <summary>Puts <paramref name="element"/> into the presentation part where CT_Presentation's sequence wants it.</summary>
    internal void InsertInOrder(XElement element)
    {
        string[] sequence =
        [
            "sldMasterIdLst", "notesMasterIdLst", "handoutMasterIdLst", "sldIdLst", "sldSz", "notesSz", "smartTags", "embeddedFontLst",
            "custShowLst", "photoAlbum", "custDataLst", "kinsoku", "defaultTextStyle", "modifyVerifier", "extLst",
        ];
        int rank = Array.IndexOf(sequence, element.Name.LocalName);
        XElement? after = main.Root.Elements().LastOrDefault(e => e.Name.Namespace == PptxNames.P && Array.IndexOf(sequence, e.Name.LocalName) is int r && r >= 0 && r < rank);
        if (after is null)
        {
            main.Root.AddFirst(element);
        }
        else
        {
            after.AddAfterSelf(element);
        }

        main.MarkChanged();
    }

    private void Read()
    {
        string part = main.Part.Name;
        Dictionary<string, SlideLayout> layouts = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement id in main.Root.Element(PptxNames.P + "sldMasterIdLst")?.Elements(PptxNames.P + "sldMasterId") ?? [])
        {
            if (IdTarget(part, id, PptxNames.SlideMasterRelationship) is not { } masterPart)
            {
                continue;
            }

            SlideMaster master = new(this, Package.Xml(masterPart));
            masters.Add(master);
            foreach (XElement layoutId in master.Xml.Root.Element(PptxNames.P + "sldLayoutIdLst")?.Elements(PptxNames.P + "sldLayoutId") ?? [])
            {
                if (IdTarget(masterPart.Name, layoutId, PptxNames.SlideLayoutRelationship) is { } layoutPart && !layouts.ContainsKey(layoutPart.Name))
                {
                    SlideLayout layout = new(this, Package.Xml(layoutPart), master);
                    layouts[layoutPart.Name] = layout;
                    master.LayoutList.Add(layout);
                }
            }
        }

        // A slide part the list names twice is one slide: the second entry is kept in the file but not in the model, so that
        // two slides never share one part's XML.
        HashSet<string> slideParts = new(StringComparer.OrdinalIgnoreCase);
        foreach (XElement id in main.Root.Element(PptxNames.P + "sldIdLst")?.Elements(PptxNames.P + "sldId") ?? [])
        {
            if (IdTarget(part, id, PptxNames.SlideRelationship) is not { } slidePart || !slideParts.Add(slidePart.Name))
            {
                continue;
            }

            XmlPart xml = Package.Xml(slidePart);
            PackagePart? layoutPart = Package.Related(slidePart.Name, PptxNames.SlideLayoutRelationship);
            SlideLayout? layout = layoutPart is null ? null : layouts.GetValueOrDefault(layoutPart.Name);
            Slides.List.Add(new Slide(this, xml, layout, id));
        }
    }

    private static SlideSize? ReadSize(XElement? element)
    {
        return PptxNames.Long(element, "cx") is long cx && PptxNames.Long(element, "cy") is long cy ? new SlideSize(cx, cy) : null;
    }

    private static string ContentTypeOf(PresentationFormat format) => format switch
    {
        PresentationFormat.Potx => MainContentType + "template.main+xml",
        PresentationFormat.Ppsx => MainContentType + "slideshow.main+xml",
        PresentationFormat.Pptm => MacroContentType + "presentation.macroEnabled.main+xml",
        PresentationFormat.Potm => MacroContentType + "template.macroEnabled.main+xml",
        PresentationFormat.Ppsm => MacroContentType + "slideshow.macroEnabled.main+xml",
        _ => MainContentType + "presentation.main+xml",
    };

    private static PresentationFormat? FormatOfExtension(string extension) => extension.ToUpperInvariant() switch
    {
        ".PPTX" => PresentationFormat.Pptx,
        ".PPTM" => PresentationFormat.Pptm,
        ".POTX" => PresentationFormat.Potx,
        ".POTM" => PresentationFormat.Potm,
        ".PPSX" => PresentationFormat.Ppsx,
        ".PPSM" => PresentationFormat.Ppsm,
        _ => null,
    };

    private static PresentationFormat FormatOf(string contentType) => contentType switch
    {
        MainContentType + "template.main+xml" => PresentationFormat.Potx,
        MainContentType + "slideshow.main+xml" => PresentationFormat.Ppsx,
        MacroContentType + "presentation.macroEnabled.main+xml" => PresentationFormat.Pptm,
        MacroContentType + "template.macroEnabled.main+xml" => PresentationFormat.Potm,
        MacroContentType + "slideshow.macroEnabled.main+xml" => PresentationFormat.Ppsm,
        _ => PresentationFormat.Pptx,
    };
}

/// <summary>The kinds of PowerPoint file: presentations, templates and shows, with or without macros.</summary>
public enum PresentationFormat
{
    /// <summary>A presentation (.pptx).</summary>
    Pptx,

    /// <summary>A presentation with macros (.pptm).</summary>
    Pptm,

    /// <summary>A template (.potx).</summary>
    Potx,

    /// <summary>A template with macros (.potm).</summary>
    Potm,

    /// <summary>A show, which PowerPoint opens straight into the slide show (.ppsx).</summary>
    Ppsx,

    /// <summary>A show with macros (.ppsm).</summary>
    Ppsm,
}

/// <summary>How a presentation is loaded.</summary>
public sealed class PresentationLoadOptions
{
    /// <summary>The limits the reader keeps to, against files made to use up memory; the defaults suit any real presentation.</summary>
    public DocumentLimits Limits { get; init; } = new();

    /// <summary>The password of an encrypted file; ignored when the file is not encrypted.</summary>
    public string? Password { get; init; }
}

/// <summary>How a presentation is saved.</summary>
public sealed class PresentationSaveOptions
{
    /// <summary>The kind of file to write, or null for the one the path's extension names (or <see cref="Presentation.Format"/> for a stream).</summary>
    public PresentationFormat? Format { get; init; }

    /// <summary>A password to encrypt the file with (agile encryption: AES-256 and SHA-512), or null to write it unencrypted.</summary>
    public string? Password { get; init; }
}

/// <summary>The size of a slide or notes page, in EMUs (English Metric Units: 914,400 to the inch, 12,700 to the point).</summary>
/// <param name="Width">The width in EMUs.</param>
/// <param name="Height">The height in EMUs.</param>
public readonly record struct SlideSize(long Width, long Height)
{
    /// <summary>The width in points.</summary>
    public double WidthPoints => Width / (double)ShapeTransform.EmusPerPoint;

    /// <summary>The height in points.</summary>
    public double HeightPoints => Height / (double)ShapeTransform.EmusPerPoint;

    /// <summary>A size given in points, rounded to whole EMUs.</summary>
    public static SlideSize FromPoints(double width, double height) => new(ShapeTransform.ToEmus(width), ShapeTransform.ToEmus(height));
}
