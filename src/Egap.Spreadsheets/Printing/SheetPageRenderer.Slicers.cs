using Egami.Drawing;
using Egami.Fonts.Layout;
using Egami.Pixels;

namespace Egap.Spreadsheets.Printing;

/// <summary>Slicers, drawn as Excel prints them: a frame, a header with the caption, and a button for each item.</summary>
internal sealed partial class SheetPageRenderer
{
    // Measured on Excel's PDF of a slicer, in points.
    private const double SlicerInset = 6.72;
    private const double SlicerHeader = 21.72;
    private const double SlicerHeaderLineTop = 20.88;
    private const double SlicerHeaderLineLeft = 7.2;
    private const double SlicerHeaderLineRight = 6.96;
    private const double SlicerFirstButton = 25.44;
    private const double SlicerFirstButtonBare = 6.4;
    private const double SlicerRowGap = 2.52;
    private const double SlicerColumnGap = 2.16;
    private const double SlicerLine = 0.96;

    /// <summary>Excel prints a row of buttons a little taller than the slicer's row height.</summary>
    private const double SlicerRowScale = 20.4 / 20.25;

    private void DrawSlicer(Slicer slicer, double left, double top, double width, double height)
    {
        IReadOnlyDictionary<SlicerStyleElement, PivotStyleFormat> style = SlicerStyles.Get(slicer.StyleName);
        canvas.Save();
        canvas.ClipRect(Rect(left, top, width, height));
        canvas.FillRect(Rect(left, top, width, height), White);

        PivotStyleFormat whole = style[SlicerStyleElement.WholeTable];
        Box(whole, left, top, width, height);

        double y = top + SlicerFirstButtonBare;
        if (slicer.ShowHeader)
        {
            PivotStyleFormat header = style[SlicerStyleElement.Header];
            SlicerText(slicer.Caption, header with { FontColor = header.FontColor ?? whole.FontColor }, left + SlicerInset, top, width - (2 * SlicerInset), SlicerHeader);
            if (header.Bottom is { Style: not CellBorderStyle.None } line)
            {
                canvas.FillRect(Rect(left + SlicerHeaderLineLeft, top + SlicerHeaderLineTop, width - SlicerHeaderLineLeft - SlicerHeaderLineRight, SlicerLine), colors.Resolve(line.Color, Black));
            }

            y = top + SlicerFirstButton;
        }

        int columnCount = slicer.ColumnCount;
        double buttonWidth = (width - (2 * SlicerInset) - ((columnCount - 1) * SlicerColumnGap)) / columnCount;
        double buttonHeight = slicer.RowHeight / 12700.0 * SlicerRowScale;
        List<Pivots.SlicerItem> items = slicer.Cache.Items();
        for (int i = 0; i < items.Count; i++)
        {
            double x = left + SlicerInset + (i % columnCount * (buttonWidth + SlicerColumnGap));
            double by = y + (i / columnCount * (buttonHeight + SlicerRowGap));
            if (by >= top + height || buttonWidth <= 0)
            {
                break;
            }

            Pivots.SlicerItem item = items[i];
            SlicerStyleElement element = (item.Selected, item.NoData) switch
            {
                (true, false) => SlicerStyleElement.SelectedItemWithData,
                (true, true) => SlicerStyleElement.SelectedItemWithNoData,
                (false, false) => SlicerStyleElement.UnselectedItemWithData,
                _ => SlicerStyleElement.UnselectedItemWithNoData,
            };
            PivotStyleFormat format = style[element];
            if (format.Fill is { } fill)
            {
                canvas.FillRect(Rect(x, by, buttonWidth, buttonHeight), colors.Resolve(fill, White));
            }

            Box(format, x, by, buttonWidth, buttonHeight);
            SlicerText(item.Label, format, x + SlicerInset, by, buttonWidth - (2 * SlicerInset), buttonHeight);
        }

        canvas.Restore();
    }

    /// <summary>A format's borders inside a box, each a thin line.</summary>
    private void Box(PivotStyleFormat format, double x, double y, double width, double height)
    {
        void Edge(CellBorder? border, double ex, double ey, double ew, double eh)
        {
            if (border is { Style: not CellBorderStyle.None } line)
            {
                canvas.FillRect(Rect(ex, ey, ew, eh), colors.Resolve(line.Color, Black));
            }
        }

        Edge(format.Top, x, y, width, SlicerLine);
        Edge(format.Bottom, x, y + height - SlicerLine, width, SlicerLine);
        Edge(format.Left, x, y, SlicerLine, height);
        Edge(format.Right, x + width - SlicerLine, y, SlicerLine, height);
    }

    /// <summary>A line of text in the workbook's normal font, centred top to bottom and cut at the box's right edge.</summary>
    private void SlicerText(string text, PivotStyleFormat format, double x, double y, double width, double height)
    {
        if (width <= 0 || text.Length == 0)
        {
            return;
        }

        CellFont font = fonts.NormalFont with { Bold = format.Bold };
        TextStyle textStyle = fonts.TextStyle(font, format.FontColor is { } color ? colors.Resolve(color, Black) : Black);
        TextLayout layout = Layout(text.ReplaceLineEndings(" "), textStyle, 1e6, TextAlign.Left);
        canvas.Save();
        canvas.ClipRect(Rect(x, y, width, height));
        canvas.DrawTextLayout(layout, new System.Numerics.Vector2((float)x, (float)(y + ((height - layout.Height) / 2))));
        canvas.Restore();
    }
}
