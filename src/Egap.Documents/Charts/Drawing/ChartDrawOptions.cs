using System.Globalization;

using Egap.Documents.Layout;

namespace Egap.Documents.Charts;

/// <summary>What <see cref="Chart.Draw"/> draws with, where the chart part does not say.</summary>
public sealed class ChartDrawOptions
{
    /// <summary>The theme for theme colours and fonts; Office's default theme when null. Word and Excel pass their file's theme.</summary>
    public Theme? Theme { get; init; }

    /// <summary>Where fonts come from; <see cref="FontSettings.Default"/> when null.</summary>
    public FontSettings? Fonts { get; init; }

    /// <summary>The culture for numbers on axes and labels; the invariant culture, which shows them as US English does, when null.</summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>The font chain already open for a document or a print, so a page of charts does not open its own.</summary>
    internal FontProvider? Provider { get; init; }
}
