namespace Egap.Documents.OfficeMath;

/// <summary>The spacing class of a math atom (TeX's atom types, which Office Math spaces by too).</summary>
internal enum MathClass
{
    /// <summary>An ordinary symbol: a letter, a digit, a whole object.</summary>
    Ordinary,

    /// <summary>A large operator, such as a sum or a function name.</summary>
    Operator,

    /// <summary>A binary operator, such as + or ×.</summary>
    Binary,

    /// <summary>A relation, such as = or →.</summary>
    Relation,

    /// <summary>An opening delimiter.</summary>
    Open,

    /// <summary>A closing delimiter.</summary>
    Close,

    /// <summary>Punctuation, such as a comma.</summary>
    Punctuation,

    /// <summary>A delimited subformula or a fraction.</summary>
    Inner,
}

/// <summary>
/// Math characters: the Unicode Mathematical Alphanumeric Symbols a run's style and script select (UTR #25, Unicode §22.2),
/// and the spacing class of each character.
/// </summary>
internal static class MathAlphabets
{
    /// <summary>
    /// The character for <paramref name="c"/> in a math run with style <paramref name="style"/> (p, b, i, bi; null for the
    /// default, italic letters) and script <paramref name="script"/> (roman, script, fraktur, double-struck, sans-serif,
    /// monospace; null for roman). Characters with no math form come back unchanged; a hyphen is a minus sign.
    /// </summary>
    public static int Map(int c, string? style, string? script)
    {
        if (c == '-')
        {
            return 0x2212;
        }

        if (c == '*')
        {
            return 0x2217;
        }

        bool latin = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        bool greek = c is >= 0x391 and <= 0x3A9 and not 0x3A2 or >= 0x3B1 and <= 0x3C9;
        bool digit = c is >= '0' and <= '9';
        style ??= latin || greek ? "i" : "p";
        bool bold = style is "b" or "bi";
        bool italic = style is "i" or "bi";
        script ??= "roman";

        if (digit)
        {
            int d = c - '0';
            return script switch
            {
                "double-struck" => 0x1D7D8 + d,
                "sans-serif" => (bold ? 0x1D7EC : 0x1D7E2) + d,
                "monospace" => 0x1D7F6 + d,
                _ => bold ? 0x1D7CE + d : c,
            };
        }

        if (greek)
        {
            if (script is not ("roman" or "sans-serif"))
            {
                return c;
            }

            int index = c >= 0x3B1 ? 26 + (c - 0x3B1) : c - 0x391;
            int start = (script, bold, italic) switch
            {
                ("roman", false, false) => 0,
                ("roman", true, false) => 0x1D6A8,
                ("roman", false, true) => 0x1D6E2,
                ("roman", true, true) => 0x1D71C,
                ("sans-serif", _, false) => 0x1D756,
                ("sans-serif", _, true) => 0x1D790,
                _ => 0,
            };
            if (start == 0)
            {
                return c;
            }

            // Each math Greek block has the capitals (with capital theta symbol in the slot of the unassigned U+03A2), nabla,
            // then the small letters.
            return start + index;
        }

        if (!latin)
        {
            return c;
        }

        int letter = c >= 'a' ? 26 + (c - 'a') : c - 'A';
        int block = (script, bold, italic) switch
        {
            ("roman", false, false) => -1,
            ("roman", true, false) => 0x1D400,
            ("roman", false, true) => 0x1D434,
            ("roman", true, true) => 0x1D468,
            ("script", false, _) => 0x1D49C,
            ("script", true, _) => 0x1D4D0,
            ("fraktur", false, _) => 0x1D504,
            ("fraktur", true, _) => 0x1D56C,
            ("double-struck", _, _) => 0x1D538,
            ("sans-serif", false, false) => 0x1D5A0,
            ("sans-serif", true, false) => 0x1D5D4,
            ("sans-serif", false, true) => 0x1D608,
            ("sans-serif", true, true) => 0x1D63C,
            ("monospace", _, _) => 0x1D670,
            _ => -1,
        };
        if (block < 0)
        {
            return c;
        }

        int mapped = block + letter;

        // The letters encoded earlier in Letterlike Symbols leave holes in the math blocks.
        return mapped switch
        {
            0x1D455 => 0x210E, // italic h
            0x1D49D => 0x212C, // script B
            0x1D4A0 => 0x2130, // script E
            0x1D4A1 => 0x2131, // script F
            0x1D4A3 => 0x210B, // script H
            0x1D4A4 => 0x2110, // script I
            0x1D4A7 => 0x2112, // script L
            0x1D4A8 => 0x2133, // script M
            0x1D4AD => 0x211B, // script R
            0x1D4BA => 0x212F, // script e
            0x1D4BC => 0x210A, // script g
            0x1D4C4 => 0x2134, // script o
            0x1D506 => 0x212D, // fraktur C
            0x1D50B => 0x210C, // fraktur H
            0x1D50C => 0x2111, // fraktur I
            0x1D515 => 0x211C, // fraktur R
            0x1D51D => 0x2128, // fraktur Z
            0x1D53A => 0x2102, // double-struck C
            0x1D53F => 0x210D, // double-struck H
            0x1D545 => 0x2115, // double-struck N
            0x1D547 => 0x2119, // double-struck P
            0x1D548 => 0x211A, // double-struck Q
            0x1D549 => 0x211D, // double-struck R
            0x1D551 => 0x2124, // double-struck Z
            _ => mapped,
        };
    }

    /// <summary>The spacing class of a character, by what it is in math.</summary>
    public static MathClass Classify(int c) => c switch
    {
        '+' or 0x2212 or 0xB1 or 0x2213 or 0xD7 or 0xF7 or 0x22C5 or 0x2217 or 0x2218 or 0x2219 or 0x2229 or 0x222A
            or 0x2227 or 0x2228 or 0x2295 or 0x2296 or 0x2297 or 0x2298 or 0x2299 or 0x22C4 or 0x2216 or 0x2240 or 0x228E
            or 0x2293 or 0x2294 or 0x25B3 or 0x25BD or 0x2020 or 0x2021 or 0x2A3F or 0xB7 => MathClass.Binary,
        '=' or '<' or '>' or ':' or 0x2260 or 0x2264 or 0x2265 or 0x2261 or 0x2248 or 0x223C or 0x2243 or 0x2245 or 0x221D
            or 0x226A or 0x226B or 0x227A or 0x227B or 0x2208 or 0x2209 or 0x220B or 0x2282 or 0x2283 or 0x2286 or 0x2287
            or 0x2284 or 0x2285 or 0x2288 or 0x2289 or 0x228F or 0x2290 or 0x2291 or 0x2292 or 0x22A2 or 0x22A3 or 0x22A8
            or 0x2225 or 0x2226 or 0x2223 or 0x2224 or 0x22A5 or 0x2250 or 0x2254 or 0x2255 or 0x225C or 0x225D or 0x225F
            or 0x2266 or 0x2267 or 0x2A7D or 0x2A7E or 0x226E or 0x226F or 0x2270 or 0x2271 or 0x2262 or 0x2249 or 0x2244
            or 0x2247 or 0x22B2 or 0x22B3 or 0x22B4 or 0x22B5 or 0x2234 or 0x2235 or 0x2237 or 0x2236 => MathClass.Relation,
        >= 0x2190 and <= 0x21FF or >= 0x27F0 and <= 0x27FF or >= 0x2900 and <= 0x297F => MathClass.Relation,
        '(' or '[' or '{' or 0x27E8 or 0x2308 or 0x230A or 0x27E6 or 0x2329 or 0x3008 or 0x2983 or 0x27EE => MathClass.Open,
        ')' or ']' or '}' or 0x27E9 or 0x2309 or 0x230B or 0x27E7 or 0x232A or 0x3009 or 0x2984 or 0x27EF or '!' => MathClass.Close,
        ',' or ';' => MathClass.Punctuation,
        >= 0x2211 and <= 0x2211 or 0x220F or 0x2210 or >= 0x222B and <= 0x2233 or 0x22C0 or 0x22C1 or 0x22C2 or 0x22C3
            or 0x2A00 or 0x2A01 or 0x2A02 or 0x2A04 or 0x2A06 or 0x2A0C => MathClass.Operator,
        _ => MathClass.Ordinary,
    };

    /// <summary>Whether <paramref name="c"/> is an integral sign, whose limits follow <c>m:intLim</c> rather than <c>m:naryLim</c>.</summary>
    public static bool IsIntegral(int c) => c is >= 0x222B and <= 0x2233 or >= 0x2A0B and <= 0x2A1C;
}
