using System.Globalization;

using Egap.Documents.Layout;

namespace Egap.Documents.Charts;

/// <summary>The application whose defaults fill in what a chart part leaves unsaid.</summary>
public enum ChartHost
{
    /// <summary>Excel's: rounded corners, automatic lines of 3 points and markers of 9.</summary>
    Excel,

    /// <summary>Word's: square corners, automatic lines of 2.25 points and markers of 7. A document's pages draw their charts this way.</summary>
    Word,
}

/// <summary>What <see cref="Chart.Draw"/> draws with, where the chart part does not say.</summary>
public sealed class ChartDrawOptions
{
    /// <summary>The theme for theme colours and fonts; Office's default theme when null. Word and Excel pass their file's theme.</summary>
    public Theme? Theme { get; init; }

    /// <summary>Where fonts come from; <see cref="FontSettings.Default"/> when null.</summary>
    public FontSettings? Fonts { get; init; }

    /// <summary>
    /// The culture for numbers on axes and labels; the invariant culture, which shows them as US English does, when null. Another culture
    /// also shows the short date and percent formats and the labels' list separator as that culture writes them, as Office does on a machine set to it.
    /// </summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>The application whose defaults apply where the chart part says nothing; <see cref="ChartHost.Excel"/> by default.</summary>
    public ChartHost Host { get; init; }

    /// <summary>The font chain already open for a document or a print, so a page of charts does not open its own.</summary>
    internal FontProvider? Provider { get; init; }
}
