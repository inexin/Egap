using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Egap.Documents.NumberFormats;

namespace Egap.Spreadsheets;

/// <summary>
/// An Excel number format code, parsed: up to four sections (positive, negative, zero, text) with colours, conditions,
/// digit placeholders, thousands separators and scaling, percent, scientific notation, fractions, dates and times, elapsed
/// times, text and literal characters, and locale tags such as <c>[$-414]</c>. <see cref="Format"/> shows a value as Excel
/// shows it in a cell, for a given culture; <see cref="Cell.Text"/> uses it.
/// </summary>
/// <example>
/// <code>
/// NumberFormatCode code = NumberFormatCode.Parse("#,##0.00;[Red]-#,##0.00");
/// string text = code.Format(-1234.567);                 // "-1,234.57"
/// </code>
/// </example>
public sealed class NumberFormatCode
{
    private static readonly ConcurrentDictionary<string, NumberFormatCode> Cache = new(StringComparer.Ordinal);

    private readonly FormatSection[] sections;

    private NumberFormatCode(string code, FormatSection[] sections)
    {
        Code = code;
        this.sections = sections;
    }

    /// <summary>The format code as given.</summary>
    public string Code { get; }

    /// <summary>Whether the code shows numbers as dates or times: its first section has date or time parts.</summary>
    public bool IsDateTime => sections[0].IsDate;

    /// <summary>Whether the code shows numbers as times only: its first section has time parts and no year, month or day.</summary>
    internal bool IsTimeOnly => sections[0].IsDate && !sections[0].Tokens.Any(t => t.Kind is TokenKind.Year or TokenKind.Month or TokenKind.Day);

    /// <summary>The <c>General</c> format, which shows numbers in up to 11 characters and text as it is.</summary>
    public static NumberFormatCode General { get; } = Parse("General");

    /// <summary>
    /// Parses a format code. Parsing never fails: a part Excel would refuse is shown as literal text, as a reader of
    /// someone else's file wants.
    /// </summary>
    /// <exception cref="ArgumentNullException">The code is null.</exception>
    public static NumberFormatCode Parse(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return new NumberFormatCode(code, FormatParser.Parse(code.Length == 0 ? "General" : code));
    }

    /// <summary>The parsed code, kept once per code for the cells that share it.</summary>
    internal static NumberFormatCode Get(string code)
    {
        if (Cache.TryGetValue(code, out NumberFormatCode? found))
        {
            return found;
        }

        if (Cache.Count > 4096)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(code, Parse);
    }

    /// <summary>
    /// The value as Excel shows it with this format: numbers through the section that applies, text through the text section,
    /// TRUE and FALSE, and errors as their text. Separators, month and day names and AM and PM come from
    /// <paramref name="culture"/> (the invariant culture, which matches Excel in English, when null), or from the locale tag a
    /// section gives. A date before the start of the date system shows as <c>#</c> signs, as Excel shows it.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="culture">The culture to show the value in, or null for the invariant culture.</param>
    /// <param name="date1904">Whether dates count from 1904-01-01 rather than 1900-01-01.</param>
    public string Format(CellValue value, CultureInfo? culture = null, bool date1904 = false)
    {
        string text = FormatWithFill(value, culture, date1904);
        int mark = text.IndexOf(FillMark, StringComparison.Ordinal);
        if (mark < 0)
        {
            return text;
        }

        StringBuilder plain = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == FillMark)
            {
                i++;
            }
            else
            {
                plain.Append(text[i]);
            }
        }

        return plain.ToString();
    }

    /// <summary>
    /// Marks where a <c>*</c> fill goes in <see cref="FormatWithFill"/>'s text: the mark, then the character that repeats to
    /// fill the cell's width.
    /// </summary>
    internal const char FillMark = '\u0001';

    /// <summary>The value as <see cref="Format"/> shows it, with each <c>*</c> fill left in as <see cref="FillMark"/> and its character.</summary>
    internal string FormatWithFill(CellValue value, CultureInfo? culture, bool date1904)
    {
        culture ??= CultureInfo.InvariantCulture;
        switch (value.Kind)
        {
            case CellValueKind.Empty:
                return "";
            case CellValueKind.Boolean:
                return value.Boolean ? "TRUE" : "FALSE";
            case CellValueKind.Error:
                return CellValue.ErrorText(value.Error);
            case CellValueKind.Text:
                return FormatText(value.Text, culture);
            default:
                // The culture's short date stands in for built-in 14 and 22; formatting it does not look for a stand-in again,
                // so a culture whose short date is the same code cannot loop.
                NumberFormatCode shown = SystemDate(culture) is { } local ? Get(local) : this;
                return shown.FormatNumber(value.Number, culture, date1904);
        }
    }

    private string FormatNumber(double value, CultureInfo culture, bool date1904)
    {
        (FormatSection? section, bool keepSign) = FormatSections.Choose(sections, value);
        if (section is null)
        {
            return new string('#', 10);
        }

        double number = keepSign ? value : Math.Abs(value);
        return NumberFormatter.Format(section, number, section.Culture ?? culture, date1904);
    }

    /// <summary>
    /// The culture's own code for the two built-in formats that follow the system's short date, as Excel's do: <c>mm-dd-yy</c>
    /// (built-in 14) and <c>m/d/yy h:mm</c> (built-in 22). The invariant culture gives Excel's English <c>m/d/yyyy</c>.
    /// </summary>
    private string? SystemDate(CultureInfo culture)
    {
        bool withTime = Code == "m/d/yy h:mm";
        if (Code != "mm-dd-yy" && !withTime)
        {
            return null;
        }

        string date = culture.Equals(CultureInfo.InvariantCulture) ? "m/d/yyyy" : DateCode.FromDotNet(culture.DateTimeFormat.ShortDatePattern);
        return withTime ? date + " h:mm" : date;
    }

    /// <summary>The colour the section that shows <paramref name="value"/> gives, such as red for <c>[Red]</c>, or null for none.</summary>
    public CellColor? GetColor(CellValue value)
    {
        FormatSection? section = value.Kind switch
        {
            CellValueKind.Number => FormatSections.Choose(sections, value.Number).Section,
            CellValueKind.Text => TextSection(),
            _ => null,
        };
        return section?.Color is { } color ? (color.Index is { } index ? CellColor.FromIndex(index) : CellColor.FromRgb(color.Red, color.Green, color.Blue)) : null;
    }

    /// <summary>The code.</summary>
    public override string ToString() => Code;

    private string FormatText(string text, CultureInfo culture)
    {
        if (TextSection() is not { } section)
        {
            return text;
        }

        StringBuilder output = new();
        foreach (FormatToken token in section.Tokens)
        {
            switch (token.Kind)
            {
                case TokenKind.Text:
                    output.Append(text);
                    break;
                case TokenKind.Literal:
                    output.Append(token.Text);
                    break;
                case TokenKind.Space:
                    output.Append(' ');
                    break;
                case TokenKind.Fill:
                    output.Append(FillMark).Append(token.Text.Length > 0 ? token.Text[0] : ' ');
                    break;
                case TokenKind.General:
                    output.Append(text);
                    break;
            }
        }

        _ = culture;
        return output.ToString();
    }

    /// <summary>The section text shows through: the fourth, or a lone section with an <c>@</c>; null to show text as it is.</summary>
    private FormatSection? TextSection()
    {
        if (sections.Length >= 4)
        {
            return sections[3];
        }

        return sections.Length == 1 && sections[0].HasText ? sections[0] : null;
    }
}
