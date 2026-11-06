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
levels without one. Setting `Reference` to null on them throws; call `SetText` first to write one level.

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
