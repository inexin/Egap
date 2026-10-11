namespace Egap.Pdf.IO;

/// <summary>
/// Collects the warnings for one file, or throws them in strict mode. Thread safe, since lazy loading can run on any thread.
/// </summary>
internal sealed class WarningLog(bool strict)
{
    /// <summary>The most warnings kept; after that one <see cref="PdfWarningCode.TooManyWarnings"/> is added and the rest dropped.</summary>
    public const int MaxWarnings = 10_000;

    private readonly List<PdfWarning> warnings = [];
    private readonly Lock gate = new();

    public bool Strict { get; } = strict;

    /// <summary>A log that throws on every warning, for code that runs without a file.</summary>
    public static WarningLog StrictLog => new(strict: true);

    public int Count
    {
        get
        {
            lock (gate)
            {
                return warnings.Count;
            }
        }
    }

    /// <summary>Records a repair, or throws a <see cref="PdfException"/> in strict mode.</summary>
    public void Add(PdfWarningCode code, string message, long? offset = null, int? objectNumber = null)
    {
        if (Strict)
        {
            throw new PdfException(message, offset, objectNumber) { Code = code };
        }

        lock (gate)
        {
            if (warnings.Count < MaxWarnings)
            {
                warnings.Add(new PdfWarning(code, message, offset, objectNumber));
            }
            else if (warnings.Count == MaxWarnings)
            {
                warnings.Add(new PdfWarning(PdfWarningCode.TooManyWarnings, $"More than {MaxWarnings} warnings; the rest were dropped."));
            }
        }
    }

    /// <summary>A copy of the warnings so far.</summary>
    public PdfWarning[] ToArray()
    {
        lock (gate)
        {
            return [.. warnings];
        }
    }
}
