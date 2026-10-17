namespace Egap.Pdf.Layout;

/// <summary>What reading a page's tags gave: the draft when the tags passed the sanity check, else the reason they did not.</summary>
internal sealed record TagResult(PageDraft? Draft, string? Problem, double Coverage);

/// <summary>
/// Tagged files first (design Q2, Build Plan task 3): when a page's structure tree passes a sanity check, the tags decide
/// roles, reading order, tables and figures; the glyphs they do not cover are left for the geometric stages. The check
/// fails when too little visible text is tagged, when the tags are not standard block types after the role map, when one
/// element holds what are clearly several paragraphs (everything in one P), or when an element's lines jump between
/// columns back and forth (tags written in content order across a two-column page).
/// </summary>
internal static class TagReader
{
    /// <summary>The share of content glyphs (spaces and artifacts aside) that must belong to an element.</summary>
    public const double MinimumCoverage = 0.9;

    /// <summary>The share of tagged glyphs whose element must resolve to a block type the analysis knows.</summary>
    public const double MinimumTyped = 0.9;

    private enum Kind
    {
        None,
        Block,
        Cell,
        Figure,
    }

    /// <summary>Reads page <paramref name="content"/>'s tags through <paramref name="index"/>.</summary>
    public static TagResult Read(TagIndex index, PdfPage page, PageContent content)
    {
        Dictionary<TagNode, (Kind Kind, TagNode Owner, LayoutRole Role, int Level)> targets = [];
        Dictionary<TagNode, List<PageGlyph>> owned = [];
        PageDraft draft = new(content) { FromTags = true };
        int total = 0, covered = 0, typed = 0;
        foreach (PageGlyph glyph in content.Glyphs)
        {
            if (glyph.IsArtifact)
            {
                draft.Artifacts.Add(glyph);
                continue;
            }

            bool counts = !glyph.IsSpace;
            total += counts ? 1 : 0;
            TagNode? node = index.Find(page, glyph.Mcid);
            if (node is null)
            {
                draft.Unplaced.Add(glyph);
                continue;
            }

            covered += counts ? 1 : 0;
            if (!targets.TryGetValue(node, out var target))
            {
                target = Target(node);
                targets[node] = target;
            }

            if (target.Kind == Kind.None)
            {
                draft.Unplaced.Add(glyph);
                continue;
            }

            typed += counts ? 1 : 0;
            if (!owned.TryGetValue(target.Owner, out List<PageGlyph>? glyphs))
            {
                glyphs = [];
                owned[target.Owner] = glyphs;
            }

            glyphs.Add(glyph);
        }

        double coverage = total == 0 ? 1 : covered / (double)total;
        // A page with no text still reads from its tags when they put an empty table on it.
        if (total == 0 && !index.OnPage(page).Any(n => n.Ancestors().Any(a => a.Type is "TD" or "TH")))
        {
            return new(null, "the page has no text", coverage);
        }

        if (coverage < MinimumCoverage)
        {
            return new(null, $"only {coverage:P0} of the text is tagged", coverage);
        }

        if (typed < covered * MinimumTyped)
        {
            return new(null, $"only {typed * 100 / Math.Max(covered, 1)} % of the tagged text is in block elements the analysis knows", coverage);
        }

        Dictionary<TagNode, TableDraft> tables = [];
        Dictionary<TagNode, FigureDraft> figures = [];
        foreach ((TagNode owner, List<PageGlyph> glyphs) in owned.OrderBy(o => o.Key.Order))
        {
            (Kind kind, _, LayoutRole role, int level) = targets.Values.First(t => t.Owner == owner);
            List<PageLine> lines = PageReader.Lines(glyphs);
            switch (kind)
            {
                case Kind.Block when lines.Count == 0:
                    // An element with nothing but spaces on this page has no block.
                    break;
                case Kind.Block:
                    draft.Blocks.Add(new BlockDraft(role, lines)
                    {
                        Order = owner.Order,
                        Level = level,
                        Tag = owner,
                        Marker = role == LayoutRole.ListItem ? Marker(owner, glyphs, index, page) : null,
                    });
                    break;
                case Kind.Cell:
                    break;
                case Kind.Figure:
                    FigureFor(owner, figures, draft).Lines.AddRange(lines);
                    break;
            }
        }

        // Tables from their rows and cells, so empty cells and spans keep their places: every table with marked content on
        // the page, text or not, and in it every row with marked content here (an empty cell still marks its paragraph).
        HashSet<TagNode> here = [.. index.OnPage(page)];
        foreach (TagNode table in here.Select(n => n.Ancestors().FirstOrDefault(a => a.Type is "TD" or "TH")).OfType<TagNode>()
            .Select(TableOf).OfType<TagNode>().Distinct().OrderBy(t => t.Order))
        {
            if (!tables.ContainsKey(table) && BuildTable(table, owned, here) is TableDraft built)
            {
                tables[table] = built;
                draft.Tables.Add(built);
            }
        }

        // Figures also hold the images and paths tagged inside them.
        foreach (PageImage image in content.Images.Where(i => !i.IsArtifact))
        {
            if (FigureNode(index.Find(page, image.Mcid)) is TagNode figure)
            {
                FigureFor(figure, figures, draft).Images.Add(image);
            }
        }

        foreach (PagePath path in content.Paths.Where(p => !p.IsArtifact))
        {
            if (FigureNode(index.Find(page, path.Mcid)) is TagNode figure)
            {
                FigureFor(figure, figures, draft).Paths.Add(path);
            }
        }

        foreach (FigureDraft figure in draft.Figures)
        {
            figure.Box = PageBox.Around(figure.Images.Select(i => i.Bounds).Concat(figure.Paths.Select(p => p.Bounds)).Concat(figure.Lines.Select(l => l.Box)));
        }

        if (Problem(draft) is string problem)
        {
            return new(null, problem, coverage);
        }

        draft.Renumber();
        return new(draft, null, coverage);
    }

    /// <summary>What a glyph tagged with <paramref name="node"/> belongs to: a block (and its role and level), a table cell or a figure.</summary>
    private static (Kind Kind, TagNode Owner, LayoutRole Role, int Level) Target(TagNode node)
    {
        List<TagNode> chain = [.. node.Ancestors()];
        if (chain.FirstOrDefault(n => n.Type == "Figure") is TagNode figure)
        {
            return (Kind.Figure, figure, LayoutRole.Figure, 0);
        }

        if (chain.FirstOrDefault(n => n.Type is "TD" or "TH") is TagNode cell)
        {
            return (Kind.Cell, cell, LayoutRole.Table, 0);
        }

        TagNode? block = chain.FirstOrDefault(n => Role(n.Type) is not null);
        if (block is null)
        {
            return (Kind.None, node, LayoutRole.Text, 0);
        }

        // A paragraph inside a list item, note or caption belongs to it; a paragraph in a block quote is a quoted paragraph.
        int above = chain.IndexOf(block);
        TagNode? container = chain.Skip(above + 1).TakeWhile(n => n.Type is not ("Table" or "Document" or "Part" or "Art" or "Sect" or "Div"))
            .FirstOrDefault(n => n.Type is "LI" or "Note" or "FENote" or "Caption");
        if (container is not null && block.Type is "P" or "Lbl" or "LBody" or "Span")
        {
            block = container;
        }

        LayoutRole role = Role(block.Type)!.Value;
        if (role == LayoutRole.Text && chain.Any(n => n.Type == "BlockQuote"))
        {
            role = LayoutRole.Quote;
        }

        int level = role switch
        {
            LayoutRole.Heading => block.Type.Length == 2 && char.IsDigit(block.Type[1]) ? block.Type[1] - '0' : Math.Max(1, chain.Count(n => n.Type == "Sect")),
            LayoutRole.ListItem => chain.Count(n => n.Type == "L"),
            _ => 0,
        };
        return (Kind.Block, block, role, level);
    }

    private static LayoutRole? Role(string type)
    {
        return type switch
        {
            "P" or "TOCI" or "BibEntry" or "Index" => LayoutRole.Text,
            "H" or "H1" or "H2" or "H3" or "H4" or "H5" or "H6" => LayoutRole.Heading,
            "Title" => LayoutRole.Title,
            "LI" or "Lbl" or "LBody" => LayoutRole.ListItem,
            "Caption" => LayoutRole.Caption,
            "Note" or "FENote" => LayoutRole.Footnote,
            "BlockQuote" => LayoutRole.Quote,
            "Code" => LayoutRole.Code,
            "Formula" => LayoutRole.Formula,
            _ => null,
        };
    }

    private static TagNode? TableOf(TagNode cell)
    {
        return cell.Ancestors().FirstOrDefault(n => n.Type == "Table");
    }

    private static TagNode? FigureNode(TagNode? node)
    {
        return node?.Ancestors().FirstOrDefault(n => n.Type == "Figure");
    }

    private static FigureDraft FigureFor(TagNode node, Dictionary<TagNode, FigureDraft> figures, PageDraft draft)
    {
        if (!figures.TryGetValue(node, out FigureDraft? figure))
        {
            figure = new FigureDraft(PageBox.Empty) { Order = node.Order, Tag = node, AltText = node.Element.AlternateText };
            figures[node] = figure;
            draft.Figures.Add(figure);
        }

        return figure;
    }

    /// <summary>A list item's marker: the text of its Lbl, when the label is on this page.</summary>
    private static string? Marker(TagNode item, List<PageGlyph> glyphs, TagIndex index, PdfPage page)
    {
        string label = string.Concat(glyphs.Where(g => !g.IsSpace && index.Find(page, g.Mcid)?.Ancestors().TakeWhile(n => n != item).Any(n => n.Type == "Lbl") == true)
            .Select(g => g.Text));
        return label.Length > 0 ? label : null;
    }

    /// <summary>
    /// A table from its rows (TR, also inside THead, TBody and TFoot) and cells (TH, TD) with the spans their Table attributes
    /// give, keeping only the rows that have marked content on this page; rows count from 0 on each page.
    /// </summary>
    private static TableDraft? BuildTable(TagNode table, Dictionary<TagNode, List<PageGlyph>> owned, HashSet<TagNode> here)
    {
        List<TagNode> rows = [.. table.DescendantsAndSelf().Where(n => n.Type == "TR" && TableOf(n) == table)];
        List<CellDraft> cells = [];
        HashSet<(int, int)> taken = [];
        int row = 0, columns = 0;
        foreach (TagNode tr in rows)
        {
            List<TagNode> rowCells = [.. tr.Children.Where(c => c.Type is "TD" or "TH")];
            if (!rowCells.Any(c => c.DescendantsAndSelf().Any(n => owned.ContainsKey(n) || here.Contains(n))))
            {
                continue;
            }

            int column = 0;
            foreach (TagNode cell in rowCells)
            {
                while (taken.Contains((row, column)))
                {
                    column++;
                }

                (int rowSpan, int colSpan) = Spans(cell.Element);
                List<PageGlyph> glyphs = owned.TryGetValue(cell, out List<PageGlyph>? g) ? g : [];
                cells.Add(new CellDraft(row, column, rowSpan, colSpan, cell.Type == "TH", PageReader.Lines(glyphs)));
                for (int r = row; r < row + rowSpan; r++)
                {
                    for (int c = column; c < column + colSpan; c++)
                    {
                        taken.Add((r, c));
                    }
                }

                column += colSpan;
                columns = Math.Max(columns, column);
            }

            row++;
        }

        int rowCount = taken.Count == 0 ? 0 : Math.Max(row, taken.Max(t => t.Item1) + 1);
        return cells.Count == 0 ? null : new TableDraft(cells, rowCount, columns) { Order = table.Order, Tag = table };
    }

    private static (int RowSpan, int ColSpan) Spans(PdfStructureElement element)
    {
        int rowSpan = 1, colSpan = 1;
        foreach (PdfDictionary attributes in element.Attributes)
        {
            if (attributes.GetName("O")?.Value == "Table")
            {
                rowSpan = (int)Math.Clamp(attributes.GetInt("RowSpan") ?? rowSpan, 1, 1000);
                colSpan = (int)Math.Clamp(attributes.GetInt("ColSpan") ?? colSpan, 1, 1000);
            }
        }

        return (rowSpan, colSpan);
    }

    /// <summary>Why the tags cannot be trusted for this page's layout, or null when they can.</summary>
    private static string? Problem(PageDraft draft)
    {
        foreach (BlockDraft block in draft.Blocks)
        {
            if (Lumped(block.Lines))
            {
                return $"one {block.Tag?.Type} element holds several paragraphs";
            }

            if (Interleaved(block.Lines))
            {
                return $"a {block.Tag?.Type} element's lines go back and forth between columns";
            }
        }

        return null;
    }

    /// <summary>Whether a block's lines, taken column by column, have three or more gaps of over twice the line pitch.</summary>
    private static bool Lumped(List<PageLine> lines)
    {
        if (lines.Count < 12)
        {
            return false;
        }

        List<double> gaps = [];
        for (int i = 1; i < lines.Count; i++)
        {
            PageBox a = lines[i - 1].Box, b = lines[i].Box;
            if (Math.Min(a.Right, b.Right) > Math.Max(a.Left, b.Left) && b.Top > a.Top)
            {
                gaps.Add(b.Top - a.Top);
            }
        }

        if (gaps.Count < 6)
        {
            return false;
        }

        double pitch = gaps.Order().ElementAt(gaps.Count / 2);
        return gaps.Count(g => g > pitch * 2.2) >= 3;
    }

    /// <summary>
    /// Whether a block's text goes back and forth between side-by-side columns: three or more of its lines (and a third of
    /// them or more) either hold two pieces of prose with a gutter between them at the same place line after line (lines cut
    /// at gaps wider than one and a half times their size, a piece of prose having four words or more), or stand beside the
    /// line before rather than below it, both being prose. Form lines with short answers to the right, table-of-contents lines
    /// with a number apart and the uneven gaps of an OCR layer do not count.
    /// </summary>
    private static bool Interleaved(List<PageLine> lines)
    {
        List<(double Gutter, double Size)> split = [];
        int beside = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            List<(int Words, double Left)> prose = [.. Pieces(lines[i]).Where(p => p.Words >= 4)];
            if (prose.Count >= 2)
            {
                split.Add((prose[1].Left, lines[i].Size));
            }
            else if (i > 0 && lines[i].Words.Count >= 4 && lines[i - 1].Words.Count >= 4
                && Math.Min(lines[i].Box.Right, lines[i - 1].Box.Right) < Math.Max(lines[i].Box.Left, lines[i - 1].Box.Left))
            {
                beside++;
            }
        }

        int aligned = 0;
        if (split.Count > 0)
        {
            double gutter = split.Select(s => s.Gutter).Order().ElementAt(split.Count / 2);
            aligned = split.Count(s => Math.Abs(s.Gutter - gutter) <= s.Size);
        }

        int mixed = aligned + beside;
        return mixed >= 3 && mixed * 3 >= lines.Count;
    }

    /// <summary>A line's pieces, cut at gaps wider than one and a half times its size: each one's word count and left edge.</summary>
    private static List<(int Words, double Left)> Pieces(PageLine line)
    {
        List<(int Words, double Left)> pieces = [(1, line.Words[0].Box.Left)];
        double gutter = line.Size * 1.5;
        for (int i = 1; i < line.Words.Count; i++)
        {
            if (line.Words[i].Box.Left - line.Words[i - 1].Box.Right > gutter)
            {
                pieces.Add((1, line.Words[i].Box.Left));
            }
            else
            {
                pieces[^1] = (pieces[^1].Words + 1, pieces[^1].Left);
            }
        }

        return pieces;
    }
}
