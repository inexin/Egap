using Egami.Fonts.Layout;

namespace Egap.Documents.Layout;

/// <summary>A floating object placed on a page, with the room it keeps clear of text.</summary>
internal sealed record PlacedFloat(FloatingObject Object, LayoutRect Bounds, ObjectBox Box)
{
    public WrapMode Wrap => Object.Placement.Wrap;

    /// <summary>The area text keeps clear of: the object with its wrap distances.</summary>
    public LayoutRect Exclusion
    {
        get
        {
            FloatingPlacement p = Object.Placement;
            return new LayoutRect(Bounds.X - p.DistanceLeft, Bounds.Y - p.DistanceTop, Bounds.Width + p.DistanceLeft + p.DistanceRight, Bounds.Height + p.DistanceTop + p.DistanceBottom);
        }
    }

    public bool Wraps => Wrap is WrapMode.Square or WrapMode.Tight or WrapMode.Through or WrapMode.TopAndBottom;
}

/// <summary>
/// Floating pictures, shapes, text boxes and islands (Phase 6 Build Plan task 19): placed by their anchor (page, margin,
/// column, paragraph, line or character, aligned or offset) and kept clear of text by their wrap mode: square, tight and
/// through take the side with more room, top and bottom moves text below, behind and in front leave text alone. Tight and
/// through wrap by the shape's bounding box, as Word does for most pictures.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>Places a paragraph's floats on the page being filled, anchored at the paragraph's top.</summary>
    private void PlaceFloats(ParagraphContent content, double paragraphTop)
    {
        if (measuring > 0)
        {
            // Measuring a cell or footer lays its text out twice; its floats are placed when it is laid out for real.
            return;
        }

        foreach (FloatingObject placed in content.Floats)
        {
            FloatingObject floating = FitToText(placed);
            LayoutRect bounds = FloatBounds(floating, paragraphTop);
            ObjectBox box = new(floating.Node, bounds, floating: true, ObjectContent(floating.Node, bounds));
            page!.Floats.Add(new PlacedFloat(floating, bounds, box));
        }
    }

    private LayoutRect FloatBounds(FloatingObject floating, double paragraphTop)
    {
        Geometry geometry = page!.Geometry;
        FloatingPlacement p = floating.Placement;
        double width = floating.Width;
        double height = floating.Height;
        (double left, double span) = p.HorizontalAnchor switch
        {
            HorizontalAnchor.Page => (0, geometry.PageWidth),
            HorizontalAnchor.Margin => (geometry.BodyLeft, geometry.BodyRight - geometry.BodyLeft),
            HorizontalAnchor.LeftMargin => (0, geometry.BodyLeft),
            HorizontalAnchor.RightMargin => (geometry.BodyRight, geometry.PageWidth - geometry.BodyRight),
            HorizontalAnchor.InsideMargin => page.Number % 2 == 0 ? (geometry.BodyRight, geometry.PageWidth - geometry.BodyRight) : (0, geometry.BodyLeft),
            HorizontalAnchor.OutsideMargin => page.Number % 2 == 0 ? (0, geometry.BodyLeft) : (geometry.BodyRight, geometry.PageWidth - geometry.BodyRight),
            _ => (Column.X, Column.Width),
        };
        double x = p.HorizontalAlignment switch
        {
            FloatingAlignment.Start or FloatingAlignment.Inside => left,
            FloatingAlignment.Center => left + ((span - width) / 2),
            FloatingAlignment.End or FloatingAlignment.Outside => left + span - width,
            _ => left + p.HorizontalOffset,
        };
        (double top, double extent) = p.VerticalAnchor switch
        {
            VerticalAnchor.Page => (0, geometry.PageHeight),
            VerticalAnchor.Margin => (geometry.BodyTop, geometry.BodyBottom - geometry.BodyTop),
            VerticalAnchor.TopMargin => (0, geometry.BodyTop),
            VerticalAnchor.BottomMargin => (geometry.BodyBottom, geometry.PageHeight - geometry.BodyBottom),
            VerticalAnchor.InsideMargin or VerticalAnchor.OutsideMargin => (0, geometry.BodyTop),
            _ => (paragraphTop, 0),
        };
        double y = p.VerticalAlignment switch
        {
            FloatingAlignment.Start or FloatingAlignment.Inside => top,
            FloatingAlignment.Center => top + ((extent - height) / 2),
            FloatingAlignment.End or FloatingAlignment.Outside => top + extent - height,
            _ => top + p.VerticalOffset,
        };

        // Word keeps an object anchored to text on its page.
        if (p.VerticalAnchor is VerticalAnchor.Paragraph or VerticalAnchor.Line)
        {
            y = Math.Clamp(y, 0, Math.Max(0, geometry.PageHeight - height));
        }

        return new LayoutRect(x, y, width, height);
    }

    /// <summary>A text box's laid-out text, inside the shape's insets; none for other objects.</summary>
    private List<LayoutBox>? ObjectContent(Inline node, LayoutRect bounds)
    {
        if (node is not Shape { TextBox: { } box } shape)
        {
            return null;
        }

        (double left, double top, double right, _) = Insets(shape);
        (List<LayoutBox> blocks, _) = LayStory(box, bounds.X + left, bounds.Y + top, Math.Max(1, bounds.Width - left - right));
        return blocks;
    }

    /// <summary>A text box's insets: its own, or Word's default of a tenth of an inch at the sides and a twentieth at top and bottom.</summary>
    private static (double Left, double Top, double Right, double Bottom) Insets(Shape shape)
    {
        return shape.TextInsets ?? (7.2, 3.6, 7.2, 3.6);
    }

    /// <summary>
    /// A text box that fits its text: as tall as its text needs, never less than its height, and, with no width of its own, as
    /// wide as its widest line, up to the width of the body.
    /// </summary>
    private FloatingObject FitToText(FloatingObject floating)
    {
        if (floating.Node is not Shape { FitToText: true, TextBox: { } story } shape)
        {
            return floating;
        }

        (double left, double top, double right, double bottom) = Insets(shape);
        double width = floating.Width;
        if (width <= 0)
        {
            double room = page!.Geometry.BodyRight - page.Geometry.BodyLeft - Math.Max(0, floating.Placement.HorizontalOffset);
            width = Math.Min(ContentWidths(story).Max + left + right, Math.Max(left + right + 1, room));
        }

        double height = Math.Max(floating.Height, MeasureBlocks(story.Blocks, Math.Max(1, width - left - right)) + top + bottom);
        return floating with { Width = width, Height = height };
    }

    /// <summary>The floats on this page that push text of the current column aside.</summary>
    private List<PlacedFloat> WrappingFloats()
    {
        ColumnGeometry geometry = Column;
        return [.. page!.Floats.Where(f => f.Wraps && f.Exclusion.Right > geometry.X && f.Exclusion.X < geometry.X + geometry.Width)];
    }

    /// <summary>
    /// Where a line of <paramref name="height"/> at <paramref name="top"/> can go between the floats: its left edge and width
    /// within the column, or, when a top-and-bottom float or too narrow a gap is in the way, the y to move down to.
    /// </summary>
    private static (double X, double Width, double? MoveTo) Available(List<PlacedFloat> floats, double top, double height, double left, double width, double minimum)
    {
        double from = left;
        double to = left + width;
        double? moveTo = null;
        foreach (PlacedFloat f in floats)
        {
            LayoutRect r = f.Exclusion;
            if (r.Bottom <= top || r.Y >= top + height)
            {
                continue;
            }

            if (f.Wrap == WrapMode.TopAndBottom)
            {
                moveTo = Math.Max(moveTo ?? 0, r.Bottom);
                continue;
            }

            // Square, tight and through: the text takes the larger side (or the side the wrap asks for).
            double leftRoom = r.X - from;
            double rightRoom = to - r.Right;
            bool takeLeft = f.Object.Placement.WrapSide switch
            {
                WrapSide.Left => true,
                WrapSide.Right => false,
                _ => leftRoom >= rightRoom,
            };
            if (takeLeft)
            {
                to = Math.Min(to, r.X);
            }
            else
            {
                from = Math.Max(from, r.Right);
            }

            if (to - from < minimum)
            {
                moveTo = Math.Max(moveTo ?? 0, r.Bottom);
            }
        }

        return (from, Math.Max(0, to - from), moveTo);
    }

    /// <summary>
    /// Lays a paragraph's lines one at a time around the floats on the page, each line at the width left beside them, going
    /// on in the next column when the column is full.
    /// </summary>
    private void FlowAroundFloats(ParagraphContent content, double before, double after)
    {
        ResolvedParagraphFormat format = content.Format;
        LineFlow flow = content.Text.CreateFlow();
        double top = y + before;
        bool continued = false;
        List<PlacedLine> part = [];
        double partTop = top;
        double guess = content.MaxFontSize * 1.2;
        while (!flow.IsComplete)
        {
            List<PlacedFloat> floats = WrappingFloats();
            bool firstLine = flow.Position == 0;
            double indent = firstLine ? format.LeftIndent + format.FirstLineIndent : format.LeftIndent;
            double left = Column.X + indent;
            double width = Math.Max(1, Column.Width - indent - format.RightIndent);
            (double x, double room, double? moveTo) = Available(floats, top, guess, left, width, Math.Min(width, 36));
            if (moveTo is double below && below > top)
            {
                // The lines so far stay above the float; the paragraph goes on below it in a new part.
                bool wasContinued = continued;
                Flush();
                continued = wasContinued || part.Count > 0;
                top = below;
                partTop = top;
                if (top >= ColumnBottom)
                {
                    Flush();
                    NextColumn();
                    top = y;
                    partTop = top;
                }

                continue;
            }

            int start = flow.Position;
            TextLine? line = flow.NextLine((float)Math.Max(1, room));
            if (line is null)
            {
                break;
            }

            (double height, double ascent) = LineHeight(line, format);
            guess = height;
            if (top + height > ColumnBottom + 0.01 && (part.Count > 0 || !AtColumnTop))
            {
                flow.Rewind(start);
                Flush();
                NextColumn();
                top = y;
                partTop = top;
                continued = true;
                continue;
            }

            part.Add(new PlacedLine(line, x - Column.X, height, ascent, room));
            top += height;
            if (content.Breaks.Any(b => b.Index == line.End) && !flow.IsComplete)
            {
                BreakKind kind = content.Breaks.First(b => b.Index == line.End).Kind;
                Flush();
                if (kind == BreakKind.Column)
                {
                    NextColumn();
                }
                else
                {
                    NewPage();
                }

                top = y;
                partTop = top;
                continued = true;
            }
        }

        Flush();
        y = top + after;

        void Flush()
        {
            if (part.Count == 0)
            {
                return;
            }

            double height = PlaceParagraphPart(content, part, 0, part.Count, partTop, continued, !flow.IsComplete);
            top = partTop + height;
            part.Clear();
            continued = true;
        }
    }

    /// <summary>The boxes of the inline objects in the lines placed, from their placeholders' places in the line.</summary>
    private List<LayoutBox> InlineObjectBoxes(ParagraphContent content, List<LineBox> lines)
    {
        List<LayoutBox> boxes = [];
        foreach (LineBox line in lines)
        {
            foreach (LayoutRun run in line.Line.Runs)
            {
                if (run.Kind != LayoutRunKind.Placeholder || run.Placeholder < 0 || run.Placeholder >= content.InlineObjects.Count)
                {
                    continue;
                }

                Inline node = content.InlineObjects[run.Placeholder];
                double height = content.Text.RichText.Placeholders[run.Placeholder].Height;
                LayoutRect bounds = new(line.Bounds.X + run.X, line.Baseline - height, run.Advance, height);
                boxes.Add(new ObjectBox(node, bounds, floating: false, ObjectContent(node, bounds)));
            }
        }

        return boxes;
    }
}
