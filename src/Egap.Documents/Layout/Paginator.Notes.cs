namespace Egap.Documents.Layout;

/// <summary>
/// Footnotes and endnotes (Phase 6 Build Plan task 20): a footnote goes at the foot of the page its reference is on, under
/// a short separator line, the body giving up the room it needs; endnotes follow the document's last text. Each note's text
/// starts with its mark.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>Space Word leaves for the separator line above a page's footnotes.</summary>
    private const double SeparatorHeight = 12;

    private readonly Dictionary<(Footnote Note, double Width), double> noteHeights = [];

    /// <summary>The footnotes referenced in the text between <paramref name="start"/> and <paramref name="end"/>.</summary>
    private static IEnumerable<Footnote> NotesIn(ParagraphContent content, int start, int end)
    {
        foreach (InlineSpan span in content.Spans)
        {
            if (span.Node is Footnote { Kind: NoteKind.Footnote } note && span.Start >= start && span.Start < end)
            {
                yield return note;
            }
        }
    }

    /// <summary>A footnote's height at the note area's width.</summary>
    private double NoteHeight(Footnote note)
    {
        double width = NoteWidth;
        if (!noteHeights.TryGetValue((note, width), out double height))
        {
            measuring++;
            (_, height) = LayNote(note, 0, 0, width);
            measuring--;
            noteHeights[(note, width)] = height;
        }

        return height;
    }

    private double NoteWidth => page!.Geometry.BodyRight - page.Geometry.BodyLeft;

    /// <summary>The room the notes of the text from <paramref name="start"/> to <paramref name="end"/> would add on this page, the separator aside.</summary>
    private double NotesRoom(ParagraphContent content, int start, int end)
    {
        double room = 0;
        foreach (Footnote note in NotesIn(content, start, end))
        {
            if (!page!.Notes.Contains(note))
            {
                room += NoteHeight(note);
            }
        }

        return room;
    }

    /// <summary>Adds the footnotes referenced in a placed line to the page.</summary>
    private void TakeNotes(ParagraphContent content, int start, int end)
    {
        if (measuring > 0 || page is null)
        {
            return;
        }

        foreach (Footnote note in NotesIn(content, start, end))
        {
            if (!page.Notes.Contains(note))
            {
                page.NotesHeight += NoteHeight(note) + (page.Notes.Count == 0 ? SeparatorHeight : 0);
                page.Notes.Add(note);
            }
        }
    }

    /// <summary>A note's text with its mark in front, laid out at <paramref name="top"/>.</summary>
    internal (List<LayoutBox> Blocks, double Height) LayNote(Footnote note, double x, double top, double width)
    {
        notePrefix = note;
        try
        {
            return LayStory(note.Story, x, top, width);
        }
        finally
        {
            notePrefix = null;
        }
    }

    /// <summary>Endnotes after the last text of the document, each with its mark, under a separator.</summary>
    private void FlowEndnotes()
    {
        List<Footnote> endnotes = [.. document.Descendants<Footnote>().Where(n => n.Kind == NoteKind.Endnote && n.Revision?.Removes != true)];
        if (endnotes.Count == 0 || page is null)
        {
            return;
        }

        y += SeparatorHeight;
        foreach (Footnote note in endnotes)
        {
            notePrefix = note;
            try
            {
                FlowBlocks(note.Story.Blocks);
            }
            finally
            {
                notePrefix = null;
            }
        }
    }

    // The note whose first paragraph is being laid out, to put its mark in front.
    private Footnote? notePrefix;
}
