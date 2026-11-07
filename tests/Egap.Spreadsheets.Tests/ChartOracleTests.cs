using System.Globalization;

using Egap.Documents.Charts;

namespace Egap.Spreadsheets.Tests;

/// <summary>
/// The chart model against Excel. <c>Oracle/charts.xlsx</c> is a book Excel made, and <c>charts.excel.tsv</c> what Excel says
/// about each of its charts (<c>tools/excel-chart-oracle.ps1</c>). <c>charts-made.xlsx</c> and <c>charts-edited.xlsx</c> are
/// what <see cref="MadeBook"/> and <see cref="EditedBook"/> save, and their facts what Excel says after opening them without
/// a repair. To make them again, set <c>EGAP_CHART_ORACLE_OUT</c> to a folder, run these tests and run the script's
/// <c>-Read</c> on each.
/// </summary>
public sealed class ChartOracleTests
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Oracle");

    [Fact]
    public void ChartsExcelMadeReadAsExcelSaysTheyAre() => Compare(Workbook.Load(Path.Combine(Folder, "charts.xlsx")), "charts.excel.tsv");

    [Fact]
    public void ChartsMadeHereAreWhatExcelFindsInTheFile()
    {
        Workbook book = MadeBook();
        Written(book, "charts-made.xlsx");
        Compare(Reloaded(book), "charts-made.excel.tsv");
    }

    [Fact]
    public void ChartsEditedHereAreWhatExcelFindsInTheFile()
    {
        Workbook book = EditedBook();
        Written(book, "charts-edited.xlsx");
        Compare(Reloaded(book), "charts-edited.excel.tsv");
    }

    /// <summary>A chart of every type, made through the model, reading the same cells.</summary>
    internal static Workbook MadeBook()
    {
        Workbook book = Workbook.Create();
        Worksheet sheet = book.Worksheets[0];
        sheet.Name = "My data";
        string[] months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun"];
        double[][] columns = [[12, 18, 9, 22, 16, 25], [7, 9, 14, 11, 13, 8], [15, 21, 17, 26, 19, 28]];
        sheet["A1"].Value = "Month";
        sheet["B1"].Value = "North";
        sheet["C1"].Value = "South";
        sheet["D1"].Value = "East";
        for (int row = 0; row < months.Length; row++)
        {
            sheet[row + 1, 0].Value = months[row];
            for (int column = 0; column < 3; column++)
            {
                sheet[row + 1, column + 1].Value = columns[column][row];
            }
        }

        RangeAddress data = RangeAddress.Parse("A1:D7");
        RangeAddress one = RangeAddress.Parse("A1:B7");
        int place = 0;
        void Add(string name, ChartType type, RangeAddress cells, bool threeD = false, Action<Chart>? dress = null)
        {
            RangeAddress at = new(10 + (place / 6 * 16), 6 + (place % 6 * 8), 24 + (place / 6 * 16), 12 + (place % 6 * 8));
            place++;
            SheetDrawing drawing = sheet.Drawings.AddChart(type, at, cells, threeD);
            drawing.Name = name;
            dress?.Invoke(drawing.Chart!);
        }

        Add("Column", ChartType.Column, data);
        Add("ColumnStacked", ChartType.Column, data, dress: c => c.Plots[0].Grouping = ChartGrouping.Stacked);
        Add("Column3D", ChartType.Column, data, true);
        Add("Bar", ChartType.Bar, data);
        Add("Bar3D", ChartType.Bar, data, true);
        Add("Line", ChartType.Line, data);
        Add("Line3D", ChartType.Line, data, true);
        Add("Area", ChartType.Area, data);
        Add("Area3D", ChartType.Area, data, true);
        Add("Pie", ChartType.Pie, one);
        Add("Pie3D", ChartType.Pie, one, true);
        Add("PieOfPie", ChartType.PieOfPie, one);
        Add("BarOfPie", ChartType.BarOfPie, one);
        Add("Doughnut", ChartType.Doughnut, one);
        Add("Scatter", ChartType.Scatter, RangeAddress.Parse("B1:D7"));
        Add("Bubble", ChartType.Bubble, RangeAddress.Parse("B1:D7"));
        Add("Radar", ChartType.Radar, data);
        Add("Stock", ChartType.Stock, data);
        Add("Surface", ChartType.Surface, data);
        Add("Surface3D", ChartType.Surface, data, true);
        Add("Waterfall", ChartType.Waterfall, one);
        Add("Histogram", ChartType.Histogram, one);
        Add("Pareto", ChartType.Pareto, one);
        Add("BoxWhisker", ChartType.BoxWhisker, one);
        Add("Treemap", ChartType.Treemap, one);
        Add("Sunburst", ChartType.Sunburst, one);
        Add("Funnel", ChartType.Funnel, one);
        Add("Dressed", ChartType.Column, data, dress: chart =>
        {
            chart.Title.Text = "Sales";
            chart.HasLegend = true;
            chart.Legend.Position = ChartLegendPosition.Top;
            chart.DisplayBlanksAs = ChartBlanks.Zero;
            ChartAxis category = chart.Axes[0];
            category.Title.Text = "Month";
            category.Reversed = true;
            ChartAxis value = chart.Axes[1];
            value.Title.Text = "Units";
            value.Minimum = 0;
            value.Maximum = 40;
            value.MajorUnit = 5;
            value.HasMajorGridlines = true;
            value.HasMinorGridlines = true;
            value.NumberFormat = "0.0";
            chart.Series[0].HasDataLabels = true;
            chart.Series[0].Format.FillColor = ChartColor.FromHex("C81E1E");
        });
        Add("Combo", ChartType.Column, RangeAddress.Parse("A1:C7"), dress: chart =>
        {
            ChartPlot line = chart.AddPlot(ChartType.Line, true);
            line.Series.Add("'My data'!$D$2:$D$7", "'My data'!$A$2:$A$7", "'My data'!$D$1");
        });
        Add("LogAxis", ChartType.Column, one, dress: chart => chart.Axes[1].LogBase = 10);
        Add("DoughnutHole", ChartType.Doughnut, one, dress: chart => chart.Plots[0].HoleSize = 70);
        return book;
    }

    /// <summary>The book Excel made, with its charts changed through the model.</summary>
    internal static Workbook EditedBook()
    {
        Workbook book = Workbook.Load(Path.Combine(Folder, "charts.xlsx"));
        Chart Find(string name) => book.Worksheets["Charts"].Drawings.First(d => d.Name == name).Chart!;

        Chart dressed = Find("Dressed");
        dressed.Title.Text = "Edited title";
        dressed.Axes[0].Reversed = false;
        dressed.Axes[1].Maximum = 50;
        dressed.Series[0].HasDataLabels = false;
        dressed.Series[1].HasDataLabels = true;
        dressed.Legend.Position = ChartLegendPosition.Left;

        Chart line = Find("Line");
        line.Plots[0].Series.Remove(line.Series[2]);

        Chart column = Find("ColumnClustered");
        column.Plots[0].Series.Add("Data!$D$2:$D$7", "Data!$A$2:$A$7", "Data!$D$1");
        column.Plots[0].Grouping = ChartGrouping.Stacked;
        column.Plots[0].Overlap = 100;

        Chart scatter = Find("Scatter");
        scatter.Axes[0].Minimum = 1;
        scatter.Axes[1].LogBase = 10;

        Chart pie = Find("Pie");
        pie.HasLegend = false;
        pie.Plots[0].FirstSliceAngle = 90;

        book.Worksheets["Data"].Drawings.First(d => d.Name == "Waterfall").Chart!.Title.Text = "Steps";
        book.Worksheets["Data"].Drawings.First(d => d.Name == "Pareto").Chart!.HasLegend = false;
        book.ChartSheets[0].Chart!.Title.Text = "On its own page";
        return book;
    }

    private static void Written(Workbook book, string name)
    {
        if (Environment.GetEnvironmentVariable("EGAP_CHART_ORACLE_OUT") is { Length: > 0 } folder)
        {
            book.Save(Path.Combine(folder, name));
        }
    }

    private static Workbook Reloaded(Workbook book)
    {
        using MemoryStream stream = new();
        book.Save(stream);
        stream.Position = 0;
        return Workbook.Load(stream);
    }

    private static void Compare(Workbook book, string factsFile)
    {
        Dictionary<string, Dictionary<string, string?>> ours = [];
        foreach (Worksheet sheet in book.Worksheets)
        {
            foreach (SheetDrawing drawing in sheet.Drawings)
            {
                if (drawing.Kind == SheetDrawingKind.Chart)
                {
                    Assert.NotNull(drawing.Chart);
                    ours[sheet.Name + "!" + drawing.Name] = Facts(drawing.Chart);
                }
            }
        }

        foreach (ChartSheet page in book.ChartSheets)
        {
            Assert.NotNull(page.Chart);
            ours[page.Name] = Facts(page.Chart);
        }

        List<string> failures = [];
        HashSet<string> seen = [];
        foreach (string line in File.ReadAllLines(Path.Combine(Folder, factsFile)))
        {
            string[] fields = line.Split('\t');
            if (fields.Length < 3 || fields[2].Length == 0)
            {
                continue;
            }

            seen.Add(fields[0]);
            if (!ours.TryGetValue(fields[0], out Dictionary<string, string?>? facts))
            {
                failures.Add($"{fields[0]}: not found");
            }
            else if (facts.TryGetValue(fields[1], out string? value) && value is not null && !Same(fields[1], fields[2], value))
            {
                failures.Add($"{fields[0]} {fields[1]}: Excel {fields[2]}, Egap {value}");
            }
        }

        failures.AddRange(ours.Keys.Where(k => !seen.Contains(k)).Select(k => $"{k}: Excel has no such chart"));
        Assert.True(failures.Count == 0, failures.Count + " differences\n" + string.Join('\n', failures.Take(200)));
    }

    private static bool Same(string key, string excel, string ours)
    {
        if (string.Equals(excel, ours, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] a = excel.Split(';');
        string[] b = ours.Split(';');
        return a.Length == b.Length && key is not "series" && a.Zip(b).All(p => p.First == p.Second
            || (double.TryParse(p.First, CultureInfo.InvariantCulture, out double x) && double.TryParse(p.Second, CultureInfo.InvariantCulture, out double y) && Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Abs(x))));
    }

    private static Dictionary<string, string?> Facts(Chart chart)
    {
        Dictionary<string, string?> facts = new(StringComparer.Ordinal)
        {
            ["hasTitle"] = chart.HasTitle.ToString(),
            ["hasLegend"] = chart.HasLegend.ToString(),
            // A Pareto chart's line is a series in the file but none to Excel: it comes from the bars.
            ["seriesCount"] = (chart.Series.Count - (chart.Type == ChartType.Pareto ? 1 : 0)).ToString(CultureInfo.InvariantCulture),
        };
        if (chart.HasTitle && chart.Title.Text is { } title)
        {
            facts["title"] = title;
        }

        if (chart.HasLegend && chart.Legend.Position is { } position)
        {
            facts["legendPosition"] = position switch
            {
                ChartLegendPosition.Bottom => "-4107",
                ChartLegendPosition.Left => "-4131",
                ChartLegendPosition.Top => "-4160",
                ChartLegendPosition.Right => "-4152",
                _ => "2",
            };
        }

        if (!chart.IsExtended)
        {
            facts["plotVisibleOnly"] = chart.PlotVisibleOnly?.ToString();
            facts["displayBlanksAs"] = chart.DisplayBlanksAs switch
            {
                ChartBlanks.Gap => "1",
                ChartBlanks.Zero => "2",
                ChartBlanks.Span => "3",
                _ => null,
            };
        }

        int number = 0;
        foreach ((ChartPlot plot, ChartSeries series) in chart.Plots.SelectMany(p => p.Series.Select(s => (p, s))).OrderBy(x => x.s.Order))
        {
            {
                string key = "series" + ++number;
                facts[key + ".name"] = series.Name;
                facts[key + ".type"] = TypeCode(chart, plot);
                facts[key + ".hasDataLabels"] = series.HasDataLabels.ToString();
                if (!chart.IsExtended)
                {
                    facts[key + ".axisGroup"] = plot.Axes.Count == 0 ? "1" : Secondary(chart, plot) ? "2" : "1";

                    // A pie of a pie shows Excel one more point, the sum of those pulled out.
                    if (plot.Type is not (ChartType.PieOfPie or ChartType.BarOfPie))
                    {
                        facts[key + ".values"] = string.Join(';', series.Values.Numbers.Select(Number));
                        facts[key + ".categories"] = Categories(plot, series);
                    }

                    facts[key + ".trendlines"] = series.Trendlines.Count.ToString(CultureInfo.InvariantCulture);
                    facts[key + ".hasErrorBars"] = (series.ErrorBars.Count > 0).ToString();
                    facts[key + ".formula"] = Formula(series, number);
                    facts[key + ".smooth"] = series.Smooth?.ToString();
                    facts[key + ".marker"] = Marker(series);
                }
            }
        }

        AxisFacts(chart, facts);
        return facts;
    }

    private static string? Categories(ChartPlot plot, ChartSeries series)
    {
        ChartData categories = series.Categories;
        if (categories.Count == 0)
        {
            return null;
        }

        if (categories.IsNumeric)
        {
            return string.Join(';', categories.Numbers.Select(Number));
        }

        // Text X values count for a scatter chart as 1, 2, 3 and on.
        if (plot.Type is ChartType.Scatter or ChartType.Bubble)
        {
            return string.Join(';', Enumerable.Range(1, categories.Count));
        }

        return categories.TextLevels.Count > 1 ? null : string.Join(';', categories.Text);
    }

    private static string Number(double? value) => value is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : "";

    private static string? Marker(ChartSeries series) => series.Marker.Symbol switch
    {
        ChartMarkerSymbol.None => "-4142",
        ChartMarkerSymbol.Circle => "8",
        ChartMarkerSymbol.Square => "1",
        ChartMarkerSymbol.Diamond => "2",
        ChartMarkerSymbol.Triangle => "3",
        ChartMarkerSymbol.X => "-4168",
        ChartMarkerSymbol.Star => "5",
        ChartMarkerSymbol.Dot => "-4118",
        ChartMarkerSymbol.Dash => "-4115",
        ChartMarkerSymbol.Plus => "9",
        ChartMarkerSymbol.Picture => "-4147",
        _ => null,
    };

    private static string? Formula(ChartSeries series, int number)
    {
        // Excel numbers plots by their chart group, so only the references are the same across all of them.
        if (series.Values.Reference is null)
        {
            return null;
        }

        string sizes = series.BubbleSizes.Reference is { } bubble
            ? "," + bubble
            : series.BubbleSizes.Count > 0 ? ",{" + string.Join(',', series.BubbleSizes.Numbers.Select(Number)) + "}" : "";
        return $"=SERIES({series.NameReference},{series.Categories.Reference},{series.Values.Reference},{series.Order + 1}{sizes})";
    }

    private static bool Secondary(Chart chart, ChartPlot plot)
    {
        if (chart.IsExtended || plot.Axes.Count == 0 || chart.Plots[0].Axes.Count == 0)
        {
            return false;
        }

        return plot.Axes[0].Id != chart.Plots[0].Axes[0].Id;
    }

    private static string? TypeCode(Chart chart, ChartPlot plot)
    {
        ChartGrouping grouping = plot.Grouping ?? ChartGrouping.Standard;
        int stack = grouping switch
        {
            ChartGrouping.Stacked => 1,
            ChartGrouping.PercentStacked => 2,
            _ => 0,
        };
        return plot.Type switch
        {
            ChartType.Column when plot.Is3D && grouping == ChartGrouping.Standard => "-4100",
            ChartType.Column when plot.Is3D => Code(54 + stack),
            ChartType.Column => Code(51 + stack),
            ChartType.Bar when plot.Is3D => Code(60 + stack),
            ChartType.Bar => Code(57 + stack),
            ChartType.Area when plot.Is3D && grouping == ChartGrouping.Standard => "-4098",
            ChartType.Area when plot.Is3D => Code(77 + stack),
            ChartType.Area => stack == 0 ? "1" : Code(75 + stack),
            ChartType.Radar => plot.RadarStyle switch
            {
                ChartRadarStyle.Filled => "82",
                _ => null,
            },
            ChartType.Pie => null,
            ChartType.PieOfPie => "68",
            ChartType.BarOfPie => "71",
            ChartType.Surface => (plot.Is3D, plot.Wireframe == true) switch
            {
                (true, false) => "83",
                (true, true) => "84",
                (false, false) => "85",
                (false, true) => "86",
            },
            ChartType.Treemap => "117",
            ChartType.Histogram => "118",
            ChartType.Waterfall => "119",
            ChartType.Sunburst => "120",
            ChartType.BoxWhisker => "121",
            ChartType.Pareto => "122",
            ChartType.Funnel => "123",
            _ => null,
        };

        static string Code(int code) => code.ToString(CultureInfo.InvariantCulture);
    }

    private static void AxisFacts(Chart chart, Dictionary<string, string?> facts)
    {
        foreach ((string prefix, bool second) in new[] { ("", false), ("2", true) })
        {
            if (second && chart.IsExtended)
            {
                continue;
            }

            ChartPlot? plot = chart.Plots.FirstOrDefault(p => Secondary(chart, p) == second && p.Axes.Count >= 2);
            if (plot is null && !chart.IsExtended)
            {
                continue;
            }

            IReadOnlyList<ChartAxis> axes = plot?.Axes ?? chart.Axes;
            ChartAxis? value = chart.IsExtended ? axes.FirstOrDefault(a => a.Kind == ChartAxisKind.Value) : axes.LastOrDefault(a => a.Kind == ChartAxisKind.Value && a.Position is ChartAxisPosition.Left or ChartAxisPosition.Right);
            ChartAxis? category = axes.FirstOrDefault(a => a != value && a.Kind != ChartAxisKind.Series);
            if (category is not null && !category.Hidden)
            {
                One("category" + prefix, category, facts);
            }

            if (value is not null && !value.Hidden)
            {
                One("value" + prefix, value, facts);
            }
        }
    }

    private static void One(string key, ChartAxis axis, Dictionary<string, string?> facts)
    {
        facts[key + ".hasTitle"] = axis.HasTitle.ToString();
        if (axis.HasTitle)
        {
            facts[key + ".title"] = axis.Title.Text;
        }

        facts[key + ".majorGridlines"] = axis.HasMajorGridlines.ToString();
        facts[key + ".minorGridlines"] = axis.HasMinorGridlines.ToString();
        facts[key + ".min"] = axis.Minimum is { } min ? Number(min) : null;
        facts[key + ".max"] = axis.Maximum is { } max ? Number(max) : null;
        facts[key + ".majorUnit"] = axis.MajorUnit is { } unit ? Number(unit) : null;
        facts[key + ".logBase"] = axis.LogBase is { } log ? Number(log) : null;
        facts[key + ".numberFormat"] = axis.NumberFormat?.Replace("\\", "", StringComparison.Ordinal);
        facts[key + ".reversed"] = axis.Reversed.ToString();
        facts[key + ".majorTick"] = Tick(axis.MajorTickMark);
        facts[key + ".minorTick"] = Tick(axis.MinorTickMark);
        facts[key + ".tickLabelPosition"] = axis.TickLabelPosition switch
        {
            ChartTickLabelPosition.None => "-4142",
            ChartTickLabelPosition.NextToAxis => "4",
            ChartTickLabelPosition.High => "-4127",
            ChartTickLabelPosition.Low => "-4134",
            _ => null,
        };
    }

    private static string? Tick(ChartTickMark? mark) => mark switch
    {
        ChartTickMark.None => "-4142",
        ChartTickMark.Inside => "2",
        ChartTickMark.Outside => "3",
        ChartTickMark.Cross => "4",
        _ => null,
    };
}
