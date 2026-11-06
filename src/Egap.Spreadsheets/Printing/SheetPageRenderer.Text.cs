using System.Globalization;
using System.Numerics;
using System.Text;

using Egami.Drawing;
using Egami.Fonts.Layout;
using Egami.Pixels;


namespace Egap.Spreadsheets.Printing;

/// <summary>Cell text and the header and footer.</summary>
internal sealed partial class SheetPageRenderer
{
    private void DrawTexts(Segment rowSegment, Segment columnSegment)
    {
        RectF segment = Rect(columnSegment.Start, rowSegment.Start, columnSegment.Size, rowSegment.Size);
        ForEachCell(rowSegment, columnSegment, (r, c, rect, merged) =>
        {
            CellValue value = sheet.GetValue(r, c);
            if (value.IsEmpty)
            {
                return;
            }

            CellStyle style = StyleAt(r, c);
            if (sheet.ConditionalFormats.Count > 0 && !Conditional().ShowsValue(r, c))
            {
                return;
            }

            NumberFormatCode format = NumberFormatCode.Get(style.NumberFormat);
            string text = format.FormatWithFill(value, book.Culture, book.Date1904);
            if (!sheet.View.ShowZeros && value.Kind == CellValueKind.Number && value.Number == 0)
            {
                return;
            }

            if (text.Length == 0)
            {
                return;
            }

            Rgba8 color = format.GetColor(value) is { } formatColor ? colors.Resolve(formatColor, Black) : colors.Resolve(style.Font.Color, Black);
            Begin(t => t.BeginCell(page, r, c, merged));
            DrawCellText(r, c, rect, merged, value, text, style, color, segment, columnSegment);
            End();
        });

        DrawRunningText(rowSegment, columnSegment, segment);
    }

    /// <summary>The text of cells on the pages to the left that runs on over the empty cells at the start of this one.</summary>
    private void DrawRunningText(Segment rowSegment, Segment columnSegment, RectF segment)
    {
        double y = rowSegment.Start;
        for (int r = rowSegment.First; r <= rowSegment.Last; r++)
        {
            double height = grid.Row(r);
            if (height > 0 && IsFree(r, columnSegment.First) && LastBefore(r, columnSegment.First) is var c and >= 0 && grid.Column(c) > 0)
            {
                CellValue value = sheet.GetValue(r, c);
                CellStyle style = StyleAt(r, c);
                if (value.Kind == CellValueKind.Text && !style.Alignment.WrapText && (sheet.ConditionalFormats.Count == 0 || Conditional().ShowsValue(r, c)))
                {
                    NumberFormatCode format = NumberFormatCode.Get(style.NumberFormat);
                    string text = format.FormatWithFill(value, book.Culture, book.Date1904);
                    Rgba8 color = format.GetColor(value) is { } formatColor ? colors.Resolve(formatColor, Black) : colors.Resolve(style.Font.Color, Black);
                    double before = grid.Columns(c, columnSegment.First - 1);
                    Begin(null);
                    DrawCellText(r, c, Rect(columnSegment.Start - before, y, grid.Column(c), height), null, value, text, style, color, segment, columnSegment);
                    End();
                }
            }

            y += Math.Max(0, height);
        }
    }

    private bool IsFree(int row, int column) =>
        sheet.GetValue(row, column).IsEmpty && !sheet.HasFormula(row, column) && (merges.Count == 0 || MergeAt(row, column) is null);

    /// <summary>The nearest cell left of a column with something in it, or -1 when there is none or it is a merge.</summary>
    private int LastBefore(int row, int column)
    {
        for (int c = column - 1; c >= 0; c--)
        {
            if (!IsFree(row, c))
            {
                return merges.Count > 0 && MergeAt(row, c) is not null ? -1 : c;
            }
        }

        return -1;
    }

    private void DrawCellText(int row, int column, RectF cell, RangeAddress? merged, CellValue value, string text, CellStyle style, Rgba8 color, RectF segment, Segment columnSegment)
    {
        CellAlignment alignment = style.Alignment;
        bool wrap = alignment.WrapText || alignment.Horizontal is CellHorizontalAlignment.Justify or CellHorizontalAlignment.Distributed
            || alignment.Vertical is CellVerticalAlignment.Justify or CellVerticalAlignment.Distributed;
        int rotation = alignment.TextRotation;
        CellHorizontalAlignment horizontal = alignment.Horizontal;
        if (horizontal == CellHorizontalAlignment.General)
        {
            horizontal = value.Kind switch
            {
                CellValueKind.Number => CellHorizontalAlignment.Right,
                CellValueKind.Boolean or CellValueKind.Error => CellHorizontalAlignment.Center,
                _ => CellHorizontalAlignment.Left,
            };
        }

        double indent = alignment.Indent * fonts.MaxDigitWidth * 9.0 / 7 * SheetFonts.PointsPerPixel;
        double innerLeft = cell.X + Padding + (horizontal is CellHorizontalAlignment.Left or CellHorizontalAlignment.Distributed ? indent : 0);
        double innerRight = cell.X + cell.Width - Padding - (horizontal == CellHorizontalAlignment.Right ? indent : 0);
        double innerWidth = Math.Max(0, innerRight - innerLeft);
        double scale = 1;
        TextStyle textStyle = fonts.TextStyle(style.Font, color);

        if (rotation != 0)
        {
            DrawRotated(cell, Strip(text), textStyle, rotation, horizontal, alignment.Vertical, segment);
            return;
        }

        int fill = text.IndexOf(NumberFormatCode.FillMark, StringComparison.Ordinal);
        bool isNumber = value.Kind == CellValueKind.Number;
        if (!wrap)
        {
            text = text.Replace("\r\n", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        }

        string shown = Strip(text);
        TextLayout layout = Layout(shown, textStyle, wrap ? innerWidth : 1e6, wrap ? Align(horizontal) : TextAlign.Left);
        if (!wrap && layout.LongestLine > innerWidth + 0.01)
        {
            if (alignment.ShrinkToFit && innerWidth > 0)
            {
                scale = innerWidth / layout.LongestLine;
                textStyle = fonts.TextStyle(style.Font, color, scale);
                layout = Layout(shown, textStyle, 1e6, TextAlign.Left);
            }
            else if (isNumber && merged is null)
            {
                // A number that does not fit shows as #: General first gives up digits, as Excel's does.
                shown = Shorter(value.Number, style.NumberFormat, textStyle, innerWidth) ?? Hashes(textStyle, innerWidth);
                layout = Layout(shown, textStyle, 1e6, TextAlign.Left);
                fill = -1;
            }
        }

        if (fill >= 0 && !wrap)
        {
            shown = Filled(text, fill, textStyle, innerWidth);
            layout = Layout(shown, textStyle, 1e6, TextAlign.Left);
            horizontal = CellHorizontalAlignment.Left;
        }

        if (horizontal == CellHorizontalAlignment.Fill && !wrap && layout.LongestLine > 0)
        {
            int copies = Math.Max(1, (int)(innerWidth / layout.LongestLine));
            shown = string.Concat(Enumerable.Repeat(shown, copies));
            layout = Layout(shown, textStyle, 1e6, TextAlign.Left);
            horizontal = CellHorizontalAlignment.Left;
        }

        // The room the text may take: the cell, or for unwrapped text the empty cells beside it, as far as the page goes.
        double clipLeft = cell.X, clipRight = cell.X + cell.Width;
        double width = layout.LongestLine;
        double x;
        if (wrap)
        {
            x = innerLeft;
        }
        else
        {
            x = horizontal switch
            {
                CellHorizontalAlignment.Right => innerRight - width,
                CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous => innerLeft + ((innerWidth - width) / 2),
                _ => innerLeft,
            };
            if (merged is null && !isNumber && width > innerWidth)
            {
                if (horizontal is CellHorizontalAlignment.Left or CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous or CellHorizontalAlignment.Justify or CellHorizontalAlignment.Distributed)
                {
                    clipRight = Overflow(row, column, +1, cell.X + cell.Width, x + width + Padding, columnSegment);
                }

                if (horizontal is CellHorizontalAlignment.Right or CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous)
                {
                    clipLeft = Overflow(row, column, -1, cell.X, x - Padding, columnSegment);
                }
            }
        }

        double innerHeight = cell.Height;
        double y = alignment.Vertical switch
        {
            CellVerticalAlignment.Top or CellVerticalAlignment.Justify or CellVerticalAlignment.Distributed => cell.Y + Padding / 2,
            CellVerticalAlignment.Center => cell.Y + ((innerHeight - layout.Height) / 2),
            _ => cell.Y + innerHeight - layout.Height - (Padding / 2),
        };

        canvas.Save();
        RectF clip = RectF.FromEdges((float)Math.Max(clipLeft, segment.X), Math.Max(cell.Y, segment.Y),
            (float)Math.Min(clipRight, segment.X + segment.Width), Math.Min(cell.Y + cell.Height, segment.Y + segment.Height));
        if (clip.Width > 0 && clip.Height > 0)
        {
            canvas.ClipRect(clip);
            canvas.DrawTextLayout(layout, new Vector2((float)x, (float)y));
        }

        canvas.Restore();
    }

    /// <summary>How far unwrapped text may run past its cell: over empty cells until it has room, up to the page's edge.</summary>
    private double Overflow(int row, int column, int step, double edge, double needed, Segment columnSegment)
    {
        int c = column + step;
        while ((step > 0 ? c <= columnSegment.Last && edge < needed : c >= columnSegment.First && edge > needed))
        {
            if (!sheet.GetValue(row, c).IsEmpty || sheet.HasFormula(row, c) || (merges.Count > 0 && MergeAt(row, c) is not null))
            {
                break;
            }

            edge += step * grid.Column(c);
            c += step;
        }

        return edge;
    }

    private TextLayout Layout(string text, TextStyle style, double width, TextAlign align)
    {
        ParagraphStyle paragraph = new() { DefaultStyle = style, Align = align, Fallback = fonts.Fallback };
        return Paragraph.Create(text, paragraph).Layout((float)Math.Max(1, width));
    }

    private static TextAlign Align(CellHorizontalAlignment horizontal) => horizontal switch
    {
        CellHorizontalAlignment.Right => TextAlign.Right,
        CellHorizontalAlignment.Center or CellHorizontalAlignment.CenterContinuous => TextAlign.Center,
        CellHorizontalAlignment.Justify or CellHorizontalAlignment.Distributed => TextAlign.Justify,
        _ => TextAlign.Left,
    };

    /// <summary>The text without its fill marks.</summary>
    private static string Strip(string text)
    {
        int mark = text.IndexOf(NumberFormatCode.FillMark, StringComparison.Ordinal);
        return mark < 0 ? text : text.Remove(mark, Math.Min(2, text.Length - mark)).Replace(NumberFormatCode.FillMark.ToString(), "", StringComparison.Ordinal);
    }

    /// <summary>The text with its <c>*</c> fill repeated to take up the room the rest leaves, as accounting formats show.</summary>
    private string Filled(string text, int mark, TextStyle style, double width)
    {
        string before = text[..mark];
        char repeat = mark + 1 < text.Length ? text[mark + 1] : ' ';
        string after = Strip(text[Math.Min(text.Length, mark + 2)..]);
        double used = Measure(before + after, style);
        double one = Measure(repeat.ToString(), style);
        int count = one > 0 ? Math.Max(0, (int)((width - used) / one)) : 0;
        return before + new string(repeat, count) + after;
    }

    private double Measure(string text, TextStyle style) => text.Length == 0 ? 0 : Layout(text, style, 1e6, TextAlign.Left).LongestLine;

    /// <summary>
    /// A General number with fewer digits that fits <paramref name="width"/>, as Excel shortens it to the column; null for
    /// another format or when no form fits.
    /// </summary>
    private string? Shorter(double number, string format, TextStyle style, double width)
    {
        if (!string.Equals(format, "General", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        NumberFormatInfo numbers = book.Culture.NumberFormat;
        for (int digits = 10; digits >= 1; digits--)
        {
            double abs = Math.Abs(number);
            string text;
            if (abs != 0 && (abs >= 1e11 || abs < 1e-4))
            {
                text = number.ToString("0." + new string('#', Math.Max(0, digits - 1)) + "E+00", CultureInfo.InvariantCulture);
            }
            else
            {
                int whole = abs < 1 ? 1 : (int)Math.Floor(Math.Log10(abs)) + 1;
                if (whole > digits)
                {
                    continue;
                }

                text = Math.Round(number, Math.Max(0, digits - whole), MidpointRounding.AwayFromZero)
                    .ToString("0." + new string('#', Math.Max(0, digits - whole)), CultureInfo.InvariantCulture);
            }

            text = text.Replace(".", numbers.NumberDecimalSeparator, StringComparison.Ordinal).Replace("-", numbers.NegativeSign, StringComparison.Ordinal);
            if (Measure(text, style) <= width)
            {
                return text;
            }
        }

        return null;
    }

    private string Hashes(TextStyle style, double width)
    {
        double one = Measure("#", style);
        int count = one > 0 ? Math.Max(1, (int)(width / one)) : 1;
        return new string('#', count);
    }

    /// <summary>
    /// Text turned by <paramref name="rotation"/>: 1 to 90 degrees up, 91 to 180 down by the amount past 90, 255 for letters
    /// stacked top to bottom.
    /// </summary>
    private void DrawRotated(RectF cell, string text, TextStyle style, int rotation, CellHorizontalAlignment horizontal, CellVerticalAlignment vertical, RectF segment)
    {
        bool stacked = rotation == 255;
        if (stacked)
        {
            StringBuilder lines = new();
            foreach (char ch in text)
            {
                lines.Append(ch).Append('\n');
            }

            text = lines.ToString().TrimEnd('\n');
        }

        TextLayout layout = Layout(text, style, stacked ? 1e6 : 1e6, stacked ? TextAlign.Center : TextAlign.Left);
        double degrees = stacked ? 0 : rotation <= 90 ? rotation : -(rotation - 90);
        double radians = degrees * Math.PI / 180;
        double w = stacked ? layout.LongestLine : layout.LongestLine;
        double h = layout.Height;
        double boxWidth = Math.Abs(w * Math.Cos(radians)) + Math.Abs(h * Math.Sin(radians));
        double boxHeight = Math.Abs(w * Math.Sin(radians)) + Math.Abs(h * Math.Cos(radians));
        double cx = horizontal switch
        {
            CellHorizontalAlignment.Left => cell.X + Padding + (boxWidth / 2),
            CellHorizontalAlignment.Right => cell.X + cell.Width - Padding - (boxWidth / 2),
            _ => cell.X + (cell.Width / 2),
        };
        double cy = vertical switch
        {
            CellVerticalAlignment.Top => cell.Y + Padding + (boxHeight / 2),
            CellVerticalAlignment.Center => cell.Y + (cell.Height / 2),
            _ => cell.Y + cell.Height - Padding - (boxHeight / 2),
        };

        canvas.Save();
        RectF clip = RectF.FromEdges(Math.Max(cell.X, segment.X), Math.Max(cell.Y, segment.Y),
            Math.Min(cell.X + cell.Width, segment.X + segment.Width), Math.Min(cell.Y + cell.Height, segment.Y + segment.Height));
        if (clip.Width > 0 && clip.Height > 0)
        {
            canvas.ClipRect(clip);
            canvas.Translate((float)cx, (float)cy);
            canvas.Rotate((float)-degrees);
            canvas.DrawTextLayout(layout, new Vector2((float)(-w / 2), (float)(-h / 2)));
        }

        canvas.Restore();
    }

    private void DrawHeaderFooter()
    {
        PageSetup setup = sheet.PageSetup;
        System.Xml.Linq.XElement? source = setup.SourceHeaderFooter;
        bool differentFirst = Flag(source, "differentFirst", false);
        bool differentOddEven = Flag(source, "differentOddEven", false);
        bool scaleWithDoc = Flag(source, "scaleWithDoc", true);
        bool alignWithMargins = Flag(source, "alignWithMargins", true);
        string? header = setup.Header, footer = setup.Footer;
        if (differentFirst && page.Index == 0)
        {
            header = Child(source, "firstHeader");
            footer = Child(source, "firstFooter");
        }
        else if (differentOddEven && page.Number % 2 == 0)
        {
            header = Child(source, "evenHeader");
            footer = Child(source, "evenFooter");
        }

        double scale = scaleWithDoc ? paginator.Scale : 1;
        double left = alignWithMargins ? setup.LeftMargin * 72 : 0.25 * 72;
        double right = page.Width - (alignWithMargins ? setup.RightMargin * 72 : 0.25 * 72);
        DrawBand(HeaderFooterCode.Parse(header), left, right, setup.HeaderMargin * 72, top: true, scale);
        DrawBand(HeaderFooterCode.Parse(footer), left, right, page.Height - (setup.FooterMargin * 72), top: false, scale);

        static bool Flag(System.Xml.Linq.XElement? element, string name, bool fallback) =>
            Xlsx.XlsxNames.Bool((string?)element?.Attribute(name), fallback);

        static string? Child(System.Xml.Linq.XElement? element, string name) =>
            (string?)element?.Elements().FirstOrDefault(e => e.Name.LocalName == name);
    }

    /// <summary>Draws a header's or footer's three sections, from <paramref name="y"/> down for a header or up to it for a footer.</summary>
    private void DrawBand(HeaderFooterCode code, double left, double right, double y, bool top, double scale)
    {
        if (code.IsEmpty)
        {
            return;
        }

        double width = Math.Max(1, right - left);
        TextAlign[] aligns = [TextAlign.Left, TextAlign.Center, TextAlign.Right];
        for (int s = 0; s < 3; s++)
        {
            if (code.Sections[s].Count == 0)
            {
                continue;
            }

            RichText text = Build(code.Sections[s], scale);
            ParagraphStyle paragraph = new() { DefaultStyle = fonts.TextStyle(fonts.NormalFont, Black, scale), Align = aligns[s], Fallback = fonts.Fallback };
            TextLayout layout = Paragraph.Create(text, paragraph).Layout((float)width);
            double ty = top ? y : y - layout.Height;
            canvas.DrawTextLayout(layout, new Vector2((float)left, (float)ty));
        }
    }

    private RichText Build(IReadOnlyList<HeaderFooterRun> runs, double scale)
    {
        RichTextBuilder builder = new();
        CellFont normal = fonts.NormalFont;
        foreach (HeaderFooterRun run in runs)
        {
            HeaderFooterFormat format = run.Format;
            CellFont font = normal with
            {
                // A cell font's name is 1 to 31 characters; a header code may name any, and a longer one prints in the normal font.
                Name = format.Font is { Length: >= 1 and <= 31 } name ? name : normal.Name,
                Size = format.Size ?? normal.Size,
                Bold = format.Bold,
                Italic = format.Italic,
                Underline = format.Underline,
                Strikethrough = format.Strikethrough,
                VerticalPosition = format.Position,
            };
            Rgba8 color = format.Color is { } c ? colors.Resolve(c, Black) : Black;
            builder.Push(fonts.TextStyle(font, color, scale));
            builder.Append(Field(run));
            builder.Pop();
        }

        return builder.ToRichText();
    }

    private string Field(HeaderFooterRun run)
    {
        CultureInfo culture = book.Culture;
        string? path = page.Context.FilePath;
        return run.Field switch
        {
            HeaderFooterField.Page => (page.Number + run.Offset).ToString(culture),
            HeaderFooterField.Pages => (page.PageCount + (page.Number - page.Index - 1)).ToString(culture),
            HeaderFooterField.Date => page.Context.Time.ToString(culture.DateTimeFormat.ShortDatePattern, culture),
            HeaderFooterField.Time => page.Context.Time.ToString(culture.DateTimeFormat.ShortTimePattern, culture),
            HeaderFooterField.File => path is null ? "" : System.IO.Path.GetFileName(path),
            HeaderFooterField.Path => path is null ? "" : (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? "") + System.IO.Path.DirectorySeparatorChar,
            HeaderFooterField.Sheet => sheet.Name,
            HeaderFooterField.Picture => "",
            _ => run.Text,
        };
    }
}
