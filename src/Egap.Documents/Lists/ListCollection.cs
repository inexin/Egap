using System.Collections;

namespace Egap.Documents;

/// <summary>
/// A document's lists and list definitions (Phase 6 design Q6): Word's numbering part. <see cref="AddBulleted"/> and
/// <see cref="AddNumbered"/> make a list with Word's default bulleted or numbered levels.
/// </summary>
public sealed class ListCollection : IReadOnlyCollection<List>
{
    private readonly Document document;
    private readonly List<ListDefinition> definitions = [];
    private readonly List<List> lists = [];

    internal ListCollection(Document document)
    {
        this.document = document;
    }

    /// <summary>How many lists there are.</summary>
    public int Count => lists.Count;

    /// <summary>The list definitions.</summary>
    public IReadOnlyList<ListDefinition> Definitions => definitions;

    /// <summary>The list with <paramref name="id"/>, or null.</summary>
    public List? this[int id] => lists.Find(l => l.Id == id);

    /// <summary>The definition with <paramref name="id"/>, or null.</summary>
    public ListDefinition? GetDefinition(int id)
    {
        return definitions.Find(d => d.Id == id);
    }

    /// <summary>Adds a definition with default levels ("%1." decimal at every level, no indents).</summary>
    public ListDefinition AddDefinition()
    {
        ListDefinition definition = new(document, NextDefinitionId());
        definitions.Add(definition);
        document.NotifyFormattingChanged($"ListDefinitions.{definition.Id}.Added");
        return definition;
    }

    /// <summary>Adds a list that uses <paramref name="definition"/>, continuing other lists that use it.</summary>
    public List Add(ListDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!Contains(definition))
        {
            throw new ArgumentException("The definition is not in this document.", nameof(definition));
        }

        List list = new(document, NextListId(), definition);
        lists.Add(list);
        document.NotifyFormattingChanged($"Lists.{list.Id}.Added");
        return list;
    }

    /// <summary>Adds a bulleted list with Word's default bullets (filled dot, circle, square) and 0.25 inch steps.</summary>
    public List AddBulleted()
    {
        ListDefinition definition = AddDefinition();
        for (int i = 0; i < 9; i++)
        {
            ListLevel level = definition.Levels[i];
            level.NumberStyle = NumberStyle.Bullet;
            (level.LevelText, level.RunFormat.FontName) = (i % 3) switch
            {
                0 => ("", "Symbol"),
                1 => ("o", "Courier New"),
                _ => ("", "Wingdings"),
            };
            Indent(level, i);
        }

        return Add(definition);
    }

    /// <summary>Adds a numbered list as Word's default: 1. a. i. repeating, the roman numerals right-aligned.</summary>
    public List AddNumbered()
    {
        ListDefinition definition = AddDefinition();
        for (int i = 0; i < 9; i++)
        {
            ListLevel level = definition.Levels[i];
            level.LevelText = "%" + (i + 1) + ".";
            (level.NumberStyle, level.Alignment) = (i % 3) switch
            {
                0 => (NumberStyle.Decimal, ParagraphAlignment.Left),
                1 => (NumberStyle.LowerLetter, ParagraphAlignment.Left),
                _ => (NumberStyle.LowerRoman, ParagraphAlignment.Right),
            };
            Indent(level, i);
            if (i % 3 == 2)
            {
                level.ParagraphFormat.FirstLineIndent = -9;
            }
        }

        return Add(definition);
    }

    /// <summary>Whether <paramref name="list"/> is in this document's lists.</summary>
    public bool Contains(List list)
    {
        return list is not null && lists.Contains(list);
    }

    /// <summary>Whether <paramref name="definition"/> is in this document's definitions.</summary>
    public bool Contains(ListDefinition definition)
    {
        return definition is not null && definitions.Contains(definition);
    }

    /// <summary>The lists in the order they were added.</summary>
    public IEnumerator<List> GetEnumerator()
    {
        return lists.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>Adds a definition with a given id, for readers.</summary>
    internal ListDefinition AddDefinition(int id)
    {
        ListDefinition definition = new(document, id);
        definitions.Add(definition);
        return definition;
    }

    /// <summary>Adds a list with a given id, for readers.</summary>
    internal List Add(int id, ListDefinition definition)
    {
        List list = new(document, id, definition);
        lists.Add(list);
        return list;
    }

    /// <summary>Copies every definition and list from <paramref name="source"/> into this empty collection.</summary>
    internal void CopyFrom(ListCollection source, CloneContext context)
    {
        Dictionary<ListDefinition, ListDefinition> map = [];
        foreach (ListDefinition definition in source.definitions)
        {
            ListDefinition copy = AddDefinition(definition.Id);
            copy.CopyFrom(definition, context);
            map[definition] = copy;
        }

        foreach (List list in source.lists)
        {
            Add(list.Id, map[list.Definition]).CopyOverrides(list, context);
        }
    }

    /// <summary>The list in this document that stands for <paramref name="list"/> of another one: a copy, made once per import.</summary>
    internal List Import(List list, CloneContext context, Dictionary<object, object> imported)
    {
        if (list == List.None || list.Document == document)
        {
            return list;
        }

        if (imported.TryGetValue(list, out object? done))
        {
            return (List)done;
        }

        if (!imported.TryGetValue(list.Definition, out object? definitionCopy))
        {
            ListDefinition copy = AddDefinition(NextDefinitionId());
            copy.CopyFrom(list.Definition, context);
            imported[list.Definition] = definitionCopy = copy;
        }

        List result = Add(NextListId(), (ListDefinition)definitionCopy);
        result.CopyOverrides(list, context);
        imported[list] = result;
        document.NotifyFormattingChanged($"Lists.{result.Id}.Added");
        return result;
    }

    private static void Indent(ListLevel level, int i)
    {
        level.ParagraphFormat.LeftIndent = 36 * (i + 1);
        level.ParagraphFormat.FirstLineIndent = -18;
    }

    private int NextDefinitionId()
    {
        return definitions.Count == 0 ? 0 : definitions.Max(d => d.Id) + 1;
    }

    private int NextListId()
    {
        return lists.Count == 0 ? 1 : lists.Max(l => l.Id) + 1;
    }
}
