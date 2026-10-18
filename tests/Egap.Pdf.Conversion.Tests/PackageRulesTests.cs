using System.Reflection;

namespace Egap.Pdf.Conversion.Tests;

/// <summary>Egap.Pdf.Conversion is the one bridge between flow documents and PDF, in both directions (Phase 6 design Q2).</summary>
public class PackageRulesTests
{
    [Fact]
    public void LayoutBridgesDocumentsAndPdf()
    {
        Assembly layout = Assembly.Load("Egap.Pdf.Conversion");
        string[] referenced = [.. layout.GetReferencedAssemblies().Select(name => name.Name!)];

        // A reference is only recorded once the bridge uses the library; until then both must at least load beside it.
        Assert.NotNull(Assembly.Load("Egap.Documents"));
        Assert.NotNull(Assembly.Load("Egap.Pdf"));
        Assert.DoesNotContain(referenced, name => name.StartsWith("Egap.", StringComparison.Ordinal)
            && name is not ("Egap.Documents" or "Egap.Pdf"));
    }
}
