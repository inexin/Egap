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
    public void AssemblyReferencesOnlyTheRuntime()
    {
        // Egap.Pdf is fully managed and dependency free: it may reference only .NET's own assemblies.
        AssemblyName[] references = typeof(PdfDocument).Assembly.GetReferencedAssemblies();

        Assert.All(references, static r => Assert.True(r.Name!.StartsWith("System.", StringComparison.Ordinal), r.Name));
    }
}
