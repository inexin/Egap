namespace Egap.Documents;

/// <summary>
/// A document's properties: the core ones every format knows (title, author, dates) and custom ones by name, which fields
/// such as TITLE, AUTHOR and DOCPROPERTY show.
/// </summary>
public sealed class DocumentProperties
{
    private readonly Document document;
    private readonly Dictionary<string, string> custom = new(StringComparer.OrdinalIgnoreCase);
    private string? title;
    private string? subject;
    private string? author;
    private string? keywords;
    private string? comments;
    private string? category;
    private string? lastSavedBy;
    private DateTimeOffset? created;
    private DateTimeOffset? saved;
    private DateTimeOffset? printed;

    internal DocumentProperties(Document document)
    {
        this.document = document;
    }

    /// <summary>The title.</summary>
    public string? Title
    {
        get => title;
        set => Set(ref title, value);
    }

    /// <summary>The subject.</summary>
    public string? Subject
    {
        get => subject;
        set => Set(ref subject, value);
    }

    /// <summary>The author (Word's creator).</summary>
    public string? Author
    {
        get => author;
        set => Set(ref author, value);
    }

    /// <summary>The keywords.</summary>
    public string? Keywords
    {
        get => keywords;
        set => Set(ref keywords, value);
    }

    /// <summary>The comments (Word's description).</summary>
    public string? Comments
    {
        get => comments;
        set => Set(ref comments, value);
    }

    /// <summary>The category.</summary>
    public string? Category
    {
        get => category;
        set => Set(ref category, value);
    }

    /// <summary>Who saved the document last.</summary>
    public string? LastSavedBy
    {
        get => lastSavedBy;
        set => Set(ref lastSavedBy, value);
    }

    /// <summary>When the document was made.</summary>
    public DateTimeOffset? Created
    {
        get => created;
        set => Set(ref created, value);
    }

    /// <summary>When the document was last saved.</summary>
    public DateTimeOffset? Saved
    {
        get => saved;
        set => Set(ref saved, value);
    }

    /// <summary>When the document was last printed.</summary>
    public DateTimeOffset? Printed
    {
        get => printed;
        set => Set(ref printed, value);
    }

    /// <summary>The custom properties' names.</summary>
    public IReadOnlyCollection<string> CustomNames => custom.Keys;

    /// <summary>A custom property's value as text, or null.</summary>
    public string? GetCustom(string name)
    {
        return custom.GetValueOrDefault(name);
    }

    /// <summary>Sets a custom property, or removes it with null.</summary>
    public void SetCustom(string name, string? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        bool changed = value is null ? custom.Remove(name) : !custom.TryGetValue(name, out string? old) || old != value;
        if (value is not null)
        {
            custom[name] = value;
        }

        if (changed)
        {
            document.NotifyChanged(document, DocumentChangeKind.PropertyChanged, document, property: "Properties.Custom." + name);
        }
    }

    /// <summary>A property by the name DOCPROPERTY uses: a core property's name (Title, Author, ...) or a custom one.</summary>
    public string? Get(string name)
    {
        return name.ToUpperInvariant() switch
        {
            "TITLE" => title,
            "SUBJECT" => subject,
            "AUTHOR" or "CREATOR" => author,
            "KEYWORDS" => keywords,
            "COMMENTS" or "DESCRIPTION" => comments,
            "CATEGORY" => category,
            "LASTSAVEDBY" => lastSavedBy,
            _ => GetCustom(name),
        };
    }

    internal void CopyFrom(DocumentProperties source)
    {
        title = source.title;
        subject = source.subject;
        author = source.author;
        keywords = source.keywords;
        comments = source.comments;
        category = source.category;
        lastSavedBy = source.lastSavedBy;
        created = source.created;
        saved = source.saved;
        printed = source.printed;
        foreach ((string key, string value) in source.custom)
        {
            custom[key] = value;
        }
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string property = "")
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            document.NotifyChanged(document, DocumentChangeKind.PropertyChanged, document, property: "Properties." + property);
        }
    }
}
