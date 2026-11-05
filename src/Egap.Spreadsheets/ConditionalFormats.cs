using System.Collections;
using System.Xml.Linq;

namespace Egap.Spreadsheets;

/// <summary>The kinds of conditional formatting rule (ECMA-376 Part 1, 18.18.12, ST_CfType).</summary>
public enum ConditionalFormatType
{
    /// <summary>Formats cells for which a formula is true.</summary>
    Expression,

    /// <summary>Formats cells whose value compares with one or two formulas.</summary>
    CellIs,

    /// <summary>Shades cells along a scale of two or three colours.</summary>
    ColorScale,

    /// <summary>Draws a bar in each cell, as long as its value.</summary>
    DataBar,

    /// <summary>Draws an icon in each cell by its value.</summary>
    IconSet,

    /// <summary>Formats the top or bottom values, by count or percent.</summary>
    Top10,

    /// <summary>Formats values that occur once.</summary>
    UniqueValues,

    /// <summary>Formats values that occur more than once.</summary>
    DuplicateValues,

    /// <summary>Formats text that contains <see cref="ConditionalFormatRule.Text"/>.</summary>
    ContainsText,

    /// <summary>Formats text that does not contain <see cref="ConditionalFormatRule.Text"/>.</summary>
    NotContainsText,

    /// <summary>Formats text that begins with <see cref="ConditionalFormatRule.Text"/>.</summary>
    BeginsWith,

    /// <summary>Formats text that ends with <see cref="ConditionalFormatRule.Text"/>.</summary>
    EndsWith,

    /// <summary>Formats empty cells.</summary>
    ContainsBlanks,

    /// <summary>Formats cells that are not empty.</summary>
    NotContainsBlanks,

    /// <summary>Formats cells that hold errors.</summary>
    ContainsErrors,

    /// <summary>Formats cells that hold no errors.</summary>
    NotContainsErrors,

    /// <summary>Formats dates in a period such as last week.</summary>
    TimePeriod,

    /// <summary>Formats values above or below the average.</summary>
    AboveAverage,
}

/// <summary>
/// One conditional formatting rule: its kind, formulas and the style it applies. Rules made in Excel that the model does
/// not describe in full, such as an icon set's thresholds, keep those parts and write them back.
/// </summary>
public sealed class ConditionalFormatRule
{
    internal ConditionalFormatRule(ConditionalFormatType type)
    {
        Type = type;
    }

    /// <summary>The kind of rule.</summary>
    public ConditionalFormatType Type { get; }

    /// <summary>How a <see cref="ConditionalFormatType.CellIs"/> rule compares the value with its formulas.</summary>
    public ComparisonOperator Operator { get; set; }

    /// <summary>The rule's formulas, without the leading <c>=</c>: one for an expression, one or two for a comparison.</summary>
    public List<string> Formulas { get; } = [];

    /// <summary>The text a text rule looks for, or null.</summary>
    public string? Text { get; set; }

    /// <summary>
    /// The rule's place among the sheet's rules: 1 is checked first. Zero lets the writer give it the next free place after
    /// the rules there are.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>Whether no rule after this one applies to a cell this rule formats.</summary>
    public bool StopIfTrue { get; set; }

    /// <summary>
    /// The style the rule applies, or null for a rule that draws (colour scale, data bar, icon set). Only what differs from
    /// <see cref="CellStyle.Default"/> applies: font colour, bold, italic, underline and strikethrough, the fill, the borders
    /// and the number format.
    /// </summary>
    public CellStyle? Style { get; set; }

    /// <summary>The file's cfRule element, for the attributes and children the model does not hold.</summary>
    internal XElement? Source { get; set; }

    /// <summary>The style index the file gave and the style it read there, kept while the style is the same.</summary>
    internal (int Index, CellStyle? Style)? ReadStyle { get; set; }

    /// <summary>The colour scale, data bar or icon set child a rule made in code writes.</summary>
    internal XElement? Drawing { get; set; }

    /// <summary>A rule for cells for which a formula, written for the top-left cell of the range, is true.</summary>
    public static ConditionalFormatRule Expression(string formula, CellStyle style)
    {
        ArgumentException.ThrowIfNullOrEmpty(formula);
        ArgumentNullException.ThrowIfNull(style);
        ConditionalFormatRule rule = new(ConditionalFormatType.Expression) { Style = style };
        rule.Formulas.Add(formula);
        return rule;
    }

    /// <summary>A rule for cells whose value compares with one formula, or two for between and not between.</summary>
    public static ConditionalFormatRule CellIs(ComparisonOperator comparison, string formula1, CellStyle style, string? formula2 = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(formula1);
        ArgumentNullException.ThrowIfNull(style);
        ConditionalFormatRule rule = new(ConditionalFormatType.CellIs) { Operator = comparison, Style = style };
        rule.Formulas.Add(formula1);
        if (formula2 is not null)
        {
            rule.Formulas.Add(formula2);
        }

        return rule;
    }

    /// <summary>A rule that shades cells from one colour at the lowest value to another at the highest.</summary>
    public static ConditionalFormatRule ColorScale(CellColor lowest, CellColor highest) =>
        Scale([("min", null, lowest), ("max", null, highest)]);

    /// <summary>A rule that shades cells along three colours: lowest, the 50th percentile and highest.</summary>
    public static ConditionalFormatRule ColorScale(CellColor lowest, CellColor middle, CellColor highest) =>
        Scale([("min", null, lowest), ("percentile", "50", middle), ("max", null, highest)]);

    /// <summary>A rule that draws a bar of a colour in each cell, from the lowest value to the highest.</summary>
    public static ConditionalFormatRule DataBar(CellColor color)
    {
        XNamespace ns = Xlsx.XlsxNames.Main;
        return new ConditionalFormatRule(ConditionalFormatType.DataBar)
        {
            Drawing = new XElement(ns + "dataBar",
                new XElement(ns + "cfvo", new XAttribute("type", "min")),
                new XElement(ns + "cfvo", new XAttribute("type", "max")),
                Xlsx.XlsxStyles.WriteColor("color", color)),
        };
    }

    /// <summary>
    /// A rule that draws one of Excel's icon sets, such as <c>3TrafficLights1</c>, <c>3Arrows</c> or <c>5Rating</c>, with
    /// the icons spread evenly over the values by percent.
    /// </summary>
    /// <exception cref="ArgumentException">The name does not start with 3, 4 or 5.</exception>
    public static ConditionalFormatRule IconSet(string iconSet = "3TrafficLights1")
    {
        ArgumentException.ThrowIfNullOrEmpty(iconSet);
        int count = iconSet[0] - '0';
        if (count is < 3 or > 5)
        {
            throw new ArgumentException("An icon set's name starts with its icon count, 3, 4 or 5, as in 3TrafficLights1.", nameof(iconSet));
        }

        XNamespace ns = Xlsx.XlsxNames.Main;
        XElement set = new(ns + "iconSet", new XAttribute("iconSet", iconSet));
        for (int i = 0; i < count; i++)
        {
            set.Add(new XElement(ns + "cfvo", new XAttribute("type", "percent"), new XAttribute("val", Xlsx.XlsxNames.Integer(i * 100 / count))));
        }

        return new ConditionalFormatRule(ConditionalFormatType.IconSet) { Drawing = set };
    }

    private static ConditionalFormatRule Scale((string Type, string? Value, CellColor Color)[] stops)
    {
        XNamespace ns = Xlsx.XlsxNames.Main;
        XElement scale = new(ns + "colorScale");
        foreach ((string type, string? value, CellColor _) in stops)
        {
            scale.Add(new XElement(ns + "cfvo", new XAttribute("type", type), value is null ? null : new XAttribute("val", value)));
        }

        foreach ((string _, string? _, CellColor color) in stops)
        {
            scale.Add(Xlsx.XlsxStyles.WriteColor("color", color));
        }

        return new ConditionalFormatRule(ConditionalFormatType.ColorScale) { Drawing = scale };
    }
}

/// <summary>Conditional formatting on one or more ranges: the rules that apply to them.</summary>
public sealed class ConditionalFormat
{
    internal ConditionalFormat(IEnumerable<RangeAddress> ranges)
    {
        Ranges = [.. ranges];
    }

    /// <summary>The cells the rules apply to.</summary>
    public List<RangeAddress> Ranges { get; }

    /// <summary>The rules, each with its own priority.</summary>
    public List<ConditionalFormatRule> Rules { get; } = [];

    /// <summary>The file's conditionalFormatting element, for the attributes the model does not hold.</summary>
    internal XElement? Source { get; set; }
}

/// <summary>A sheet's conditional formatting.</summary>
public sealed class ConditionalFormatCollection : IReadOnlyList<ConditionalFormat>
{
    private readonly List<ConditionalFormat> formats = [];

    internal ConditionalFormatCollection()
    {
    }

    /// <summary>How many formatted ranges there are.</summary>
    public int Count => formats.Count;

    /// <summary>The conditional formatting at a position.</summary>
    public ConditionalFormat this[int index] => formats[index];

    /// <summary>Adds rules for a range.</summary>
    /// <exception cref="ArgumentException">No rule is given.</exception>
    public ConditionalFormat Add(RangeAddress range, params ConditionalFormatRule[] rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Length == 0)
        {
            throw new ArgumentException("Give at least one rule.", nameof(rules));
        }

        ConditionalFormat format = new([range]);
        format.Rules.AddRange(rules);
        formats.Add(format);
        return format;
    }

    /// <summary>Adds rules for a range given in A1 form.</summary>
    /// <exception cref="FormatException">The range is not an A1 reference.</exception>
    /// <exception cref="ArgumentException">No rule is given.</exception>
    public ConditionalFormat Add(string range, params ConditionalFormatRule[] rules) => Add(RangeAddress.Parse(range), rules);

    /// <summary>Removes conditional formatting and its rules.</summary>
    /// <returns>Whether it was on the sheet.</returns>
    public bool Remove(ConditionalFormat format) => formats.Remove(format);

    /// <summary>Removes every rule.</summary>
    public void Clear() => formats.Clear();

    /// <inheritdoc/>
    public IEnumerator<ConditionalFormat> GetEnumerator() => formats.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void AddRead(ConditionalFormat format) => formats.Add(format);
}
