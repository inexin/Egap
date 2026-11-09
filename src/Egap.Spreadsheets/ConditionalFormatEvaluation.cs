using System.Globalization;
using System.Xml.Linq;

using Egami.Pixels;

using Egap.Spreadsheets.Formulas;
using Egap.Spreadsheets.Printing;
using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets;

/// <summary>A data bar a rule draws in a cell: where the cell's value lies between the bar's lowest and highest.</summary>
/// <param name="Fraction">From 0 at the bar's lowest value to 1 at its highest.</param>
/// <param name="Color">The bar's colour.</param>
public readonly record struct ConditionalDataBar(double Fraction, CellColor Color);

/// <summary>An icon an icon set rule draws in a cell.</summary>
/// <param name="IconSet">The set's name, such as <c>3TrafficLights1</c> or <c>5Rating</c>.</param>
/// <param name="Index">The icon's place in the set, from 0: the icon the lowest values get, unless the rule reverses the set.</param>
public readonly record struct ConditionalIcon(string IconSet, int Index);

/// <summary>What conditional formatting does to one cell, as Excel shows it.</summary>
public sealed class ConditionalCellFormat
{
    internal ConditionalCellFormat(IReadOnlyList<ConditionalFormatRule> rules, CellStyle style, ConditionalDataBar? dataBar, ConditionalIcon? icon, bool showsValue)
    {
        Rules = rules;
        Style = style;
        DataBar = dataBar;
        Icon = icon;
        ShowsValue = showsValue;
    }

    /// <summary>The rules that apply to the cell, first by priority, up to a rule that stops the rest.</summary>
    public IReadOnlyList<ConditionalFormatRule> Rules { get; }

    /// <summary>
    /// The cell's style with the rules' formats on it: each rule's font colour, bold, italic, underline, strikethrough, fill,
    /// borders and number format, a rule of higher priority winning where two set the same; a colour scale's colour as a
    /// solid fill.
    /// </summary>
    public CellStyle Style { get; }

    /// <summary>The data bar the cell shows, or null.</summary>
    public ConditionalDataBar? DataBar { get; }

    /// <summary>The icon the cell shows, or null.</summary>
    public ConditionalIcon? Icon { get; }

    /// <summary>Whether the cell's value shows: a data bar or icon set can hide it.</summary>
    public bool ShowsValue { get; }

    /// <summary>The style the rules were applied to.</summary>
    internal CellStyle? BaseStyle { get; init; }
}

/// <summary>
/// A sheet's conditional formatting evaluated as Excel evaluates it, over the values the cells hold when this is made:
/// which rules apply to each cell and what they make of it. Rules that look at the whole of their ranges, such as top 10,
/// above average, duplicates, colour scales, data bars and icon sets, measure their ranges once.
/// </summary>
public sealed class ConditionalFormatEvaluation
{
    private readonly Worksheet sheet;
    private readonly List<RuleState> rules = [];
    private readonly Dictionary<DefinedName, CompiledFormula?> names = [];
    private readonly Dictionary<long, ConditionalCellFormat> cache = [];
    private SheetColors? colors;

    internal ConditionalFormatEvaluation(Worksheet sheet)
    {
        this.sheet = sheet;
        int order = 0;
        foreach (ConditionalFormat format in sheet.ConditionalFormats)
        {
            foreach (ConditionalFormatRule rule in format.Rules)
            {
                rules.Add(new RuleState(format, rule, order++));
            }
        }

        // Priority 1 first; a rule without one goes after the rest, in the order the sheet holds them.
        rules.Sort((a, b) => a.Rule.Priority != b.Rule.Priority
            ? (a.Rule.Priority <= 0 ? int.MaxValue : a.Rule.Priority).CompareTo(b.Rule.Priority <= 0 ? int.MaxValue : b.Rule.Priority)
            : a.Order.CompareTo(b.Order));
    }

    /// <summary>What conditional formatting does to a cell.</summary>
    public ConditionalCellFormat this[CellAddress cell] => this[cell.Row, cell.Column];

    /// <summary>What conditional formatting does to a cell given in A1 form.</summary>
    /// <exception cref="FormatException">The text is not a cell reference.</exception>
    public ConditionalCellFormat this[string cell] => this[CellAddress.Parse(cell)];

    /// <summary>What conditional formatting does to a cell given by zero-based row and column.</summary>
    public ConditionalCellFormat this[int row, int column] => Evaluate(row, column, BaseStyle(row, column));

    private SheetColors Colors => colors ??= SheetColors.For(sheet.Workbook);

    /// <summary>What the rules do to a cell whose own style is <paramref name="style"/>.</summary>
    internal ConditionalCellFormat Evaluate(int row, int column, CellStyle style)
    {
        long key = Worksheet.Key(row, column);
        if (cache.TryGetValue(key, out ConditionalCellFormat? known) && ReferenceEquals(known.BaseStyle, style))
        {
            return known;
        }

        CellAddress cell = new(row, column);
        List<ConditionalFormatRule>? applied = null;
        Parts set = Parts.None;
        CellFont font = style.Font;
        CellFill fill = style.Fill;
        CellBorders borders = style.Borders;
        string numberFormat = style.NumberFormat;
        ConditionalDataBar? bar = null;
        ConditionalIcon? icon = null;
        bool showsValue = true;
        foreach (RuleState rule in rules)
        {
            if (!rule.Covers(cell))
            {
                continue;
            }

            bool applies;
            switch (rule.Rule.Type)
            {
                case ConditionalFormatType.ColorScale:
                    Rgba8? shade = ScaleColor(rule, row, column);
                    applies = shade is not null;
                    if (shade is { } color && (set & Parts.Fill) == 0)
                    {
                        fill = CellFill.Solid(CellColor.FromRgb(color.R, color.G, color.B));
                        set |= Parts.Fill;
                    }

                    break;
                case ConditionalFormatType.DataBar:
                    ConditionalDataBar? drawn = DataBar(rule, row, column);
                    applies = drawn is not null;
                    if (applies && bar is null)
                    {
                        bar = drawn;
                        showsValue &= rule.ShowsValue;
                    }

                    break;
                case ConditionalFormatType.IconSet:
                    ConditionalIcon? shown = Icon(rule, row, column);
                    applies = shown is not null;
                    if (applies && icon is null)
                    {
                        icon = shown;
                        showsValue &= rule.ShowsValue;
                    }

                    break;
                default:
                    applies = Matches(rule, row, column);
                    if (applies && rule.Rule.Style is { } format)
                    {
                        int stated = rule.Rule.ReadStyle is { } read && Equals(read.Style, format) ? rule.Rule.StatedFontFlags : 0;
                        Merge(format, stated, ref set, ref font, ref fill, ref borders, ref numberFormat);
                    }

                    break;
            }

            if (applies)
            {
                (applied ??= []).Add(rule.Rule);
                if (rule.Rule.StopIfTrue && rule.Rule.Type is not (ConditionalFormatType.ColorScale or ConditionalFormatType.DataBar or ConditionalFormatType.IconSet))
                {
                    break;
                }
            }
        }

        ConditionalCellFormat result = applied is null
            ? new ConditionalCellFormat([], style, null, null, true) { BaseStyle = style }
            : new ConditionalCellFormat(applied, set == Parts.None ? style : style with { Font = font, Fill = fill, Borders = borders, NumberFormat = numberFormat }, bar, icon, showsValue)
            {
                BaseStyle = style,
            };
        cache[key] = result;
        return result;
    }

    /// <summary>The style of a cell: its own, else its row's, else its column's.</summary>
    internal CellStyle BaseStyle(int row, int column)
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

        Storage.StyleTable styles = sheet.Workbook.Styles;
        return index >= 0 && index < styles.Count ? styles[index] : CellStyle.Default;
    }

    /// <summary>The colour a colour scale gives the cell, or null when the cell holds no number.</summary>
    internal Rgba8? ScaleColor(RuleState rule, int row, int column)
    {
        if (rule.Drawing is not { } element || Number(row, column) is not { } value)
        {
            return null;
        }

        if (Stops(rule) is not { } stops)
        {
            return null;
        }

        Rgba8[] stopColors = rule.StopColors ??= [.. element.Elements().Where(e => e.Name.LocalName == "color").Select(e => Colors.Resolve(XlsxStyles.ReadColor(e).Color, new Rgba8(0, 0, 0, 255)))];
        int count = Math.Min(stops.Length, stopColors.Length);
        if (count < 2)
        {
            return null;
        }

        if (value <= stops[0])
        {
            return stopColors[0];
        }

        for (int i = 1; i < count; i++)
        {
            if (value <= stops[i])
            {
                double span = stops[i] - stops[i - 1];
                double t = span <= 0 ? 1 : (value - stops[i - 1]) / span;
                return Mix(stopColors[i - 1], stopColors[i], t);
            }
        }

        return stopColors[count - 1];
    }

    private ConditionalDataBar? DataBar(RuleState rule, int row, int column)
    {
        if (rule.Drawing is not { } element || Number(row, column) is not { } value)
        {
            return null;
        }

        if (Stops(rule) is not { Length: >= 2 } stops)
        {
            return null;
        }

        double low = stops[0], high = stops[1];
        double t = high <= low ? 1 : Math.Clamp((value - low) / (high - low), 0, 1);
        CellColor color = XlsxStyles.ReadColor(element.Elements().FirstOrDefault(e => e.Name.LocalName == "color")).Color;
        return new ConditionalDataBar(t, color.Kind == CellColorKind.Auto ? CellColor.FromRgb(0x63, 0x8E, 0xC6) : color);
    }

    private ConditionalIcon? Icon(RuleState rule, int row, int column)
    {
        if (rule.Drawing is not { } element || Number(row, column) is not { } value)
        {
            return null;
        }

        string set = (string?)element.Attribute("iconSet") ?? "3TrafficLights1";
        XElement[] cfvos = [.. element.Elements().Where(e => e.Name.LocalName == "cfvo")];
        if (Stops(rule) is not { } stops)
        {
            return null;
        }

        int icon = 0;
        for (int i = 1; i < stops.Length; i++)
        {
            bool greaterOrEqual = XlsxNames.Bool((string?)cfvos[i].Attribute("gte"), true);
            if (greaterOrEqual ? ExcelValues.CompareNumbers(value, stops[i]) >= 0 : ExcelValues.CompareNumbers(value, stops[i]) > 0)
            {
                icon = i;
            }
        }

        if (XlsxNames.Bool((string?)element.Attribute("reverse"), false))
        {
            icon = Math.Max(1, stops.Length) - 1 - icon;
        }

        return new ConditionalIcon(set, icon);
    }

    /// <summary>Whether a rule that formats applies to the cell.</summary>
    private bool Matches(RuleState rule, int row, int column)
    {
        switch (rule.Rule.Type)
        {
            case ConditionalFormatType.CellIs:
                return CellIs(rule, row, column);
            case ConditionalFormatType.Top10:
                return Number(row, column) is { } top && Top(rule, top);
            case ConditionalFormatType.AboveAverage:
                return Number(row, column) is { } number && Average(rule, number);
            case ConditionalFormatType.ContainsBlanks:
            case ConditionalFormatType.NotContainsBlanks:
                // By the cell, not the formula Excel writes: an error is not blank, though LEN(TRIM()) of it is no number.
                CellValue value = sheet.GetValue(row, column);
                bool blank = value.Kind == CellValueKind.Empty || (value.Kind == CellValueKind.Text && value.Text.Trim(' ').Length == 0);
                return blank == (rule.Rule.Type == ConditionalFormatType.ContainsBlanks);
            case ConditionalFormatType.UniqueValues:
            case ConditionalFormatType.DuplicateValues:
                return Key(sheet.GetValue(row, column)) is { } key
                    && (Counts(rule).GetValueOrDefault(key) > 1) == (rule.Rule.Type == ConditionalFormatType.DuplicateValues);
            default:
                return Formula(rule, 0) is { } formula && Truth(Run(formula, rule, row, column));
        }
    }

    private bool CellIs(RuleState rule, int row, int column)
    {
        EvalValue value = EvalValue.FromCell(sheet.GetValue(row, column));
        if (value.IsError || Formula(rule, 0) is not { } first)
        {
            return false;
        }

        CellValue one = Run(first, rule, row, column);
        if (one.Kind is CellValueKind.Error or CellValueKind.Empty)
        {
            return false;
        }

        EvalContext context = new(sheet, row, column);
        int a = ExcelValues.Compare(value, EvalValue.FromCell(one), context);
        ComparisonOperator comparison = rule.Rule.Operator;
        if (comparison is ComparisonOperator.Between or ComparisonOperator.NotBetween)
        {
            if (Formula(rule, 1) is not { } second || Run(second, rule, row, column) is var two && two.Kind is CellValueKind.Error or CellValueKind.Empty)
            {
                return false;
            }

            int b = ExcelValues.Compare(value, EvalValue.FromCell(two), context);
            bool lowFirst = ExcelValues.Compare(EvalValue.FromCell(one), EvalValue.FromCell(two), context) <= 0;
            bool inside = lowFirst ? a >= 0 && b <= 0 : b >= 0 && a <= 0;
            return inside == (comparison == ComparisonOperator.Between);
        }

        return comparison switch
        {
            ComparisonOperator.Equal => a == 0,
            ComparisonOperator.NotEqual => a != 0,
            ComparisonOperator.LessThan => a < 0,
            ComparisonOperator.LessThanOrEqual => a <= 0,
            ComparisonOperator.GreaterThan => a > 0,
            _ => a >= 0,
        };
    }

    private bool Top(RuleState rule, double value)
    {
        List<double> numbers = Numbers(rule);
        if (numbers.Count == 0)
        {
            return false;
        }

        bool bottom = XlsxNames.Bool(rule.Attribute("bottom"), false);
        int rank = XlsxNames.Int(rule.Attribute("rank"), 10);
        int count = XlsxNames.Bool(rule.Attribute("percent"), false) ? numbers.Count * rank / 100 : rank;
        count = Math.Clamp(count, 1, numbers.Count);
        double threshold = bottom ? numbers[count - 1] : numbers[^count];
        return bottom ? ExcelValues.CompareNumbers(value, threshold) <= 0 : ExcelValues.CompareNumbers(value, threshold) >= 0;
    }

    private bool Average(RuleState rule, double value)
    {
        List<double> numbers = Numbers(rule);
        if (numbers.Count == 0)
        {
            return false;
        }

        (double mean, double deviation) = rule.Moments ??= Moments(numbers);
        bool above = XlsxNames.Bool(rule.Attribute("aboveAverage"), true);
        bool equal = XlsxNames.Bool(rule.Attribute("equalAverage"), false);
        int deviations = XlsxNames.Int(rule.Attribute("stdDev"), 0);
        double limit = mean + ((above ? 1 : -1) * deviations * deviation);
        int compared = ExcelValues.CompareNumbers(value, limit);
        return above ? compared > 0 || (equal && compared == 0) : compared < 0 || (equal && compared == 0);
    }

    private static (double Mean, double Deviation) Moments(List<double> numbers)
    {
        double mean = numbers.Average();
        double squares = numbers.Sum(n => (n - mean) * (n - mean));
        return (mean, numbers.Count > 1 ? Math.Sqrt(squares / (numbers.Count - 1)) : 0);
    }

    /// <summary>How many times each value occurs in the rule's ranges: numbers, text without case, logicals and errors.</summary>
    private Dictionary<(CellValueKind, double, string?), int> Counts(RuleState rule)
    {
        if (rule.Counts is { } known)
        {
            return known;
        }

        Dictionary<(CellValueKind, double, string?), int> counts = [];
        foreach ((int r, int c) in Occupied(rule))
        {
            if (Key(sheet.GetValue(r, c)) is { } key)
            {
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        rule.Counts = counts;
        return counts;
    }

    private (CellValueKind, double, string?)? Key(CellValue value) => value.Kind switch
    {
        CellValueKind.Number => (CellValueKind.Number, ExcelValues.Round15(value.Number), null),
        CellValueKind.Text => (CellValueKind.Text, 0, value.Text.ToUpper(sheet.Workbook.Culture)),
        CellValueKind.Boolean => (CellValueKind.Boolean, value.Boolean ? 1 : 0, null),
        CellValueKind.Error => (CellValueKind.Error, (int)value.Error, null),
        _ => null,
    };

    /// <summary>The numbers in the rule's ranges, sorted.</summary>
    private List<double> Numbers(RuleState rule)
    {
        if (rule.Numbers is { } known)
        {
            return known;
        }

        List<double> numbers = [];
        foreach ((int r, int c) in Occupied(rule))
        {
            if (Number(r, c) is { } n)
            {
                numbers.Add(n);
            }
        }

        numbers.Sort();
        rule.Numbers = numbers;
        return numbers;
    }

    private IEnumerable<(int Row, int Column)> Occupied(RuleState rule)
    {
        HashSet<long>? seen = rule.Format.Ranges.Count > 1 ? [] : null;
        foreach (RangeAddress range in rule.Format.Ranges)
        {
            foreach ((int r, int c) in sheet.Store.Occupied(range))
            {
                if (seen is null || seen.Add(Worksheet.Key(r, c)))
                {
                    yield return (r, c);
                }
            }
        }
    }

    /// <summary>
    /// The rule's thresholds, from its cfvo elements, over the numbers in its ranges; null when a formula among them gives no
    /// number, which leaves every cell of the rule as it is.
    /// </summary>
    private double[]? Stops(RuleState rule)
    {
        if (rule.StopsMeasured)
        {
            return rule.Stops;
        }

        rule.StopsMeasured = true;

        List<double> values = Numbers(rule);
        double min = values.Count == 0 ? 0 : values[0];
        double max = values.Count == 0 ? 0 : values[^1];
        List<double> stops = [];
        foreach (XElement cfvo in rule.Drawing!.Elements().Where(e => e.Name.LocalName == "cfvo"))
        {
            string type = (string?)cfvo.Attribute("type") ?? "num";
            string? text = (string?)cfvo.Attribute("val");
            double val = 0;
            if (text is not null && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
            {
                if (Evaluate(text, rule) is not { Kind: CellValueKind.Number } evaluated)
                {
                    return null;
                }

                val = evaluated.Number;
            }

            stops.Add(type switch
            {
                "min" => min,
                "max" => max,
                "autoMin" => Math.Min(0, min),
                "autoMax" => Math.Max(0, max),
                "percent" => min + ((max - min) * val / 100),
                "percentile" => Percentile(values, val / 100),
                _ => val,
            });
        }

        rule.Stops = [.. stops];
        return rule.Stops;
    }

    /// <summary>A formula of the rule's own, such as a cfvo's, evaluated at the top-left cell of its ranges.</summary>
    private CellValue? Evaluate(string formula, RuleState rule)
    {
        try
        {
            CompiledFormula compiled = FormulaCompiler.Compile(FormulaParser.Parse(formula.StartsWith('=') ? formula[1..] : formula, r1c1: false, rule.Origin));
            return Run(compiled, rule, rule.Origin.Row, rule.Origin.Column);
        }
        catch (FormulaException)
        {
            return null;
        }
    }

    /// <summary>The percentile as PERCENTILE.INC works it out: interpolated between the two values around rank (n - 1) * p.</summary>
    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        double rank = (sorted.Count - 1) * Math.Clamp(p, 0, 1);
        int below = (int)Math.Floor(rank);
        int above = Math.Min(below + 1, sorted.Count - 1);
        return sorted[below] + ((sorted[above] - sorted[below]) * (rank - below));
    }

    /// <summary>The rule's formula at a place, compiled for the top-left cell of its ranges; null when it has none or it does not parse.</summary>
    private static CompiledFormula? Formula(RuleState rule, int index)
    {
        rule.Compiled ??= new CompiledFormula?[2];
        if (rule.Broken || index >= rule.Compiled.Length)
        {
            return null;
        }

        if (rule.Compiled[index] is { } known)
        {
            return known;
        }

        string? text = index < rule.Rule.Formulas.Count ? rule.Rule.Formulas[index] : index == 0 ? Generated(rule) : null;
        if (text is null)
        {
            return null;
        }

        try
        {
            rule.Compiled[index] = FormulaCompiler.Compile(FormulaParser.Parse(text.StartsWith('=') ? text[1..] : text, r1c1: false, rule.Origin));
        }
        catch (FormulaException)
        {
            rule.Broken = true;
        }

        return rule.Compiled[index];
    }

    /// <summary>The formula Excel writes for a rule of a kind that is a formula underneath, for a rule that has none.</summary>
    private static string? Generated(RuleState rule)
    {
        string cell = rule.Origin.ToString();
        string text = "\"" + (rule.Rule.Text ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        return rule.Rule.Type switch
        {
            ConditionalFormatType.ContainsText => $"NOT(ISERROR(SEARCH({text},{cell})))",
            ConditionalFormatType.NotContainsText => $"ISERROR(SEARCH({text},{cell}))",
            ConditionalFormatType.BeginsWith => $"LEFT({cell},LEN({text}))={text}",
            ConditionalFormatType.EndsWith => $"RIGHT({cell},LEN({text}))={text}",
            ConditionalFormatType.ContainsBlanks => $"LEN(TRIM({cell}))=0",
            ConditionalFormatType.NotContainsBlanks => $"LEN(TRIM({cell}))>0",
            ConditionalFormatType.ContainsErrors => $"ISERROR({cell})",
            ConditionalFormatType.NotContainsErrors => $"NOT(ISERROR({cell}))",
            ConditionalFormatType.TimePeriod => rule.Attribute("timePeriod") switch
            {
                "today" => $"FLOOR({cell},1)=TODAY()",
                "yesterday" => $"FLOOR({cell},1)=TODAY()-1",
                "tomorrow" => $"FLOOR({cell},1)=TODAY()+1",
                "last7Days" => $"AND(TODAY()-FLOOR({cell},1)<=6,FLOOR({cell},1)<=TODAY())",
                "thisWeek" => $"AND(TODAY()-ROUNDDOWN({cell},0)<=WEEKDAY(TODAY())-1,ROUNDDOWN({cell},0)-TODAY()<=7-WEEKDAY(TODAY()))",
                "lastWeek" => $"AND(TODAY()-ROUNDDOWN({cell},0)>=(WEEKDAY(TODAY())),TODAY()-ROUNDDOWN({cell},0)<(WEEKDAY(TODAY())+7))",
                "nextWeek" => $"AND(ROUNDDOWN({cell},0)-TODAY()>(7-WEEKDAY(TODAY())),ROUNDDOWN({cell},0)-TODAY()<(15-WEEKDAY(TODAY())))",
                "thisMonth" => $"AND(MONTH({cell})=MONTH(TODAY()),YEAR({cell})=YEAR(TODAY()))",
                "lastMonth" => $"AND(MONTH({cell})=MONTH(EDATE(TODAY(),0-1)),YEAR({cell})=YEAR(EDATE(TODAY(),0-1)))",
                "nextMonth" => $"AND(MONTH({cell})=MONTH(EDATE(TODAY(),0+1)),YEAR({cell})=YEAR(EDATE(TODAY(),0+1)))",
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>Runs a rule's formula for a cell, as an array formula written for the top-left cell of the rule's ranges.</summary>
    private CellValue Run(CompiledFormula formula, RuleState rule, int row, int column)
    {
        EvalContext context = EvalContext.ForCell(sheet, row, column, rule.Origin, names, arrayMode: true);
        try
        {
            return FormulaEvaluator.Result(FormulaEvaluator.Run(formula, context), context);
        }
        catch (FormulaException)
        {
            return CellValue.FromError(CellError.Value);
        }
    }

    /// <summary>Whether a formula's result formats the cell: TRUE, a number other than 0, or the text TRUE.</summary>
    private static bool Truth(CellValue value) => value.Kind switch
    {
        CellValueKind.Boolean => value.Boolean,
        CellValueKind.Number => value.Number != 0,
        CellValueKind.Text => value.Text.Equals("TRUE", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private double? Number(int row, int column)
    {
        CellValue value = sheet.GetValue(row, column);
        return value.Kind == CellValueKind.Number ? value.Number : null;
    }

    private static void Merge(CellStyle format, int stated, ref Parts set, ref CellFont font, ref CellFill fill, ref CellBorders borders, ref string numberFormat)
    {
        CellFont given = format.Font;
        CellFont none = CellFont.Default;
        if ((set & Parts.Color) == 0 && given.Color != none.Color)
        {
            font = font with { Color = given.Color };
            set |= Parts.Color;
        }

        // A flag the rule states false, as Excel writes <b val="0"/>, takes it away; one it leaves out is not the rule's.
        if ((set & Parts.Bold) == 0 && (given.Bold || (stated & 1) != 0))
        {
            font = font with { Bold = given.Bold };
            set |= Parts.Bold;
        }

        if ((set & Parts.Italic) == 0 && (given.Italic || (stated & 2) != 0))
        {
            font = font with { Italic = given.Italic };
            set |= Parts.Italic;
        }

        if ((set & Parts.Underline) == 0 && (given.Underline != CellUnderline.None || (stated & 8) != 0))
        {
            font = font with { Underline = given.Underline };
            set |= Parts.Underline;
        }

        if ((set & Parts.Strikethrough) == 0 && (given.Strikethrough || (stated & 4) != 0))
        {
            font = font with { Strikethrough = given.Strikethrough };
            set |= Parts.Strikethrough;
        }

        if ((set & Parts.Fill) == 0 && format.Fill != CellFill.None)
        {
            fill = format.Fill;
            set |= Parts.Fill;
        }

        if ((set & Parts.NumberFormat) == 0 && format.NumberFormat != CellStyle.Default.NumberFormat)
        {
            numberFormat = format.NumberFormat;
            set |= Parts.NumberFormat;
        }

        CellBorders lines = format.Borders;
        if ((set & Parts.Left) == 0 && lines.Left.Style != CellBorderStyle.None)
        {
            borders = borders with { Left = lines.Left };
            set |= Parts.Left;
        }

        if ((set & Parts.Right) == 0 && lines.Right.Style != CellBorderStyle.None)
        {
            borders = borders with { Right = lines.Right };
            set |= Parts.Right;
        }

        if ((set & Parts.Top) == 0 && lines.Top.Style != CellBorderStyle.None)
        {
            borders = borders with { Top = lines.Top };
            set |= Parts.Top;
        }

        if ((set & Parts.Bottom) == 0 && lines.Bottom.Style != CellBorderStyle.None)
        {
            borders = borders with { Bottom = lines.Bottom };
            set |= Parts.Bottom;
        }
    }

    /// <summary>
    /// A colour between two, as Excel's colour scales mix them, measured against Excel: each channel moves its share of its
    /// distance, cut to whole steps toward where it starts.
    /// </summary>
    private static Rgba8 Mix(Rgba8 a, Rgba8 b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgba8(Step(a.R, b.R, t), Step(a.G, b.G, t), Step(a.B, b.B, t), 255);
    }

    private static byte Step(byte from, byte to, double t) => (byte)(from + (int)Math.Truncate((to - from) * t));

    [Flags]
    private enum Parts
    {
        None = 0,
        Color = 1,
        Bold = 2,
        Italic = 4,
        Underline = 8,
        Strikethrough = 16,
        Fill = 32,
        NumberFormat = 64,
        Left = 128,
        Right = 256,
        Top = 512,
        Bottom = 1024,
    }

    /// <summary>A rule with what its evaluation measures once: its formulas, its ranges' numbers and its thresholds.</summary>
    internal sealed class RuleState
    {
        public RuleState(ConditionalFormat format, ConditionalFormatRule rule, int order)
        {
            Format = format;
            Rule = rule;
            Order = order;
            Origin = format.Ranges.Count > 0 ? format.Ranges[0].First : default;
            string? name = rule.Type switch
            {
                ConditionalFormatType.ColorScale => "colorScale",
                ConditionalFormatType.DataBar => "dataBar",
                ConditionalFormatType.IconSet => "iconSet",
                _ => null,
            };
            Drawing = name is null ? null : rule.Drawing ?? rule.Source?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            ShowsValue = Drawing is null || XlsxNames.Bool((string?)Drawing.Attribute("showValue"), true);
        }

        public ConditionalFormat Format { get; }

        public ConditionalFormatRule Rule { get; }

        public int Order { get; }

        /// <summary>The top-left cell of the first range, which relative references in the rule's formulas count from.</summary>
        public CellAddress Origin { get; }

        public XElement? Drawing { get; }

        public bool ShowsValue { get; }

        public CompiledFormula?[]? Compiled { get; set; }

        /// <summary>Whether a formula of the rule does not parse, so the rule formats nothing.</summary>
        public bool Broken { get; set; }

        public List<double>? Numbers { get; set; }

        public double[]? Stops { get; set; }

        public bool StopsMeasured { get; set; }

        public Rgba8[]? StopColors { get; set; }

        public (double Mean, double Deviation)? Moments { get; set; }

        public Dictionary<(CellValueKind, double, string?), int>? Counts { get; set; }

        public string? Attribute(string name) => (string?)Rule.Source?.Attribute(name);

        public bool Covers(CellAddress cell)
        {
            foreach (RangeAddress range in Format.Ranges)
            {
                if (range.Contains(cell))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
