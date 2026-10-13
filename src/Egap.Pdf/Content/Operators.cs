using System.Collections.Frozen;

namespace Egap.Pdf.Content;

/// <summary>
/// The content stream operators of ISO 32000-2 (table A.1) with their operand counts, so the lexer can intern operator names
/// and check what it reads. Every operator is at most three bytes, so each is looked up by its bytes packed into an integer.
/// </summary>
internal static class Operators
{
    /// <summary>An operand count that varies: the colour operators <c>SC</c>, <c>SCN</c>, <c>sc</c> and <c>scn</c>.</summary>
    public const int Variable = -1;

    private static readonly FrozenDictionary<int, (string Name, int Operands)> Table = Build();

    /// <summary>
    /// Looks up an operator by its bytes: the interned name and the operand count, or <see langword="false"/> for an operator
    /// the spec does not define.
    /// </summary>
    public static bool TryGet(ReadOnlySpan<byte> bytes, out string name, out int operands)
    {
        if (bytes.Length is > 0 and <= 3 && Table.TryGetValue(Key(bytes), out (string Name, int Operands) entry))
        {
            name = entry.Name;
            operands = entry.Operands;
            return true;
        }

        name = "";
        operands = 0;
        return false;
    }

    /// <summary>The operand count of a known operator, or <see langword="null"/>.</summary>
    public static int? OperandCount(string name)
    {
        return name.Length is > 0 and <= 3 && Table.TryGetValue(Key(name), out (string Name, int Operands) entry) ? entry.Operands : null;
    }

    private static int Key(ReadOnlySpan<byte> bytes)
    {
        int key = 0;
        foreach (byte b in bytes)
        {
            key = (key << 8) | b;
        }

        return key;
    }

    private static int Key(string name)
    {
        int key = 0;
        foreach (char c in name)
        {
            key = (key << 8) | (c & 0xFF);
        }

        return key;
    }

    private static FrozenDictionary<int, (string, int)> Build()
    {
        (string Name, int Operands)[] operators =
        [
            // General graphics state (8.4.4).
            ("w", 1), ("J", 1), ("j", 1), ("M", 1), ("d", 2), ("ri", 1), ("i", 1), ("gs", 1),

            // Special graphics state (8.4.2).
            ("q", 0), ("Q", 0), ("cm", 6),

            // Path construction (8.5.2).
            ("m", 2), ("l", 2), ("c", 6), ("v", 4), ("y", 4), ("h", 0), ("re", 4),

            // Path painting (8.5.3) and clipping (8.5.4).
            ("S", 0), ("s", 0), ("f", 0), ("F", 0), ("f*", 0), ("B", 0), ("B*", 0), ("b", 0), ("b*", 0), ("n", 0), ("W", 0), ("W*", 0),

            // Text objects, state, positioning and showing (9.3, 9.4).
            ("BT", 0), ("ET", 0), ("Tc", 1), ("Tw", 1), ("Tz", 1), ("TL", 1), ("Tf", 2), ("Tr", 1), ("Ts", 1),
            ("Td", 2), ("TD", 2), ("Tm", 6), ("T*", 0), ("Tj", 1), ("TJ", 1), ("'", 1), ("\"", 3),

            // Type 3 fonts (9.6.4).
            ("d0", 2), ("d1", 6),

            // Colour (8.6.8).
            ("CS", 1), ("cs", 1), ("SC", Variable), ("SCN", Variable), ("sc", Variable), ("scn", Variable),
            ("G", 1), ("g", 1), ("RG", 3), ("rg", 3), ("K", 4), ("k", 4),

            // Shading, XObjects and inline images (8.7.4.2, 8.8, 8.9.7).
            ("sh", 1), ("Do", 1), ("BI", 0), ("ID", 0), ("EI", 0),

            // Marked content (14.6) and compatibility (7.8.2).
            ("MP", 1), ("DP", 2), ("BMC", 1), ("BDC", 2), ("EMC", 0), ("BX", 0), ("EX", 0),
        ];

        return operators.ToFrozenDictionary(o => Key(o.Name), o => (string.Intern(o.Name), o.Operands));
    }
}
