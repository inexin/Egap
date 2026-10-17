using System.Numerics;

using Egami.Drawing;

namespace Egap.Pdf.Layout;

/// <summary>
/// The page-level pipeline (Build Plan task 10): page input; tags first when they pass; a region detector's regions; ruled,
/// partly ruled, aligned and explicit tables; figures; blocks from the glyphs left; reading order over blocks, tables and
/// figures; paragraphs. Roles, which need the whole document, and the public result are made by <see cref="PdfLayoutAnalyzer"/>.
/// </summary>
internal static class PageAnalysis
{
    /// <summary>Analyses page <paramref name="index"/> up to its paragraphs; roles are given later, over the whole document.</summary>
    public static PageDraft Analyze(PdfDocument document, int index, TagIndex? tags, IReadOnlyList<OutlineEntry> outline, PdfLayoutOptions options)
    {
        PageContent content = PageReader.Read(document, index, outline, options.TrackOperations);
        PdfPage page = document.Pages[index];
        LayoutThresholds thresholds = options.Thresholds;

        PageDraft draft;
        List<PageGlyph> pool;
        TagResult? tagged = options.UseTags && tags is not null && tags.HasPage(page) ? TagReader.Read(tags, page, content) : null;
        if (tagged?.Draft is PageDraft fromTags)
        {
            draft = fromTags;
            pool = [.. draft.Unplaced];
            draft.Unplaced.Clear();
            if (options.Tables != TableDetection.None)
            {
                TaggedGrids.Place(draft, content);
            }
        }
        else
        {
            draft = new PageDraft(content);
            pool = [.. content.Glyphs.Where(g => !g.IsArtifact)];
            draft.Artifacts.AddRange(content.Glyphs.Where(g => g.IsArtifact));
        }

        // The geometric items found from here on, each with its place in the content stream.
        Dictionary<PageGlyph, int> place = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < content.Glyphs.Count; i++)
        {
            place.TryAdd(content.Glyphs[i], i);
        }

        List<(object Item, int Content)> found = [];
        List<PageBox> areas = [];
        if (options.RegionDetector is ILayoutRegionDetector detector)
        {
            foreach (LayoutRegionHint hint in detector.Detect(page))
            {
                PageBox box = new(hint.Bounds.X, hint.Bounds.Y, hint.Bounds.X + hint.Bounds.Width, hint.Bounds.Y + hint.Bounds.Height);
                List<PageGlyph> inside = Take(pool, box);
                switch (hint.Role)
                {
                    case LayoutRole.Table:
                        pool.AddRange(inside);
                        areas.Add(box);
                        break;
                    case LayoutRole.Figure:
                        FigureDraft figure = new(box) { Order = int.MaxValue };
                        figure.Images.AddRange(content.Images.Where(i => box.Contains(i.Bounds.CenterX, i.Bounds.CenterY)));
                        figure.Paths.AddRange(content.Paths.Where(p => box.Contains(p.Bounds.CenterX, p.Bounds.CenterY)));
                        figure.Lines.AddRange(PageReader.Lines(inside));
                        draft.Figures.Add(figure);
                        found.Add((figure, inside.Count == 0 ? int.MaxValue / 2 : inside.Min(g => place[g])));
                        break;
                    default:
                        foreach ((BlockDraft block, _) in BlockBuilder.Build(inside, content.Paths, thresholds))
                        {
                            block.Role = hint.Role;
                            block.Confidence = hint.Confidence;
                            block.Fixed = true;
                            draft.Blocks.Add(block);
                            found.Add((block, Place(block, place)));
                        }

                        break;
                }
            }
        }

        // Tables: the caller's areas and a replacement detector's, then Egap's own detection on what is left.
        if (options.TableAreas?.GetValueOrDefault(index) is IReadOnlyList<RectF> given)
        {
            areas.AddRange(given.Select(r => new PageBox(r.X, r.Y, r.X + r.Width, r.Y + r.Height)));
        }

        if (options.TableDetector is ITableDetector tableDetector)
        {
            areas.AddRange(tableDetector.Detect(page).Select(r => new PageBox(r.X, r.Y, r.X + r.Width, r.Y + r.Height)));
        }

        if (options.Tables != TableDetection.None || areas.Count > 0)
        {
            List<TableDraft> tables = [];
            List<TableHint> hints = [];
            if (options.Tables != TableDetection.None && options.TableDetector is null)
            {
                (List<TableDraft> ruled, hints) = RuledTables.Find(content.Paths, pool, content.Images);
                tables.AddRange(ruled);
                Take(pool, [.. ruled.SelectMany(Glyphs)]);
            }

            bool aligned = options.Tables == TableDetection.RuledAndAligned && options.TableDetector is null;
            List<TableDraft> byAlignment = BorderlessTables.Find(pool, aligned ? hints : [], areas, thresholds, aligned);
            tables.AddRange(byAlignment);
            Take(pool, [.. byAlignment.SelectMany(Glyphs)]);
            foreach (TableDraft table in tables)
            {
                List<PageGlyph> glyphs = [.. Glyphs(table)];
                draft.Tables.Add(table);
                found.Add((table, glyphs.Count == 0 ? int.MaxValue / 2 : glyphs.Min(g => place.GetValueOrDefault(g, int.MaxValue / 2))));
            }
        }

        // Figures among the images and drawings no earlier stage placed, with the text inside them.
        foreach (FigureDraft figure in FigureFinder.Find(content, pool, draft.Tables, draft.Figures))
        {
            List<PageGlyph> labels = [.. figure.Lines.SelectMany(l => l.Words).SelectMany(w => w.Glyphs)];
            Take(pool, labels);
            draft.Figures.Add(figure);
            found.Add((figure, labels.Count == 0 ? int.MaxValue / 2 : labels.Min(g => place.GetValueOrDefault(g, int.MaxValue / 2))));
        }

        // Blocks from the glyphs left; artifact glyphs make blocks too, kept only when they turn out to be page furniture.
        foreach ((BlockDraft block, _) in BlockBuilder.Build(pool, content.Paths, thresholds))
        {
            draft.Blocks.Add(block);
            found.Add((block, Place(block, place)));
        }

        foreach ((BlockDraft block, _) in BlockBuilder.Build(draft.Artifacts, [], thresholds))
        {
            block.Artifact = true;
            draft.Blocks.Add(block);
            found.Add((block, Place(block, place)));
        }

        Order(draft, found, options, content);
        Paragraphs(draft);
        return draft;
    }

    /// <summary>
    /// Puts the items found by geometry into reading order: by themselves on an untagged page, or slotted into the tags'
    /// order on a tagged one, each before the first tagged item that starts below it in the same column.
    /// </summary>
    private static void Order(PageDraft draft, List<(object Item, int Content)> found, PdfLayoutOptions options, PageContent content)
    {
        List<PageBox> boxes = [.. found.Select(f => Box(f.Item))];
        List<int> contentOrder = [.. found.Select(f => f.Content)];
        double size = content.Lines.Count == 0 ? 10 : content.Lines.Select(l => l.Size).Order().ElementAt(content.Lines.Count / 2);
        List<int> order = options.ReadingOrderDetector is IReadingOrderDetector reader
            ? [.. reader.Order([.. boxes.Select(Rect)], contentOrder)]
            : ReadingOrder.Order(boxes, contentOrder, size, options.Thresholds);

        // The tagged items keep their order; the geometric ones go in among them.
        List<object> sequence = [.. draft.Blocks.Where(b => b.Tag is not null).Cast<object>().Concat(draft.Tables.Where(t => t.Tag is not null))
            .Concat(draft.Figures.Where(f => f.Tag is not null)).OrderBy(OrderOf)];
        bool tagged = sequence.Count > 0;
        foreach (int i in order.Where(i => i >= 0 && i < found.Count).Distinct())
        {
            PageBox box = boxes[i];
            int at = tagged ? sequence.FindIndex(s => Box(s) is var b && b.Top > box.Top + 1 && Math.Min(b.Right, box.Right) > Math.Max(b.Left, box.Left)) : -1;
            sequence.Insert(at < 0 ? sequence.Count : at, found[i].Item);
        }

        for (int k = 0; k < sequence.Count; k++)
        {
            switch (sequence[k])
            {
                case BlockDraft b: b.Order = k; break;
                case TableDraft t: t.Order = k; break;
                case FigureDraft f: f.Order = k; break;
            }
        }
    }

    /// <summary>Splits the geometric blocks into paragraphs and joins paragraphs a column break or a pull quote interrupts.</summary>
    private static void Paragraphs(PageDraft draft)
    {
        List<BlockDraft> geometric = [.. draft.Blocks.Where(b => b.Tag is null)];
        List<BlockDraft> split = [];
        foreach (BlockDraft block in geometric)
        {
            List<BlockDraft> parts = Layout.Paragraphs.Split([block]);
            foreach (BlockDraft part in parts)
            {
                part.Fixed = block.Fixed;
                part.Artifact = block.Artifact;
            }

            split.AddRange(parts);
        }

        draft.Blocks.RemoveAll(b => b.Tag is null);
        draft.Blocks.AddRange(split);
        draft.Renumber();
        List<BlockDraft> ordered = [.. draft.Blocks.Where(b => !b.Artifact).OrderBy(b => b.Order)];
        Layout.Paragraphs.JoinAcrossColumns(ordered, ordered);
    }

    private static IEnumerable<PageGlyph> Glyphs(TableDraft table)
    {
        return table.Cells.SelectMany(c => c.Lines).SelectMany(l => l.Words).SelectMany(w => w.Glyphs);
    }

    /// <summary>A block's place in the page's content stream: its earliest glyph's.</summary>
    private static int Place(BlockDraft block, Dictionary<PageGlyph, int> place)
    {
        return block.Lines.SelectMany(l => l.Words).SelectMany(w => w.Glyphs).Min(g => place.GetValueOrDefault(g, int.MaxValue / 2));
    }

    private static List<PageGlyph> Take(List<PageGlyph> pool, PageBox box)
    {
        List<PageGlyph> inside = [.. pool.Where(g => box.Contains(g.Box.CenterX, g.Box.CenterY))];
        pool.RemoveAll(g => box.Contains(g.Box.CenterX, g.Box.CenterY));
        return inside;
    }

    private static void Take(List<PageGlyph> pool, IEnumerable<PageBox> boxes)
    {
        List<PageBox> all = [.. boxes];
        pool.RemoveAll(g => all.Any(b => b.Contains(g.Box.CenterX, g.Box.CenterY)));
    }

    private static void Take(List<PageGlyph> pool, List<PageGlyph> glyphs)
    {
        HashSet<PageGlyph> set = new(glyphs, ReferenceEqualityComparer.Instance);
        pool.RemoveAll(set.Contains);
    }

    private static PageBox Box(object item)
    {
        return item switch
        {
            BlockDraft b => b.Box,
            TableDraft t => t.Box,
            FigureDraft f => f.Box,
            _ => PageBox.Empty,
        };
    }

    private static int OrderOf(object item)
    {
        return item switch
        {
            BlockDraft b => b.Order,
            TableDraft t => t.Order,
            FigureDraft f => f.Order,
            _ => 0,
        };
    }

    internal static RectF Rect(PageBox box)
    {
        return box.IsEmpty ? default : new RectF((float)box.Left, (float)box.Top, (float)box.Width, (float)box.Height);
    }

    internal static Vector2 Point(PdfPoint point)
    {
        return new((float)point.X, (float)point.Y);
    }
}
