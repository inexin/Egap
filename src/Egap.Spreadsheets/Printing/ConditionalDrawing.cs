using System.Numerics;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Pixels;

using Egap.Spreadsheets.Xlsx;

namespace Egap.Spreadsheets.Printing;

/// <summary>
/// Conditional formatting on the printed page (ECMA-376 Part 1, 18.3.1.10): the style the rules give a cell, colour scales
/// as its fill, and the data bars and icons drawn in it, as <see cref="ConditionalFormatEvaluation"/> works them out.
/// </summary>
internal sealed class ConditionalDrawing
{
    private readonly ConditionalFormatEvaluation evaluation;
    private readonly SheetColors colors;

    public ConditionalDrawing(Worksheet sheet, SheetColors colors)
    {
        evaluation = new ConditionalFormatEvaluation(sheet);
        this.colors = colors;
    }

    /// <summary>A cell's style with the rules that apply to it on it.</summary>
    public CellStyle Style(int row, int column, CellStyle style) => evaluation.Evaluate(row, column, style).Style;

    /// <summary>Whether the cell's value shows: a data bar or icon set with showValue off hides it.</summary>
    public bool ShowsValue(int row, int column) => Format(row, column).ShowsValue;

    public void DrawBar(Canvas canvas, int row, int column, RectF cell)
    {
        ConditionalCellFormat format = Format(row, column);
        if (format.DataBar is not { } bar || format.BarElement is not { } element)
        {
            return;
        }

        double minimum = XlsxNames.Int((string?)element.Attribute("minLength"), 10) / 100.0;
        double maximum = XlsxNames.Int((string?)element.Attribute("maxLength"), 90) / 100.0;
        float pad = (float)(2 * SheetFonts.PointsPerPixel);
        float length = (float)((cell.Width - (2 * pad)) * (minimum + (bar.Fraction * (maximum - minimum))));
        Rgba8 color = colors.Resolve(bar.Color, new Rgba8(0x63, 0x8E, 0xC6, 255));
        canvas.FillRect(new RectF(cell.X + pad, cell.Y + pad, Math.Max(0, length), Math.Max(0, cell.Height - (2 * pad))), color);
    }

    public void DrawIcon(Canvas canvas, int row, int column, RectF cell)
    {
        if (Format(row, column).Icon is not { } icon)
        {
            return;
        }

        int count = icon.IconSet.Length > 0 && icon.IconSet[0] is >= '3' and <= '5' ? icon.IconSet[0] - '0' : 3;
        float size = Math.Min((float)(16 * SheetFonts.PointsPerPixel), cell.Height - 1);
        Vector2 center = new(cell.X + 1.5f + (size / 2), cell.Y + (cell.Height / 2));
        DrawIcon(canvas, icon.IconSet, icon.Index, count, center, size / 2);
    }

    private ConditionalCellFormat Format(int row, int column) => evaluation.Evaluate(row, column, evaluation.BaseStyle(row, column));

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

    private static Rgba8 Mix(Rgba8 a, Rgba8 b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return new Rgba8((byte)Math.Round(a.R + ((b.R - a.R) * t)), (byte)Math.Round(a.G + ((b.G - a.G) * t)), (byte)Math.Round(a.B + ((b.B - a.B) * t)), 255);
    }
}
