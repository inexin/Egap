using System.Xml.Linq;

using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Storage;

namespace Egap.Spreadsheets;

/// <summary>Copying and moving ranges of cells, as Excel's Copy, Cut and Paste do.</summary>
public sealed partial class Worksheet
{
    /// <summary>A cell as it is taken up to be written somewhere else, at its distance from the range's first cell.</summary>
    private readonly record struct TakenCell(int Row, int Column, CellRecord Record, FormulaEntry? Formula, bool Dynamic, bool Read, XElement? Extra);

    /// <summary>Copies the cells of a range of this sheet to a sheet of the same book, as Excel's Copy and Paste do.</summary>
    internal void CopyRange(RangeAddress range, Worksheet to, CellAddress at)
    {
        if (Filtered() && VisibleRuns(range) is { } runs)
        {
            CopyVisible(range, to, at, runs);
            return;
        }

        CellMove copy = new(this, range, to, at.Row - range.First.Row, at.Column - range.First.Column);
        CheckPaste(copy, moving: false);
        if (copy.RowOffset == 0 && copy.ColumnOffset == 0 && !copy.Across)
        {
            return;
        }

        // The paste may write over the first cell of a shared formula whose other cells it leaves, so those get their own.
        to.UnshareAll(false);
        Workbook book = Workbook;
        List<TakenCell> cells = Take(range, copy, moving: false);
        List<DataValidation> validations = RulesFor(copy, moving: false);
        List<ConditionalFormat> formats = FormatsFor(copy, moving: false);
        RangeAddress destination = copy.Destination;

        // Excel unmerges the cells it writes over first, so a merge there goes even when it is in the range too; a merge the
        // range cuts is copied as the part of it in the range when its first cell is in the range.
        List<RangeAddress> merges = [.. MergedCells.Where(m => range.Contains(m.First) && (copy.Across || !m.Intersects(destination)))
            .Select(m => Intersection(m, range)!.Value).Where(m => m.First != m.Last)];
        List<CellComment> notes = [.. Comments.Where(c => range.Contains(c.Cell))];
        // Excel copies the part of a link in the range. A copy onto its own cells leaves the links of the cells written over
        // when it copies none, and copies none of those where the two meet.
        RangeAddress? meet = copy.Across ? null : Intersection(range, destination);
        List<(Hyperlink Link, RangeAddress Lands)> links = [.. Hyperlinks
            .Where(l => l.Range.Intersects(range) && (meet is not { } both || !l.Range.Intersects(both)))
            .Select(l => (l, copy.Offset(Intersection(l.Range, range)!.Value)))];
        bool keepLinks = meet is not null && !Hyperlinks.Any(l => l.Range.Intersects(range));

        to.ClearForPaste(destination, keepLinks);
        to.Put(cells, destination.First);
        foreach (RangeAddress merge in merges)
        {
            to.MergedCells.AddRead(copy.Offset(merge));
        }

        foreach (CellComment note in notes)
        {
            CellComment made = to.Comments.Add(copy.Offset(note.Cell), note.Text, note.Author);
            made.Visible = note.Visible;
            made.RichText = note.RichText is null ? null : new XElement(note.RichText);
        }

        foreach (Hyperlink old in to.Hyperlinks.Where(l => links.Exists(c => c.Lands.Intersects(l.Range))).ToList())
        {
            to.Hyperlinks.Remove(old);
        }

        foreach ((Hyperlink link, RangeAddress lands) in links)
        {
            to.Hyperlinks.AddRead(Copied(link, lands));
        }

        // Excel keeps the conditional formats of the cells written over whole when a copy goes onto its own cells.
        to.TrimRules(destination, formats: meet is null);
        foreach (DataValidation rule in validations)
        {
            to.DataValidations.AddRead(rule);
        }

        foreach (ConditionalFormat format in formats)
        {
            to.ConditionalFormats.AddRead(format);
        }

        to.sharedMasters = null;
        book.Recompile();
    }

    /// <summary>Whether a filter of the sheet or of one of its tables hides rows, so that Excel copies only the rows it shows.</summary>
    private bool Filtered() =>
        HasFilterCriteria || Tables.Any(t => t.Source?.Element?.Elements().Any(e => e.Name.LocalName == "autoFilter" && e.Elements().Any(c => c.Name.LocalName == "filterColumn")) == true);

    /// <summary>The runs of rows of a range that are not hidden, first and last; null when none is hidden.</summary>
    private List<(int First, int Last)>? VisibleRuns(RangeAddress range)
    {
        List<(int First, int Last)> runs = [];
        bool hidden = false;
        for (int row = range.First.Row; row <= range.Last.Row; row++)
        {
            if (Rows[row].Hidden)
            {
                hidden = true;
            }
            else if (runs.Count > 0 && runs[^1].Last == row - 1)
            {
                runs[^1] = (runs[^1].First, row);
            }
            else
            {
                runs.Add((row, row));
            }
        }

        return hidden ? runs : null;
    }

    /// <summary>
    /// Copies the rows of a filtered range that show, one under another, as Excel does; when they come in more than one run,
    /// Excel pastes the formulas' results instead of the formulas.
    /// </summary>
    private void CopyVisible(RangeAddress range, Worksheet to, CellAddress at, List<(int First, int Last)> runs)
    {
        int height = runs.Sum(r => r.Last - r.First + 1);
        if (height == 0)
        {
            return;
        }

        if (at.Row + height - 1 >= CellAddress.MaxRows || at.Column + range.Last.Column - range.First.Column >= CellAddress.MaxColumns)
        {
            throw new ArgumentException("The cells would run past the end of the sheet.");
        }

        RangeAddress packed = new(at.Row, at.Column, at.Row + height - 1, at.Column + range.Last.Column - range.First.Column);
        if (runs.Count > 1 && to == this && packed.Intersects(range))
        {
            throw new InvalidOperationException("The rows a filter shows come in more than one run, so the copy can't go onto the cells it copies.");
        }

        int row = at.Row;
        foreach ((int first, int last) in runs)
        {
            RangeAddress run = new(first, range.First.Column, last, range.Last.Column);
            CopyRange(run, to, new CellAddress(row, at.Column));
            if (runs.Count > 1)
            {
                foreach ((int r, int c) in store.Occupied(run).ToList())
                {
                    if (store.Get(r, c).Kind == CellKind.Formula)
                    {
                        to.SetValue(row + r - first, at.Column + c - range.First.Column, GetValue(r, c));
                    }
                }
            }

            row += last - first + 1;
        }
    }

    /// <summary>Moves the cells of a range of this sheet to a sheet of the same book, as Excel's Cut and Paste do.</summary>
    internal void MoveRange(RangeAddress range, Worksheet to, CellAddress at)
    {
        CellMove move = new(this, range, to, at.Row - range.First.Row, at.Column - range.First.Column);
        CheckPaste(move, moving: true);
        if (move.RowOffset == 0 && move.ColumnOffset == 0 && !move.Across)
        {
            return;
        }

        Workbook book = Workbook;

        // Each cell gets its own formula, as the cells of a shared formula need not move alike.
        UnshareAll(false);
        foreach (Worksheet other in book.Worksheets)
        {
            if (other != this)
            {
                other.UnshareAll(other != to);
            }
        }

        // The rules of the cells that move are written for where they go before the references move, as theirs are moved
        // with them.
        List<DataValidation> validations = RulesFor(move, moving: true);
        List<ConditionalFormat> formats = FormatsFor(move, moving: true);
        book.CellsMovingTo(move);

        List<TakenCell> cells = Take(range, move, moving: true);
        RangeAddress destination = move.Destination;
        List<RangeAddress> taken = [.. MergedCells.Where(range.Contains)];
        List<CellComment> notes = [.. Comments.Where(c => range.Contains(c.Cell))];
        List<Hyperlink> links = [.. Hyperlinks.Where(l => range.Contains(l.Range))];

        // A link with only an edge in the cut changes as a reference to its cells does, and keeps clear of the paste.
        List<Hyperlink> edged = [];
        foreach (Hyperlink link in Hyperlinks.Where(l => l.Range.Intersects(range) && !range.Contains(l.Range)).ToList())
        {
            RangeAddress was = link.Range;
            (Area? moved, _) = ReferenceMove.Moved(new Area(AreaKind.Range, was.First.Row, was.First.Column, was.Last.Row, was.Last.Column, AreaFlags.None), this, move);
            if (moved is { Kind: AreaKind.Range } area)
            {
                link.Range = new RangeAddress(area.FirstRow, area.FirstColumn, area.LastRow, area.LastColumn);
                Hyperlinks.Remove(link);
                edged.Add(link);
            }
        }

        List<SheetTable> tables = [.. Tables.Where(t => range.Contains(t.Range))];

        // Excel unmerges the cells it writes over first, all of each merge they touch, so a merge of the range that the
        // cells land on goes.
        foreach (RangeAddress merge in taken)
        {
            MergedCells.Remove(merge);
        }

        List<RangeAddress> merges = [.. taken.Where(m => move.Across || !m.Intersects(destination))];
        foreach (RangeAddress merge in to.MergedCells.Where(destination.Intersects).ToList())
        {
            to.MergedCells.Remove(merge);
        }

        foreach (CellComment note in notes)
        {
            Comments.Remove(note);
        }

        foreach (Hyperlink link in links)
        {
            Hyperlinks.Remove(link);
        }

        ClearForPaste(range);
        TrimRules(range);

        to.ClearForPaste(destination);
        to.TrimRules(destination);
        to.Put(cells, destination.First);
        foreach (RangeAddress merge in merges)
        {
            to.MergedCells.AddRead(move.Offset(merge));
        }

        foreach (CellComment note in notes)
        {
            if (move.Across)
            {
                CellComment made = to.Comments.Add(move.Offset(note.Cell), note.Text, note.Author);
                made.Visible = note.Visible;
                made.RichText = note.RichText;
            }
            else
            {
                note.Cell = move.Offset(note.Cell);
                Comments.AddRead(note);
                Comments.Changed = true;
            }
        }

        foreach (Hyperlink link in edged)
        {
            Hyperlinks.AddRead(link);
        }

        foreach (Hyperlink link in links)
        {
            if (move.Across)
            {
                to.Hyperlinks.AddRead(Copied(link, move.Offset(link.Range)));
            }
            else
            {
                link.Range = move.Offset(link.Range);
                Hyperlinks.AddRead(link);
            }
        }

        foreach (SheetTable table in tables)
        {
            table.Range = move.Offset(table.Range);
            if (table.Source?.Element is { } read)
            {
                OffsetRanges(read, move);
            }
        }

        foreach (DataValidation rule in validations)
        {
            to.DataValidations.AddRead(rule);
        }

        foreach (ConditionalFormat format in formats)
        {
            to.ConditionalFormats.AddRead(format);
        }

        sharedMasters = null;
        to.sharedMasters = null;
        book.Recompile();
    }

    /// <summary>Refuses a paste Excel refuses, or one Egap can't make.</summary>
    private void CheckPaste(CellMove move, bool moving)
    {
        if (move.Source.Last.Row + move.RowOffset >= CellAddress.MaxRows || move.Source.Last.Column + move.ColumnOffset >= CellAddress.MaxColumns)
        {
            throw new ArgumentException("The cells would run past the end of the sheet.");
        }

        RangeAddress destination = move.Destination;

        if (move.To.Workbook != Workbook)
        {
            throw new ArgumentException("The cells can only go to a sheet of the same workbook.");
        }

        if (Arrays(this).Any(a => Partly(a, move.Source)) || Arrays(move.To).Any(a => Partly(a, destination) && !(moving && !move.Across && move.Source.Contains(a))))
        {
            throw new InvalidOperationException("Part of an array formula can't be changed.");
        }

        // Excel cuts only whole merges, and unmerges what a cut lands on; it copies part of a merge, but not onto the cells it
        // copies, and writes a copy only over whole merges, and not over one the copied cells cut.
        if (moving
            ? MergedCells.Any(m => Partly(m, move.Source))
            : move.To.MergedCells.Any(m => Partly(m, destination) || (!move.Across && m.Intersects(destination) && Partly(m, move.Source)))
                || (!move.Across && move.Source.Intersects(destination) && MergedCells.Any(m => Partly(m, move.Source))))
        {
            throw new InvalidOperationException("Part of a merged cell can't be changed.");
        }

        // Egap keeps pivot tables as they were read, so one the paste would change would be left as it was.
        foreach (RangeAddress pivot in Xlsx.SheetObjectReader.PivotTables(move.To).Where(destination.Intersects)
            .Concat(moving ? Xlsx.SheetObjectReader.PivotTables(this).Where(move.Source.Intersects) : []))
        {
            throw new InvalidOperationException($"The cells would change the pivot table at {pivot}, which Egap does not change.");
        }

        if (moving)
        {
            foreach (SheetTable table in Tables.Where(t => t.Range.Intersects(move.Source)))
            {
                if (!move.Source.Contains(table.Range))
                {
                    throw new InvalidOperationException($"The cells take part of the table {table.Name}; a table moves only as a whole.");
                }

                if (move.Across)
                {
                    throw new InvalidOperationException($"The table {table.Name} can't go to another sheet; Egap moves a table only on its own sheet.");
                }
            }
        }
    }

    /// <summary>The ranges of a sheet's array and data table formulas, which a paste must change all of or none.</summary>
    private static IEnumerable<RangeAddress> Arrays(Worksheet sheet)
    {
        foreach (FormulaEntry entry in sheet.formulas.Entries)
        {
            if (entry.Text.Length > 0 && entry.Attribute("t") is "array" or "dataTable" && !sheet.IsDynamic(entry)
                && RangeAddress.TryParse(entry.Attribute("ref"), out RangeAddress range))
            {
                yield return range;
            }
        }
    }

    /// <summary>Whether a range has cells both inside an area and outside it.</summary>
    private static bool Partly(RangeAddress range, RangeAddress area) => range.Intersects(area) && !area.Contains(range);

    /// <summary>
    /// The cells of a range as they will be written where they go: for a copy, formulas written for their new cells, as
    /// Excel's relative references move with them; for a move, formulas as they are. The cells of a dynamic array formula's
    /// spill are left for the formula to fill again.
    /// </summary>
    private List<TakenCell> Take(RangeAddress range, CellMove move, bool moving)
    {
        List<RangeAddress> spilled = [];
        foreach (FormulaEntry entry in formulas.Entries)
        {
            if (IsDynamic(entry) && entry.ArrayRange is { } spill && range.Contains(spill.First))
            {
                spilled.Add(spill);
            }
        }

        List<TakenCell> cells = [];
        foreach ((int row, int column) in store.Occupied(range))
        {
            CellRecord record = store.Get(row, column);
            CellAddress cell = new(row, column);
            XElement? extra = moving && CellExtras.TryGetValue(Key(row, column), out XElement? e) ? e : null;
            if (record.Kind != CellKind.Formula)
            {
                if (spilled.Exists(s => s.Contains(cell) && s.First != cell))
                {
                    record = new CellRecord { Style = record.Style };
                }

                cells.Add(new TakenCell(row - range.First.Row, column - range.First.Column, record, null, false, false, extra));
                continue;
            }

            int index = (int)record.Payload;
            FormulaEntry entry = formulas[index];
            if (entry.Attribute("t") == "dataTable")
            {
                // A data table's cells are copied as the values they show, as Excel copies them.
                cells.Add(new TakenCell(row - range.First.Row, column - range.First.Column, record, new FormulaEntry("", entry.Result), false, false, null));
                continue;
            }

            string text = GetFormula(row, column) ?? "";
            CellAddress target = move.Offset(cell);
            if (!moving && FormulaParser.TryParse(text, r1c1: false, cell) is { } parsed)
            {
                text = FormulaPrinter.Print(parsed, ReferenceStyle.A1, target);
            }

            XAttribute[]? attributes = entry.Attribute("t") == "shared" ? Without(entry.Attributes, "t", "ref", "si") : Clone(entry.Attributes);
            foreach (XAttribute attribute in attributes ?? [])
            {
                if (attribute.Name.LocalName == "ref" && RangeAddress.TryParse(attribute.Value, out RangeAddress area))
                {
                    attribute.Value = move.Offset(area).ToString();
                }
            }

            FormulaEntry taken = new(text, moving ? entry.Result : CellValue.Empty) { Attributes = attributes };
            cells.Add(new TakenCell(row - range.First.Row, column - range.First.Column, record, taken, IsDynamic(entry), formulas.IsRead(index), extra));
        }

        return cells;
    }

    private static XAttribute[]? Clone(XAttribute[]? attributes) => attributes is null ? null : [.. attributes.Select(a => new XAttribute(a))];

    /// <summary>
    /// Empties a range for a paste: its cells, with their formulas and file extras, and the merges, notes and, unless
    /// <paramref name="keepLinks"/>, links in it.
    /// </summary>
    private void ClearForPaste(RangeAddress range, bool keepLinks = false)
    {
        foreach ((int row, int column) in store.Occupied(range).ToList())
        {
            ref readonly CellRecord old = ref store.Get(row, column);
            if (old.Kind == CellKind.Formula)
            {
                spills.Remove(formulas[(int)old.Payload]);
                formulas.Remove((int)old.Payload);
            }

            store.Set(row, column, default);
            CellExtras.Remove(Key(row, column));
            Changed(row, column);
        }

        foreach (RangeAddress merge in MergedCells.Where(range.Contains).ToList())
        {
            MergedCells.Remove(merge);
        }

        foreach (CellComment note in Comments.Where(c => range.Contains(c.Cell)).ToList())
        {
            Comments.Remove(note);
        }

        foreach (Hyperlink link in Hyperlinks.Where(l => !keepLinks && range.Contains(l.Range)).ToList())
        {
            Hyperlinks.Remove(link);
        }
    }

    /// <summary>Writes taken cells into a range that was emptied for them, from its first cell.</summary>
    private void Put(List<TakenCell> cells, CellAddress at)
    {
        foreach (TakenCell taken in cells)
        {
            int row = at.Row + taken.Row;
            int column = at.Column + taken.Column;
            if (!taken.Record.IsVacant)
            {
                SpillOverwritten(row, column);
            }

            CellRecord record = taken.Record;
            if (taken.Formula is { } formula)
            {
                if (formula.Text.Length == 0)
                {
                    store.Set(row, column, new CellRecord { Style = record.Style });
                    SetValue(row, column, formula.Result);
                    continue;
                }

                int index = formulas.Add(formula.Text, formula.Result, formula.Attributes);
                formulas.SetRead(index, taken.Read);
                if (taken.Dynamic)
                {
                    spills.Add(formulas[index]);
                }

                record = new CellRecord { Kind = CellKind.Formula, Payload = index, Style = record.Style };
            }

            store.Set(row, column, record);
            if (taken.Extra is { } extra)
            {
                CellExtras[Key(row, column)] = extra;
            }

            Changed(row, column);
        }
    }

    /// <summary>A link like another, on other cells, written as a new link.</summary>
    private static Hyperlink Copied(Hyperlink link, RangeAddress range) => new(range, link.Address, link.Location) { Display = link.Display, ToolTip = link.ToolTip };

    /// <summary>Moves the ranges in a table's filter and sort, which the move takes with the table.</summary>
    private static void OffsetRanges(XElement element, CellMove move)
    {
        foreach (XAttribute attribute in element.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "ref" or "sqref"))
        {
            if (RangeAddress.TryParse(attribute.Value, out RangeAddress range) && move.Source.Contains(range))
            {
                attribute.Value = move.Offset(range).ToString();
            }
        }
    }

    /// <summary>
    /// The validations of the cells that go, as new rules for where they go: a copy's formulas move as the cells' do, a move's
    /// keep to the cells they named, as the cells' formulas do.
    /// </summary>
    private List<DataValidation> RulesFor(CellMove move, bool moving)
    {
        List<DataValidation> made = [];
        foreach (DataValidation rule in DataValidations)
        {
            if (Pieces(rule.Ranges, move) is not { } ranges)
            {
                continue;
            }

            CellAddress anchor = rule.Ranges[0].First;
            made.Add(new DataValidation(ranges, rule.Type)
            {
                Operator = rule.Operator,
                Formula1 = Carried(rule.Formula1, anchor, ranges[0].First, move, moving),
                Formula2 = Carried(rule.Formula2, anchor, ranges[0].First, move, moving),
                AllowBlank = rule.AllowBlank,
                ShowDropDown = rule.ShowDropDown,
                ShowInputMessage = rule.ShowInputMessage,
                ShowErrorMessage = rule.ShowErrorMessage,
                ErrorStyle = rule.ErrorStyle,
                PromptTitle = rule.PromptTitle,
                Prompt = rule.Prompt,
                ErrorTitle = rule.ErrorTitle,
                Error = rule.Error,
                Source = rule.Source is null ? null : new XElement(rule.Source),
            });
        }

        return made;
    }

    /// <summary>The conditional formats of the cells that go, as new formats for where they go; see <see cref="RulesFor"/>.</summary>
    private List<ConditionalFormat> FormatsFor(CellMove move, bool moving)
    {
        List<ConditionalFormat> made = [];
        foreach (ConditionalFormat format in ConditionalFormats)
        {
            if (Pieces(format.Ranges, move) is not { } ranges)
            {
                continue;
            }

            CellAddress anchor = format.Ranges[0].First;
            ConditionalFormat copy = new(ranges) { Source = format.Source is null ? null : new XElement(format.Source) };
            foreach (ConditionalFormatRule rule in format.Rules)
            {
                ConditionalFormatRule copied = new(rule.Type)
                {
                    Operator = rule.Operator,
                    Text = rule.Text,
                    Priority = rule.Priority,
                    StopIfTrue = rule.StopIfTrue,
                    Style = rule.Style,
                    Source = rule.Source is null ? null : new XElement(rule.Source),
                    ReadStyle = rule.ReadStyle,
                    Drawing = rule.Drawing is null ? null : new XElement(rule.Drawing),
                };
                copied.Formulas.AddRange(rule.Formulas.Select(f => Carried(f, anchor, ranges[0].First, move, moving)!));
                copy.Rules.Add(copied);
            }

            made.Add(copy);
        }

        return made;
    }

    /// <summary>The parts of a rule's ranges in the cells that go, where they go; null when it has none there.</summary>
    private static List<RangeAddress>? Pieces(List<RangeAddress> ranges, CellMove move)
    {
        List<RangeAddress>? pieces = null;
        foreach (RangeAddress range in ranges)
        {
            if (Intersection(range, move.Source) is { } part)
            {
                (pieces ??= []).Add(move.Offset(part));
            }
        }

        return pieces;
    }

    /// <summary>
    /// A rule's formula, written for <paramref name="anchor"/>, as the new rule whose first cell is <paramref name="first"/>
    /// has it.
    /// </summary>
    private string? Carried(string? text, CellAddress anchor, CellAddress first, CellMove move, bool moving)
    {
        if (text is null)
        {
            return null;
        }

        if (moving)
        {
            // The cells keep to what they named: the text is written for the new rule's first cell where it was, and its
            // references move from there as every other formula's do.
            CellAddress was = new(first.Row - move.RowOffset, first.Column - move.ColumnOffset);
            return ReferenceMove.Apply(Reanchored(text, anchor, was), was, this, move, relocated: move.Across);
        }

        return Reanchored(text, anchor, first);
    }

    /// <summary>A formula written for one cell, written for another, its relative references moved by the distance.</summary>
    private static string Reanchored(string text, CellAddress from, CellAddress to) =>
        from == to || FormulaParser.TryParse(text, r1c1: false, from) is not { } parsed ? text : FormulaPrinter.Print(parsed, ReferenceStyle.A1, to);

    /// <summary>
    /// Takes an area out of the sheet's validations and conditional formats, as a paste writes over theirs; a rule left with
    /// no cells goes, and one whose first cell goes has its formulas written for its new first cell. Conditional formats
    /// are left as they are unless <paramref name="formats"/>.
    /// </summary>
    private void TrimRules(RangeAddress area, bool formats = true)
    {
        foreach (DataValidation rule in DataValidations.ToList())
        {
            if (Trimmed(rule.Ranges, area) is not { } anchor)
            {
                continue;
            }

            if (rule.Ranges.Count == 0)
            {
                DataValidations.Remove(rule);
                continue;
            }

            rule.Formula1 = rule.Formula1 is null ? null : Reanchored(rule.Formula1, anchor, rule.Ranges[0].First);
            rule.Formula2 = rule.Formula2 is null ? null : Reanchored(rule.Formula2, anchor, rule.Ranges[0].First);
        }

        foreach (ConditionalFormat format in formats ? ConditionalFormats.ToList() : [])
        {
            if (Trimmed(format.Ranges, area) is not { } anchor)
            {
                continue;
            }

            if (format.Ranges.Count == 0)
            {
                ConditionalFormats.Remove(format);
                continue;
            }

            foreach (ConditionalFormatRule rule in format.Rules)
            {
                for (int i = 0; i < rule.Formulas.Count; i++)
                {
                    rule.Formulas[i] = Reanchored(rule.Formulas[i], anchor, format.Ranges[0].First);
                }
            }
        }
    }

    /// <summary>Takes an area out of a list of ranges; the old first cell when it changed anything, else null.</summary>
    private static CellAddress? Trimmed(List<RangeAddress> ranges, RangeAddress area)
    {
        if (!ranges.Exists(area.Intersects))
        {
            return null;
        }

        CellAddress anchor = ranges[0].First;
        List<RangeAddress> left = [];
        foreach (RangeAddress range in ranges)
        {
            left.AddRange(Without(range, area));
        }

        ranges.Clear();
        ranges.AddRange(left);
        return anchor;
    }

    /// <summary>The cells two ranges share, or null.</summary>
    private static RangeAddress? Intersection(RangeAddress a, RangeAddress b)
    {
        if (!a.Intersects(b))
        {
            return null;
        }

        return new RangeAddress(Math.Max(a.First.Row, b.First.Row), Math.Max(a.First.Column, b.First.Column),
            Math.Min(a.Last.Row, b.Last.Row), Math.Min(a.Last.Column, b.Last.Column));
    }

    /// <summary>A range without an area, as up to four ranges: the rows above it, the rows below it, then the cells left and right of it.</summary>
    private static IEnumerable<RangeAddress> Without(RangeAddress range, RangeAddress area)
    {
        if (Intersection(range, area) is not { } cut)
        {
            yield return range;
            yield break;
        }

        if (cut.First.Row > range.First.Row)
        {
            yield return new RangeAddress(range.First.Row, range.First.Column, cut.First.Row - 1, range.Last.Column);
        }

        if (cut.First.Column > range.First.Column)
        {
            yield return new RangeAddress(cut.First.Row, range.First.Column, cut.Last.Row, cut.First.Column - 1);
        }

        if (cut.Last.Column < range.Last.Column)
        {
            yield return new RangeAddress(cut.First.Row, cut.Last.Column + 1, cut.Last.Row, range.Last.Column);
        }

        if (cut.Last.Row < range.Last.Row)
        {
            yield return new RangeAddress(cut.Last.Row + 1, range.First.Column, range.Last.Row, range.Last.Column);
        }
    }
}
