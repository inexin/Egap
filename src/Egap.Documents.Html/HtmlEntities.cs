using System.Globalization;
using System.Text;

namespace Egap.Documents;

/// <summary>
/// The HTML named character references (WHATWG list, entities.txt) and the numeric ones, for the Markdown and HTML readers.
/// Names include the terminating semicolon where the list has it; 106 legacy names also work without one.
/// </summary>
internal static class HtmlEntities
{
    private static readonly Lazy<Dictionary<string, string>> Table = new(Load);

    /// <summary>The longest name in the list, semicolon included.</summary>
    public const int MaxNameLength = 32;

    /// <summary>The characters for <paramref name="name"/> (without the ampersand, with its semicolon if any), or null.</summary>
    public static string? Lookup(ReadOnlySpan<char> name)
    {
        return Table.Value.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out string? value) ? value : null;
    }

    /// <summary>
    /// The character a numeric reference names, with the HTML rules: 0, surrogates and values past U+10FFFF become U+FFFD;
    /// the C1 controls Windows-1252 has letters for become those letters when <paramref name="windows1252"/> is set.
    /// </summary>
    public static string FromCodePoint(long codePoint, bool windows1252)
    {
        if (codePoint == 0 || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
        {
            return "�";
        }

        if (windows1252 && codePoint is >= 0x80 and <= 0x9F && Windows1252[codePoint - 0x80] is var mapped && mapped != 0)
        {
            return ((char)mapped).ToString();
        }

        return char.ConvertFromUtf32((int)codePoint);
    }

    /// <summary>The replacements HTML makes for numeric references to the C1 range (0x80 to 0x9F); 0 means none.</summary>
    private static readonly ushort[] Windows1252 =
    [
        0x20AC, 0, 0x201A, 0x0192, 0x201E, 0x2026, 0x2020, 0x2021, 0x02C6, 0x2030, 0x0160, 0x2039, 0x0152, 0, 0x017D, 0,
        0, 0x2018, 0x2019, 0x201C, 0x201D, 0x2022, 0x2013, 0x2014, 0x02DC, 0x2122, 0x0161, 0x203A, 0x0153, 0, 0x017E, 0x0178,
    ];

    private static Dictionary<string, string> Load()
    {
        Dictionary<string, string> table = new(2300, StringComparer.Ordinal);
        using Stream stream = typeof(HtmlEntities).Assembly.GetManifestResourceStream("Egap.Documents.Html.entities.txt")
            ?? throw new InvalidOperationException("The entity list is missing from the assembly.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int tab = line.IndexOf('\t');
            StringBuilder value = new();
            foreach (string hex in line[(tab + 1)..].Split(' '))
            {
                value.Append(char.ConvertFromUtf32(int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
            }

            table[line[..tab]] = value.ToString();
        }

        return table;
    }
}
