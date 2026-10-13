using System.Globalization;
using System.Text;

namespace Egap.Pdf.Fonts;

/// <summary>
/// Glyph names to Unicode, as the Adobe Glyph List Specification says: a suffix after a period is dropped, ligature parts
/// joined by underscores are mapped one by one, and each part is a name from the Adobe Glyph List (bundled as
/// <c>glyphlist.txt</c>), a <c>uniXXXX</c> sequence or a <c>uXXXX</c> to <c>uXXXXXX</c> code point. Without the bundled list
/// the names of the standard Latin encodings are still known.
/// </summary>
internal static class GlyphList
{
    private static readonly Lazy<Dictionary<string, string>> Names = new(Load);

    /// <summary>The text a glyph name stands for, or null when it says none (such as <c>.notdef</c> or <c>g123</c>).</summary>
    public static string? ToUnicode(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        int dot = name.IndexOf('.', StringComparison.Ordinal);
        string stem = dot >= 0 ? name[..dot] : name;
        if (stem.Length == 0)
        {
            return null;
        }

        if (!stem.Contains('_', StringComparison.Ordinal))
        {
            return Component(stem);
        }

        StringBuilder text = new();
        foreach (string part in stem.Split('_'))
        {
            if (Component(part) is string piece)
            {
                text.Append(piece);
            }
        }

        return text.Length > 0 ? text.ToString() : null;
    }

    private static string? Component(string name)
    {
        if (Names.Value.TryGetValue(name, out string? text))
        {
            return text;
        }

        // uniXXXX, uniXXXXYYYY...: BMP code points in groups of four, surrogates excluded.
        if (name.Length >= 7 && name.StartsWith("uni", StringComparison.Ordinal) && (name.Length - 3) % 4 == 0)
        {
            StringBuilder text2 = new();
            for (int i = 3; i < name.Length; i += 4)
            {
                if (!int.TryParse(name.AsSpan(i, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp)
                    || !IsUpperHex(name.AsSpan(i, 4)) || cp is >= 0xD800 and <= 0xDFFF)
                {
                    return null;
                }

                text2.Append((char)cp);
            }

            return text2.ToString();
        }

        // uXXXX to uXXXXXX: one code point.
        if (name.Length is >= 5 and <= 7 && name[0] == 'u'
            && int.TryParse(name.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int value)
            && IsUpperHex(name.AsSpan(1)) && value <= 0x10FFFF && value is < 0xD800 or > 0xDFFF)
        {
            return char.ConvertFromUtf32(value);
        }

        return null;
    }

    private static bool IsUpperHex(ReadOnlySpan<char> digits)
    {
        foreach (char c in digits)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'A' and <= 'F')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The bundled Adobe Glyph List when present, over the names of WinAnsi and MacRoman.</summary>
    private static Dictionary<string, string> Load()
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach ((string name, char c) in FontEncodings.LatinNames())
        {
            names.TryAdd(name, c.ToString());
        }

        using Stream? stream = typeof(GlyphList).Assembly.GetManifestResourceStream("Egap.Pdf.Fonts.glyphlist.txt");
        if (stream is null)
        {
            return names;
        }

        // aglfn and glyphlist lines: name;XXXX[ YYYY...] after comments starting with #.
        using StreamReader reader = new(stream, Encoding.ASCII);
        for (string? line = reader.ReadLine(); line is not null; line = reader.ReadLine())
        {
            int semicolon = line.IndexOf(';', StringComparison.Ordinal);
            if (line.StartsWith('#') || semicolon <= 0)
            {
                continue;
            }

            StringBuilder text = new();
            foreach (string hex in line[(semicolon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int cp) && cp <= 0x10FFFF)
                {
                    text.Append(char.ConvertFromUtf32(cp));
                }
            }

            names[line[..semicolon]] = text.ToString();
        }

        return names;
    }
}
