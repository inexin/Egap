using System.Collections;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>A note on a cell (Excel's classic comment): its author and text.</summary>
public sealed class CellComment
{
    internal CellComment(CellAddress cell, string text, string author)
    {
        Cell = cell;
        this.text = text;
        Author = author;
    }

    private string text;

    /// <summary>The cell the note is on.</summary>
    public CellAddress Cell { get; internal set; }

    /// <summary>The note's author; empty for none.</summary>
    public string Author { get; set; }

    /// <summary>The note's text. Setting it drops the formatting runs a note made in Excel had.</summary>
    public string Text
    {
        get => text;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value != text)
            {
                text = value;
                RichText = null;
            }
        }
    }

    /// <summary>Whether the note shows all the time rather than when the pointer rests on the cell.</summary>
    public bool Visible { get; set; }

    /// <summary>The file's text element with its formatting runs, written back while the text is the same.</summary>
    internal XElement? RichText { get; set; }

    /// <summary>What the model held when the file was read, to tell whether the part can be written back as it was.</summary>
    internal string? ReadState { get; set; }
}

/// <summary>A sheet's notes, at most one a cell.</summary>
public sealed class CellCommentCollection : IReadOnlyCollection<CellComment>
{
    private readonly List<CellComment> comments = [];

    internal CellCommentCollection()
    {
    }

    /// <summary>How many notes there are.</summary>
    public int Count => comments.Count;

    /// <summary>Whether the notes, or the drawing that shows them, changed since the file was read.</summary>
    internal bool Changed { get; set; }

    /// <summary>The note on a cell, or null when it has none.</summary>
    public CellComment? this[CellAddress cell] => comments.Find(c => c.Cell == cell);

    /// <summary>Adds a note to a cell.</summary>
    /// <exception cref="ArgumentException">The cell has a note already.</exception>
    public CellComment Add(CellAddress cell, string text, string author = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(author);
        if (this[cell] is not null)
        {
            throw new ArgumentException($"{cell} has a note already.", nameof(cell));
        }

        CellComment comment = new(cell, text, author);
        comments.Add(comment);
        Changed = true;
        return comment;
    }

    /// <summary>Adds a note to a cell given in A1 form.</summary>
    /// <exception cref="FormatException">The cell is not an A1 reference.</exception>
    /// <exception cref="ArgumentException">The cell has a note already.</exception>
    public CellComment Add(string cell, string text, string author = "") => Add(CellAddress.Parse(cell), text, author);

    /// <summary>Removes a note.</summary>
    /// <returns>Whether the note was on the sheet.</returns>
    public bool Remove(CellComment comment)
    {
        bool removed = comments.Remove(comment);
        Changed |= removed;
        return removed;
    }

    /// <summary>Removes every note.</summary>
    public void Clear()
    {
        Changed |= comments.Count > 0;
        comments.Clear();
    }

    /// <inheritdoc/>
    public IEnumerator<CellComment> GetEnumerator() => comments.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(CellComment comment) => comments.Add(comment);
}
