# Charts

`Egap.Documents.Charts` is one model for the charts in workbooks and Word documents: `c:` parts (column, bar, line, area,
pie, doughnut, scatter, bubble, radar, stock, surface and their 3D forms) and `cx:` parts (waterfall, histogram, Pareto,
box and whisker, treemap, sunburst, funnel and region map). It reads, edits, creates and saves them. Egap does not draw
charts yet.

The model is a view of the part's XML, so what it has no property for stays in the file, and a chart nobody changed is
saved with the bytes it came with. Excel checks: the tests compare the model with what Excel reports for every type, and
Excel opens the charts made and edited here without a repair.

## Reading

`SheetDrawing.Chart` is the chart of an object on a sheet, and `Workbook.ChartSheets` lists the sheets that hold only a chart.

```csharp
using Egap.Documents.Charts;
using Egap.Spreadsheets;

Workbook book = Workbook.Load("sales.xlsx");
foreach (SheetDrawing drawing in book.Worksheets[0].Drawings)
{
    if (drawing.Chart is { } chart)
    {
        Console.WriteLine($"{drawing.Name}: {chart.Type}, {chart.Series.Count} series");
        foreach (ChartSeries series in chart.Series)
        {
            Console.WriteLine($"  {series.Name}: {series.Values.Reference}");
        }
    }
}

foreach (ChartSheet page in book.ChartSheets)
{
    Console.WriteLine($"{page.Name}: {page.Chart?.Type}");
}
```

A chart has plots, and each plot has series and axes: a combination chart has more than one plot. `chart.Series` lists all
the series in their order. A property a file does not set reads as null, which leaves the choice to whoever draws the chart.

A series keeps a reference to its cells and the values saved with it. Excel saves no values in a `cx:` chart, and points its
references at hidden defined names; `RefreshChart` follows them and reads the cells.

```csharp
using Egap.Documents.Charts;
using Egap.Spreadsheets;

Workbook book = Workbook.Load("sales.xlsx");
SheetDrawing drawing = book.Worksheets[0].Drawings[0];
drawing.RefreshChart();
IReadOnlyList<double?> values = drawing.Chart!.Series[0].Values.Numbers;
```

Refreshing follows Excel: a chart skips hidden rows and columns unless `PlotVisibleOnly` is false. Saving does it for any
chart that changed.

## Editing

```csharp
using Egap.Documents.Charts;
using Egap.Spreadsheets;

Workbook book = Workbook.Load("sales.xlsx");
Chart chart = book.Worksheets[0].Drawings[0].Chart!;

chart.Title.Text = "Sales by month";
chart.Legend.Position = ChartLegendPosition.Bottom;

ChartAxis value = chart.Axes.First(a => a.Kind == ChartAxisKind.Value);
value.Minimum = 0;
value.MajorUnit = 10;
value.Title.Text = "Units";

ChartSeries first = chart.Series[0];
first.Format.FillColor = ChartColor.FromHex("2F5597");
first.DataLabels.ShowValue = true;

chart.Plots[0].Series.Add("Sheet1!$D$2:$D$7", "Sheet1!$A$2:$A$7", "Sheet1!$D$1");
book.Save("edited.xlsx");
```

Labels in levels, taken from more than one column, must keep their reference, because a chart part has no way to write
levels without one. Setting `Reference` to null on them throws; call `SetText` first to write one level. A series's values,
bubble sizes and error amounts must be numbers, so `SetText` throws on them.

A property only one kind of part has reads as null on the other, and setting it there does nothing: a `cx:` chart has no
markers, plot groupings or manual layouts, and a `c:` chart has no bins or subtotals.

## Making a chart

`AddChart` reads a range: a first row of text gives the names, leading text columns the categories and each other column a
series. A scatter chart takes its X values from the first column, and a bubble chart reads X, then Y and size for each series.

```csharp
using Egap.Documents.Charts;
using Egap.Spreadsheets;

Workbook book = Workbook.Create();
Worksheet sheet = book.Worksheets[0];
sheet["A1"].Value = "Month";
sheet["B1"].Value = "North";
sheet["A2"].Value = "Jan";
sheet["B2"].Value = 12;
sheet["A3"].Value = "Feb";
sheet["B3"].Value = 18;

SheetDrawing drawing = sheet.Drawings.AddChart(ChartType.Column, RangeAddress.Parse("D1:J15"), RangeAddress.Parse("A1:B3"));
drawing.Name = "Sales";
drawing.Chart!.Title.Text = "Sales";

// Add a line on an axis of its own to the same chart.
ChartPlot line = drawing.Chart.AddPlot(ChartType.Line, secondaryAxis: true);
line.Series.Add("Sheet1!$B$2:$B$3", "Sheet1!$A$2:$A$3", "Sheet1!$B$1");
book.Save("chart.xlsx");
```

## Drawing

`Chart.Draw` draws a chart on any Egami canvas, with the layout Excel gives it: the title, the legend, the plot, the axes' steps
and the labels. `ChartDrawOptions.Host` picks whose defaults to follow where the file leaves something out: `ChartHost.Excel`,
the default, or `ChartHost.Word`, which a Word document's pages use. They differ in corners, line and marker sizes, the size of a
title and the spacing of a legend. Column, bar, line, area, pie, doughnut, scatter, bubble, radar, stock and combined charts draw this way, and so do waterfall,
histogram, Pareto, box and whisker, treemap, sunburst and funnel charts, 3D charts with the view their part sets (right-angle
or turned, with perspective), surfaces, and a pie of pie or bar of pie. A region map draws nothing, because its shapes come from a
map service: a Word document shows the picture saved with it.

```csharp
using Egami;
using Egami.Drawing;
using Egami.Pixels;
using Egap.Documents.Charts;
using Egap.Spreadsheets;

Workbook book = Workbook.Load("sales.xlsx");
Chart chart = book.Worksheets[0].Drawings[0].Chart!;

using Raster<Rgba8> picture = Raster.Create(720, 432, new Rgba8(255, 255, 255, 255));
using RasterCanvas<Rgba8> canvas = new(picture);
chart.Draw(canvas, new RectF(0, 0, 720, 432), new ChartDrawOptions { Culture = book.Culture });
picture.Save("chart.png");
```

A Word document's chart draws in the document's pages and PDFs, and a sheet's in its printed pages, PDFs and images. A chart
made in code and not refreshed yet prints from its cells. `Workbook.Paginate` and `SaveAsPdf` also print each visible chart
sheet, after the sheets, on a page of its own (`SheetPrintLayout.ChartPages`), which a single sheet's print leaves out.

Text that has no room is cut as Office cuts it: a title or a radar's label in a very small chart ends in an ellipsis, and a
legend that wraps leaves out the entries past half the chart's height.

Not all of Excel's look comes through: bevels and shadows from a theme's effects are left out. `ChartDrawOptions.Culture`
decides how dates, percents and list separators read; Office uses the machine's locale, so pass the reader's culture to match it.

## What each type holds

Setting something a chart type has no place for throws `InvalidOperationException`, because the file would be invalid and Excel
refuses some such files outright. Setting null always works. A value read from a file stays as it was.

| Property | Plots that have it |
| --- | --- |
| `Grouping` | Column, bar, line, area; line and area are not clustered |
| `VaryColors` | All but stock and surface |
| `GapWidth` | Column, bar, pie of pie |
| `Overlap` | 2D column and bar |
| `GapDepth` | 3D column, bar, line and area |
| `BarShape` | 3D column and bar |
| `FirstSliceAngle` | 2D pie, doughnut |
| `HoleSize` | Doughnut |
| `ScatterStyle` | Scatter |
| `RadarStyle` | Radar |
| `BubbleScale`, `ShowNegativeBubbles`, `BubbleSizeIsWidth` | Bubble |
| `Wireframe` | Surface |
| `ShowMarkers` | 2D line |
| `ChartSeries.Smooth` | Line, scatter, stock |
| `ChartSeries.InvertIfNegative` | Column, bar, bubble |
| `ChartSeries.Explosion` | Pie, pie of pie, doughnut |
| `ChartSeries.Bubble3D` | Bubble |

`ChartDataLabels.Position` depends on the type as well:

| Chart | Positions |
| --- | --- |
| Clustered 2D column and bar | `Center`, `InsideEnd`, `InsideBase`, `OutsideEnd` |
| Stacked 2D column and bar | `Center`, `InsideEnd`, `InsideBase` |
| 2D line, scatter, bubble, stock | `Top`, `Bottom`, `Left`, `Right`, `Center` |
| Pie, 3D pie, pie of pie | `BestFit`, `Center`, `InsideEnd`, `OutsideEnd` |
| Radar, area, doughnut, surface, other 3D | None |

Changing `Grouping` takes away positions the new grouping does not allow.

## Word documents

A chart in a Word document is an `Island` of kind `IslandKind.Chart`, with the same model in `island.Chart`. A chart of the
`cx:` types comes in as the picture Word saves with it, and cannot be added yet. A chart made here has its values in the part
and no embedded workbook, so Word shows it but offers no data to edit.

```csharp
using Egap.Documents;
using Egap.Documents.Charts;
using Egap.Documents.Docx;

Document document = DocxFormat.Load("report.docx");
foreach (Island island in document.Descendants<Island>())
{
    if (island.Chart is { } read)
    {
        read.Title.Text = "Revised";
    }
}

Chart chart = Chart.Create(ChartType.Pie);
chart.Plots[0].Series.Add("Share", ["North", "South"], [60, 40]);
document.Sections[0].Body.AddParagraph("Share by region: ").Inlines.Add(new Island(document, chart, 360, 216));
DocxFormat.Save(document, "revised.docx");
```

## On its own

`Chart.Load` and `Chart.Save` read and write the part itself, the XML of a `c:chartSpace` or `cx:chartSpace`. `Chart.Create`
makes one of any type, and `chart.Clone()` copies one. Corrupt input gives a `DocumentException`.
