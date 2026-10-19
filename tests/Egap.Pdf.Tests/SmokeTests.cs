using System.Reflection;

namespace Egap.Pdf.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void AssemblyLoads()
    {
        Assembly assembly = typeof(PdfDocument).Assembly;

        Assert.Equal("Egap.Pdf", assembly.GetName().Name);
    }

    [Fact]
    public void AssemblyReferencesOnlyTheRuntimeAndEgami()
    {
        // Egap.Pdf is fully managed: it may reference only .NET's own assemblies, Egami's (Phase 3 design Q2), including
        // Egami.Fonts.Substitutes, the shared substitute fonts (Phase 4 design Q5), and Egami.Barcodes for barcode stamps (Phase 9 design Q16).
        AssemblyName[] references = typeof(PdfDocument).Assembly.GetReferencedAssemblies();

        Assert.All(references, static r => Assert.True(r.Name!.StartsWith("System.", StringComparison.Ordinal) || r.Name is "Egami" or "Egami.Fonts" or "Egami.Drawing" or "Egami.Fonts.Substitutes" or "Egami.Barcodes", r.Name));
    }
}
