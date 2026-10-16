using System.Collections;

namespace Egap.Documents;

/// <summary>
/// A document's styles, by id, with the document defaults every style builds on (Word's docDefaults). A new document has
/// Word's Normal paragraph style, Default Paragraph Font and Normal Table, with Word 2013+'s defaults (11 pt body font from
/// the theme, 8 pt after paragraphs, 1.08 line spacing); <see cref="AddBuiltIn"/> adds the others Word knows, such as headings.
/// </summary>
public sealed class StyleCollection : IReadOnlyCollection<Style>, IFormatOwner
{
    Document IFormatOwner.Document => document;

    private readonly Document document;
    private readonly Dictionary<string, Style> byId = new(StringComparer.Ordinal);
    private readonly List<Style> order = [];
    private readonly Style?[] defaults = new Style?[4];

    internal StyleCollection(Document document)
    {
        this.document = document;
        DefaultRunFormat = new RunFormat { Owner = this };
        DefaultParagraphFormat = new ParagraphFormat { Owner = this };
    }

    /// <summary>How many styles the document has.</summary>
    public int Count => order.Count;

    /// <summary>Character formatting every run starts from (Word's rPrDefault).</summary>
    public RunFormat DefaultRunFormat { get; }

    /// <summary>Paragraph formatting every paragraph starts from (Word's pPrDefault).</summary>
    public ParagraphFormat DefaultParagraphFormat { get; }

    /// <summary>The style with <paramref name="id"/>, or null.</summary>
    public Style? this[string id] => byId.GetValueOrDefault(id);

    /// <summary>The style named <paramref name="name"/> (case-insensitive, as Word compares names), or null.</summary>
    public Style? GetByName(string name)
    {
        return order.Find(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The default style of <paramref name="type"/>: the one paragraphs, runs or tables without a style use.</summary>
    public Style? GetDefault(StyleType type)
    {
        Style? style = defaults[(int)type];
        return style is not null && Contains(style) ? style : null;
    }

    /// <summary>Makes <paramref name="style"/> the default of its type.</summary>
    public void SetDefault(Style style)
    {
        CheckMine(style);
        if (defaults[(int)style.Type] != style)
        {
            defaults[(int)style.Type] = style;
            document.NotifyFormattingChanged($"Styles.Default{style.Type}");
        }
    }

    /// <summary>The default paragraph style (Normal in a new document).</summary>
    public ParagraphStyle? DefaultParagraphStyle => GetDefault(StyleType.Paragraph) as ParagraphStyle;

    /// <summary>The default character style (Default Paragraph Font in a new document).</summary>
    public CharacterStyle? DefaultCharacterStyle => GetDefault(StyleType.Character) as CharacterStyle;

    /// <summary>The default table style (Normal Table in a new document).</summary>
    public TableStyle? DefaultTableStyle => GetDefault(StyleType.Table) as TableStyle;

    /// <summary>Adds an empty paragraph style.</summary>
    /// <exception cref="ArgumentException">A style with <paramref name="id"/> exists.</exception>
    public ParagraphStyle AddParagraphStyle(string id, string? name = null)
    {
        return Add(new ParagraphStyle(document, CheckNewId(id), name ?? id));
    }

    /// <summary>Adds an empty character style.</summary>
    public CharacterStyle AddCharacterStyle(string id, string? name = null)
    {
        return Add(new CharacterStyle(document, CheckNewId(id), name ?? id));
    }

    /// <summary>Adds an empty table style.</summary>
    public TableStyle AddTableStyle(string id, string? name = null)
    {
        return Add(new TableStyle(document, CheckNewId(id), name ?? id));
    }

    /// <summary>Adds a list style.</summary>
    public ListStyle AddListStyle(string id, string? name = null)
    {
        return Add(new ListStyle(document, CheckNewId(id), name ?? id));
    }

    /// <summary>The built-in style <paramref name="style"/>, added with Word's definition when the document does not have it.</summary>
    public Style AddBuiltIn(BuiltInStyle style)
    {
        return BuiltInStyles.GetOrAdd(this, style);
    }

    /// <summary>
    /// Takes <paramref name="style"/> out of the document. Paragraphs, runs and tables that point at it are formatted as if they
    /// had no style; styles based on it take its place in their chain from its own base.
    /// </summary>
    /// <returns>False when the style was not in this document.</returns>
    public bool Remove(Style style)
    {
        if (style is null || !Contains(style))
        {
            return false;
        }

        foreach (Style other in order)
        {
            if (other.BasedOn == style)
            {
                other.BasedOn = style.BasedOn;
            }
        }

        byId.Remove(style.Id);
        order.Remove(style);
        document.NotifyFormattingChanged($"Styles.{style.Id}.Removed");
        return true;
    }

    /// <summary>Whether <paramref name="style"/> is in this collection.</summary>
    public bool Contains(Style style)
    {
        return style is not null && byId.TryGetValue(style.Id, out Style? found) && found == style;
    }

    /// <summary>The styles in the order they were added.</summary>
    public IEnumerator<Style> GetEnumerator()
    {
        return order.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    void IFormatOwner.FormatChanged(FormatBase format, string property)
    {
        document.NotifyFormattingChanged((format is RunFormat ? "Styles.DefaultRunFormat." : "Styles.DefaultParagraphFormat.") + property);
    }

    /// <summary>A style id not used yet, from <paramref name="id"/> with a number added when needed.</summary>
    internal string UniqueId(string id)
    {
        if (!byId.ContainsKey(id))
        {
            return id;
        }

        for (int i = 1; ; i++)
        {
            string candidate = id + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!byId.ContainsKey(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Adds a style made for this document (by a reader, an import or the built-ins).</summary>
    internal T Add<T>(T style)
        where T : Style
    {
        byId.Add(style.Id, style);
        order.Add(style);
        document.NotifyFormattingChanged($"Styles.{style.Id}.Added");
        return style;
    }

    /// <summary>Copies every style, the defaults and the document defaults from <paramref name="source"/> into this empty collection.</summary>
    internal void CopyFrom(StyleCollection source, CloneContext context)
    {
        Dictionary<Style, Style> map = [];
        foreach (Style style in source.order)
        {
            Style copy = style.CopyTo(context, style.Id);
            map[style] = copy;
            byId.Add(copy.Id, copy);
            order.Add(copy);
        }

        foreach (Style style in source.order)
        {
            style.CopyLinks(map[style], s => map.TryGetValue(s, out Style? c) ? c : s);
        }

        for (int i = 0; i < defaults.Length; i++)
        {
            defaults[i] = source.defaults[i] is { } d && map.TryGetValue(d, out Style? c) ? c : null;
        }

        DefaultRunFormat.CopyRaw(source.DefaultRunFormat);
        DefaultParagraphFormat.CopyRaw(source.DefaultParagraphFormat);
        context.FixLists(DefaultParagraphFormat);
    }

    /// <summary>
    /// The style in this document that stands for <paramref name="style"/> of another one: the style with the same id when
    /// there is one (as Word's "use destination styles"), otherwise a copy, with the styles it links to brought along too.
    /// </summary>
    internal Style Import(Style style, CloneContext context)
    {
        if (style.Document == document)
        {
            return style;
        }

        Dictionary<object, object> imported = context.Imported;
        if (imported.TryGetValue(style, out object? done))
        {
            return (Style)done;
        }

        if (byId.TryGetValue(style.Id, out Style? existing) && existing.Type == style.Type)
        {
            imported[style] = existing;
            return existing;
        }

        Style copy = style.CopyTo(context, UniqueId(style.Id));
        imported[style] = copy;
        Add(copy);
        style.CopyLinks(copy, s => Import(s, context));
        return copy;
    }

    private void CheckMine(Style style)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (!Contains(style))
        {
            throw new ArgumentException($"Style {style.Id} is not in this document.", nameof(style));
        }
    }

    private string CheckNewId(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (byId.ContainsKey(id))
        {
            throw new ArgumentException($"The document already has a style with id {id}.", nameof(id));
        }

        return id;
    }
}
