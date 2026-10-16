namespace Egap.Documents;

/// <summary>
/// One clone or import: the document the copies go to, and the styles and lists already brought over, so an import copies
/// each style (with the styles it is based on) and each list once. A whole-document copy finds them by id instead.
/// </summary>
internal sealed class CloneContext(Document target, bool wholeDocument = false)
{
    private Dictionary<object, object>? imported;

    public Document Target { get; } = target;

    /// <summary>The style in the target document that stands for <paramref name="style"/>.</summary>
    public T? Map<T>(T? style)
        where T : Style
    {
        if (style is null || !style.IsInDocument)
        {
            return null;
        }

        if (style.Document == Target)
        {
            return style;
        }

        return wholeDocument ? (T?)Target.Styles[style.Id] : (T)Target.Styles.Import(style, this);
    }

    /// <summary>The list in the target document that stands for <paramref name="list"/>.</summary>
    public List? Map(List? list)
    {
        if (list is null || list == List.None || list.Document == Target)
        {
            return list;
        }

        if (list.Document is not { } source || !source.Lists.Contains(list))
        {
            return null;
        }

        return wholeDocument ? Target.Lists[list.Id] : Target.Lists.Import(list, this, Imported);
    }

    /// <summary>What this import has copied so far, source to copy.</summary>
    public Dictionary<object, object> Imported => imported ??= [];

    /// <summary>Points the lists a copied paragraph format names at the target document's.</summary>
    public void FixLists(FormatBase format)
    {
        if (format is ParagraphFormat paragraph && paragraph.GetRaw(ParagraphFormat.ListId) is List list && list != List.None && list.Document != Target)
        {
            paragraph.SetRawSilently(ParagraphFormat.ListId, Map(list));
        }
    }
}
