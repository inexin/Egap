using Egami.Drawing;

using Egap.Documents.Charts;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Printing;

/// <summary>Draws a sheet's charts on a page.</summary>
internal static class ChartPrinting
{
    /// <summary>
    /// Draws <paramref name="chart"/> into <paramref name="rect"/>. A chart with references and no data yet, as one made in code is
    /// until <see cref="SheetDrawing.RefreshChart"/>, draws from a copy filled from the cells; the chart itself is not touched.
    /// </summary>
    public static void Draw(Chart chart, Workbook book, Canvas canvas, RectF rect, SheetColors colors, SheetFonts fonts)
    {
        Chart shown = chart;
        if (chart.Series.Any(s => s.Values.Count == 0 && s.Values.Reference is not null))
        {
            shown = chart.Clone();
            ChartCells.Fill(shown, book);
        }

        shown.Draw(canvas, rect, new ChartDrawOptions { Theme = colors.ChartTheme, Provider = fonts.Provider });
    }
}
