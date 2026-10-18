using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Comparing;

/// <summary>
/// The visual pass of compare (Phase 7 design Q12, Build Plan task 15): each pair of matching pages rendered at the same
/// resolution, pixels whose channels differ by more than the tolerance counted in cells of 8 by 8, cells with enough of them
/// joined with their neighbours into boxes, and the boxes mapped back to the new page's user space. Pages of different
/// sizes are one change the size of the page.
/// </summary>
internal static class VisualCompare
{
    private const int Cell = 8;

    public static List<PdfVisualChange> Run(PdfDocument oldDocument, PdfDocument newDocument, IReadOnlyList<PdfPageMatch> pages, PdfCompareOptions options,
        CancellationToken cancellationToken = default)
    {
        List<PdfVisualChange> changes = [];
        try
        {
            Collect(oldDocument, newDocument, pages, options, changes, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The differences found so far hold rasters: nobody else will dispose them.
            foreach (PdfVisualChange change in changes)
            {
                change.Dispose();
            }

            throw;
        }

        return changes;
    }

    private static void Collect(PdfDocument oldDocument, PdfDocument newDocument, IReadOnlyList<PdfPageMatch> pages, PdfCompareOptions options,
        List<PdfVisualChange> changes, CancellationToken cancellationToken)
    {
        PdfRenderOptions render = new() { Dpi = options.Dpi };
        foreach (PdfPageMatch match in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (match is not { OldPage: int o, NewPage: int n })
            {
                continue;
            }

            PdfPage newPage = newDocument.Pages[n - 1];
            Raster<Rgba8>? a = null, b = null;
            try
            {
                a = oldDocument.Pages[o - 1].Render(render, cancellationToken);
                b = newPage.Render(render, cancellationToken);
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                a?.Dispose();
                b?.Dispose();
                continue;
            }
            catch
            {
                a?.Dispose();
                b?.Dispose();
                throw;
            }

            using (a)
            using (b)
            {
                (List<PdfRectangle> boxes, Raster<Rgba8>? difference) = Compare(a, b, newPage, options);
                if (boxes.Count > 0)
                {
                    changes.Add(new PdfVisualChange(o, n, boxes, difference));
                }
            }
        }

    }

    /// <summary>The changed areas of two renders of a page, and on request the difference raster.</summary>
    internal static (List<PdfRectangle> Boxes, Raster<Rgba8>? Difference) Compare(Raster<Rgba8> a, Raster<Rgba8> b, PdfPage page, PdfCompareOptions options)
    {
        double scale = 72 / options.Dpi;
        (PdfMatrix shown, double width, double height) = PdfPage.Displayed(page.CropBox.Normalize(), page.Rotate, page.UserUnit);
        PdfMatrix back = shown.TryInvert(out PdfMatrix inverse) ? inverse : PdfMatrix.Identity;
        PdfRectangle ToUser(double x0, double y0, double x1, double y1)
        {
            // Pixels are y down from the shown page's top left.
            (double ax, double ay) = Apply(back, x0 * scale, height - (y0 * scale));
            (double bx, double by) = Apply(back, x1 * scale, height - (y1 * scale));
            return new PdfRectangle(Math.Min(ax, bx), Math.Min(ay, by), Math.Max(ax, bx), Math.Max(ay, by));
        }

        if (a.Width != b.Width || a.Height != b.Height)
        {
            return ([ToUser(0, 0, b.Width, b.Height)], null);
        }

        int columns = (b.Width + Cell - 1) / Cell, rows = (b.Height + Cell - 1) / Cell;
        int[] counts = new int[columns * rows];
        bool[]? changed = options.DifferenceImages ? new bool[b.Width * b.Height] : null;
        int tolerance = options.Tolerance;
        for (int y = 0; y < b.Height; y++)
        {
            Span<Rgba8> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < b.Width; x++)
            {
                Rgba8 p = ra[x], q = rb[x];
                if (Math.Abs(p.R - q.R) > tolerance || Math.Abs(p.G - q.G) > tolerance || Math.Abs(p.B - q.B) > tolerance || Math.Abs(p.A - q.A) > tolerance)
                {
                    counts[((y / Cell) * columns) + (x / Cell)]++;
                    if (changed is not null)
                    {
                        changed[(y * b.Width) + x] = true;
                    }
                }
            }
        }

        // Cells with enough changed pixels, joined with the cells around them (diagonals too) into boxes.
        bool[] hot = [.. counts.Select(c => c >= options.MinChangedPixels)];
        int[] label = new int[hot.Length];
        List<PdfRectangle> boxes = [];
        Stack<int> pending = new();
        for (int start = 0; start < hot.Length; start++)
        {
            if (!hot[start] || label[start] != 0)
            {
                continue;
            }

            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            label[start] = boxes.Count + 1;
            pending.Push(start);
            while (pending.TryPop(out int cell))
            {
                int cx = cell % columns, cy = cell / columns;
                (x0, y0, x1, y1) = (Math.Min(x0, cx), Math.Min(y0, cy), Math.Max(x1, cx), Math.Max(y1, cy));
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= columns || ny >= rows)
                        {
                            continue;
                        }

                        int next = (ny * columns) + nx;
                        if (hot[next] && label[next] == 0)
                        {
                            label[next] = boxes.Count + 1;
                            pending.Push(next);
                        }
                    }
                }
            }

            boxes.Add(ToUser(x0 * Cell, y0 * Cell, Math.Min(b.Width, (x1 + 1) * Cell), Math.Min(b.Height, (y1 + 1) * Cell)));
        }

        if (changed is null || boxes.Count == 0)
        {
            return (boxes, null);
        }

        Raster<Rgba8> difference = Raster.Create<Rgba8>(b.Width, b.Height);
        for (int y = 0; y < b.Height; y++)
        {
            Span<Rgba8> source = b.Row(y), target = difference.Row(y);
            for (int x = 0; x < b.Width; x++)
            {
                Rgba8 q = source[x];
                target[x] = changed[(y * b.Width) + x] ? new Rgba8(230, 0, 0, 255) : new Rgba8((byte)(178 + (q.R * 3 / 10)), (byte)(178 + (q.G * 3 / 10)), (byte)(178 + (q.B * 3 / 10)), 255);
            }
        }

        return (boxes, difference);
    }

    private static (double X, double Y) Apply(PdfMatrix m, double x, double y) => ((m.A * x) + (m.C * y) + m.E, (m.B * x) + (m.D * y) + m.F);
}
