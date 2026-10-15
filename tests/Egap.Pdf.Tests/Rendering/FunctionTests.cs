using System.Text;

using Egap.Pdf.Images;

namespace Egap.Pdf.Tests.Rendering;

/// <summary>PDF functions (Phase 4 task 7): the four types as 7.10 defines them, sampled tables, and lenient failures.</summary>
public sealed class FunctionTests
{
    [Fact]
    public void SampledFunctionsInterpolateTheirSamples()
    {
        // One input, two outputs, three 8-bit samples: (0, 255), (128, 0), (255, 255).
        PdfStream stream = Stream(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Domain"] = Array(0, 1),
            ["Range"] = Array(0, 1, 0, 1),
            ["Size"] = Array(3),
            ["BitsPerSample"] = new PdfInteger(8),
        }, [0, 255, 128, 0, 255, 255]);
        PdfFunction function = PdfFunction.Read(stream);

        Assert.Equal([0, 1], Run(function, 0), Close);
        Assert.Equal([128 / 255.0, 0], Run(function, 0.5), Close);
        Assert.Equal([(128 + 255) / 2.0 / 255, 0.5], Run(function, 0.75), Close);
        Assert.Equal([1, 1], Run(function, 7), Close);
    }

    [Fact]
    public void SampledFunctionsGiveMissingInputsTheirDomainsStart()
    {
        // Found by the corpus render gate: a shading evaluated a two-input sampled function with one input.
        PdfStream stream = Stream(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(0),
            ["Domain"] = Array(0, 1, 0, 1),
            ["Range"] = Array(0, 1),
            ["Size"] = Array(2, 2),
            ["BitsPerSample"] = new PdfInteger(8),
        }, [0, 255, 255, 255]);

        Assert.Equal([1], Run(PdfFunction.Read(stream), 1), Close);
    }

    [Fact]
    public void ExponentialFunctionsRaiseToN()
    {
        PdfFunction function = PdfFunction.Read(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(2),
            ["Domain"] = Array(0, 1),
            ["C0"] = Array(0, 1),
            ["C1"] = Array(1, 0),
            ["N"] = new PdfInteger(2),
        });

        Assert.Equal([0.25, 0.75], Run(function, 0.5), Close);
    }

    [Fact]
    public void StitchingFunctionsPickASubdomainAndEncodeIt()
    {
        PdfDictionary up = new() { ["FunctionType"] = new PdfInteger(2), ["Domain"] = Array(0, 1), ["C0"] = Array(0), ["C1"] = Array(1), ["N"] = new PdfInteger(1) };
        PdfFunction function = PdfFunction.Read(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(3),
            ["Domain"] = Array(0, 1),
            ["Functions"] = new PdfArray { up, up.Clone() },
            ["Bounds"] = Array(0.5),
            ["Encode"] = Array(0, 1, 1, 0),
        });

        Assert.Equal([0.5], Run(function, 0.25), Close);
        Assert.Equal([0.5], Run(function, 0.75), Close);
        Assert.Equal([1], Run(function, 0.5), Close);
    }

    [Theory]
    [InlineData("{ 2 mul 1 exch sub }", 0.25, 0.5)]
    [InlineData("{ dup 0.5 gt { pop 1 } { pop 0 } ifelse }", 0.7, 1)]
    [InlineData("{ dup 0.5 gt { pop 1 } { pop 0 } ifelse }", 0.3, 0)]
    [InlineData("{ dup 0.5 lt { 0.5 add } if }", 0.25, 0.75)]
    [InlineData("{ 3 2 1 3 1 roll pop pop pop }", 0, 0)]
    [InlineData("{ 1 2 3 3 -1 roll pop pop }", 0, 2)]
    [InlineData("{ pop 90 sin 180 cos add }", 0, 0)]
    [InlineData("{ pop 7 2 idiv 7 2 mod add }", 0, 4)]
    [InlineData("{ pop true false or { 1 } { 0 } ifelse }", 0, 1)]
    [InlineData("{ pop 1 2 2 copy add add 1 index add }", 0, 6)]
    [InlineData("{ pop 2 0.5 exp 2 sqrt sub }", 0, 0)]
    [InlineData("{ 4 3 bitshift 32 sub add }", 0.5, 0.5)]
    public void PostScriptFunctionsRunTheirPrograms(string program, double input, double expected)
    {
        PdfStream stream = Stream(new PdfDictionary { ["FunctionType"] = new PdfInteger(4), ["Domain"] = Array(0, 1), ["Range"] = Array(-10, 10) }, Encoding.ASCII.GetBytes(program));

        Assert.Equal(expected, Run(PdfFunction.Read(stream), input)[0], 9);
    }

    [Fact]
    public void BrokenPostScriptStopsQuietlyAtRunTimeAndThrowsWhenRead()
    {
        PdfStream underflow = Stream(new PdfDictionary { ["FunctionType"] = new PdfInteger(4), ["Domain"] = Array(0, 1), ["Range"] = Array(0, 1) }, Encoding.ASCII.GetBytes("{ pop pop pop add }"));
        PdfStream unknown = Stream(new PdfDictionary { ["FunctionType"] = new PdfInteger(4), ["Domain"] = Array(0, 1), ["Range"] = Array(0, 1) }, Encoding.ASCII.GetBytes("{ frobnicate }"));
        PdfStream dangling = Stream(new PdfDictionary { ["FunctionType"] = new PdfInteger(4), ["Domain"] = Array(0, 1), ["Range"] = Array(0, 1) }, Encoding.ASCII.GetBytes("{ { 1 } }"));

        Assert.Equal([0], Run(PdfFunction.Read(underflow), 0.5));
        Assert.Throws<InvalidDataException>(() => PdfFunction.Read(unknown));
        Assert.Throws<InvalidDataException>(() => PdfFunction.Read(dangling));
    }

    [Fact]
    public void TablesFollowTheFunctionBetweenSamples()
    {
        PdfFunction function = PdfFunction.Read(new PdfDictionary
        {
            ["FunctionType"] = new PdfInteger(2),
            ["Domain"] = Array(0, 2),
            ["C0"] = Array(0),
            ["C1"] = Array(1),
            ["N"] = new PdfReal(1.5),
        });
        FunctionTable table = function.Sample(256, 1);
        double[] value = new double[1];
        for (double t = 0; t <= 2; t += 0.0137)
        {
            table.Evaluate(t, value);
            Assert.Equal(Run(function, t)[0], value[0], 3);
        }

        table.Evaluate(-5, value);
        Assert.Equal(0, value[0]);
    }

    private static readonly Func<double, double, bool> CloseCheck = (a, b) => Math.Abs(a - b) < 1e-9;

    private static IEqualityComparer<double> Close => EqualityComparer<double>.Create((a, b) => CloseCheck(a, b), _ => 0);

    private static double[] Run(PdfFunction function, double input)
    {
        double[] output = new double[Math.Max(function.Outputs, 1)];
        function.Evaluate([input], output);
        return output;
    }

    private static PdfArray Array(params double[] values)
    {
        PdfArray array = [];
        foreach (double v in values)
        {
            array.Add(new PdfReal(v));
        }

        return array;
    }

    private static PdfStream Stream(PdfDictionary dictionary, byte[] data)
    {
        PdfStream stream = new(dictionary);
        stream.SetDecodedBytes(data, compress: false);
        return stream;
    }
}
