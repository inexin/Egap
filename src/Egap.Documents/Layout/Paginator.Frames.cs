namespace Egap.Documents.Layout;

/// <summary>
/// Paragraph frames (Word's <c>w:framePr</c>): a run of paragraphs with the same frame is placed as one float by the
/// frame's anchors, through the same placement as floating pictures and tables, and is never split. A drop cap frame sits
/// at the start of the next paragraph, which wraps beside it, or in the margin beside it.
/// </summary>
internal sealed partial class Paginator
{
    /// <summary>The paragraphs from <paramref name="start"/> that share its frame: as many as follow with an equal one.</summary>
    private static int FrameRun(NodeCollection<Block> blocks, int start, FramePosition frame)
    {
        int count = 1;
        while (start + count < blocks.Count && blocks[start + count] is Paragraph next && Equals(next.GetEffectiveFormat().Frame, frame))
        {
            count++;
        }

        return count;
    }

    /// <summary>Whether <paramref name="paragraph"/> is laid out as a frame: it has one and sits in a section's body, outside every cell.</summary>
    private bool Framed(Paragraph paragraph, out FramePosition frame)
    {
        frame = null!;
        if (measuring > 0 || page is null || paragraph.GetEffectiveFormat().Frame is not { } f || paragraph.Parent is not Body)
        {
            return false;
        }

        frame = f;
        return true;
    }

    /// <summary>Places the paragraphs <paramref name="start"/> to <paramref name="start"/> + <paramref name="count"/> as one frame.</summary>
    private void FlowFrame(NodeCollection<Block> blocks, int start, int count, FramePosition frame)
    {
        List<Block> paragraphs = [.. Enumerable.Range(start, count).Select(i => blocks[i])];
        double columnWidth = Column.Width;
        double width = frame.Width ?? Math.Min(columnWidth, Math.Max(1, IntrinsicOf(paragraphs).Max));
        double height = MeasureBlocks(paragraphs, width);
        if (frame.Height is double fixedHeight)
        {
            height = frame.HeightRule switch
            {
                RowHeightRule.Exactly => fixedHeight,
                RowHeightRule.AtLeast => Math.Max(height, fixedHeight),
                _ => height,
            };
        }

        FloatingPlacement placement = PlacementOf(frame);
        LayoutRect bounds;
        if (frame.DropCap == DropCap.None)
        {
            bounds = FloatBounds(placement, width, height, y);
        }
        else
        {
            // A drop cap starts at the top of the next paragraph, at the column's edge or in the margin before it.
            double x = frame.DropCap == DropCap.Margin ? Column.X - width - frame.DistanceHorizontal : Column.X;
            bounds = new LayoutRect(x, y, width, height);
        }

        (List<LayoutBox> boxes, _) = LayBlocks(paragraphs, bounds.X, bounds.Y, width);
        foreach (LayoutBox box in boxes)
        {
            page!.Floats.Add(new PlacedFloat(placement, box.Bounds, box));
        }

        // The text keeps clear of the whole frame: an exclusion as tall as the frame, drawn by nothing.
        if (boxes.Count > 0 && boxes[^1].Bounds.Bottom < bounds.Bottom - 0.01)
        {
            LayoutRect rest = new(bounds.X, boxes[^1].Bounds.Bottom, bounds.Width, bounds.Bottom - boxes[^1].Bounds.Bottom);
            page!.Floats.Add(new PlacedFloat(placement, rest, new AreaBox(PageArea.Body, null, rest, [])));
        }
    }

    private static FloatingPlacement PlacementOf(FramePosition f)
    {
        return new FloatingPlacement
        {
            HorizontalAnchor = f.HorizontalAnchor,
            HorizontalAlignment = f.HorizontalAlignment,
            HorizontalOffset = f.HorizontalOffset,
            VerticalAnchor = f.VerticalAnchor,
            VerticalAlignment = f.VerticalAlignment,
            VerticalOffset = f.VerticalOffset,
            Wrap = f.DropCap == DropCap.Margin || f.Wrap == FrameWrap.None ? WrapMode.InFrontOfText
                : f.Wrap == FrameWrap.NotBeside ? WrapMode.TopAndBottom : WrapMode.Square,
            WrapSide = f.DropCap == DropCap.Drop ? WrapSide.Right : WrapSide.Largest,
            DistanceLeft = f.DistanceHorizontal,
            DistanceRight = f.DistanceHorizontal,
            DistanceTop = f.DistanceVertical,
            DistanceBottom = f.DistanceVertical,
        };
    }
}
