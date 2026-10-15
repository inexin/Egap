namespace Egap.Pdf.Images;

/// <summary>
/// A one-input function sampled at even steps over its domain, read back by linear interpolation between the two nearest
/// samples (Phase 4 design Q10): what PDFium and pdf.js do for tint transforms and shadings.
/// </summary>
internal sealed class FunctionTable(double start, double end, int outputs, double[] values)
{
    private readonly int count = values.Length / outputs;

    public int Outputs => outputs;

    /// <summary>The function at <paramref name="t"/>, clipped to the domain.</summary>
    public void Evaluate(double t, Span<double> output)
    {
        double position = end > start && double.IsFinite(t) ? Math.Clamp((t - start) / (end - start), 0, 1) * (count - 1) : 0;
        int i = Math.Min((int)position, count - 2);
        if (i < 0)
        {
            values.AsSpan(0, outputs).CopyTo(output);
            return;
        }

        double f = position - i;
        int a = i * outputs, b = a + outputs;
        for (int k = 0; k < outputs && k < output.Length; k++)
        {
            output[k] = values[a + k] + ((values[b + k] - values[a + k]) * f);
        }
    }
}
