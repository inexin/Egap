using System.Xml;
using System.Xml.Linq;

using Egap.Pdf.Metadata;

namespace Egap.Pdf;

/// <summary>How an attached file relates to the document (<c>/AFRelationship</c>, ISO 32000-2 14.13.2).</summary>
public enum PdfAttachmentRelationship
{
    /// <summary>The relationship is not known or does not fit the others.</summary>
    Unspecified,

    /// <summary>The original the document was made from, such as a word processor file.</summary>
    Source,

    /// <summary>Data the document shows, such as the table behind a chart.</summary>
    Data,

    /// <summary>Another representation of the same content, such as an e-invoice's XML.</summary>
    Alternative,

    /// <summary>A supplement to the content, such as a file it refers to.</summary>
    Supplement,

    /// <summary>An encrypted payload document (PDF 2.0 unencrypted wrappers).</summary>
    EncryptedPayload,

    /// <summary>Data from the document's form.</summary>
    FormData,

    /// <summary>A schema the document's data follows.</summary>
    Schema,
}

/// <summary>The e-invoice standard an embedded invoice follows.</summary>
public enum PdfInvoiceStandard
{
    /// <summary>Factur-X 1.0, which is also ZUGFeRD 2.1 and later: the XML is named <c>factur-x.xml</c> (<c>xrechnung.xml</c> for XRechnung).</summary>
    FacturX,

    /// <summary>ZUGFeRD 2.0: the XML is named <c>zugferd-invoice.xml</c>. Read only.</summary>
    Zugferd20,

    /// <summary>ZUGFeRD 1.0: the XML is named <c>ZUGFeRD-invoice.xml</c>. Read only.</summary>
    Zugferd10,
}

/// <summary>The Factur-X and ZUGFeRD profile, how much of the invoice the XML holds.</summary>
public enum PdfInvoiceProfile
{
    /// <summary>MINIMUM: the totals and the parties, for booking only.</summary>
    Minimum,

    /// <summary>BASIC WL: the header without invoice lines.</summary>
    BasicWL,

    /// <summary>BASIC: a subset of EN 16931 with lines.</summary>
    Basic,

    /// <summary>EN 16931 (COMFORT in ZUGFeRD): the European standard's core invoice.</summary>
    EN16931,

    /// <summary>EXTENDED: EN 16931 with more fields.</summary>
    Extended,

    /// <summary>XRECHNUNG: the German public sector's EN 16931 profile.</summary>
    XRechnung,
}

/// <summary>How <see cref="PdfInvoice.Embed"/> embeds an invoice.</summary>
public sealed record PdfInvoiceOptions
{
    /// <summary>Gets the defaults: Factur-X, the profile read from the XML, PDF/A-3b.</summary>
    public static PdfInvoiceOptions Default { get; } = new();

    /// <summary>Gets the standard. Defaults to <see cref="PdfInvoiceStandard.FacturX"/>, the only one written.</summary>
    public PdfInvoiceStandard Standard { get; init; } = PdfInvoiceStandard.FacturX;

    /// <summary>Gets the profile; null (the default) to read it from the XML's guideline identifier.</summary>
    public PdfInvoiceProfile? Profile { get; init; }

    /// <summary>
    /// Gets the PDF/A-3 level the document is converted to, unless it is PDF/A-3 already: <see cref="PdfAConformance.PdfA3b"/>
    /// (the default), <see cref="PdfAConformance.PdfA3u"/> or <see cref="PdfAConformance.PdfA3a"/>.
    /// </summary>
    public PdfAConformance Conformance { get; init; } = PdfAConformance.PdfA3b;

    /// <summary>
    /// Gets the relationship of the XML to the document; null (the default) for what Factur-X asks: Data for MINIMUM and
    /// BASIC WL, Alternative for the others.
    /// </summary>
    public PdfAttachmentRelationship? Relationship { get; init; }
}

/// <summary>
/// A Factur-X or ZUGFeRD e-invoice in a PDF: the invoice's XML (UN/CEFACT Cross Industry Invoice) attached to a PDF/A-3
/// document, with the Factur-X schema in the document's XMP saying the file name, the profile and the version. The XML is
/// the invoicing system's to write; Egap embeds it and reads it back.
/// </summary>
/// <param name="Xml">The invoice's XML as stored.</param>
/// <param name="FileName">The attachment's name, such as <c>factur-x.xml</c>.</param>
/// <param name="Standard">The standard, from the XMP or the file name.</param>
/// <param name="Profile">The profile, from the XMP or else the XML; null when neither says.</param>
public sealed record PdfInvoice(byte[] Xml, string FileName, PdfInvoiceStandard Standard, PdfInvoiceProfile? Profile)
{
    private static readonly XNamespace FacturXSchema = "urn:factur-x:pdfa:CrossIndustryDocument:invoice:1p0#";
    private static readonly XNamespace Zugferd20Schema = "urn:zugferd:pdfa:CrossIndustryDocument:invoice:2p0#";
    private static readonly XNamespace Zugferd10Schema = "urn:ferd:pdfa:CrossIndustryDocument:invoice:1p0#";
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";

    /// <summary>
    /// Embeds an invoice's XML: attached under its Factur-X name with the relationship Factur-X asks for and the
    /// <c>text/xml</c> type, listed among the document's associated files, the document converted to PDF/A-3 when it is not
    /// already, and the Factur-X extension schema written into its XMP. Save the document afterwards.
    /// </summary>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Open("invoice.pdf");
    /// PdfInvoice.Embed(pdf, File.ReadAllBytes("factur-x.xml"), new PdfInvoiceOptions { Profile = PdfInvoiceProfile.EN16931 });
    /// pdf.Save("invoice-facturx.pdf");
    /// </code>
    /// </example>
    /// <returns>The PDF/A conversion's report; it changed nothing when <see cref="PdfAReport.Succeeded"/> is false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// The XML is not well-formed, the profile is not given and the XML does not say it, a legacy standard is asked for, or
    /// the conformance is not PDF/A-3.
    /// </exception>
    public static PdfAReport Embed(PdfDocument document, ReadOnlySpan<byte> xml, PdfInvoiceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= PdfInvoiceOptions.Default;
        if (options.Standard != PdfInvoiceStandard.FacturX)
        {
            throw new ArgumentException("Only Factur-X (ZUGFeRD 2.1 and later) is written; ZUGFeRD 1.0 and 2.0 are read only.", nameof(options));
        }

        if (options.Conformance is not (PdfAConformance.PdfA3b or PdfAConformance.PdfA3u or PdfAConformance.PdfA3a))
        {
            throw new ArgumentException("E-invoices are PDF/A-3: choose PdfA3b, PdfA3u or PdfA3a.", nameof(options));
        }

        byte[] bytes = xml.ToArray();
        XDocument parsed = Parse(bytes) ?? throw new ArgumentException("The invoice is not well-formed XML.", nameof(xml));
        PdfInvoiceProfile profile = options.Profile ?? ProfileOf(parsed)
            ?? throw new ArgumentException("The XML names no Factur-X profile (ram:GuidelineSpecifiedDocumentContextParameter); give one in the options.", nameof(options));
        string fileName = profile == PdfInvoiceProfile.XRechnung ? "xrechnung.xml" : "factur-x.xml";
        PdfAttachmentRelationship relationship = options.Relationship
            ?? (profile is PdfInvoiceProfile.Minimum or PdfInvoiceProfile.BasicWL ? PdfAttachmentRelationship.Data : PdfAttachmentRelationship.Alternative);

        foreach (string legacy in (ReadOnlySpan<string>)["factur-x.xml", "xrechnung.xml", "zugferd-invoice.xml", "ZUGFeRD-invoice.xml"])
        {
            document.Attachments.Remove(legacy);
        }

        DateTimeOffset now = document.Clock.GetUtcNow();
        document.Attachments.Add(fileName, bytes, relationship, "text/xml", "Factur-X/ZUGFeRD invoice", now);

        PdfAReport report = document.Conformance is PdfAConformance.PdfA3a or PdfAConformance.PdfA3b or PdfAConformance.PdfA3u
            ? PdfAConverter.Convert(document, document.Conformance)
            : PdfAConverter.Convert(document, options.Conformance);
        if (!report.Succeeded)
        {
            return report;
        }

        document.Metadata.EditXmp(packet =>
        {
            packet.DeclareExtension(FacturXSchema, "fx", "Factur-X PDFA Extension Schema",
            [
                ("DocumentFileName", "Text", "external", "The name of the embedded XML invoice file"),
                ("DocumentType", "Text", "external", "INVOICE"),
                ("Version", "Text", "external", "The version of the Factur-X XML schema"),
                ("ConformanceLevel", "Text", "external", "The conformance level of the embedded Factur-X data"),
            ]);
            packet.DeclarePrefix(FacturXSchema, "fx");
            packet.SetText(FacturXSchema + "DocumentType", "INVOICE");
            packet.SetText(FacturXSchema + "DocumentFileName", fileName);
            packet.SetText(FacturXSchema + "Version", "1.0");
            packet.SetText(FacturXSchema + "ConformanceLevel", LevelName(profile));
        });
        return report;
    }

    /// <summary>
    /// The e-invoice a document carries, Factur-X or ZUGFeRD 1.0 to 2.3: found by the file name the document's XMP names,
    /// else by the standard file names; null when there is none.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    public static PdfInvoice? Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        XmpPacket? packet = document.Metadata.ReadXmp();
        foreach ((XNamespace ns, PdfInvoiceStandard standard) in (ReadOnlySpan<(XNamespace, PdfInvoiceStandard)>)[(FacturXSchema, PdfInvoiceStandard.FacturX), (Zugferd20Schema, PdfInvoiceStandard.Zugferd20), (Zugferd10Schema, PdfInvoiceStandard.Zugferd10)])
        {
            if (packet?.GetText(ns + "DocumentFileName") is { Length: > 0 } name && document.Attachments.TryGet(name, out PdfAttachment attachment) && BytesOf(attachment) is byte[] bytes)
            {
                PdfInvoiceProfile? profile = LevelOf(packet.GetText(ns + "ConformanceLevel")) ?? (Parse(bytes) is { } xml ? ProfileOf(xml) : null);
                return new PdfInvoice(bytes, name, standard, profile);
            }
        }

        foreach ((string name, PdfInvoiceStandard standard) in (ReadOnlySpan<(string, PdfInvoiceStandard)>)[("factur-x.xml", PdfInvoiceStandard.FacturX), ("xrechnung.xml", PdfInvoiceStandard.FacturX), ("zugferd-invoice.xml", PdfInvoiceStandard.Zugferd20), ("ZUGFeRD-invoice.xml", PdfInvoiceStandard.Zugferd10)])
        {
            if (document.Attachments.TryGet(name, out PdfAttachment attachment) && BytesOf(attachment) is byte[] bytes)
            {
                return new PdfInvoice(bytes, name, standard, Parse(bytes) is { } xml ? ProfileOf(xml) : null);
            }
        }

        return null;
    }

    /// <summary>The XMP <c>fx:ConformanceLevel</c> value of a profile.</summary>
    private static string LevelName(PdfInvoiceProfile profile) => profile switch
    {
        PdfInvoiceProfile.Minimum => "MINIMUM",
        PdfInvoiceProfile.BasicWL => "BASIC WL",
        PdfInvoiceProfile.Basic => "BASIC",
        PdfInvoiceProfile.EN16931 => "EN 16931",
        PdfInvoiceProfile.Extended => "EXTENDED",
        _ => "XRECHNUNG",
    };

    private static PdfInvoiceProfile? LevelOf(string? level) => level?.Trim().ToUpperInvariant() switch
    {
        "MINIMUM" => PdfInvoiceProfile.Minimum,
        "BASIC WL" => PdfInvoiceProfile.BasicWL,
        "BASIC" => PdfInvoiceProfile.Basic,
        "EN 16931" or "COMFORT" => PdfInvoiceProfile.EN16931,
        "EXTENDED" => PdfInvoiceProfile.Extended,
        "XRECHNUNG" => PdfInvoiceProfile.XRechnung,
        _ => null,
    };

    /// <summary>The profile the XML's guideline identifier names (ram:GuidelineSpecifiedDocumentContextParameter/ram:ID).</summary>
    private static PdfInvoiceProfile? ProfileOf(XDocument xml)
    {
        string? id = xml.Descendants(Ram + "GuidelineSpecifiedDocumentContextParameter").Elements(Ram + "ID").FirstOrDefault()?.Value.Trim()
            ?? xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "GuidelineSpecifiedDocumentContextParameter")?.Elements().FirstOrDefault(e => e.Name.LocalName == "ID")?.Value.Trim();
        if (id is null)
        {
            return null;
        }

        string lower = id.ToLowerInvariant();
        return lower switch
        {
            _ when lower.Contains("xrechnung", StringComparison.Ordinal) => PdfInvoiceProfile.XRechnung,
            _ when lower.EndsWith(":minimum", StringComparison.Ordinal) => PdfInvoiceProfile.Minimum,
            _ when lower.EndsWith(":basicwl", StringComparison.Ordinal) => PdfInvoiceProfile.BasicWL,
            _ when lower.EndsWith(":basic", StringComparison.Ordinal) => PdfInvoiceProfile.Basic,
            _ when lower.EndsWith(":extended", StringComparison.Ordinal) => PdfInvoiceProfile.Extended,
            _ when lower.StartsWith("urn:cen.eu:en16931:2017", StringComparison.Ordinal) || lower.EndsWith(":comfort", StringComparison.Ordinal) => PdfInvoiceProfile.EN16931,
            _ => null,
        };
    }

    /// <summary>An attachment's bytes; null when it has none or they cannot be decoded.</summary>
    private static byte[]? BytesOf(PdfAttachment attachment)
    {
        try
        {
            return attachment.GetBytes();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return null;
        }
    }

    private static XDocument? Parse(byte[] bytes)
    {
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
