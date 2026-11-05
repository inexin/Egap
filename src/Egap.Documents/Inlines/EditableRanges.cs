namespace Egap.Documents;

/// <summary>A group of people an editable range is open to (ECMA-376 Part 1, 17.18.18).</summary>
public enum EditorGroup
{
    /// <summary>No group: the range names one <see cref="EditableRangeStart.Editor"/>.</summary>
    None,

    /// <summary>Everyone who opens the document.</summary>
    Everyone,

    /// <summary>The administrators of the document's server.</summary>
    Administrators,

    /// <summary>The contributors of the document's server.</summary>
    Contributors,

    /// <summary>The editors of the document's server.</summary>
    Editors,

    /// <summary>The owners of the document's server.</summary>
    Owners,

    /// <summary>The person editing the document now.</summary>
    Current,
}

/// <summary>
/// The start of a range that stays editable while the document is protected (<see cref="Document.Protection"/>); it pairs
/// with the <see cref="EditableRangeEnd"/> of the same <see cref="Id"/>. The range is open to an <see cref="EditorGroup"/> or
/// to one named <see cref="Editor"/>.
/// </summary>
public sealed class EditableRangeStart : Inline
{
    private int id;
    private EditorGroup group;
    private string? editor;
    private int? firstColumn;
    private int? lastColumn;

    /// <summary>An editable range start open to <paramref name="group"/>, not yet in a paragraph.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="group"/> is <see cref="EditorGroup.None"/> or not defined.</exception>
    public EditableRangeStart(Document document, int id, EditorGroup group)
        : base(document)
    {
        this.id = id;
        this.group = CheckGroup(group);
    }

    /// <summary>An editable range start open to the person named <paramref name="editor"/> (such as "DOMAIN\user" or an e-mail address).</summary>
    public EditableRangeStart(Document document, int id, string editor)
        : base(document)
    {
        ArgumentException.ThrowIfNullOrEmpty(editor);
        this.id = id;
        this.editor = editor;
    }

    /// <summary>The range's id; the start and the end pair by it.</summary>
    public int Id
    {
        get => id;
        set => SetProperty(ref id, value);
    }

    /// <summary>The group the range is open to; <see cref="EditorGroup.None"/> when it names an <see cref="Editor"/>.</summary>
    public EditorGroup Group
    {
        get => group;
        set
        {
            SetProperty(ref group, value == EditorGroup.None ? EditorGroup.None : CheckGroup(value));
            if (value != EditorGroup.None)
            {
                SetProperty(ref editor, null, nameof(Editor));
            }
        }
    }

    /// <summary>The one person the range is open to, or null when it is open to a <see cref="Group"/>.</summary>
    public string? Editor
    {
        get => editor;
        set
        {
            SetProperty(ref editor, string.IsNullOrEmpty(value) ? null : value);
            if (editor is not null)
            {
                SetProperty(ref group, EditorGroup.None, nameof(Group));
            }
        }
    }

    /// <summary>For a range that starts in a table: the first column (0-based) it covers in each row; otherwise null.</summary>
    public int? FirstColumn
    {
        get => firstColumn;
        set => SetProperty(ref firstColumn, value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <summary>For a range that starts in a table: the last column (0-based) it covers in each row; otherwise null.</summary>
    public int? LastColumn
    {
        get => lastColumn;
        set => SetProperty(ref lastColumn, value is null or >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));
    }

    /// <inheritdoc/>
    public override EditableRangeStart Clone(bool deep = true)
    {
        return (EditableRangeStart)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitEditableRangeStart(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        EditableRangeStart copy = editor is not null ? new(context.Target, id, editor) : new(context.Target, id, group);
        copy.firstColumn = firstColumn;
        copy.lastColumn = lastColumn;
        return CopyInline(copy);
    }

    private static EditorGroup CheckGroup(EditorGroup group)
    {
        return group != EditorGroup.None && Enum.IsDefined(group) ? group : throw new ArgumentOutOfRangeException(nameof(group));
    }
}

/// <summary>The end of an editable range; it pairs with the <see cref="EditableRangeStart"/> of the same <see cref="Id"/>.</summary>
public sealed class EditableRangeEnd : Inline
{
    private int id;

    /// <summary>An editable range end for the range with <paramref name="id"/>, not yet in a paragraph.</summary>
    public EditableRangeEnd(Document document, int id)
        : base(document)
    {
        this.id = id;
    }

    /// <summary>The id of the range this ends.</summary>
    public int Id
    {
        get => id;
        set => SetProperty(ref id, value);
    }

    /// <inheritdoc/>
    public override EditableRangeEnd Clone(bool deep = true)
    {
        return (EditableRangeEnd)base.Clone(deep);
    }

    private protected override VisitorAction VisitStart(DocumentVisitor visitor)
    {
        return visitor.VisitEditableRangeEnd(this);
    }

    private protected override Node CloneShallow(CloneContext context)
    {
        return CopyInline(new EditableRangeEnd(context.Target, id));
    }
}
