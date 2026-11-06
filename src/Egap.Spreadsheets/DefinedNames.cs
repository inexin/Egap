using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>
/// A defined name: a name for a cell, a range, a constant or a formula, such as <c>TaxRate</c> for <c>Sheet1!$B$1</c>. A name
/// belongs to the workbook, or to one sheet when it is local to that sheet. Excel's own names start with <c>_xlnm.</c>, such
/// as <c>_xlnm.Print_Area</c>.
/// </summary>
public sealed class DefinedName
{
    internal DefinedName(string name, string formula, Worksheet? sheet)
    {
        Name = name;
        Formula = formula;
        Sheet = sheet;
    }

    /// <summary>The name.</summary>
    public string Name { get; }

    /// <summary>
    /// What the name stands for, as a formula without the <c>=</c>, such as <c>Sheet1!$B$1</c> or <c>LAMBDA(x,x*2)</c>. A
    /// formula set here is written to a file in Excel's stored form; one read from a file stays as it was.
    /// </summary>
    /// <exception cref="ArgumentException">The formula is empty.</exception>
    public string Formula
    {
        get;
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            field = value[0] == '=' && value.Length > 1 ? value[1..] : value;
            Typed = true;
        }
    }

    /// <summary>Whether <see cref="Formula"/> was set through the API rather than read from a file.</summary>
    internal bool Typed { get; set; }

    /// <summary>The sheet the name is local to, or null for a name of the whole workbook.</summary>
    public Worksheet? Sheet { get; internal set; }

    /// <summary>Whether the name is hidden from Excel's name manager.</summary>
    public bool Hidden { get; set; }

    /// <summary>A comment on the name, or null.</summary>
    public string? Comment { get; set; }

    /// <summary>Attributes of the file's definedName element this model does not hold, written back as they were.</summary>
    internal XAttribute[]? Extra { get; set; }

    /// <summary>The sheet index a name read from a file was local to, when that sheet is not a worksheet; -1 otherwise.</summary>
    internal int KeptScope { get; set; } = -1;

    /// <summary>The name and what it stands for, such as <c>TaxRate = Sheet1!$B$1</c>.</summary>
    public override string ToString() => $"{(Sheet is null ? "" : Sheet.Name + "!")}{Name} = {Formula}";
}

/// <summary>A workbook's defined names. A name is unique among the workbook's names, or among one sheet's, case ignored.</summary>
public sealed class DefinedNameCollection : IReadOnlyCollection<DefinedName>
{
    private readonly List<DefinedName> names = [];

    internal DefinedNameCollection()
    {
    }

    /// <summary>How many names there are.</summary>
    public int Count => names.Count;

    /// <summary>The workbook name with this name, case ignored.</summary>
    /// <exception cref="KeyNotFoundException">The workbook has no such name.</exception>
    public DefinedName this[string name] =>
        TryGet(name, null, out DefinedName? found) ? found : throw new KeyNotFoundException($"The workbook has no name '{name}'.");

    /// <summary>Adds a name.</summary>
    /// <param name="name">
    /// The name: up to 255 characters, starting with a letter, an underscore or a backslash, with no spaces, and not a cell
    /// reference such as <c>A1</c> or <c>R1C1</c>.
    /// </param>
    /// <param name="formula">What it stands for, such as <c>Sheet1!$B$1</c>; a leading <c>=</c> is dropped.</param>
    /// <param name="sheet">The sheet the name is local to, or null for the whole workbook.</param>
    /// <exception cref="ArgumentException">The name is not one Excel allows, or the scope has it already.</exception>
    public DefinedName Add(string name, string formula, Worksheet? sheet = null)
    {
        CheckName(name);
        if (TryGet(name, sheet, out _))
        {
            throw new ArgumentException($"'{name}' is defined already{(sheet is null ? "" : " on " + sheet.Name)}.", nameof(name));
        }

        DefinedName added = new(name, "x", sheet) { Formula = formula };
        names.Add(added);
        return added;
    }

    /// <summary>Removes a name.</summary>
    /// <returns>Whether the name was in the workbook.</returns>
    public bool Remove(DefinedName name) => names.Remove(name);

    /// <summary>Finds a name in a scope: the workbook's when <paramref name="sheet"/> is null, else that sheet's.</summary>
    public bool TryGet(string name, Worksheet? sheet, [NotNullWhen(true)] out DefinedName? found)
    {
        ArgumentNullException.ThrowIfNull(name);
        found = names.Find(n => n.Sheet == sheet && n.KeptScope < 0 && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        return found is not null;
    }

    /// <inheritdoc/>
    public IEnumerator<DefinedName> GetEnumerator() => names.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(DefinedName name)
    {
        name.Typed = false;
        names.Add(name);
    }

    /// <summary>Takes out the names local to a sheet that is being removed.</summary>
    internal void RemoveLocalTo(Worksheet sheet) => names.RemoveAll(n => n.Sheet == sheet);

    private static void CheckName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool ok = name.Length is > 0 and <= 255
            && (char.IsLetter(name[0]) || name[0] is '_' or '\\')
            && !name.Any(c => char.IsWhiteSpace(c) || c is ',' or ';' or '(' or ')' or '"' or '\'' or '!' or '&' or '+' or '-' or '*' or '/' or '^' or '<' or '>' or '=' or '{' or '}' or '[' or ']' or ':' or '%' or '#' or '@' or '~' or '|')
            && !CellAddress.TryParse(name, out _)
            && !IsR1C1(name);
        if (!ok)
        {
            throw new ArgumentException(
                $"'{name}' is not a name Excel allows: up to 255 characters, starting with a letter, _ or \\, no spaces or operators, and not a cell reference.",
                nameof(name));
        }
    }

    private static bool IsR1C1(string name)
    {
        string upper = name.ToUpperInvariant();
        if (upper is "R" or "C")
        {
            return true;
        }

        int i = 0;
        if (i < upper.Length && upper[i] == 'R')
        {
            i++;
            while (i < upper.Length && char.IsAsciiDigit(upper[i]))
            {
                i++;
            }
        }

        if (i < upper.Length && upper[i] == 'C')
        {
            i++;
            while (i < upper.Length && char.IsAsciiDigit(upper[i]))
            {
                i++;
            }
        }

        return i > 0 && i == upper.Length;
    }
}
