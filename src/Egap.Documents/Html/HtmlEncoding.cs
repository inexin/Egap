using System.Text;

namespace Egap.Documents.Html;

/// <summary>
/// An HTML byte stream's encoding (WHATWG 13.2.3.2): a byte order mark, else the prescan of the first 1024 bytes for a meta
/// charset or http-equiv pragma or an XML declaration, else windows-1252. Labels follow the Encoding Standard.
/// </summary>
internal static class HtmlEncoding
{
    // The Encoding Standard's labels (encodings.json, WHATWG, CC BY 4.0): label=name, space separated.
    private static readonly Dictionary<string, string> Labels = Build(
        "unicode-1-1-utf-8=UTF-8 unicode11utf8=UTF-8 unicode20utf8=UTF-8 utf-8=UTF-8 utf8=UTF-8 " +
        "x-unicode20utf8=UTF-8 866=IBM866 cp866=IBM866 csibm866=IBM866 ibm866=IBM866 csisolatin2=ISO-8859-2 " +
        "iso-8859-2=ISO-8859-2 iso-ir-101=ISO-8859-2 iso8859-2=ISO-8859-2 iso88592=ISO-8859-2 " +
        "iso_8859-2=ISO-8859-2 iso_8859-2:1987=ISO-8859-2 l2=ISO-8859-2 latin2=ISO-8859-2 " +
        "csisolatin3=ISO-8859-3 iso-8859-3=ISO-8859-3 iso-ir-109=ISO-8859-3 iso8859-3=ISO-8859-3 " +
        "iso88593=ISO-8859-3 iso_8859-3=ISO-8859-3 iso_8859-3:1988=ISO-8859-3 l3=ISO-8859-3 " +
        "latin3=ISO-8859-3 csisolatin4=ISO-8859-4 iso-8859-4=ISO-8859-4 iso-ir-110=ISO-8859-4 " +
        "iso8859-4=ISO-8859-4 iso88594=ISO-8859-4 iso_8859-4=ISO-8859-4 iso_8859-4:1988=ISO-8859-4 " +
        "l4=ISO-8859-4 latin4=ISO-8859-4 csisolatincyrillic=ISO-8859-5 cyrillic=ISO-8859-5 " +
        "iso-8859-5=ISO-8859-5 iso-ir-144=ISO-8859-5 iso8859-5=ISO-8859-5 iso88595=ISO-8859-5 " +
        "iso_8859-5=ISO-8859-5 iso_8859-5:1988=ISO-8859-5 arabic=ISO-8859-6 asmo-708=ISO-8859-6 " +
        "csiso88596e=ISO-8859-6 csiso88596i=ISO-8859-6 csisolatinarabic=ISO-8859-6 ecma-114=ISO-8859-6 " +
        "iso-8859-6=ISO-8859-6 iso-8859-6-e=ISO-8859-6 iso-8859-6-i=ISO-8859-6 iso-ir-127=ISO-8859-6 " +
        "iso8859-6=ISO-8859-6 iso88596=ISO-8859-6 iso_8859-6=ISO-8859-6 iso_8859-6:1987=ISO-8859-6 " +
        "csisolatingreek=ISO-8859-7 ecma-118=ISO-8859-7 elot_928=ISO-8859-7 greek=ISO-8859-7 " +
        "greek8=ISO-8859-7 iso-8859-7=ISO-8859-7 iso-ir-126=ISO-8859-7 iso8859-7=ISO-8859-7 " +
        "iso88597=ISO-8859-7 iso_8859-7=ISO-8859-7 iso_8859-7:1987=ISO-8859-7 sun_eu_greek=ISO-8859-7 " +
        "csiso88598e=ISO-8859-8 csisolatinhebrew=ISO-8859-8 hebrew=ISO-8859-8 iso-8859-8=ISO-8859-8 " +
        "iso-8859-8-e=ISO-8859-8 iso-ir-138=ISO-8859-8 iso8859-8=ISO-8859-8 iso88598=ISO-8859-8 " +
        "iso_8859-8=ISO-8859-8 iso_8859-8:1988=ISO-8859-8 visual=ISO-8859-8 csiso88598i=ISO-8859-8-I " +
        "iso-8859-8-i=ISO-8859-8-I logical=ISO-8859-8-I csisolatin6=ISO-8859-10 iso-8859-10=ISO-8859-10 " +
        "iso-ir-157=ISO-8859-10 iso8859-10=ISO-8859-10 iso885910=ISO-8859-10 l6=ISO-8859-10 " +
        "latin6=ISO-8859-10 iso-8859-13=ISO-8859-13 iso8859-13=ISO-8859-13 iso885913=ISO-8859-13 " +
        "iso-8859-14=ISO-8859-14 iso8859-14=ISO-8859-14 iso885914=ISO-8859-14 csisolatin9=ISO-8859-15 " +
        "iso-8859-15=ISO-8859-15 iso8859-15=ISO-8859-15 iso885915=ISO-8859-15 iso_8859-15=ISO-8859-15 " +
        "l9=ISO-8859-15 iso-8859-16=ISO-8859-16 cskoi8r=KOI8-R koi=KOI8-R koi8=KOI8-R koi8-r=KOI8-R " +
        "koi8_r=KOI8-R koi8-ru=KOI8-U koi8-u=KOI8-U csmacintosh=macintosh mac=macintosh macintosh=macintosh " +
        "x-mac-roman=macintosh dos-874=windows-874 iso-8859-11=windows-874 iso8859-11=windows-874 " +
        "iso885911=windows-874 tis-620=windows-874 windows-874=windows-874 cp1250=windows-1250 " +
        "windows-1250=windows-1250 x-cp1250=windows-1250 cp1251=windows-1251 windows-1251=windows-1251 " +
        "x-cp1251=windows-1251 ansi_x3.4-1968=windows-1252 ascii=windows-1252 cp1252=windows-1252 " +
        "cp819=windows-1252 csisolatin1=windows-1252 ibm819=windows-1252 iso-8859-1=windows-1252 " +
        "iso-ir-100=windows-1252 iso8859-1=windows-1252 iso88591=windows-1252 iso_8859-1=windows-1252 " +
        "iso_8859-1:1987=windows-1252 l1=windows-1252 latin1=windows-1252 us-ascii=windows-1252 " +
        "windows-1252=windows-1252 x-cp1252=windows-1252 cp1253=windows-1253 windows-1253=windows-1253 " +
        "x-cp1253=windows-1253 cp1254=windows-1254 csisolatin5=windows-1254 iso-8859-9=windows-1254 " +
        "iso-ir-148=windows-1254 iso8859-9=windows-1254 iso88599=windows-1254 iso_8859-9=windows-1254 " +
        "iso_8859-9:1989=windows-1254 l5=windows-1254 latin5=windows-1254 windows-1254=windows-1254 " +
        "x-cp1254=windows-1254 cp1255=windows-1255 windows-1255=windows-1255 x-cp1255=windows-1255 " +
        "cp1256=windows-1256 windows-1256=windows-1256 x-cp1256=windows-1256 cp1257=windows-1257 " +
        "windows-1257=windows-1257 x-cp1257=windows-1257 cp1258=windows-1258 windows-1258=windows-1258 " +
        "x-cp1258=windows-1258 x-mac-cyrillic=x-mac-cyrillic x-mac-ukrainian=x-mac-cyrillic chinese=GBK " +
        "csgb2312=GBK csiso58gb231280=GBK gb2312=GBK gb_2312=GBK gb_2312-80=GBK gbk=GBK iso-ir-58=GBK " +
        "x-gbk=GBK gb18030=gb18030 big5=Big5 big5-hkscs=Big5 cn-big5=Big5 csbig5=Big5 x-x-big5=Big5 " +
        "cseucpkdfmtjapanese=EUC-JP euc-jp=EUC-JP x-euc-jp=EUC-JP csiso2022jp=ISO-2022-JP " +
        "iso-2022-jp=ISO-2022-JP csshiftjis=Shift_JIS ms932=Shift_JIS ms_kanji=Shift_JIS shift-jis=Shift_JIS " +
        "shift_jis=Shift_JIS sjis=Shift_JIS windows-31j=Shift_JIS x-sjis=Shift_JIS cseuckr=EUC-KR " +
        "csksc56011987=EUC-KR euc-kr=EUC-KR iso-ir-149=EUC-KR korean=EUC-KR ks_c_5601-1987=EUC-KR " +
        "ks_c_5601-1989=EUC-KR ksc5601=EUC-KR ksc_5601=EUC-KR windows-949=EUC-KR csiso2022kr=replacement " +
        "hz-gb-2312=replacement iso-2022-cn=replacement iso-2022-cn-ext=replacement iso-2022-kr=replacement " +
        "replacement=replacement unicodefffe=UTF-16BE utf-16be=UTF-16BE csunicode=UTF-16LE " +
        "iso-10646-ucs-2=UTF-16LE ucs-2=UTF-16LE unicode=UTF-16LE unicodefeff=UTF-16LE utf-16=UTF-16LE " +
        "utf-16le=UTF-16LE x-user-defined=x-user-defined ");

    static HtmlEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>The encoding name for a label (trimmed, case-insensitive), or null for none.</summary>
    public static string? FromLabel(string label)
    {
        return Labels.TryGetValue(label.Trim('\t', '\n', '\f', '\r', ' '), out string? name) ? name : null;
    }

    /// <summary>The encoding the bytes declare or start with, and whether a byte order mark settled it.</summary>
    public static (string Name, int BomLength) Sniff(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return ("UTF-8", 3);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return ("UTF-16BE", 2);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return ("UTF-16LE", 2);
        }

        return (Prescan(bytes[..Math.Min(bytes.Length, 1024)]) ?? "windows-1252", 0);
    }

    /// <summary>The bytes decoded: the sniffed encoding unless <paramref name="encodingName"/> names one.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, string? encodingName = null)
    {
        (string sniffed, int bom) = Sniff(bytes);
        string name = encodingName is not null && FromLabel(encodingName) is { } given ? given : sniffed;
        return DotNetEncoding(name).GetString(bytes[(name == sniffed ? bom : 0)..]);
    }

    /// <summary>The .NET encoding for an Encoding Standard name; windows-1252 for those .NET lacks.</summary>
    public static Encoding DotNetEncoding(string name)
    {
        string dotnet = name switch
        {
            "UTF-8" => "utf-8",
            "UTF-16BE" => "utf-16BE",
            "UTF-16LE" => "utf-16",
            "ISO-8859-8-I" => "iso-8859-8",
            "x-mac-cyrillic" => "x-mac-cyrillic",
            "GBK" => "gb2312",
            "gb18030" => "GB18030",
            "Big5" => "big5",
            "EUC-JP" => "euc-jp",
            "ISO-2022-JP" => "iso-2022-jp",
            "Shift_JIS" => "shift_jis",
            "EUC-KR" => "euc-kr",
            "x-user-defined" or "replacement" => "windows-1252",
            _ => name,
        };
        try
        {
            return Encoding.GetEncoding(dotnet, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (ArgumentException)
        {
            return Encoding.GetEncoding(1252);
        }
    }

    private static Dictionary<string, string> Build(string pairs)
    {
        Dictionary<string, string> table = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            table[pair[..equals]] = pair[(equals + 1)..];
        }

        return table;
    }

    /// <summary>The prescan (13.2.3.2): meta charset, a meta pragma's content, else an XML declaration's encoding.</summary>
    private static string? Prescan(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x3C, 0x00, 0x3F, 0x00, 0x78, 0x00]))
        {
            return "UTF-16LE";
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x3C, 0x00, 0x3F, 0x00, 0x78]))
        {
            return "UTF-16BE";
        }

        int position = 0;
        while (position < bytes.Length)
        {
            if (bytes[position..].StartsWith("<!--"u8))
            {
                int end = bytes[(position + 2)..].IndexOf("-->"u8);
                if (end < 0)
                {
                    return XmlEncoding(bytes);
                }

                position += 2 + end + 3;
                continue;
            }

            if (IsMetaStart(bytes[position..]))
            {
                position += 5;
                List<string> seen = [];
                bool gotPragma = false;
                bool? needPragma = null;
                string? charset = null;
                bool charsetFailed = false;
                while (Attribute(bytes, ref position) is { } attribute)
                {
                    if (seen.Contains(attribute.Name))
                    {
                        continue;
                    }

                    seen.Add(attribute.Name);
                    switch (attribute.Name)
                    {
                        case "http-equiv" when attribute.Value == "content-type":
                            gotPragma = true;
                            break;
                        case "content" when charset is null && !charsetFailed:
                            if (MetaCharset(attribute.Value) is { } fromContent)
                            {
                                charset = FromLabel(fromContent);
                                charsetFailed = charset is null;
                                needPragma = true;
                            }

                            break;
                        case "charset":
                            charset = FromLabel(attribute.Value);
                            charsetFailed = charset is null;
                            needPragma = false;
                            break;
                    }
                }

                if (position >= bytes.Length)
                {
                    return XmlEncoding(bytes);
                }

                if (needPragma is not null && !(needPragma == true && !gotPragma) && charset is not null)
                {
                    return charset switch
                    {
                        "UTF-16BE" or "UTF-16LE" => "UTF-8",
                        "x-user-defined" => "windows-1252",
                        _ => charset,
                    };
                }

                position++;
                continue;
            }

            if (bytes[position] == '<' && position + 1 < bytes.Length
                && (IsAsciiLetter(bytes[position + 1]) || (bytes[position + 1] == '/' && position + 2 < bytes.Length && IsAsciiLetter(bytes[position + 2]))))
            {
                while (position < bytes.Length && bytes[position] is not ((byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)' ' or (byte)'>'))
                {
                    position++;
                }

                while (Attribute(bytes, ref position) is not null)
                {
                }

                if (position >= bytes.Length)
                {
                    return XmlEncoding(bytes);
                }

                position++;
                continue;
            }

            if (bytes[position..].StartsWith("<!"u8) || bytes[position..].StartsWith("</"u8) || bytes[position..].StartsWith("<?"u8))
            {
                int end = bytes[(position + 1)..].IndexOf((byte)'>');
                if (end < 0)
                {
                    return XmlEncoding(bytes);
                }

                position += 1 + end + 1;
                continue;
            }

            position++;
        }

        return XmlEncoding(bytes);
    }

    private static bool IsAsciiLetter(byte b)
    {
        return b is (>= (byte)'A' and <= (byte)'Z') or (>= (byte)'a' and <= (byte)'z');
    }

    private static bool IsSpace(byte b)
    {
        return b is (byte)'\t' or (byte)'\n' or (byte)'\f' or (byte)'\r' or (byte)' ';
    }

    private static bool IsMetaStart(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= 6 && bytes[0] == '<' && (bytes[1] | 0x20) == 'm' && (bytes[2] | 0x20) == 'e' && (bytes[3] | 0x20) == 't'
            && (bytes[4] | 0x20) == 'a' && (IsSpace(bytes[5]) || bytes[5] == '/');
    }

    /// <summary>The prescan's "get an attribute": a name and value lowercased, or null at '>' or the end.</summary>
    private static (string Name, string Value)? Attribute(ReadOnlySpan<byte> bytes, ref int position)
    {
        while (position < bytes.Length && (IsSpace(bytes[position]) || bytes[position] == '/'))
        {
            position++;
        }

        if (position >= bytes.Length || bytes[position] == '>')
        {
            return null;
        }

        StringBuilder name = new();
        StringBuilder value = new();
        while (true)
        {
            if (position >= bytes.Length)
            {
                return null;
            }

            byte b = bytes[position];
            if (b == '=' && name.Length > 0)
            {
                position++;
                break;
            }

            if (IsSpace(b))
            {
                while (position < bytes.Length && IsSpace(bytes[position]))
                {
                    position++;
                }

                if (position >= bytes.Length)
                {
                    return null;
                }

                if (bytes[position] != '=')
                {
                    return (name.ToString(), "");
                }

                position++;
                break;
            }

            if (b is (byte)'/' or (byte)'>')
            {
                return (name.ToString(), "");
            }

            name.Append((char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 0x20 : b));
            position++;
        }

        while (position < bytes.Length && IsSpace(bytes[position]))
        {
            position++;
        }

        if (position >= bytes.Length)
        {
            return null;
        }

        byte first = bytes[position];
        if (first is (byte)'"' or (byte)'\'')
        {
            while (true)
            {
                position++;
                if (position >= bytes.Length)
                {
                    return null;
                }

                byte b = bytes[position];
                if (b == first)
                {
                    position++;
                    return (name.ToString(), value.ToString());
                }

                value.Append((char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 0x20 : b));
            }
        }

        if (first == '>')
        {
            return (name.ToString(), "");
        }

        while (position < bytes.Length)
        {
            byte b = bytes[position];
            if (IsSpace(b) || b == '>')
            {
                return (name.ToString(), value.ToString());
            }

            value.Append((char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 0x20 : b));
            position++;
        }

        return null;
    }

    /// <summary>The charset in a meta content attribute ("text/html; charset=utf-8"), or null.</summary>
    internal static string? MetaCharset(string content)
    {
        int position = 0;
        while (true)
        {
            int found = content.IndexOf("charset", position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return null;
            }

            position = found + 7;
            while (position < content.Length && content[position] is '\t' or '\n' or '\f' or '\r' or ' ')
            {
                position++;
            }

            if (position >= content.Length || content[position] != '=')
            {
                continue;
            }

            position++;
            while (position < content.Length && content[position] is '\t' or '\n' or '\f' or '\r' or ' ')
            {
                position++;
            }

            if (position >= content.Length)
            {
                return null;
            }

            char quote = content[position];
            if (quote is '"' or '\'')
            {
                int end = content.IndexOf(quote, position + 1);
                return end < 0 ? null : content[(position + 1)..end];
            }

            int stop = position;
            while (stop < content.Length && content[stop] is not ('\t' or '\n' or '\f' or '\r' or ' ' or ';'))
            {
                stop++;
            }

            return content[position..stop];
        }
    }

    /// <summary>"Get an XML encoding": an encoding="..." in a leading XML declaration.</summary>
    private static string? XmlEncoding(ReadOnlySpan<byte> bytes)
    {
        if (!bytes.StartsWith("<?xml"u8))
        {
            return null;
        }

        int end = bytes.IndexOf((byte)'>');
        if (end < 0)
        {
            return null;
        }

        ReadOnlySpan<byte> declaration = bytes[..end];
        int at = declaration.IndexOf("encoding"u8);
        if (at < 0)
        {
            return null;
        }

        at += 8;
        while (at < declaration.Length && declaration[at] <= 0x20)
        {
            at++;
        }

        if (at >= declaration.Length || declaration[at] != '=')
        {
            return null;
        }

        at++;
        while (at < declaration.Length && declaration[at] <= 0x20)
        {
            at++;
        }

        if (at >= declaration.Length || declaration[at] is not ((byte)'"' or (byte)'\''))
        {
            return null;
        }

        byte quote = declaration[at++];
        int close = declaration[at..].IndexOf(quote);
        if (close < 0)
        {
            return null;
        }

        ReadOnlySpan<byte> label = declaration.Slice(at, close);
        foreach (byte b in label)
        {
            if (b <= 0x20)
            {
                return null;
            }
        }

        string? encoding = FromLabel(Encoding.Latin1.GetString(label));
        return encoding is "UTF-16BE" or "UTF-16LE" ? "UTF-8" : encoding;
    }
}
