using System.Globalization;
using System.Text.RegularExpressions;

namespace Egap.Pdf;

/// <summary>The values one page's placeholders are filled with, for <see cref="PdfPlaceholders.Fill"/>.</summary>
public sealed record PdfPlaceholderValues
{
    /// <summary>Gets the page number, from 1.</summary>
    public int Page { get; init; } = 1;

    /// <summary>Gets the number of pages.</summary>
    public int TotalPages { get; init; } = 1;

    /// <summary>Gets the page label, such as <c>iv</c>; null for the page number.</summary>
    public string? Label { get; init; }

    /// <summary>Gets the date and time <c>{date}</c> and <c>{time}</c> show.</summary>
    public DateTimeOffset Now { get; init; }

    /// <summary>Gets the document title; null for none.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the file name; null for none.</summary>
    public string? File { get; init; }

    /// <summary>Gets the culture numbers, dates and times are written in; null for the invariant culture.</summary>
    public CultureInfo? Culture { get; init; }

    /// <summary>The Bates number a Bates stamp fills <c>{bates}</c> with.</summary>
    internal string? Bates { get; init; }
}

/// <summary>
/// The placeholders page furniture is written with, the same everywhere Egap fills text per page (stamps, and headers and
/// footers on export): <c>{page}</c>, <c>{total-pages}</c> (also <c>{pages}</c>), <c>{label}</c> (the page label),
/// <c>{date}</c>, <c>{time}</c>, <c>{title}</c> and <c>{file}</c>.
/// </summary>
/// <remarks>
/// A number, date or time takes a .NET format after a colon: <c>{page:000}</c>, <c>{date:yyyy-MM-dd}</c>, <c>{time:HH:mm}</c>.
/// Without one, <c>{date}</c> is the culture's short date and <c>{time}</c> its short time. A placeholder that is not one of
/// these stays as written, and <c>{{</c> writes a brace.
/// </remarks>
/// <example>
/// <code>
/// string footer = PdfPlaceholders.Fill("Page {page} of {total-pages}", new PdfPlaceholderValues { Page = 2, TotalPages = 9 });
/// </code>
/// </example>
public static partial class PdfPlaceholders
{
    /// <summary>Gets the placeholder names, without braces: <c>page</c>, <c>total-pages</c>, <c>pages</c>, <c>label</c>, <c>date</c>, <c>time</c>, <c>title</c> and <c>file</c>.</summary>
    public static IReadOnlyList<string> Names { get; } = ["page", "total-pages", "pages", "label", "date", "time", "title", "file"];

    /// <summary>The template with its placeholders filled for one page.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    public static string Fill(string template, PdfPlaceholderValues values)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        if (!template.Contains('{', StringComparison.Ordinal))
        {
            return template;
        }

        CultureInfo culture = values.Culture ?? CultureInfo.InvariantCulture;
        return Field().Replace(template, m =>
        {
            if (m.Value == "{{")
            {
                return "{";
            }

            string format = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            return m.Groups[1].Value switch
            {
                "page" => values.Page.ToString(format, culture),
                "pages" or "total-pages" => values.TotalPages.ToString(format, culture),
                "label" => values.Label ?? values.Page.ToString(format, culture),
                "date" => values.Now.ToString(format.Length > 0 ? format : "d", culture),
                "time" => values.Now.ToString(format.Length > 0 ? format : "t", culture),
                "title" => values.Title ?? string.Empty,
                "file" => values.File ?? string.Empty,
                "bates" when values.Bates is not null => values.Bates,
                _ => m.Value,
            };
        });
    }

    [GeneratedRegex(@"\{\{|\{(page|total-pages|pages|label|date|time|title|file|bates)(?::([^}]*))?\}")]
    private static partial Regex Field();
}
