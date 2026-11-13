using System.Collections.Frozen;

namespace Egap.Spreadsheets.Formulas;

/// <summary>
/// The functions newer than the file format, which a file stores with a prefix: without it Excel reads a name it does not
/// know. The list is what Excel 16.0 build 20527 itself stored for every function Egap has (2026-11-06, and 2026-11-12 for the functions added then).
/// </summary>
internal static class FutureFunctions
{
    private static readonly FrozenSet<string> Prefixed = new[]
    {
        "ACOT", "ACOTH", "AGGREGATE", "ARABIC", "ARRAYTOTEXT", "BASE", "BETA.DIST", "BETA.INV", "BINOM.DIST",
        "BINOM.DIST.RANGE", "BINOM.INV", "BITAND", "BITLSHIFT", "BITOR", "BITRSHIFT", "BITXOR", "BYCOL", "BYROW", "CEILING.MATH",
        "CEILING.PRECISE", "CHISQ.DIST", "CHISQ.DIST.RT", "CHISQ.INV", "CHISQ.INV.RT", "CHISQ.TEST", "CHOOSECOLS",
        "CHOOSEROWS", "COMBINA", "CONCAT", "CONFIDENCE.NORM", "CONFIDENCE.T", "COT", "COTH", "COVARIANCE.P",
        "COVARIANCE.S", "CSC", "CSCH", "DAYS", "DECIMAL", "DROP", "ENCODEURL", "ERF.PRECISE", "ERFC.PRECISE", "EXPAND",
        "EXPON.DIST", "F.DIST", "F.DIST.RT", "F.INV", "F.INV.RT", "F.TEST", "FLOOR.MATH", "FLOOR.PRECISE",
        "FILTERXML", "FORECAST.ETS", "FORECAST.ETS.CONFINT", "FORECAST.ETS.SEASONALITY", "FORECAST.ETS.STAT",
        "FORECAST.LINEAR", "FORMULATEXT", "GROUPBY", "GAMMA", "GAMMA.DIST", "GAMMA.INV", "GAMMALN.PRECISE", "GAUSS", "HSTACK",
        "HYPGEOM.DIST", "IFS", "IMCOSH", "IMCOT", "IMCSC", "IMCSCH", "IMSEC", "IMSECH", "IMSINH", "IMTAN", "ISFORMULA",
        "ISOMITTED", "ISOWEEKNUM", "LAMBDA", "LET", "LOGNORM.DIST", "LOGNORM.INV", "MAKEARRAY", "MAP", "MAXIFS", "MINIFS", "MODE.MULT", "MODE.SNGL", "MUNIT",
        "NEGBINOM.DIST", "NORM.DIST", "NORM.INV", "NORM.S.DIST", "NORM.S.INV", "NUMBERVALUE", "PDURATION",
        "PERCENTOF", "PERCENTILE.EXC", "PERCENTILE.INC", "PERCENTRANK.EXC", "PERCENTRANK.INC", "PERMUTATIONA", "PHI", "PIVOTBY",
        "POISSON.DIST", "QUARTILE.EXC", "QUARTILE.INC", "RANDARRAY", "RANK.AVG", "RANK.EQ", "REDUCE", "REGEXEXTRACT", "REGEXREPLACE", "REGEXTEST", "RRI", "SCAN", "SEC", "SECH", "SHEET", "SHEETS",
        "SEQUENCE", "SKEW.P", "SORTBY", "STDEV.P", "STDEV.S", "SWITCH", "T.DIST", "T.DIST.2T", "T.DIST.RT", "T.INV",
        "T.INV.2T", "T.TEST", "TAKE", "TEXTAFTER", "TEXTBEFORE", "TEXTJOIN", "TEXTSPLIT", "TOCOL", "TOROW",
        "TRIMRANGE", "UNICHAR", "UNICODE", "UNIQUE", "VALUETOTEXT", "VAR.P", "VAR.S", "VSTACK", "WEIBULL.DIST",
        "WRAPCOLS", "WRAPROWS", "XLOOKUP", "XMATCH", "XOR", "Z.TEST",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Whether a typed formula may differ in a file: it has <c>#</c>, <c>@</c>, a <c>[</c> or the name of a newer function before a parenthesis. Most do not, and need no parsing.</summary>
    public static bool MayNeedStoring(string text)
    {
        ReadOnlySpan<char> rest = text;
        if (rest.IndexOfAny('#', '@', '[') >= 0)
        {
            return true;
        }

        int open;
        int at = 0;
        while ((open = text.IndexOf('(', at)) >= 0)
        {
            int start = open;
            while (start > 0 && (char.IsAsciiLetterOrDigit(text[start - 1]) || text[start - 1] == '.' || text[start - 1] == '_'))
            {
                start--;
            }

            string name = text[start..open].ToUpperInvariant();
            if (name is "FILTER" or "SORT" || Prefixed.Contains(name))
            {
                return true;
            }

            at = open + 1;
        }

        return false;
    }

    /// <summary>A function's name as a file stores it; one already stored so, or not known, is left as written.</summary>
    public static string StoredName(string name)
    {
        string plain = FormulaCompiler.FunctionName(name);
        if (plain.Length != name.Length)
        {
            return name;
        }

        if (plain is "FILTER" or "SORT")
        {
            return "_xlfn._xlws." + plain;
        }

        return Prefixed.Contains(plain) ? "_xlfn." + plain : FormulaFunctions.IsBuiltIn(plain) ? plain : name;
    }
}
