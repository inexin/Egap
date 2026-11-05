using System.Globalization;
using System.Text;

namespace Egap.Spreadsheets.Printing;

/// <summary>A piece of a header or footer that the page fills in.</summary>
internal enum HeaderFooterField
{
    /// <summary>Plain text.</summary>
    None,

    /// <summary>The page number, <c>&amp;P</c>, with any offset written after it.</summary>
    Page,

    /// <summary>The number of pages, <c>&amp;N</c>.</summary>
    Pages,

    /// <summary>The date printed, <c>&amp;D</c>.</summary>
    Date,

    /// <summary>The time printed, <c>&amp;T</c>.</summary>
    Time,

    /// <summary>The workbook's folder, <c>&amp;Z</c>.</summary>
    Path,

    /// <summary>The workbook's file name, <c>&amp;F</c>.</summary>
    File,

    /// <summary>The sheet's name, <c>&amp;A</c>.</summary>
    Sheet,

    /// <summary>A picture, <c>&amp;G</c>: its drawing is in the sheet's VML part, not drawn here.</summary>
    Picture,
}

/// <summary>How a run of a header or footer looks; null members take the workbook's normal font.</summary>
internal readonly record struct HeaderFooterFormat(
    string? Font, double? Size, bool Bold, bool Italic, CellUnderline Underline, bool Strikethrough,
    CellVerticalTextPosition Position, CellColor? Color);

/// <summary>A run of a header or footer: text or a field, with its format.</summary>
internal readonly record struct HeaderFooterRun(string Text, HeaderFooterField Field, int Offset, HeaderFooterFormat Format);

/// <summary>
/// A header or footer in Excel's code (ECMA-376 Part 1, 18.3.1.46 and the header and footer codes Excel documents): the left,
/// centre and right sections, each a list of runs.
/// </summary>
internal sealed class HeaderFooterCode
{
    private HeaderFooterCode(List<HeaderFooterRun>[] sections)
    {
        Sections = sections;
    }

    /// <summary>The left, centre and right sections.</summary>
    public IReadOnlyList<HeaderFooterRun>[] Sections { get; }

    /// <summary>Whether no section has anything to print.</summary>
    public bool IsEmpty => Sections.All(s => s.Count == 0);

    /// <summary>The code read; text before any section code belongs to the centre, as in Excel.</summary>
    public static HeaderFooterCode Parse(string? code)
    {
        List<HeaderFooterRun>[] sections = [[], [], []];
        if (string.IsNullOrEmpty(code))
        {
            return new HeaderFooterCode(sections);
        }

        int section = 1;
        HeaderFooterFormat format = default;
        StringBuilder text = new();
        int i = 0;
        while (i < code.Length)
        {
            char c = code[i];
            if (c != '&' || i + 1 >= code.Length)
            {
                text.Append(c);
                i++;
                continue;
            }

            char next = code[i + 1];
            i += 2;
            switch (char.ToUpperInvariant(next))
            {
                case '&':
                    text.Append('&');
                    continue;
                case 'L':
                case 'C':
                case 'R':
                    Flush();
                    section = char.ToUpperInvariant(next) switch { 'L' => 0, 'C' => 1, _ => 2 };
                    format = default;
                    continue;
                case 'P':
                    Flush();
                    int offset = 0;
                    if (i < code.Length && code[i] is '+' or '-' && i + 1 < code.Length && char.IsAsciiDigit(code[i + 1]))
                    {
                        int sign = code[i] == '-' ? -1 : 1;
                        int start = ++i;
                        while (i < code.Length && char.IsAsciiDigit(code[i]) && i - start < 6)
                        {
                            i++;
                        }

                        offset = sign * int.Parse(code.AsSpan(start, i - start), CultureInfo.InvariantCulture);
                    }

                    sections[section].Add(new HeaderFooterRun("", HeaderFooterField.Page, offset, format));
                    continue;
                case 'N':
                    AddField(HeaderFooterField.Pages);
                    continue;
                case 'D':
                    AddField(HeaderFooterField.Date);
                    continue;
                case 'T':
                    AddField(HeaderFooterField.Time);
                    continue;
                case 'Z':
                    AddField(HeaderFooterField.Path);
                    continue;
                case 'F':
                    AddField(HeaderFooterField.File);
                    continue;
                case 'A':
                    AddField(HeaderFooterField.Sheet);
                    continue;
                case 'G':
                    AddField(HeaderFooterField.Picture);
                    continue;
                case 'B':
                    Flush();
                    format = format with { Bold = !format.Bold };
                    continue;
                case 'I':
                    Flush();
                    format = format with { Italic = !format.Italic };
                    continue;
                case 'U':
                    Flush();
                    format = format with { Underline = format.Underline == CellUnderline.Single ? CellUnderline.None : CellUnderline.Single };
                    continue;
                case 'E':
                    Flush();
                    format = format with { Underline = format.Underline == CellUnderline.Double ? CellUnderline.None : CellUnderline.Double };
                    continue;
                case 'S':
                    Flush();
                    format = format with { Strikethrough = !format.Strikethrough };
                    continue;
                case 'X':
                    Flush();
                    format = format with { Position = format.Position == CellVerticalTextPosition.Superscript ? CellVerticalTextPosition.Baseline : CellVerticalTextPosition.Superscript };
                    continue;
                case 'Y':
                    Flush();
                    format = format with { Position = format.Position == CellVerticalTextPosition.Subscript ? CellVerticalTextPosition.Baseline : CellVerticalTextPosition.Subscript };
                    continue;
                case 'O':
                case 'H':
                    // Outline and shadow: Mac-only styles Excel for Windows ignores.
                    continue;
                case '"':
                    Flush();
                    int end = code.IndexOf('"', i);
                    if (end < 0)
                    {
                        end = code.Length;
                    }

                    format = Font(format, code[i..end]);
                    i = Math.Min(end + 1, code.Length);
                    continue;
                case 'K':
                    Flush();
                    int length = Math.Min(6, code.Length - i);
                    format = format with { Color = Color(code.AsSpan(i, length)) ?? format.Color };
                    i += length;
                    continue;
                default:
                    if (char.IsAsciiDigit(next))
                    {
                        Flush();
                        int start = i - 1;
                        while (i < code.Length && char.IsAsciiDigit(code[i]) && i - start < 3)
                        {
                            i++;
                        }

                        int size = int.Parse(code.AsSpan(start, i - start), CultureInfo.InvariantCulture);
                        format = size is >= 1 and <= 409 ? format with { Size = size } : format;
                        continue;
                    }

                    // An unknown code prints as it is.
                    text.Append('&').Append(next);
                    continue;
            }
        }

        Flush();
        return new HeaderFooterCode(sections);

        void Flush()
        {
            if (text.Length > 0)
            {
                sections[section].Add(new HeaderFooterRun(text.ToString(), HeaderFooterField.None, 0, format));
                text.Clear();
            }
        }

        void AddField(HeaderFooterField field)
        {
            Flush();
            sections[section].Add(new HeaderFooterRun("", field, 0, format));
        }
    }

    /// <summary>The format after <c>&amp;"name,style"</c>: a font name, a style or both; <c>-</c> keeps the name.</summary>
    private static HeaderFooterFormat Font(HeaderFooterFormat format, string spec)
    {
        int comma = spec.IndexOf(',', StringComparison.Ordinal);
        string name = (comma < 0 ? spec : spec[..comma]).Trim();
        string style = comma < 0 ? "" : spec[(comma + 1)..].Trim();
        if (name.Length > 0 && name != "-")
        {
            format = format with { Font = name };
        }

        if (style.Length > 0)
        {
            bool bold = style.Contains("bold", StringComparison.OrdinalIgnoreCase) || style.Contains("fet", StringComparison.OrdinalIgnoreCase);
            bool italic = style.Contains("italic", StringComparison.OrdinalIgnoreCase) || style.Contains("kursiv", StringComparison.OrdinalIgnoreCase);
            format = format with { Bold = bold, Italic = italic };
        }

        return format;
    }

    /// <summary>The colour of <c>&amp;K</c>: six hex digits, or a theme colour as <c>TTSNNN</c> (index, sign, tint percent).</summary>
    private static CellColor? Color(ReadOnlySpan<char> code)
    {
        if (code.Length < 6)
        {
            return null;
        }

        if (uint.TryParse(code, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb) && !(code[2] is '+' or '-'))
        {
            return CellColor.FromArgb(0xFF000000 | rgb);
        }

        if (int.TryParse(code[..2], NumberStyles.None, CultureInfo.InvariantCulture, out int theme) && theme is >= 0 and <= 11
            && code[2] is '+' or '-' && int.TryParse(code[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int percent) && percent <= 100)
        {
            return CellColor.FromTheme(theme, (code[2] == '-' ? -percent : percent) / 100.0);
        }

        return null;
    }
}
