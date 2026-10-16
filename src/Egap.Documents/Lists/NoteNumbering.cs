namespace Egap.Documents;

/// <summary>
/// Footnote and endnote marks: numbered in document order through the section bodies, footnotes 1, 2, 3 and endnotes i, ii,
/// iii as Word does by default; notes with a custom mark keep it and are not counted. Kept until the document changes.
/// </summary>
internal static class NoteNumbering
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Document, State> States = new();

    public static string Mark(Footnote note)
    {
        if (note.CustomMark is { } custom)
        {
            return custom;
        }

        Document document = note.Document;
        State state = States.GetValue(document, _ => new State());
        if (state.Version != document.Version || state.Numbers is null)
        {
            state.Numbers = Compute(document);
            state.Version = document.Version;
        }

        return state.Numbers.TryGetValue(note, out int number)
            ? Numbering.Format(number, note.Kind == NoteKind.Footnote ? document.Settings.FootnoteNumberStyle : document.Settings.EndnoteNumberStyle)
            : "";
    }

    private static Dictionary<Footnote, int> Compute(Document document)
    {
        Dictionary<Footnote, int> numbers = [];
        int footnotes = document.Settings.FootnoteStartAt - 1, endnotes = document.Settings.EndnoteStartAt - 1;
        foreach (Section section in document.Sections)
        {
            foreach (Footnote note in section.Body.Descendants<Footnote>())
            {
                if (note.CustomMark is null)
                {
                    numbers[note] = note.Kind == NoteKind.Footnote ? ++footnotes : ++endnotes;
                }
            }
        }

        return numbers;
    }

    private sealed class State
    {
        public long Version { get; set; } = -1;

        public Dictionary<Footnote, int>? Numbers { get; set; }
    }
}
