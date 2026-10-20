using System.Reflection;

namespace Egap.Documents.Tests;

/// <summary>
/// The package boundaries: Egap.Documents builds on Egap.Pdf for PDF export and PDF to document, and otherwise only on Egami
/// and the framework.
/// </summary>
public class PackageRulesTests
{
    private static readonly Assembly Documents = Assembly.Load("Egap.Documents");

    [Fact]
    public void DocumentsReferencesOnlyEgapPdfEgamiAndTheFramework()
    {
        string[] others = [.. Documents.GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal) && name != "netstandard"
                && !name.StartsWith("Egami", StringComparison.Ordinal) && name != "Egap.Pdf")];

        Assert.Empty(others);
    }
}
