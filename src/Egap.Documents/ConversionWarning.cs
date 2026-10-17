namespace Egap.Documents;

/// <summary>What a reader or writer had to leave out or change because a format cannot hold it.</summary>
public enum ConversionWarningKind
{
    /// <summary>Character or paragraph formatting the format has no way to say, such as colours, fonts and sizes.</summary>
    FormattingDropped,

    /// <summary>Merged table cells, written as separate cells.</summary>
    MergedCellsSplit,

    /// <summary>A table the format cannot hold as a table (nested, or cells with blocks), written in a simpler form.</summary>
    TableSimplified,

    /// <summary>A picture with no address to write it at.</summary>
    PictureDropped,

    /// <summary>Shapes, text boxes or kept islands the format cannot hold.</summary>
    DrawingDropped,

    /// <summary>Headers, footers and page setup.</summary>
    PageLayoutDropped,

    /// <summary>Comments.</summary>
    CommentsDropped,

    /// <summary>Tracked changes, written as if accepted.</summary>
    ChangesAccepted,

    /// <summary>Fields, written as their results.</summary>
    FieldsFlattened,

    /// <summary>Content controls, written as their content.</summary>
    ContentControlsDropped,

    /// <summary>Layout the model can only approximate, such as flexbox, grid and absolutely positioned boxes read from HTML.</summary>
    LayoutApproximated,

    /// <summary>Pictures or style sheets a reader could not load, as no resolver gave them.</summary>
    ResourceNotLoaded,
}

/// <summary>One kind of thing a writer left out, with how many times it happened.</summary>
/// <param name="Kind">What was left out.</param>
/// <param name="Message">What, in words.</param>
/// <param name="Count">How many times.</param>
public sealed record ConversionWarning(ConversionWarningKind Kind, string Message, int Count)
{
    /// <summary>The warning as one line.</summary>
    public override string ToString()
    {
        return Count == 1 ? $"{Kind}: {Message}" : $"{Kind}: {Message} ({Count} times)";
    }
}

/// <summary>Gathers a writer's warnings, one per kind and message.</summary>
internal sealed class WarningCollector
{
    private readonly Dictionary<(ConversionWarningKind, string), int> counts = [];

    public void Add(ConversionWarningKind kind, string message)
    {
        counts[(kind, message)] = counts.GetValueOrDefault((kind, message)) + 1;
    }

    public IReadOnlyList<ConversionWarning> ToList()
    {
        return [.. counts.Select(c => new ConversionWarning(c.Key.Item1, c.Key.Item2, c.Value)).OrderBy(w => w.Kind).ThenBy(w => w.Message, StringComparer.Ordinal)];
    }
}
