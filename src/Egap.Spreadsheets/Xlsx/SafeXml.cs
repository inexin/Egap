using System.Xml;
using System.Xml.Linq;

using Egap.Documents;
using Egap.Documents.Docx;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>
/// The one way the package reads XML from a file: no DTDs (so no entity expansion), no external resolver, and a character
/// cap from <see cref="DocumentLimits.MaxPartSize"/>. Every part, the streaming reader's too, goes through here.
/// </summary>
internal static class SafeXml
{
    /// <summary>Reader settings that refuse DTDs and entity expansion and cap the characters read.</summary>
    public static XmlReaderSettings Settings(DocumentLimits limits, bool closeInput = true) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersFromEntities = 0,
        MaxCharactersInDocument = limits.MaxPartSize,
        IgnoreWhitespace = false,
        IgnoreProcessingInstructions = false,
        CloseInput = closeInput,
    };

    /// <summary>A safe reader over <paramref name="data"/>.</summary>
    public static XmlReader Open(byte[] data, DocumentLimits limits) => XmlReader.Create(new MemoryStream(data, writable: false), Settings(limits));

    /// <summary>A safe reader over <paramref name="stream"/>, which the reader leaves open.</summary>
    public static XmlReader Open(Stream stream, DocumentLimits limits) => XmlReader.Create(stream, Settings(limits, closeInput: false));

    /// <summary>The XML in <paramref name="data"/>, read safely.</summary>
    /// <exception cref="XmlException">The XML is not well-formed, has a DTD or is too long.</exception>
    public static XDocument Load(byte[] data, DocumentLimits limits)
    {
        using XmlReader reader = Open(data, limits);
        return XDocument.Load(reader);
    }

    /// <summary>A part's XML read safely, or null when it is not well-formed or has a DTD (Excel's VML is not always XML).</summary>
    public static XDocument? TryLoad(OpcPart part)
    {
        try
        {
            return part.LoadXml();
        }
        catch (DocumentException)
        {
            return null;
        }
    }
}
