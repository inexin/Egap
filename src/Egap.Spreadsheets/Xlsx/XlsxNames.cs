using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using Egap.Documents.Docx;

namespace Egap.Spreadsheets.Xlsx;

/// <summary>SpreadsheetML's namespaces, relationship types and content types (ECMA-376 Part 1, chapter 18, and MS-XLSX).</summary>
internal static class XlsxNames
{
    public static readonly XNamespace Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public static readonly XNamespace Xml = XNamespace.Xml;

    public const string MainUri = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    public const string StrictMainUri = "http://purl.oclc.org/ooxml/spreadsheetml/main";
    public const string RUri = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    public const string StrictRUri = "http://purl.oclc.org/ooxml/officeDocument/relationships";

    private const string Office = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/";
    public const string WorksheetRelationship = Office + "worksheet";
    public const string SharedStringsRelationship = Office + "sharedStrings";
    public const string StylesRelationship = Office + "styles";
    public const string CalcChainRelationship = Office + "calcChain";

    private const string Ml = "application/vnd.openxmlformats-officedocument.spreadsheetml.";
    public const string WorksheetType = Ml + "worksheet+xml";
    public const string SharedStringsType = Ml + "sharedStrings+xml";
    public const string StylesType = Ml + "styles+xml";

    /// <summary>The content type of the workbook part for each kind of file.</summary>
    public static string WorkbookType(WorkbookFormat format) => format switch
    {
        WorkbookFormat.Xlsm => "application/vnd.ms-excel.sheet.macroEnabled.main+xml",
        WorkbookFormat.Xltx => Ml + "template.main+xml",
        WorkbookFormat.Xltm => "application/vnd.ms-excel.template.macroEnabled.main+xml",
        _ => Ml + "sheet.main+xml",
    };

    /// <summary>The kind of file a workbook part's content type says, or null when it is not a workbook's.</summary>
    public static WorkbookFormat? FormatOf(string contentType) => contentType switch
    {
        Ml + "sheet.main+xml" => WorkbookFormat.Xlsx,
        "application/vnd.ms-excel.sheet.macroEnabled.main+xml" => WorkbookFormat.Xlsm,
        Ml + "template.main+xml" => WorkbookFormat.Xltx,
        "application/vnd.ms-excel.template.macroEnabled.main+xml" => WorkbookFormat.Xltm,
        _ => null,
    };

    public static bool IsMain(string namespaceUri) => namespaceUri is MainUri or StrictMainUri;

    public static bool IsRelationships(string namespaceUri) => namespaceUri is RUri or StrictRUri;

    public static bool SameType(string a, string b) => OpcRelationshipTypes.Same(a, b);

    /// <summary>A number as SpreadsheetML writes it: the shortest text that reads back as the same double.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    public static string Integer(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    public static int Int(string? text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : fallback;

    /// <summary>An xsd:boolean: 1 or true.</summary>
    public static bool Bool(string? text, bool fallback) => text switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => fallback,
    };

    /// <summary>
    /// Text with SpreadsheetML's <c>_xHHHH_</c> escapes decoded: Excel writes characters XML cannot hold, and carriage
    /// returns, as <c>_x000D_</c>, and a literal <c>_x</c> that would look like an escape as <c>_x005F_x</c>.
    /// </summary>
    public static string Decode(string text)
    {
        int at = text.IndexOf("_x", StringComparison.Ordinal);
        if (at < 0)
        {
            return text;
        }

        StringBuilder decoded = new(text.Length);
        int from = 0;
        while (at >= 0)
        {
            if (at + 7 <= text.Length && text[at + 6] == '_' && ushort.TryParse(text.AsSpan(at + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
            {
                decoded.Append(text, from, at - from).Append((char)code);
                from = at + 7;
                at = text.IndexOf("_x", from, StringComparison.Ordinal);
            }
            else
            {
                at = text.IndexOf("_x", at + 2, StringComparison.Ordinal);
            }
        }

        return decoded.Append(text, from, text.Length - from).ToString();
    }

    /// <summary>Text with the characters XML cannot hold, and anything that would read as an escape, escaped as <c>_xHHHH_</c>.</summary>
    public static string Encode(string text)
    {
        int i = 0;
        for (; i < text.Length; i++)
        {
            if (NeedsEscape(text, i))
            {
                break;
            }
        }

        if (i == text.Length)
        {
            return text;
        }

        StringBuilder encoded = new(text.Length + 16);
        encoded.Append(text, 0, i);
        for (; i < text.Length; i++)
        {
            if (NeedsEscape(text, i))
            {
                encoded.Append("_x").Append(((int)text[i]).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
            }
            else
            {
                encoded.Append(text[i]);
            }
        }

        return encoded.ToString();
    }

    private static bool NeedsEscape(string text, int i)
    {
        char c = text[i];
        if (c == '_')
        {
            return i + 6 < text.Length && text[i + 1] == 'x' && text[i + 6] == '_' && IsHex(text, i + 2, 4);
        }

        if (char.IsSurrogate(c))
        {
            return char.IsHighSurrogate(c) ? i + 1 >= text.Length || !char.IsLowSurrogate(text[i + 1]) : i == 0 || !char.IsHighSurrogate(text[i - 1]);
        }

        return c == '\r' || !XmlConvert.IsXmlChar(c);
    }

    private static bool IsHex(string text, int start, int length)
    {
        for (int i = start; i < start + length; i++)
        {
            if (!char.IsAsciiHexDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
