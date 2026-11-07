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

        return !FormulaParser.LooksLikeR1C1(name)
            && !FormulaParser.TryCell(name, out _, out _, out _, out _)
            && !name.Equals("TRUE", StringComparison.OrdinalIgnoreCase)
            && !name.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The name as a reference writes it, such as <c>Sheet1</c> or <c>'My sheet'</c>.</summary>
    public static string Quoted(string name) => IsPlain(name) ? name : "'" + name.Replace("'", "''", StringComparison.Ordinal) + "'";
}
