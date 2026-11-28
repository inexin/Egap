using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Barcodes;
using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;

namespace Egap.Documents;

/// <summary>
/// Word's DISPLAYBARCODE field (<c>DISPLAYBARCODE "data" TYPE [switches]</c>), drawn with Egami.Barcodes: the symbol a field
/// code names, its size in points and the picture layout puts in place of the field's result.
/// </summary>
/// <remarks>
/// Word documents the switches but not the sizes it draws at, so a symbol is drawn at its standard's nominal size at 100 %
/// (EAN and UPC at an X of 0.33 mm, ITF-14 at 1.016 mm, Code 39, Code 128 and NW7 at 0.254 mm, a Japan Post symbol at its
/// manual's 0.6 mm, a QR Code at 0.53 mm a module), which <c>\s</c> scales.
/// </remarks>
internal static class BarcodeField
{
    private const float PointsPerMillimetre = 72 / 25.4f;

    /// <summary>The pictures layout made, per field start: drawing a symbol again on every layout would be wasted.</summary>
    private static readonly ConditionalWeakTable<FieldStart, Drawn> Cache = [];

    /// <summary>A barcode field's symbol, its size and how it is drawn.</summary>
    /// <param name="Barcode">The symbol.</param>
    /// <param name="Module">The module width in points.</param>
    /// <param name="BarHeight">A linear symbol's bar height in points, or null for its own.</param>
    /// <param name="Rotation">Quarter turns clockwise, 0 to 3.</param>
    /// <param name="Foreground">The colour of the bars.</param>
    /// <param name="Background">The colour behind the symbol, or null for none.</param>
    /// <param name="ShowText">Whether a linear symbol's text is drawn under it.</param>
    /// <param name="TextScale">The scale, 1 at 100 %, of the text under the symbol.</param>
    internal sealed record Symbol(Barcode Barcode, float Module, float? BarHeight, int Rotation, RgbaF32 Foreground, RgbaF32? Background, bool ShowText, float TextScale);

    private sealed record Drawn(string Code, string? Family, Picture? Picture);

    /// <summary>
    /// The picture layout shows for a DISPLAYBARCODE field, made once per code and font: an SVG of the symbol at its size,
    /// with its text as the alternative text; null when the code names no symbol Egap can draw.
    /// </summary>
    public static Picture? Picture(Field field, Func<string, float, Font> font)
    {
        string code = field.Code;
        string? family = field.CodeNodes.OfType<Run>().FirstOrDefault()?.GetEffectiveFormat().FontAscii;
        if (Cache.TryGetValue(field.Start, out Drawn? drawn) && drawn.Code == code && drawn.Family == family)
        {
            return drawn.Picture;
        }

        Picture? picture = Parse(FieldCode.Parse(code)) is { } symbol ? Draw(field.Start.Document, symbol, symbol.ShowText ? font(family ?? "", 8 * symbol.TextScale) : null) : null;
        Cache.AddOrUpdate(field.Start, new Drawn(code, family, picture));
        return picture;
    }

    /// <summary>The symbol a DISPLAYBARCODE code names, or null when its type is unknown or its data does not fit the type.</summary>
    public static Symbol? Parse(FieldCode code)
    {
        if (code.Arguments.Count < 2)
        {
            return null;
        }

        string data = code.Arguments[0];
        string type = code.Arguments[1].ToUpperInvariant();
        bool fix = code.Has("\\X");
        float scale = Number(code.Switch("\\S")) is double s ? (float)Math.Clamp(s, 10, 1000) / 100 : 1;
        float? height = Number(code.Switch("\\H")) is double h && h > 0 ? (float)(h / 20) : null;
        Barcode barcode;
        float module;
        try
        {
            switch (type)
            {
                case "QR":
                    barcode = Barcode.Qr(data, new QrOptions { ErrorCorrection = QrLevel(code.Switch("\\Q")) });
                    module = 1.5f;
                    break;
                case "CODE128":
                    barcode = Barcode.Code128(data);
                    module = 0.254f * PointsPerMillimetre;
                    break;
                case "CODE39":
                    barcode = Barcode.Code39(data.Trim('*'));
                    module = 0.254f * PointsPerMillimetre;
                    break;
                case "NW7":
                    barcode = Barcode.Codabar(data);
                    module = 0.254f * PointsPerMillimetre;
                    break;
                case "EAN13" or "JAN13" or "EAN8" or "JAN8" or "UPCA" or "UPCE":
                    barcode = EanUpc(type, data, fix, code.Switch("\\P"));
                    module = 0.33f * PointsPerMillimetre;
                    break;
                case "ITF14" or "CASE":
                    barcode = Barcode.Itf14(CheckDigit(Digits(data), 14, fix));
                    module = 1.016f * PointsPerMillimetre;
                    height ??= 32 * PointsPerMillimetre;
                    break;
                case "JPPOST":
                    barcode = Barcode.JapanPost(data);
                    module = 0.6f * PointsPerMillimetre;
                    break;
                default:
                    return null;
            }
        }
        catch (ArgumentException)
        {
            // Data the type cannot hold: Word shows an error in place of the symbol, and Egap keeps the stored result.
            return null;
        }

        int rotation = Number(code.Switch("\\R")) is double r ? Math.Clamp((int)r, 0, 3) : 0;
        RgbaF32 foreground = Color(code.Switch("\\F")) ?? new RgbaF32(0, 0, 0, 1);
        if (!barcode.Modules.IsLinear && height is float side)
        {
            // \h sets a 2D symbol's height, quiet zone and all.
            module = side / (barcode.Modules.Height + (2 * barcode.QuietZone));
            height = null;
        }

        return new Symbol(barcode, module * scale, height * scale, rotation, foreground, Color(code.Switch("\\B")), code.Has("\\T") && barcode.Modules.IsLinear, scale);
    }

    /// <summary>The symbol's size in points before rotation: its quiet zone, bearer bars, bars and text.</summary>
    public static (float Width, float Height) Size(Symbol symbol, Font? font)
    {
        Barcode barcode = symbol.Barcode;
        BarcodeModules modules = barcode.Modules;
        int bearer = barcode.Format == BarcodeFormat.Itf ? 5 : 0;
        float x = symbol.Module;
        float width = (modules.Width + (2 * (barcode.QuietZone + bearer))) * x;
        float text = 0;
        if (font is Font textFont && !string.IsNullOrEmpty(barcode.HumanReadableText))
        {
            // As Egami.Barcodes sets it: one module of gap, then the text's line.
            TextMeasure measure = textFont.MeasureText(barcode.HumanReadableText);
            text = x + measure.Ascent - measure.Descent;
        }

        float height = modules.IsLinear
            ? (2 * bearer * x) + (symbol.BarHeight ?? (modules.Height * x)) + text
            : (modules.Height + (2 * barcode.QuietZone)) * x;
        return (width, height);
    }

    /// <summary>Draws the symbol over <paramref name="width"/> by <paramref name="height"/> points (its size, turned).</summary>
    public static void Draw(Canvas canvas, Symbol symbol, Font? font, float width, float height)
    {
        (float w, float h) = Size(symbol, font);
        if (symbol.Background is RgbaF32 background)
        {
            canvas.FillRect(new RectF(0, 0, width, height), new SolidPaint(background));
        }

        canvas.Save();
        switch (symbol.Rotation)
        {
            case 1:
                canvas.Translate(width, 0);
                canvas.Rotate(90);
                break;
            case 2:
                canvas.Translate(width, height);
                canvas.Rotate(180);
                break;
            case 3:
                canvas.Translate(0, height);
                canvas.Rotate(270);
                break;
        }

        canvas.DrawBarcode(symbol.Barcode, new RectF(0, 0, w, h), new SolidPaint(symbol.Foreground), new BarcodeDrawOptions { Font = font });
        canvas.Restore();
    }

    private static Picture? Draw(Document document, Symbol symbol, Font? font)
    {
        (float w, float h) = Size(symbol, font);
        (float width, float height) = symbol.Rotation % 2 == 1 ? (h, w) : (w, h);
        if (!(width > 0 && height > 0))
        {
            return null;
        }

        using MemoryStream svg = new();
        using (Egami.Svg.SvgCanvas canvas = new(svg, width, height))
        {
            Draw(canvas, symbol, font, width, height);
        }

        return new Picture(document, new ImageData(svg.ToArray(), "image/svg+xml"), width, height) { Description = symbol.Barcode.HumanReadableText };
    }

    /// <summary>An EAN, JAN or UPC symbol; <c>\p SUP2</c> or <c>SUP5</c> takes an add-on after a space in the data.</summary>
    private static Barcode EanUpc(string type, string data, bool fix, string? style)
    {
        string[] parts = data.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string digits = parts.Length > 0 ? Digits(parts[0]) : "";
        string? addOn = style?.ToUpperInvariant() is "SUP2" or "SUP5" && parts.Length > 1 ? Digits(parts[1]) : null;
        EanUpcOptions options = new() { AddOn = addOn is { Length: > 0 } ? addOn : null };
        return type switch
        {
            "EAN13" or "JAN13" => Barcode.Ean13(CheckDigit(digits, 13, fix), options),
            "EAN8" or "JAN8" => Barcode.Ean8(CheckDigit(digits, 8, fix), options),
            "UPCA" => Barcode.UpcA(CheckDigit(digits, 12, fix), options),
            _ => Barcode.UpcE(fix && digits.Length is 8 or 12 ? digits[..^1] : digits, options),
        };
    }

    /// <summary>With <c>\x</c>, a full-length number without its check digit, which the symbol then adds right.</summary>
    private static string CheckDigit(string digits, int length, bool fix) => fix && digits.Length == length ? digits[..^1] : digits;

    private static string Digits(string text) => string.Concat(text.Where(char.IsAsciiDigit));

    private static QrErrorCorrection? QrLevel(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "0" or "L" => QrErrorCorrection.L,
        "1" or "M" => QrErrorCorrection.M,
        "2" or "Q" => QrErrorCorrection.Q,
        "3" or "H" => QrErrorCorrection.H,
        _ => null,
    };

    /// <summary>A <c>\f</c> or <c>\b</c> colour, 0xRRGGBB (Microsoft calls the values RGB) or a decimal number.</summary>
    private static RgbaF32? Color(string? value)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Trim();
        bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || value.StartsWith('#')
            ? int.TryParse(value.AsSpan(value[0] == '#' ? 1 : 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb)
            : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rgb);
        return parsed && rgb is >= 0 and <= 0xFFFFFF
            ? new RgbaF32(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1)
            : null;
    }

    private static double? Number(string? value) =>
        value is not null && double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
}
