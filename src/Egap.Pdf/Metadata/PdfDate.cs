using System.Globalization;
using System.Text.RegularExpressions;

namespace Egap.Pdf.Metadata;

/// <summary>Dates in PDF's string form (7.9.4) and XMP's ISO 8601 form, read leniently as pdf.js and PDFBox read them.</summary>
internal static partial class PdfDate
{
    /// <summary>
    /// A PDF date <c>D:YYYYMMDDHHmmSSOHH'mm'</c>: every part after the year may be left out, the <c>D:</c> too, and the
    /// offset may lack its apostrophes; null when even the year is not there or a part is out of range.
    /// </summary>
    public static DateTimeOffset? ParsePdf(string? text)
    {
        if (text is null || PdfDatePattern().Match(text.Trim()) is not { Success: true } match)
        {
            return null;
        }

        int Part(string name, int fallback)
        {
            return match.Groups[name].Success ? int.Parse(match.Groups[name].Value, CultureInfo.InvariantCulture) : fallback;
        }

        TimeSpan offset = TimeSpan.Zero;
        if (match.Groups["sign"].Value is "+" or "-")
        {
            offset = new TimeSpan(Part("oh", 0), Part("om", 0), 0);
            offset = match.Groups["sign"].Value == "-" ? -offset : offset;
        }

        try
        {
            return new DateTimeOffset(Part("year", 0), Math.Max(Part("month", 1), 1), Math.Max(Part("day", 1), 1), Part("hour", 0), Part("minute", 0), Part("second", 0), offset);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>A date in PDF's form, <c>D:20261012183000+02'00'</c>, or <c>Z</c> for UTC.</summary>
    public static string FormatPdf(DateTimeOffset date)
    {
        string zone = date.Offset == TimeSpan.Zero ? "Z" : string.Create(CultureInfo.InvariantCulture, $"{(date.Offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(date.Offset.Hours):D2}'{Math.Abs(date.Offset.Minutes):D2}'");
        return "D:" + date.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + zone;
    }

    /// <summary>An XMP date (ISO 8601, possibly only a year or a date); null when it is not one.</summary>
    public static DateTimeOffset? ParseXmp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string trimmed = text.Trim();
        string[] formats = ["yyyy", "yyyy-MM", "yyyy-MM-dd", "yyyy-MM-ddTHH:mmK", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"];
        return DateTimeOffset.TryParseExact(trimmed, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date) ? date : null;
    }

    /// <summary>A date in XMP's form, <c>2026-10-12T18:30:00+02:00</c>.</summary>
    public static string FormatXmp(DateTimeOffset date)
    {
        return date.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"^(D:)?(?<year>\d{4})(?<month>\d{2})?(?<day>\d{2})?(?<hour>\d{2})?(?<minute>\d{2})?(?<second>\d{2})?(?:(?<sign>[+\-Z])(?:(?<oh>\d{2})'?(?<om>\d{2})?'?)?)?", RegexOptions.CultureInvariant)]
    private static partial Regex PdfDatePattern();
}
