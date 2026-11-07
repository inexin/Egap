using System.Globalization;
using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Printing;

/// <summary>
/// The conditional formats that draw rather than restyle (ECMA-376 Part 1, 18.3.1.16, 18.3.1.28 and 18.3.1.49): colour scales
/// shade a cell's fill, data bars draw a bar by the value's place between the lowest and highest, icon sets draw an icon by
/// thresholds. The rules that apply a style are evaluated later.
/// </summary>
internal sealed class ConditionalDrawing
{
    private readonly Worksheet sheet;
    private readonly SheetColors colors;
    private readonly List<Rule> rules = [];

    public ConditionalDrawing(Worksheet sheet, SheetColors colors)
    {
        this.sheet = sheet;
        this.colors = colors;
        foreach (ConditionalFormat format in sheet.ConditionalFormats)
        {
            foreach (ConditionalFormatRule rule in format.Rules.OrderBy(r => r.Priority))
            {
                string? name = rule.Type switch
                {
                    ConditionalFormatType.ColorScale => "colorScale",
                    ConditionalFormatType.DataBar => "dataBar",
                    ConditionalFormatType.IconSet => "iconSet",
                    _ => null,
                };
                XElement? element = name is null ? null : rule.Drawing ?? rule.Source?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
                if (element is not null)
                {
                    rules.Add(new Rule(rule.Type, format.Ranges, element));
                }
            }
        }
    }

    /// <summary>The fill a colour scale gives the cell, or null.</summary>
    public Rgba8? ScaleColor(int row, int column)
    {
        foreach (Rule rule in rules)
        {
            if (rule.Type != ConditionalFormatType.ColorScale || !rule.Covers(row, column) || Number(row, column) is not { } value)
            {
                continue;
            }

            double[] stops = Stops(rule);
            Rgba8[] stopColors = [.. rule.Element.Elements().Where(e => e.Name.LocalName == "color").Select(e => colors.Resolve(XlsxStyles.ReadColor(e).Color, new Rgba8(0, 0, 0, 255)))];
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

        return null;
    }

    /// <summary>Whether the cell's value shows: a data bar or icon set with showValue off hides it.</summary>
    public bool ShowsValue(int row, int column)
    {
        foreach (Rule rule in rules)
        {
            if (rule.Type != ConditionalFormatType.ColorScale && rule.Covers(row, column)
                && !XlsxNames.Bool((string?)rule.Element.Attribute("showValue"), true) && Number(row, column) is not null)
            {
                return false;
            }
        }

        return true;
    }

    public void DrawBar(Canvas canvas, int row, int column, RectF cell)
    {
        foreach (Rule rule in rules)
        {
            if (rule.Type != ConditionalFormatType.DataBar || !rule.Covers(row, column) || Number(row, column) is not { } value)
            {
                continue;
            }

            double[] stops = Stops(rule);
            if (stops.Length < 2)
            {
                return;
            }

            double low = stops[0], high = stops[1];
            double t = high <= low ? 1 : Math.Clamp((value - low) / (high - low), 0, 1);
            double minimum = XlsxNames.Int((string?)rule.Element.Attribute("minLength"), 10) / 100.0;
            double maximum = XlsxNames.Int((string?)rule.Element.Attribute("maxLength"), 90) / 100.0;
            float pad = (float)(2 * SheetFonts.PointsPerPixel);
            float length = (float)((cell.Width - (2 * pad)) * (minimum + (t * (maximum - minimum))));
            Rgba8 color = colors.Resolve(XlsxStyles.ReadColor(rule.Element.Elements().FirstOrDefault(e => e.Name.LocalName == "color")).Color, new Rgba8(0x63, 0x8E, 0xC6, 255));
            canvas.FillRect(new RectF(cell.X + pad, cell.Y + pad, Math.Max(0, length), Math.Max(0, cell.Height - (2 * pad))), color);
            return;
        }
    }

    public void DrawIcon(Canvas canvas, int row, int column, RectF cell)
    {
        foreach (Rule rule in rules)
        {
            if (rule.Type != ConditionalFormatType.IconSet || !rule.Covers(row, column) || Number(row, column) is not { } value)
            {
                continue;
            }

            string set = (string?)rule.Element.Attribute("iconSet") ?? "3TrafficLights1";
            XElement[] cfvos = [.. rule.Element.Elements().Where(e => e.Name.LocalName == "cfvo")];
            double[] stops = Stops(rule);
            int icon = 0;
            for (int i = 1; i < stops.Length; i++)
            {
                bool greaterOrEqual = XlsxNames.Bool((string?)cfvos[i].Attribute("gte"), true);
                if (greaterOrEqual ? value >= stops[i] : value > stops[i])
                {
                    icon = i;
                }
            }

            int count = Math.Max(1, stops.Length);
            if (XlsxNames.Bool((string?)rule.Element.Attribute("reverse"), false))
            {
                icon = count - 1 - icon;
            }

            float size = Math.Min((float)(16 * SheetFonts.PointsPerPixel), cell.Height - 1);
            Vector2 center = new(cell.X + 1.5f + (size / 2), cell.Y + (cell.Height / 2));
            DrawIcon(canvas, set, icon, count, center, size / 2);
            return;
        }
    }

    private static void DrawIcon(Canvas canvas, string set, int icon, int count, Vector2 center, float radius)
    {
        Rgba8 red = new(0xF8, 0x69, 0x6B, 255), yellow = new(0xFF, 0xEB, 0x84, 255), green = new(0x63, 0xBE, 0x7B, 255);
        Rgba8 gray = new(0x80, 0x80, 0x80, 255), black = new(0x30, 0x30, 0x30, 255);
        double t = count <= 1 ? 1 : icon / (double)(count - 1);
        Rgba8 color = t < 0.34 ? red : t < 0.67 ? yellow : green;
        if (set.Contains("Gray", StringComparison.Ordinal))
        {
            color = gray;
        }
        else if (set.Contains("RedToBlack", StringComparison.Ordinal))
        {
            color = Mix(black, red, t);
        }

        if (set.Contains("Arrows", StringComparison.Ordinal))
        {
            // Up for the highest, down for the lowest, turned between.
            float angle = (float)((t - 0.5) * Math.PI);
            Vector2 tip = center + (new Vector2(MathF.Cos(angle), -MathF.Sin(angle)) * radius);
            Vector2 tail = center - (new Vector2(MathF.Cos(angle), -MathF.Sin(angle)) * radius);
            Vector2 across = new Vector2(MathF.Sin(angle), MathF.Cos(angle)) * radius * 0.6f;
            Vector2 neck = center + ((tip - center) * 0.1f);
            canvas.DrawLine(tail, neck, new Stroke(radius * 0.5f), color);
            Egami.Drawing.Path head = new PathBuilder().MoveTo(tip).LineTo(neck + across).LineTo(neck - across).Close().ToPath();
            canvas.FillPath(head, color);
        }
        else if (set.Contains("Rating", StringComparison.Ordinal) || set.Contains("Quarters", StringComparison.Ordinal))
        {
            canvas.StrokeRect(new RectF(center.X - radius, center.Y - radius, radius * 2, radius * 2), new Stroke(0.5f), gray);
            canvas.FillRect(new RectF(center.X - radius, center.Y + radius - (float)(2 * radius * t), radius * 2, (float)(2 * radius * t)), gray);
        }
        else if (set.Contains("Flags", StringComparison.Ordinal))
        {
            canvas.DrawLine(new Vector2(center.X - (radius * 0.6f), center.Y - radius), new Vector2(center.X - (radius * 0.6f), center.Y + radius), new Stroke(0.75f), black);
            canvas.FillRect(new RectF(center.X - (radius * 0.6f), center.Y - radius, radius * 1.4f, radius), color);
        }
        else
        {
            canvas.FillCircle(center, radius * 0.9f, color);
        }
    }

    /// <summary>The rule's thresholds, from its cfvo elements, over the numbers in its ranges.</summary>
    private double[] Stops(Rule rule)
    {
        if (rule.Stops is { } cached)
        {
            return cached;
        }

        List<double> values = [];
        foreach (RangeAddress range in rule.Ranges)
        {
            foreach ((int r, int c) in sheet.Store.Occupied(range))
            {
                if (Number(r, c) is { } n)
                {
                    values.Add(n);
                }
            }
        }

        values.Sort();
        double min = values.Count == 0 ? 0 : values[0];
        double max = values.Count == 0 ? 0 : values[^1];
        List<double> stops = [];
        foreach (XElement cfvo in rule.Element.Elements().Where(e => e.Name.LocalName == "cfvo"))
        {
            string type = (string?)cfvo.Attribute("type") ?? "num";
            string? text = (string?)cfvo.Attribute("val");
            double val = 0;
            if (text is not null && !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out val))
            {
                try
                {
                    CellValue evaluated = sheet.Evaluate(text, rule.Ranges[0].First);
                    val = evaluated.Kind == CellValueKind.Number ? evaluated.Number : 0;
                }
                catch (FormulaException)
                {
                    val = 0;
                }
            }

            stops.Add(type switch
            {
                "min" or "autoMin" => min,
                "max" or "autoMax" => max,
                "percent" => min + ((max - min) * val / 100),
                "percentile" => Percentile(values, val / 100),
                _ => val,
            });
        }

        rule.Stops = [.. stops];
        return rule.Stops;
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

    private double? Number(int row, int column)
    {
        CellValue value = sheet.GetValue(row, column);
        return value.Kind == CellValueKind.Number ? value.Number : null;
    }

    private static Rgba8 Mix(Rgba8 a, Rgba8 b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgba8((byte)Math.Round(a.R + ((b.R - a.R) * t)), (byte)Math.Round(a.G + ((b.G - a.G) * t)), (byte)Math.Round(a.B + ((b.B - a.B) * t)), 255);
    }

    private sealed class Rule(ConditionalFormatType type, List<RangeAddress> ranges, XElement element)
    {
        public ConditionalFormatType Type { get; } = type;

        public List<RangeAddress> Ranges { get; } = ranges;

        public XElement Element { get; } = element;

        public double[]? Stops { get; set; }

        public bool Covers(int row, int column)
        {
            CellAddress cell = new(row, column);
            foreach (RangeAddress range in Ranges)
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
