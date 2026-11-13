using System.Globalization;
using System.Text;
using System.Text.Json;

using Egap.Spreadsheets.Formulas;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// Formulas the formula fuzzer found whose R1C1 form, printed from their A1 form, did not parse again. Each is checked the
/// way the fuzzer checks it: parsed in A1 and R1C1 style for a cell taken from its length, printed as A1, parsed and printed
/// again, and its R1C1 form parsed.
/// </summary>
public sealed class FormulaRoundTripTests
{
    [Fact]
    public void FuzzedFormulasReadBackInBothStyles()
    {
        string[] formulas = JsonSerializer.Deserialize<string[]>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Oracle", "formula-r1c1-roundtrip.json")))!;
        StringBuilder failures = new();
        foreach (string text in formulas)
        {
            CellAddress cell = new(text.Length % 40, text.Length % 7);
            foreach (bool r1c1 in (bool[])[false, true])
            {
                ParsedFormula? parsed = FormulaParser.TryParse(text, r1c1, cell);
                if (parsed is null)
                {
                    continue;
                }

                string a1 = FormulaPrinter.Print(parsed, ReferenceStyle.A1, cell);
                ParsedFormula? again = FormulaParser.TryParse(a1, r1c1: false, cell);
                if (again is null || FormulaPrinter.Print(again, ReferenceStyle.A1, cell) != a1)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"{Show(text)} ({(r1c1 ? "R1C1" : "A1")}): A1 form {Show(a1)} does not read back");
                    continue;
                }

                string r1c1Form = FormulaPrinter.Print(again, ReferenceStyle.R1C1);
                if (FormulaParser.TryParse(r1c1Form, r1c1: true, cell) is null)
                {
                    failures.AppendLine(CultureInfo.InvariantCulture, $"{Show(text)} ({(r1c1 ? "R1C1" : "A1")}): A1 {Show(a1)}, R1C1 {Show(r1c1Form)} does not parse");
                }
            }
        }

        Assert.True(formulas.Length == 82, $"{formulas.Length} formulas");
        Assert.True(failures.Length == 0, failures.ToString());
    }

    private static string Show(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
