using System.Text;

using Egap.Documents.Word;

namespace Egap.Documents.Doc;

/// <summary>
/// The text of a part, paragraph by paragraph: a paragraph runs to its mark, its properties from the PAPX at the mark; its
/// characters split into runs where the character properties, the piece or a mark (a bookmark or comment range) change.
/// Tables are paragraphs in table: a cell ends at a cell mark, a row at a paragraph that ends the row (the TTP), which holds
/// the row's properties.
/// </summary>
internal sealed partial class DocReader
{
    private enum StoryKind
    {
        Main,
        Note,
        Header,
        Comment,
        TextBox,
    }

    /// <summary>A built cell with its left and right edges in twips, for the grid.</summary>
    private readonly record struct PlacedCell(Cell Cell, int Left, int Right);

    /// <summary>The table being built at one nesting level of a story.</summary>
    private sealed class TableLevel
    {
        public WordRowDefinition Definition { get; set; } = new();

        public Table? Table { get; set; }

        public Cell? Current { get; set; }

        public List<Cell> Cells { get; } = [];

        public List<List<PlacedCell>> Rows { get; } = [];
    }

    /// <summary>A mark between characters: a bookmark or comment range start or end, in the order it goes at its CP.</summary>
    private readonly record struct Marker(int Cp, int Order, Func<Inline> Make);

    private readonly List<Marker> markers = [];
    private int[] markerCps = [];
    private readonly Dictionary<int, (bool Locked, bool Dirty)> fieldEnds = [];
    private readonly Dictionary<int, (NoteKind Kind, int Start, int End)> notes = [];
    private readonly Dictionary<int, Comment> comments = [];
    private readonly HashSet<int> notesRead = [];
    private readonly HashSet<int> commentsPlaced = [];

    private sealed class StoryBuilder(DocReader reader, NodeCollection<Block> blocks, StoryKind kind)
    {
        private readonly Document document = reader.document;
        private readonly List<TableLevel> tables = [];
        private readonly Stack<FieldStart> fields = new();
        private readonly StringBuilder pending = new();
        private Paragraph? paragraph;
        private DocChp? pendingChp;

        public void Read(int start, int end)
        {
            end = Math.Min(end, reader.text.Length);
            int cp = start;
            if (kind is StoryKind.Note or StoryKind.Comment && cp < end && reader.text.Chars[cp] is '\u0002' or '\u0005')
            {
                // The note's or comment's own reference mark, which the model leaves out of its story.
                cp++;
            }

            int guard = 0;
            while (cp < end)
            {
                if (++guard > reader.limits.MaxEntries * 4)
                {
                    throw new DocumentLimitException($"The Word document has more than {reader.limits.MaxEntries} paragraphs in a part.");
                }

                int mark = reader.ParagraphEnd(cp, end);
                ReadParagraph(cp, mark);
                cp = mark + 1;
            }
        }

        private void ReadParagraph(int start, int mark)
        {
            (DocPap pap, DocRow row) = reader.ParagraphProperties(mark);
            int level = pap.Itap > 0 ? pap.Itap : pap.InTable ? 1 : 0;
            char last = reader.text.Chars[mark];
            bool rowEnd = level == 1 ? pap.Ttp : level > 1 && pap.InnerTtp;
            if (rowEnd)
            {
                // The paragraph ending a row holds nothing but its mark, and the row's properties.
                TableLevel table = LevelAt(level);
                table.Definition = row.Build();
                EndRow(level);
                return;
            }

            NodeCollection<Block> container = Container(level);
            paragraph = new Paragraph(document);
            ApplyParagraph(paragraph, pap, mark);
            ReadRuns(start, mark, pap.Style);
            container.AddTrusted(paragraph);
            paragraph = null;
            if (level > 0 && last == '\a' && (level == 1 || pap.InnerCell))
            {
                EndCell(level);
            }
            else if (level > 1 && pap.InnerCell)
            {
                EndCell(level);
            }
        }

        private void ApplyParagraph(Paragraph target, DocPap pap, int mark)
        {
            target.Format.CopyRaw(pap.Format);
            if (pap.Frame is { } frame)
            {
                target.Format.Frame = frame;
            }

            if (reader.styles[pap.Style]?.Style is ParagraphStyle style)
            {
                target.Style = style;
            }

            // A list the paragraph names; ilfo 0 takes away the style's list. The level, when the paragraph does not give it,
            // is the style's.
            if (pap.Ilfo is > 0 and int ilfo)
            {
                target.Format.List = reader.lists[ilfo] ?? List.None;
            }
            else if (pap.Anld is { } anld && reader.lists.Number(anld, pap.NLvlAnm) is { } number)
            {
                target.Format.List = number;
                target.Format.ListLevel = 0;
            }
            else if (pap.Ilfo == 0)
            {
                target.Format.List = List.None;
            }

            if (pap.Ilvl is int ilvl && target.Format.ListLevel is null)
            {
                target.Format.ListLevel = Math.Clamp(ilvl, 0, 8);
            }

            DocChp chp = reader.CharacterProperties(mark, pap.Style);
            target.MarkFormat.CopyRaw(chp.Format);
            if (reader.RevisionOf(chp) is { } revision)
            {
                target.MarkRevision = revision;
            }
        }

        // ---- Runs ----

        private void ReadRuns(int start, int end, int style)
        {
            char[] chars = reader.text.Chars;
            int markerIndex = reader.FirstMarker(start);
            int cp = start;
            while (cp < end || (markerIndex < reader.markers.Count && reader.markers[markerIndex].Cp <= end && cp == end))
            {
                while (markerIndex < reader.markers.Count && reader.markers[markerIndex].Cp <= cp)
                {
                    if (reader.markers[markerIndex].Cp == cp)
                    {
                        Flush();
                        Add(reader.markers[markerIndex].Make());
                    }

                    markerIndex++;
                }

                if (cp >= end)
                {
                    break;
                }

                int next = Math.Min(reader.NextChpBoundary(cp, end), markerIndex < reader.markers.Count ? Math.Max(cp + 1, reader.markers[markerIndex].Cp) : end);
                DocChp chp = reader.CharacterProperties(cp, style);
                if (pendingChp is not null && !ReferenceEquals(pendingChp, chp))
                {
                    Flush();
                }

                pendingChp = chp;
                for (int i = cp; i < next; i++)
                {
                    Character(chars[i], i, chp);
                }

                cp = next;
            }

            Flush();
        }

        private void Character(char c, int cp, DocChp chp)
        {
            switch (c)
            {
                case '\t':
                    Flush();
                    Add(new Tab(document), chp);
                    return;
                case '\v':
                    Flush();
                    Add(new Break(document, BreakKind.Line), chp);
                    return;
                case '\f':
                    Flush();
                    Add(new Break(document, BreakKind.Page), chp);
                    return;
                case '\u000E':
                    Flush();
                    Add(new Break(document, BreakKind.Column), chp);
                    return;
                case '\u0013':
                    {
                        Flush();
                        FieldStart field = new(document);
                        fields.Push(field);
                        Add(field, chp);
                        return;
                    }

                case '\u0014':
                    Flush();
                    if (fields.Count > 0)
                    {
                        Add(new FieldSeparator(document), chp);
                    }

                    return;
                case '\u0015':
                    Flush();
                    if (fields.Count > 0)
                    {
                        FieldStart field = fields.Pop();
                        if (reader.fieldEnds.TryGetValue(cp, out (bool Locked, bool Dirty) flags))
                        {
                            field.IsLocked = flags.Locked;
                            field.IsDirty = flags.Dirty;
                        }

                        Add(new FieldEnd(document), chp);
                    }

                    return;
                case '\u001E':
                    pending.Append('\u2011');
                    return;
                case '\u001F':
                    pending.Append('\u00AD');
                    return;
            }

            // Each reference is read once: a damaged file's note may name itself, or another note may name it again.
            if (reader.notes.TryGetValue(cp, out (NoteKind Kind, int Start, int End) note) && reader.notesRead.Add(cp))
            {
                Flush();
                Footnote footnote = new(document, note.Kind);
                if (c != '\u0002')
                {
                    footnote.CustomMark = c.ToString();
                }

                reader.Enter();
                StoryBuilder story = new(reader, footnote.Story.Blocks, StoryKind.Note);
                story.Read(note.Start, note.End);
                story.Finish();
                reader.Leave();
                Add(footnote, chp);
                return;
            }

            // A comment's story holds no comments: a damaged file's comment could otherwise end up inside itself.
            if (c == '\u0005' && kind != StoryKind.Comment && reader.comments.TryGetValue(cp, out Comment? comment) && reader.commentsPlaced.Add(cp))
            {
                Flush();
                Add(comment);
                return;
            }

            if (c < ' ')
            {
                // A 0x01 with fData holds a field's data (a hyperlink's, a form field's), with fOle2 an OLE object's storage
                // name; neither is a picture. An OLE object shows the picture in its field result.
                if (chp.Special && c == '\u0001' && !chp.Data && !chp.Ole2 && !(kind == StoryKind.Main && cp >= reader.pictureBullets.Start && cp < reader.pictureBullets.End)
                    && reader.InlinePicture(chp.PicLocation) is { } picture)
                {
                    Flush();
                    Add(picture, chp);
                }
                else if (chp.Special && c == '\u0008')
                {
                    // Floating shapes are anchored in the main text and the headers; a text box holds none.
                    if (kind is StoryKind.Main or StoryKind.Header)
                    {
                        Flush();
                        foreach (Inline drawing in reader.FloatingDrawings(cp, kind == StoryKind.Header))
                        {
                            Add(drawing, chp);
                        }
                    }
                    else
                    {
                        reader.warnings.Add(ConversionWarningKind.DrawingDropped, "A drawing of the Word document is left out.");
                    }
                }

                return;
            }

            if (chp.Special && c == '(' && chp.Symbol is { } symbol)
            {
                Flush();
                Run run = new(document, symbol.Char.ToString());
                ApplyRun(run, chp);
                if (symbol.Font < reader.context.Fonts.Count && reader.context.Fonts[symbol.Font] is { Length: > 0 } font)
                {
                    run.Format.FontAscii = font;
                    run.Format.FontHighAnsi = font;
                }

                Add(run);
                return;
            }

            pending.Append(c);
        }

        private void Flush()
        {
            if (pending.Length == 0 || pendingChp is null)
            {
                pending.Clear();
                return;
            }

            Run run = new(document, pending.ToString());
            pending.Clear();
            ApplyRun(run, pendingChp);
            Add(run);
        }

        private void ApplyRun(Inline inline, DocChp chp)
        {
            if (inline is Run run)
            {
                run.Format.CopyRaw(chp.Format);
                if (chp.Style >= 0 && reader.styles[chp.Style]?.Style is CharacterStyle style)
                {
                    run.Style = style;
                }
            }

            if (reader.RevisionOf(chp) is { } revision)
            {
                inline.Revision = revision;
            }
        }

        private void Add(Inline inline, DocChp chp)
        {
            ApplyRun(inline, chp);
            Add(inline);
        }

        private void Add(Inline inline)
        {
            paragraph!.Inlines.AddTrusted(inline);
        }

        // ---- Tables ----

        private TableLevel LevelAt(int level)
        {
            while (tables.Count < level)
            {
                tables.Add(new TableLevel());
            }

            return tables[level - 1];
        }

        /// <summary>Where blocks at table level <paramref name="level"/> go: the story, or the current cell of that level's table.</summary>
        private NodeCollection<Block> Container(int level)
        {
            level = Math.Min(level, Math.Max(1, reader.limits.MaxDepth / 4));
            CloseTablesAbove(level);
            if (level == 0)
            {
                return blocks;
            }

            TableLevel table = LevelAt(level);
            if (table.Table is null)
            {
                NodeCollection<Block> parent = Container(level - 1);
                table.Table = parent.AddTrusted(new Table(document));
            }

            table.Current ??= new Cell(document);
            return table.Current.Blocks;
        }

        private void EndCell(int level)
        {
            level = Math.Min(level, Math.Max(1, reader.limits.MaxDepth / 4));
            TableLevel table = LevelAt(level);
            if (table.Current is not null)
            {
                table.Cells.Add(table.Current);
                table.Current = null;
            }
        }

        private void EndRow(int level)
        {
            level = Math.Min(level, Math.Max(1, reader.limits.MaxDepth / 4));
            CloseTablesAbove(level);
            TableLevel table = LevelAt(level);
            if (table.Current is { } current)
            {
                // Content after the last cell mark: a cell of its own.
                table.Cells.Add(current);
                table.Current = null;
            }

            if (table.Cells.Count == 0)
            {
                return;
            }

            if (table.Table is null)
            {
                NodeCollection<Block> parent = Container(level - 1);
                table.Table = parent.AddTrusted(new Table(document));
            }

            AddRow(table);
        }

        /// <summary>Makes a row from the cells read and the row definition, merging cells across where the definition says so.</summary>
        private void AddRow(TableLevel level)
        {
            Table table = level.Table!;
            WordRowDefinition definition = level.Definition;
            if (table.Rows.Count == 0)
            {
                ApplyTableFormat(table, definition);
            }

            Row row = table.Rows.AddTrusted(new Row(document));
            row.Format.CopyRaw(definition.Row);
            List<PlacedCell> placed = [];
            int left = definition.Left;
            for (int i = 0; i < level.Cells.Count; i++)
            {
                Cell cell = level.Cells[i];
                WordCellDefinition? cellDefinition = i < definition.Cells.Count ? definition.Cells[i] : null;
                int right = cellDefinition?.Right ?? left;
                if (cellDefinition is { Merged: true } && placed.Count > 0)
                {
                    // A cell merged into the one before it: Word keeps its text in the first.
                    PlacedCell previous = placed[^1];
                    placed[^1] = previous with { Right = Math.Max(previous.Right, right) };
                    left = Math.Max(left, right);
                    continue;
                }

                if (cellDefinition is not null)
                {
                    cell.Format.CopyRaw(cellDefinition.Format);
                    if (cellDefinition.WidthUnit is 2 or 3 && cellDefinition.Width > 0)
                    {
                        cell.Format.Width = cellDefinition.WidthUnit == 2 ? TableWidth.Percent(cellDefinition.Width / 50.0) : TableWidth.Points(WordProperties.Twips(cellDefinition.Width));
                    }
                    else if (right > left)
                    {
                        cell.Format.Width = TableWidth.Points(((long)right - left) / 20.0);
                    }
                }

                row.Cells.AddTrusted(cell);
                placed.Add(new PlacedCell(cell, left, Math.Max(left, right)));
                left = Math.Max(left, right);
            }

            level.Rows.Add(placed);
            level.Cells.Clear();
        }

        private void ApplyTableFormat(Table table, WordRowDefinition definition)
        {
            table.Format.CopyRaw(definition.Table);
            table.Format.Layout = definition.AutoFit ? TableLayoutKind.Autofit : TableLayoutKind.Fixed;
            table.Format.Indent ??= WordProperties.Twips(definition.Left + (definition.Gap ?? 0));
            if (definition.Gap is int gap)
            {
                table.Format.CellMarginLeft ??= Math.Max(0, WordProperties.Twips(gap));
                table.Format.CellMarginRight ??= Math.Max(0, WordProperties.Twips(gap));
            }

            if (definition.WidthUnit is 2 or 3 && definition.Width > 0)
            {
                table.Format.Width = definition.WidthUnit == 2 ? TableWidth.Percent(definition.Width / 50.0) : TableWidth.Points(WordProperties.Twips(definition.Width));
            }

            if (definition.Position is { } position)
            {
                table.Format.Position = position;
            }

            if (reader.styles[definition.Style]?.Style is TableStyle style)
            {
                table.Style = style;
            }
        }

        /// <summary>Ends the tables nested deeper than <paramref name="level"/>.</summary>
        private void CloseTablesAbove(int level)
        {
            for (int i = tables.Count; i > level; i--)
            {
                TableLevel table = tables[i - 1];
                if (table.Table is null)
                {
                    continue;
                }

                if (table.Current is not null || table.Cells.Count > 0)
                {
                    // A row the file never ended.
                    if (table.Current is { } current)
                    {
                        table.Cells.Add(current);
                        table.Current = null;
                    }

                    AddRow(table);
                }

                FinishTable(table);
            }
        }

        /// <summary>Sets the table's grid from its cells' edges and the span of each cell over it.</summary>
        private static void FinishTable(TableLevel level)
        {
            Table table = level.Table!;
            level.Table = null;
            if (table.Rows.Count == 0)
            {
                table.Remove();
                level.Rows.Clear();
                return;
            }

            SortedSet<int> edges = [];
            foreach (List<PlacedCell> row in level.Rows)
            {
                foreach (PlacedCell cell in row)
                {
                    edges.Add(cell.Left);
                    edges.Add(cell.Right);
                }
            }

            int[] grid = [.. edges];
            if (grid.Length >= 2 && grid.Length <= 64)
            {
                double[] widths = new double[grid.Length - 1];
                for (int i = 0; i < widths.Length; i++)
                {
                    widths[i] = ((long)grid[i + 1] - grid[i]) / 20.0;
                }

                table.Grid = widths;
                foreach (List<PlacedCell> row in level.Rows)
                {
                    foreach (PlacedCell cell in row)
                    {
                        int span = Array.BinarySearch(grid, cell.Right) - Array.BinarySearch(grid, cell.Left);
                        if (span > 1)
                        {
                            cell.Cell.Format.GridSpan = span;
                        }
                    }
                }
            }

            level.Rows.Clear();
        }

        /// <summary>Ends the story: its open tables; a part other than the main text gets an empty paragraph when it has none.</summary>
        public void Finish()
        {
            CloseTablesAbove(0);
            if (blocks.Count == 0 && kind != StoryKind.Main)
            {
                blocks.AddTrusted(new Paragraph(document));
            }
        }
    }

    /// <summary>The first marker at or after <paramref name="cp"/>.</summary>
    private int FirstMarker(int cp)
    {
        int index = Array.BinarySearch(markerCps, cp);
        if (index < 0)
        {
            return ~index;
        }

        while (index > 0 && markerCps[index - 1] == cp)
        {
            index--;
        }

        return index;
    }

    /// <summary>Where the run of characters with the same properties from <paramref name="cp"/> ends: its piece or CHPX run.</summary>
    private int NextChpBoundary(int cp, int limit)
    {
        int index = text.PieceAt(cp);
        if (index < 0)
        {
            return cp + 1;
        }

        DocPiece piece = text.Pieces[index];
        long fc = piece.Fc + ((long)(cp - piece.Cp) * piece.Width);
        int run = chpx.Find(fc);
        int end = piece.CpEnd;
        if (run < 0)
        {
            end = cp + 1;
        }
        else
        {
            long to = chpx[run].To;
            long cps = (to - piece.Fc + piece.Width - 1) / piece.Width;
            if (piece.Cp + cps > cp && piece.Cp + cps < end)
            {
                end = (int)(piece.Cp + cps);
            }
        }

        return Math.Clamp(end, cp + 1, Math.Max(cp + 1, limit));
    }

    private void SortMarkers()
    {
        markers.Sort((a, b) => a.Cp != b.Cp ? a.Cp.CompareTo(b.Cp) : a.Order.CompareTo(b.Order));
        markerCps = [.. markers.Select(m => m.Cp)];
    }
}
