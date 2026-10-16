namespace Egap.Documents;

/// <summary>
/// One clone or import: the document the copies go to, and the styles already brought over, so an import copies each style
/// (and the styles it is based on) once.
/// </summary>
internal sealed class CloneContext(Document target)
{
    private Dictionary<Style, Style>? imported;

    public Document Target { get; } = target;

    /// <summary>The style in the target document that stands for <paramref name="style"/>.</summary>
    public T? Map<T>(T? style)
        where T : Style
    {
        if (style is null || !style.IsInDocument)
        {
            return null;
        }

        return style.Document == Target ? style : (T)Target.Styles.Import(style, imported ??= []);
    }
}
