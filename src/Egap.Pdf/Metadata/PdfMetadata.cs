using System.Xml.Linq;

using Egap.Pdf.Metadata;

namespace Egap.Pdf;

/// <summary>
/// The document's metadata (14.3) through one API over both stores (Phase 2 Q8): the document information dictionary
/// (<c>/Info</c> in the trailer) and the XMP packet (the catalog's <c>/Metadata</c> stream). Reading prefers XMP when the
/// packet has the property, as pdf.js does. Setting writes the Info entry and, when the document has an XMP packet or is PDF
/// 2.0, the matching XMP property; a document before 2.0 without a packet gets none unless <see cref="CreateXmp"/> is called.
/// Setting <see langword="null"/> removes the property from both.
/// </summary>
public sealed class PdfMetadata
{
    private readonly PdfDocument document;

    internal PdfMetadata(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>The title: <c>dc:title</c> or <c>/Title</c>.</summary>
    public string? Title
    {
        get => Xmp()?.GetAlternative(XmpPacket.Dc + "title") ?? InfoText("Title");
        set => Set("Title", value, (x, v) => x.SetAlternative(XmpPacket.Dc + "title", v));
    }

    /// <summary>The author: <c>dc:creator</c> (several joined with <c>"; "</c>) or <c>/Author</c>.</summary>
    public string? Author
    {
        get => Xmp()?.GetList(XmpPacket.Dc + "creator") ?? InfoText("Author");
        set => Set("Author", value, (x, v) => x.SetSequence(XmpPacket.Dc + "creator", v));
    }

    /// <summary>The subject: <c>dc:description</c> or <c>/Subject</c>.</summary>
    public string? Subject
    {
        get => Xmp()?.GetAlternative(XmpPacket.Dc + "description") ?? InfoText("Subject");
        set => Set("Subject", value, (x, v) => x.SetAlternative(XmpPacket.Dc + "description", v));
    }

    /// <summary>The keywords: <c>pdf:Keywords</c> or <c>/Keywords</c>.</summary>
    public string? Keywords
    {
        get => Xmp()?.GetText(XmpPacket.Pdf + "Keywords") ?? InfoText("Keywords");
        set => Set("Keywords", value, (x, v) => x.SetText(XmpPacket.Pdf + "Keywords", v));
    }

    /// <summary>The application that made the original document: <c>xmp:CreatorTool</c> or <c>/Creator</c>.</summary>
    public string? Creator
    {
        get => Xmp()?.GetText(XmpPacket.Xmp + "CreatorTool") ?? InfoText("Creator");
        set => Set("Creator", value, (x, v) => x.SetText(XmpPacket.Xmp + "CreatorTool", v));
    }

    /// <summary>
    /// The application that wrote the PDF: <c>pdf:Producer</c> or <c>/Producer</c>. Egap never sets it on its own (Phase 2 Q9).
    /// </summary>
    public string? Producer
    {
        get => Xmp()?.GetText(XmpPacket.Pdf + "Producer") ?? InfoText("Producer");
        set => Set("Producer", value, (x, v) => x.SetText(XmpPacket.Pdf + "Producer", v));
    }

    /// <summary>When the document was created: <c>xmp:CreateDate</c> or <c>/CreationDate</c>.</summary>
    public DateTimeOffset? CreationDate
    {
        get => PdfDate.ParseXmp(Xmp()?.GetText(XmpPacket.Xmp + "CreateDate")) ?? PdfDate.ParsePdf(InfoText("CreationDate"));
        set => SetDate("CreationDate", XmpPacket.Xmp + "CreateDate", value);
    }

    /// <summary>
    /// When the document was last changed: <c>xmp:ModifyDate</c> or <c>/ModDate</c>. A save with changes sets it, in the
    /// stores the document has, to the time of saving unless <see cref="PdfSaveOptions.UpdateModificationDate"/> is off or
    /// the save is deterministic (Phase 2 Q9).
    /// </summary>
    public DateTimeOffset? ModificationDate
    {
        get => PdfDate.ParseXmp(Xmp()?.GetText(XmpPacket.Xmp + "ModifyDate")) ?? PdfDate.ParsePdf(InfoText("ModDate"));
        set => SetDate("ModDate", XmpPacket.Xmp + "ModifyDate", value);
    }

    /// <summary>The document information dictionary, or <see langword="null"/> when the trailer has none.</summary>
    public PdfDictionary? Info => document.Trailer.Get<PdfDictionary>("Info");

    /// <summary>Whether the catalog has an XMP packet that parses.</summary>
    public bool HasXmp => Xmp() is not null;

    /// <summary>A copy of the XMP packet as XML, or <see langword="null"/> when there is none that parses.</summary>
    public XDocument? GetXmp()
    {
        return Xmp() is XmpPacket packet ? new XDocument(packet.Document) : null;
    }

    /// <summary>Replaces the XMP packet with the given XML, which must hold an <c>rdf:RDF</c> element.</summary>
    public void SetXmp(XDocument xmp)
    {
        ArgumentNullException.ThrowIfNull(xmp);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(xmp.ToString(SaveOptions.DisableFormatting));
        XmpPacket packet = XmpPacket.Parse(bytes) ?? throw new ArgumentException("The XML is not an XMP packet: it has no rdf:RDF element.", nameof(xmp));
        Write(packet);
    }

    /// <summary>
    /// Gives a document without one an XMP packet holding the current Info values, so later changes are written to both.
    /// Does nothing when the document already has a packet.
    /// </summary>
    public void CreateXmp()
    {
        if (Xmp() is not null)
        {
            return;
        }

        XmpPacket packet = XmpPacket.Create();
        packet.SetAlternative(XmpPacket.Dc + "title", InfoText("Title"));
        packet.SetSequence(XmpPacket.Dc + "creator", InfoText("Author"));
        packet.SetAlternative(XmpPacket.Dc + "description", InfoText("Subject"));
        packet.SetText(XmpPacket.Pdf + "Keywords", InfoText("Keywords"));
        packet.SetText(XmpPacket.Xmp + "CreatorTool", InfoText("Creator"));
        packet.SetText(XmpPacket.Pdf + "Producer", InfoText("Producer"));
        packet.SetText(XmpPacket.Xmp + "CreateDate", PdfDate.ParsePdf(InfoText("CreationDate")) is DateTimeOffset created ? PdfDate.FormatXmp(created) : null);
        packet.SetText(XmpPacket.Xmp + "ModifyDate", PdfDate.ParsePdf(InfoText("ModDate")) is DateTimeOffset modified ? PdfDate.FormatXmp(modified) : null);
        Write(packet);
    }

    /// <summary>
    /// Sets the modification date in the stores the document has (and <c>xmp:MetadataDate</c>), as a save with changes
    /// does. A store the document lacks is not created, so a removed Info dictionary stays removed.
    /// </summary>
    internal void Touch(DateTimeOffset now)
    {
        if (Info is PdfDictionary info)
        {
            info["ModDate"] = PdfString.FromText(PdfDate.FormatPdf(now));
        }

        if (Xmp() is XmpPacket packet)
        {
            packet.SetText(XmpPacket.Xmp + "ModifyDate", PdfDate.FormatXmp(now));
            packet.SetText(XmpPacket.Xmp + "MetadataDate", PdfDate.FormatXmp(now));
            Write(packet);
        }
    }

    /// <summary>The Info entries PDF/A pairs with XMP properties (ISO 19005-2 6.6.3), and how each property is written.</summary>
    private static readonly (string Key, XName Property, char Kind)[] InfoPairs =
    [
        ("Title", XmpPacket.Dc + "title", 'a'),
        ("Author", XmpPacket.Dc + "creator", 's'),
        ("Subject", XmpPacket.Dc + "description", 'a'),
        ("Keywords", XmpPacket.Pdf + "Keywords", 't'),
        ("Creator", XmpPacket.Xmp + "CreatorTool", 't'),
        ("Producer", XmpPacket.Pdf + "Producer", 't'),
        ("CreationDate", XmpPacket.Xmp + "CreateDate", 'd'),
        ("ModDate", XmpPacket.Xmp + "ModifyDate", 'd'),
    ];

    /// <summary>The Info entries whose value the XMP packet lacks or holds differently, as PDF/A-1 to 3 forbid.</summary>
    internal List<string> InfoXmpMismatches()
    {
        List<string> keys = [];
        if (Info is not PdfDictionary info)
        {
            return keys;
        }

        XmpPacket? packet = Xmp();
        foreach ((string key, XName property, char kind) in InfoPairs)
        {
            if (info.Get(key) is null)
            {
                continue;
            }

            string? value = info.GetText(key);

            // Text compares without surrounding white space, which the packet's reader trims, and without characters XML cannot hold.
            static bool Equal(string? a, string? b) => string.Equals((XmpPacket.Clean(a) ?? "").Trim(), (b ?? "").Trim(), StringComparison.Ordinal);
            bool same = packet is not null && kind switch
            {
                'a' => Equal(value, packet.GetAlternative(property)),
                's' => Equal(value, packet.GetList(property)),
                'd' => PdfDate.ParsePdf(value) is DateTimeOffset date && PdfDate.ParseXmp(packet.GetText(property)) is DateTimeOffset other && date == other,
                _ => Equal(value, packet.GetText(property)),
            };
            if (!same)
            {
                keys.Add(key);
            }
        }

        return keys;
    }

    /// <summary>Sets the XMP packet's paired properties from the Info entries; an Info date that does not parse is removed.</summary>
    internal void SyncXmpFromInfo()
    {
        if (Info is not PdfDictionary info)
        {
            return;
        }

        CreateXmp();
        XmpPacket packet = Xmp()!;
        foreach ((string key, XName property, char kind) in InfoPairs)
        {
            if (info.Get(key) is null)
            {
                continue;
            }

            string? value = info.GetText(key);
            if (XmpPacket.Clean(value) is string clean && clean != value)
            {
                // XMP cannot hold the control characters, so the Info value loses them too and the two stay equal.
                info[key] = PdfString.FromText(clean);
                value = clean;
            }

            switch (kind)
            {
                case 'a':
                    packet.SetAlternative(property, value);
                    break;
                case 's':
                    packet.SetSequence(property, value);
                    break;
                case 'd' when PdfDate.ParsePdf(value) is DateTimeOffset date:
                    packet.SetText(property, PdfDate.FormatXmp(date));
                    break;
                case 'd':
                    info.Remove(key);
                    break;
                default:
                    packet.SetText(property, value);
                    break;
            }
        }

        Write(packet);
    }

    /// <summary>What in the XMP packet breaks PDF/A's XMP rules (<see cref="XmpPdfA"/>); empty without a packet.</summary>
    internal List<string> XmpPdfAProblems()
    {
        return Xmp() is XmpPacket packet ? XmpPdfA.Problems(packet) : [];
    }

    /// <summary>Puts the XMP packet right for PDF/A and writes it.</summary>
    internal void FixXmpForPdfA()
    {
        if (Xmp() is XmpPacket packet)
        {
            XmpPdfA.Fix(packet);
            Write(packet);
        }
    }

    /// <summary>The PDF/UA part the XMP packet claims (<c>pdfuaid:part</c>), or null.</summary>
    internal string? GetPdfUAId()
    {
        return Xmp()?.GetText(XmpPacket.PdfUAId + "part")?.Trim();
    }

    /// <summary>Writes the PDF/UA identification, with the extension schema PDF/A needs to accept it.</summary>
    internal void SetPdfUAId(int part)
    {
        CreateXmp();
        XmpPacket packet = Xmp()!;
        packet.SetText(XmpPacket.PdfUAId + "part", part.ToString(System.Globalization.CultureInfo.InvariantCulture));
        packet.DeclareExtension(XmpPacket.PdfUAId, "pdfuaid", "PDF/UA Universal Accessibility Schema", "part", "Integer", "Indicates, which part of ISO 14289 standard is followed");
        Write(packet);
    }

    /// <summary>The PDF/A identification in the XMP packet (ISO 19005-2 6.6.4): part, conformance letter and revision; nulls without one.</summary>
    internal (string? Part, string? Conformance, string? Revision) GetPdfAId()
    {
        XmpPacket? packet = Xmp();
        return (packet?.GetText(XmpPacket.PdfAId + "part"), packet?.GetText(XmpPacket.PdfAId + "conformance"), packet?.GetText(XmpPacket.PdfAId + "rev"));
    }

    /// <summary>Writes the PDF/A identification, creating the XMP packet from the Info values when there is none.</summary>
    internal void SetPdfAId(int part, string? conformance, string? revision)
    {
        CreateXmp();
        XmpPacket packet = Xmp()!;
        packet.SetText(XmpPacket.PdfAId + "part", part.ToString(System.Globalization.CultureInfo.InvariantCulture));
        packet.SetText(XmpPacket.PdfAId + "conformance", conformance);
        packet.SetText(XmpPacket.PdfAId + "rev", revision);
        Write(packet);
    }

    private string? InfoText(PdfName key)
    {
        return Info?.GetText(key);
    }

    private void Set(PdfName key, string? value, Action<XmpPacket, string?> setXmp)
    {
        if (InfoForWriting(value is null) is PdfDictionary info)
        {
            info[key] = value is null ? null : PdfString.FromText(value);
        }

        if (XmpForWriting() is XmpPacket packet)
        {
            setXmp(packet, value);
            Write(packet);
        }
    }

    private void SetDate(PdfName key, XName xmpName, DateTimeOffset? value)
    {
        if (InfoForWriting(value is null) is PdfDictionary info)
        {
            info[key] = value is DateTimeOffset date ? PdfString.FromText(PdfDate.FormatPdf(date)) : null;
        }

        if (XmpForWriting() is XmpPacket packet)
        {
            packet.SetText(xmpName, value is DateTimeOffset date ? PdfDate.FormatXmp(date) : null);
            Write(packet);
        }
    }

    /// <summary>The Info dictionary to write to, created as an indirect object when needed; null when removing from none.</summary>
    private PdfDictionary? InfoForWriting(bool removing)
    {
        if (Info is PdfDictionary info)
        {
            return info;
        }

        if (removing)
        {
            return null;
        }

        PdfReference reference = document.Objects.Add(new PdfDictionary());
        document.Trailer["Info"] = reference;
        return (PdfDictionary)document.Objects[reference];
    }

    /// <summary>
    /// The packet to write to: the document's, or for a PDF 2.0 document a new one holding the Info values so far (Phase 2 Q8).
    /// </summary>
    private XmpPacket? XmpForWriting()
    {
        if (Xmp() is XmpPacket packet)
        {
            return packet;
        }

        if (string.CompareOrdinal(document.Version, "2.0") < 0)
        {
            return null;
        }

        CreateXmp();
        return Xmp();
    }

    private XmpPacket? Xmp()
    {
        return document.Catalog.Get<PdfStream>("Metadata") is PdfStream stream && TryDecode(stream) is byte[] bytes ? XmpPacket.Parse(bytes) : null;
    }

    private static byte[]? TryDecode(PdfStream stream)
    {
        try
        {
            return stream.GetDecodedBytes();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return null;
        }
    }

    /// <summary>Writes a packet into the catalog's metadata stream, uncompressed as PDF/A asks, creating the stream if needed.</summary>
    private void Write(XmpPacket packet)
    {
        byte[] bytes = packet.ToBytes();
        if (document.Catalog.Get<PdfStream>("Metadata") is PdfStream stream)
        {
            stream.Dictionary.Remove("DecodeParms");
            stream.SetDecodedBytes(bytes, compress: false);
            return;
        }

        PdfStream created = new(new PdfDictionary { ["Type"] = PdfName.Get("Metadata"), ["Subtype"] = PdfName.Get("XML") });
        created.SetDecodedBytes(bytes, compress: false);
        document.Catalog["Metadata"] = document.Objects.Add(created);
    }
}
