using System.Globalization;
using System.Numerics;

using Egami.Drawing;
using Egami.Fonts.Layout;
using Egami.Pixels;

using Egap.Documents.Charts;


namespace Egap.Spreadsheets.Printing;

/// <summary>What one print of a workbook shares: the workbook, its fonts and colours, and the header and footer fields.</summary>
internal sealed record PrintContext(Workbook Book, SheetFonts Fonts, SheetColors Colors, DateTime Time, string? FilePath);

/// <summary>Groups what a page draws into tags, for a tagged PDF; the renderer calls it around each piece.</summary>
internal interface IPageTagger
{
    /// <summary>Starts drawing that is not content: gridlines, fills, borders, headings, headers and footers.</summary>
    void BeginArtifact();

    /// <summary>Starts a cell's text: the cell's table cell the first time it prints, an artifact when it repeats as a title.</summary>
    void BeginCell(SheetPage page, int row, int column, RangeAddress? merged);

    /// <summary>Starts a picture: a figure with its alternative text.</summary>
    void BeginFigure(SheetPage page, SheetDrawing drawing);

    /// <summary>Ends what the last begin started.</summary>
    void End();
}

/// <summary>A run of rows or columns on a page: its sheet indexes and where it starts, in sheet points from the content's corner.</summary>
internal readonly record struct Segment(int First, int Last, double Start, double Size, double Skip = 0);

/// <summary>
/// Draws one page of a sheet: gridlines, fills, colour scales, borders, data bars and icon sets, then the
/// cells' text with overflow, wrapping, rotation, shrink to fit and indent, then pictures, headings, and the header and footer.
/// The cells draw in sheet points under the page's scale; the header and footer draw in page points.
/// </summary>
internal sealed partial class SheetPageRenderer
{
    /// <summary>The space between a cell's text and its edges: two pixels at 96 dpi.</summary>
    private const double Padding = 2 * SheetFonts.PointsPerPixel;

    private static readonly Rgba8 Black = new(0, 0, 0, 255);
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    private readonly SheetPage page;
    private readonly Canvas canvas;
    private readonly IPageTagger? tagger;
    private readonly Worksheet sheet;
    private readonly Workbook book;
    private readonly SheetGrid grid;
    private readonly SheetPaginator paginator;
    private readonly SheetFonts fonts;
    private readonly SheetColors colors;
    private readonly List<Segment> rows = [];
    private readonly List<Segment> columns = [];
    private readonly List<RangeAddress> merges = [];
    private ConditionalDrawing? conditional;

    public SheetPageRenderer(SheetPage page, Canvas canvas, IPageTagger? tagger)
    {
        this.page = page;
        this.canvas = canvas;
        this.tagger = tagger;
        paginator = page.Paginator;
        sheet = paginator.Sheet;
        book = sheet.Workbook;
        grid = paginator.Grid;
        fonts = page.Context.Fonts;
        colors = page.Context.Colors;
        PagePlan plan = page.Plan;
        double y = paginator.HeadingHeight;
        if (plan.TitleRows && paginator.TitleRows is { } titleRows)
        {
            y = Add(rows, titleRows.First, titleRows.Last, y, grid.Rows);
        }

        y = Add(rows, plan.Rows.First, plan.Rows.Last, y, grid.Rows);
        double x = paginator.HeadingWidth;
        if (plan.TitleColumns && paginator.TitleColumns is { } titleColumns)
        {
            x = Add(columns, titleColumns.First, titleColumns.Last, x, grid.Columns);
        }

        if (plan.Columns.Size > 0)
        {
            // A later part of a column wider than the page.
            columns.Add(new Segment(plan.Columns.First, plan.Columns.Last, x, plan.Columns.Size, plan.Columns.Offset));
            x += plan.Columns.Size;
        }
        else
        {
            x = Add(columns, plan.Columns.First, plan.Columns.Last, x, grid.Columns);
        }

        ContentWidth = x;
        ContentHeight = y;
        foreach (RangeAddress merged in sheet.MergedCells)
        {
            if (rows.Any(r => merged.Last.Row >= r.First && merged.First.Row <= r.Last)
                && columns.Any(c => merged.Last.Column >= c.First && merged.First.Column <= c.Last))
            {
                merges.Add(merged);
            }
        }

        static double Add(List<Segment> list, int first, int last, double start, Func<int, int, double> size)
        {
            double length = size(first, last);
            list.Add(new Segment(first, last, start, length));
            return start + length;
        }
    }

    /// <summary>The cells' width and height on the page, in sheet points, with titles and headings.</summary>
    public double ContentWidth { get; }

    public double ContentHeight { get; }

    public void Draw()
    {
        PageSetup setup = sheet.PageSetup;
        float scale = (float)paginator.Scale;
        double left = setup.LeftMargin * 72;
        double top = setup.TopMargin * 72;
        if (setup.CenterHorizontally)
        {
            left += Math.Max(0, (paginator.PrintableWidth - (ContentWidth * scale)) / 2);
        }

        if (setup.CenterVertically)
        {
            top += Math.Max(0, (paginator.PrintableHeight - (ContentHeight * scale)) / 2);
        }

        canvas.Save();
        canvas.Translate((float)left, (float)top);
        canvas.Scale(scale, scale);
        foreach (Segment row in rows)
        {
            foreach (Segment column in columns)
            {
                canvas.Save();
                canvas.ClipRect(Rect(column.Start, row.Start, column.Size, row.Size));
                Begin(null);
                if (setup.PrintGridLines)
                {
                    DrawGridLines(row, column);
                }

                DrawFills(row, column);
                DrawBorders(row, column);
                DrawConditionalMarks(row, column);
                End();
                DrawTexts(row, column);
                canvas.Restore();
            }
        }

        DrawPictures();
        if (setup.PrintHeadings)
        {
            Begin(null);
            DrawHeadings();
            End();
        }

        canvas.Restore();
        Begin(null);
        DrawHeaderFooter();
        End();
    }

    private void Begin(Action<IPageTagger>? begin)
    {
        if (tagger is null)
        {
            return;
        }

        if (begin is null)
        {
            tagger.BeginArtifact();
        }
        else
        {
            begin(tagger);
        }
    }

    private void End() => tagger?.End();

    private static RectF Rect(double x, double y, double width, double height) => new((float)x, (float)y, (float)width, (float)height);

    /// <summary>Where column <paramref name="column"/> starts in <paramref name="segment"/>, in sheet points; it may lie outside it.</summary>
    private double X(Segment segment, int column) => segment.Start - segment.Skip + grid.Columns(segment.First, column - 1);

    private double Y(Segment segment, int row) => segment.Start + grid.Rows(segment.First, row - 1);

    /// <summary>The merged range holding the cell, among those on the page, or null.</summary>
    private RangeAddress? MergeAt(int row, int column)
    {
        foreach (RangeAddress merged in merges)
        {
            if (merged.Contains(new CellAddress(row, column)))
            {
                return merged;
            }
        }

        return null;
    }

    /// <summary>The style of a cell: its own, else its row's, else its column's.</summary>
    private CellStyle StyleAt(int row, int column)
    {
        ref readonly Storage.CellRecord record = ref sheet.Store.Get(row, column);
        int index = record.Style;
        if (record.IsVacant)
        {
            if (sheet.RowRecords.TryGetValue(row, out RowRecord? rowRecord) && rowRecord.Style >= 0)
            {
                index = rowRecord.Style;
            }
            else if (sheet.ColumnSpans.Find(column) is { Style: >= 0 } span)
            {
                index = span.Style;
            }
        }

        return index >= 0 && index < book.Styles.Count ? book.Styles[index] : CellStyle.Default;
    }

    /// <summary>Calls <paramref name="draw"/> for each cell of the segments, a merged range once at its first cell there.</summary>
    private void ForEachCell(Segment rowSegment, Segment columnSegment, Action<int, int, RectF, RangeAddress?> draw)
    {
        double y = rowSegment.Start;
        for (int r = rowSegment.First; r <= rowSegment.Last; r++)
        {
            double height = grid.Row(r);
            if (height <= 0)
            {
                continue;
            }

            double x = columnSegment.Start - columnSegment.Skip;
            for (int c = columnSegment.First; c <= columnSegment.Last; c++)
            {
                double width = grid.Column(c);
                if (width <= 0)
                {
                    continue;
                }

                if (merges.Count > 0 && MergeAt(r, c) is { } merged)
                {
                    int firstRow = Math.Max(merged.First.Row, rowSegment.First);
                    int firstColumn = Math.Max(merged.First.Column, columnSegment.First);
                    if (r == FirstVisibleRow(firstRow, merged.Last.Row) && c == FirstVisibleColumn(firstColumn, merged.Last.Column))
                    {
                        double mx = X(columnSegment, merged.First.Column);
                        double my = Y(rowSegment, merged.First.Row);
                        draw(merged.First.Row, merged.First.Column, Rect(mx, my, grid.Columns(merged.First.Column, merged.Last.Column), grid.Rows(merged.First.Row, merged.Last.Row)), merged);
                    }
                }
                else
                {
                    draw(r, c, Rect(x, y, width, height), null);
                }

                x += width;
            }

            y += height;
        }
    }

    private int FirstVisibleRow(int first, int last)
    {
        for (int r = first; r < last; r++)
        {
            if (grid.Row(r) > 0)
            {
                return r;
            }
        }

        return last;
    }

    private int FirstVisibleColumn(int first, int last)
    {
        for (int c = first; c < last; c++)
        {
            if (grid.Column(c) > 0)
            {
                return c;
            }
        }

        return last;
    }

    private void DrawGridLines(Segment rowSegment, Segment columnSegment)
    {
        Stroke stroke = new(0.5f);
        Rgba8 color = new(0, 0, 0, 255);
        float left = (float)columnSegment.Start;
        float right = (float)(columnSegment.Start + columnSegment.Size);
        float top = (float)rowSegment.Start;
        float bottom = (float)(rowSegment.Start + rowSegment.Size);
        double y = rowSegment.Start;
        canvas.DrawLine(new Vector2(left, top), new Vector2(right, top), stroke, color);
        for (int r = rowSegment.First; r <= rowSegment.Last; r++)
        {
            double height = grid.Row(r);
            if (height <= 0)
            {
                continue;
            }

            y += height;
            canvas.DrawLine(new Vector2(left, (float)y), new Vector2(right, (float)y), stroke, color);
        }

        double x = columnSegment.Start - columnSegment.Skip;
        canvas.DrawLine(new Vector2(left, top), new Vector2(left, bottom), stroke, color);
        for (int c = columnSegment.First; c <= columnSegment.Last; c++)
        {
            double width = grid.Column(c);
            if (width <= 0)
            {
                continue;
            }

            x += width;
            canvas.DrawLine(new Vector2((float)x, top), new Vector2((float)x, bottom), stroke, color);
        }

        // Inside a merged range there are no gridlines: its fill, white when it has none, covers them.
        foreach (RangeAddress merged in merges)
        {
            RectF area = Rect(X(columnSegment, merged.First.Column), Y(rowSegment, merged.First.Row),
                grid.Columns(merged.First.Column, merged.Last.Column), grid.Rows(merged.First.Row, merged.Last.Row));
            canvas.FillRect(new RectF(area.X + 0.25f, area.Y + 0.25f, Math.Max(0, area.Width - 0.5f), Math.Max(0, area.Height - 0.5f)), White);
        }
    }

    private void DrawFills(Segment rowSegment, Segment columnSegment)
    {
        ForEachCell(rowSegment, columnSegment, (r, c, rect, _) =>
        {
            CellStyle style = StyleAt(r, c);
            Rgba8? fill = (sheet.ConditionalFormats.Count > 0 ? Conditional().ScaleColor(r, c) : null) ?? FillColor(style.Fill);
            if (fill is { } color)
            {
                canvas.FillRect(rect, color);
            }
        });
    }

    private ConditionalDrawing Conditional() => conditional ??= new ConditionalDrawing(sheet, colors);

    /// <summary>
    /// The colour a fill shows: a solid fill's foreground, and for a pattern the foreground and background mixed by how much
    /// of the cell the pattern covers.
    /// </summary>
    private Rgba8? FillColor(CellFill fill)
    {
        if (fill.Pattern == CellFillPattern.None)
        {
            return null;
        }

        Rgba8 foreground = colors.Resolve(fill.Foreground, Black);
        if (fill.Pattern == CellFillPattern.Solid)
        {
            return foreground;
        }

        Rgba8 background = colors.Resolve(fill.Background, White);
        double cover = fill.Pattern switch
        {
            CellFillPattern.DarkGray => 0.75,
            CellFillPattern.MediumGray => 0.5,
            CellFillPattern.LightGray => 0.25,
            CellFillPattern.Gray125 => 0.125,
            CellFillPattern.Gray0625 => 0.0625,
            CellFillPattern.DarkHorizontal or CellFillPattern.DarkVertical or CellFillPattern.DarkDown or CellFillPattern.DarkUp => 0.5,
            CellFillPattern.DarkGrid or CellFillPattern.DarkTrellis => 0.75,
            CellFillPattern.LightGrid or CellFillPattern.LightTrellis => 0.4375,
            _ => 0.25,
        };
        return Mix(background, foreground, cover);
    }

    private static Rgba8 Mix(Rgba8 a, Rgba8 b, double t) => new(
        (byte)Math.Round(a.R + ((b.R - a.R) * t)), (byte)Math.Round(a.G + ((b.G - a.G) * t)), (byte)Math.Round(a.B + ((b.B - a.B) * t)), 255);

    private void DrawBorders(Segment rowSegment, Segment columnSegment)
    {
        ForEachCell(rowSegment, columnSegment, (r, c, rect, merged) =>
        {
            CellBorders borders = StyleAt(r, c).Borders;
            if (merged is { } m)
            {
                // A merged range's edges are its edge cells' borders: the right and bottom ones from the last row and column.
                borders = borders with
                {
                    Right = StyleAt(m.First.Row, m.Last.Column).Borders.Right,
                    Bottom = StyleAt(m.Last.Row, m.First.Column).Borders.Bottom,
                };
            }

            if (ReferenceEquals(borders, CellBorders.None))
            {
                return;
            }

            float left = rect.X, top = rect.Y, right = rect.X + rect.Width, bottom = rect.Y + rect.Height;
            Edge(borders.Top, new Vector2(left, top), new Vector2(right, top));
            Edge(borders.Bottom, new Vector2(left, bottom), new Vector2(right, bottom));
            Edge(borders.Left, new Vector2(left, top), new Vector2(left, bottom));
            Edge(borders.Right, new Vector2(right, top), new Vector2(right, bottom));
            if (borders.DiagonalDown)
            {
                Edge(borders.Diagonal, new Vector2(left, top), new Vector2(right, bottom));
            }

            if (borders.DiagonalUp)
            {
                Edge(borders.Diagonal, new Vector2(left, bottom), new Vector2(right, top));
            }
        });
    }

    /// <summary>Draws one border line in Excel's widths and dashes, in pixels at 96 dpi.</summary>
    private void Edge(CellBorder border, Vector2 from, Vector2 to)
    {
        if (border.Style == CellBorderStyle.None)
        {
            return;
        }

        Rgba8 color = colors.Resolve(border.Color, Black);
        const float Px = (float)SheetFonts.PointsPerPixel;
        if (border.Style == CellBorderStyle.Double)
        {
            Vector2 along = Vector2.Normalize(to - from);
            Vector2 across = new Vector2(-along.Y, along.X) * Px;
            Stroke thin = new(Px);
            canvas.DrawLine(from - across, to - across, thin, color);
            canvas.DrawLine(from + across, to + across, thin, color);
            return;
        }

        (float width, float[]? dash) = border.Style switch
        {
            CellBorderStyle.Medium => (2f, (float[]?)null),
            CellBorderStyle.Thick => (3f, null),
            CellBorderStyle.Dashed => (1f, [3f, 1f]),
            CellBorderStyle.Dotted => (1f, [1f, 1f]),
            CellBorderStyle.Hair => (0.5f, [1f, 1f]),
            CellBorderStyle.MediumDashed => (2f, [9f, 3f]),
            CellBorderStyle.DashDot => (1f, [9f, 3f, 3f, 3f]),
            CellBorderStyle.MediumDashDot => (2f, [9f, 3f, 3f, 3f]),
            CellBorderStyle.DashDotDot => (1f, [9f, 3f, 3f, 3f, 3f, 3f]),
            CellBorderStyle.MediumDashDotDot => (2f, [9f, 3f, 3f, 3f, 3f, 3f]),
            CellBorderStyle.SlantDashDot => (2f, [11f, 1f, 5f, 1f]),
            _ => (1f, null),
        };
        Stroke stroke = dash is null
            ? new Stroke(width * Px)
            : new Stroke(width * Px) { Dash = new DashPattern([.. dash.Select(d => d * Px)]) };
        canvas.DrawLine(from, to, stroke, color);
    }

    private void DrawConditionalMarks(Segment rowSegment, Segment columnSegment)
    {
        if (sheet.ConditionalFormats.Count == 0)
        {
            return;
        }

        ConditionalDrawing drawing = Conditional();
        ForEachCell(rowSegment, columnSegment, (r, c, rect, _) =>
        {
            drawing.DrawBar(canvas, r, c, rect);
            drawing.DrawIcon(canvas, r, c, rect);
        });
    }

    private void DrawPictures()
    {
        foreach (SheetDrawing drawing in sheet.Drawings)
        {
            Chart? chart = drawing.Chart;
            byte[]? data = drawing.Kind == SheetDrawingKind.Picture ? drawing.ImageData : null;
            if (data is null && chart is null)
            {
                continue;
            }

            (double x, double y, double width, double height) = grid.Bounds(drawing);
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            EncodedImage? image = null;
            foreach (Segment row in rows)
            {
                foreach (Segment column in columns)
                {
                    double left = column.Start - column.Skip + x - grid.Columns(0, column.First - 1);
                    double top = row.Start + y - grid.Rows(0, row.First - 1);
                    if (left >= column.Start + column.Size || left + width <= column.Start || top >= row.Start + row.Size || top + height <= row.Start)
                    {
                        continue;
                    }

                    if (chart is not null)
                    {
                        canvas.Save();
                        canvas.ClipRect(Rect(column.Start, row.Start, column.Size, row.Size));
                        Begin(t => t.BeginFigure(page, drawing));
                        ChartPrinting.Draw(chart, book, canvas, Rect(left, top, width, height), colors, fonts);
                        End();
                        canvas.Restore();
                        continue;
                    }

                    try
                    {
                        image ??= EncodedImage.Load(data!);
                    }
                    catch (Exception e) when (e is Egami.EgamiException or InvalidDataException or NotSupportedException or ArgumentException)
                    {
                        // An image Egami cannot read (an EMF, say) is left out.
                        break;
                    }

                    canvas.Save();
                    canvas.ClipRect(Rect(column.Start, row.Start, column.Size, row.Size));
                    Begin(t => t.BeginFigure(page, drawing));
                    canvas.DrawImage(image, Rect(left, top, width, height));
                    End();
                    canvas.Restore();
                }
            }
        }
    }

    private void DrawHeadings()
    {
        Stroke stroke = new(0.5f);
        Rgba8 line = new(0, 0, 0, 255);
        CellFont font = fonts.NormalFont;
        TextStyle style = fonts.TextStyle(font, Black);
        float headingWidth = (float)paginator.HeadingWidth;
        float headingHeight = (float)paginator.HeadingHeight;
        canvas.StrokeRect(new RectF(0, 0, headingWidth, headingHeight), stroke, line);
        foreach (Segment column in columns)
        {
            double x = column.Start;
            for (int c = column.First; c <= column.Last; c++)
            {
                double width = grid.Column(c);
                if (width <= 0)
                {
                    continue;
                }

                RectF rect = Rect(x, 0, width, headingHeight);
                canvas.StrokeRect(rect, stroke, line);
                Centered(CellAddress.ColumnName(c), rect, style);
                x += width;
            }
        }

        foreach (Segment row in rows)
        {
            double y = row.Start;
            for (int r = row.First; r <= row.Last; r++)
            {
                double height = grid.Row(r);
                if (height <= 0)
                {
                    continue;
                }

                RectF rect = Rect(0, y, headingWidth, height);
                canvas.StrokeRect(rect, stroke, line);
                Centered((r + 1).ToString(CultureInfo.InvariantCulture), rect, style);
                y += height;
            }
        }
    }

    private void Centered(string text, RectF rect, TextStyle style)
    {
        ParagraphStyle paragraph = new() { DefaultStyle = style, Align = TextAlign.Center, Fallback = fonts.Fallback };
        TextLayout layout = Paragraph.Create(text, paragraph).Layout(rect.Width);
        canvas.DrawTextLayout(layout, new Vector2(rect.X, rect.Y + ((rect.Height - layout.Height) / 2)));
    }
}
