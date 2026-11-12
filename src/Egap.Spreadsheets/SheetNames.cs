using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets;

/// <summary>How a sheet's name is written in front of a reference.</summary>
internal static class SheetNames
{
    /// <summary>Whether a sheet name reads back unquoted: a word that is no reference in either style.</summary>
    public static bool IsPlain(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c is '_' or '.'))
            {
                return false;
            }
        }

        // A name whose part before a dot reads as a reference, as E11.XLS, is quoted too.
        int dot = name.IndexOf('.');
        if (dot > 0 && LooksLikeReference(name[..dot]))
        {
            return false;
        }

        return !LooksLikeReference(name);
    }

    private static bool LooksLikeReference(string name)
    {
        return FormulaParser.LooksLikeR1C1(name)
            || FormulaParser.TryCell(name, out _, out _, out _, out _)
            || LettersThenDigits(name)
            || name.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a name is one to three letters and then digits, such as <c>May06</c>: Excel quotes it as it would a cell.</summary>
    private static bool LettersThenDigits(string name)
    {
        int letters = 0;
        while (letters < name.Length && char.IsAsciiLetter(name[letters]))
        {
            letters++;
        }

        return letters is > 0 and <= 3 && letters < name.Length && name.AsSpan(letters).IndexOfAnyExceptInRange('0', '9') < 0;
    }

    /// <summary>The name as a reference writes it, such as <c>Sheet1</c> or <c>'My sheet'</c>.</summary>
    public static string Quoted(string name) => IsPlain(name) ? name : "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
}
