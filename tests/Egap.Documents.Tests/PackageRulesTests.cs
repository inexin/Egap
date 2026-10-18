using System.Reflection;

namespace Egap.Documents.Tests;

/// <summary>The package boundaries Phase 6 rests on (hub decision 2026-10-11, Phase 6 design Q1 and Q2).</summary>
public class PackageRulesTests
{
    private static readonly Assembly Documents = Assembly.Load("Egap.Documents");

    [Fact]
    public void DocumentsNeverReferencesThePdfLibrary()
    {
        string[] referenced = [.. Documents.GetReferencedAssemblies().Select(name => name.Name!)];

        Assert.DoesNotContain(referenced, name => name.StartsWith("Egap.Pdf", StringComparison.Ordinal));
    }

    [Fact]
    public void DocumentsReferencesOnlyEgamiAndTheFramework()
    {
        string[] others = [.. Documents.GetReferencedAssemblies()
            .Select(name => name.Name!)
            .Where(name => !name.StartsWith("System", StringComparison.Ordinal) && name != "netstandard"
                && !name.StartsWith("Egami", StringComparison.Ordinal))];

        Assert.Empty(others);
    }
}
