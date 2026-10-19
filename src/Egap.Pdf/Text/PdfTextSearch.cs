using System.Text.RegularExpressions;

using Egap.Pdf.Text;

namespace Egap.Pdf;

/// <summary>How <see cref="PdfDocument.FindText(string, PdfFindOptions?, CancellationToken)"/> and the methods like it search.</summary>
public sealed record PdfFindOptions
{
    /// <summary>Gets the defaults: case matters, matches may be parts of words, every page.</summary>
    public static PdfFindOptions Default { get; } = new();

    /// <summary>Gets whether upper and lower case must match. Defaults to true. A <see cref="Regex"/> uses its own options instead.</summary>
    public bool MatchCase { get; init; } = true;

    /// <summary>
    /// Gets whether a match must be whole words, with no letter or digit just before or after it. Defaults to false. A
    /// <see cref="Regex"/> uses <c>\b</c> instead.
    /// </summary>
    public bool WholeWord { get; init; }

    /// <summary>Gets the pages to search; null (the default) for every page.</summary>
    public PdfPageRange? Pages { get; init; }

    /// <summary>
    /// Gets how the text is built into words and lines before it is searched; null for <see cref="PdfTextOptions.Default"/>.
    /// </summary>
    public PdfTextOptions? Text { get; init; }
}

/// <summary>A quadrilateral on the page, such as the area of a matched run of text on one line, in default user space.</summary>
/// <param name="UpperLeft">The upper left corner.</param>
/// <param name="UpperRight">The upper right corner.</param>
/// <param name="LowerLeft">The lower left corner.</param>
/// <param name="LowerRight">The lower right corner.</param>
public readonly record struct PdfQuad(PdfPoint UpperLeft, PdfPoint UpperRight, PdfPoint LowerLeft, PdfPoint LowerRight)
{
    /// <summary>The smallest upright rectangle around the quadrilateral.</summary>
    public PdfRectangle Bounds => new(
        Math.Min(Math.Min(UpperLeft.X, UpperRight.X), Math.Min(LowerLeft.X, LowerRight.X)),
        Math.Min(Math.Min(UpperLeft.Y, UpperRight.Y), Math.Min(LowerLeft.Y, LowerRight.Y)),
        Math.Max(Math.Max(UpperLeft.X, UpperRight.X), Math.Max(LowerLeft.X, LowerRight.X)),
        Math.Max(Math.Max(UpperLeft.Y, UpperRight.Y), Math.Max(LowerLeft.Y, LowerRight.Y)));

    /// <summary>A rectangle as a quadrilateral.</summary>
    public static PdfQuad FromRectangle(PdfRectangle rectangle)
    {
        PdfRectangle r = rectangle.Normalize();
        return new PdfQuad(new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom));
    }

    /// <summary>The four corners in the order annotations' <c>QuadPoints</c> take them: upper left, upper right, lower left, lower right.</summary>
    public IReadOnlyList<PdfPoint> ToPoints() => [UpperLeft, UpperRight, LowerLeft, LowerRight];
}

/// <summary>A run of text found on a page.</summary>
public sealed class PdfTextMatch
{
    internal PdfTextMatch(int pageIndex, string text, IReadOnlyList<PdfTextGlyph> glyphs, IReadOnlyList<PdfQuad> quads)
    {
        PageIndex = pageIndex;
        Text = text;
        Glyphs = glyphs;
        Quads = quads;
    }

    /// <summary>Gets the 0-based index of the page it is on.</summary>
    public int PageIndex { get; }

    /// <summary>Gets the text matched, with a single space where it runs across a line break.</summary>
    public string Text { get; }

    /// <summary>Gets the glyphs matched, in order.</summary>
    public IReadOnlyList<PdfTextGlyph> Glyphs { get; }

    /// <summary>Gets one quadrilateral for each line the match is on, around its glyphs there.</summary>
    public IReadOnlyList<PdfQuad> Quads { get; }

    /// <summary>Gets the box around every quadrilateral.</summary>
    public PdfRectangle Box => PdfTextWord.Union(Quads.Select(q => q.Bounds));

    /// <inheritdoc/>
    public override string ToString() => $"page {PageIndex + 1}: {Text}";
}

/// <summary>Finding text on pages.</summary>
public sealed partial class PdfDocument
{
    /// <summary>
    /// Finds every place a phrase is shown, page by page, as <see cref="PdfPage.FindText(string, PdfFindOptions?)"/> finds
    /// it on each.
    /// </summary>
    /// <example>
    /// <code>
    /// using PdfDocument pdf = PdfDocument.Open("contract.pdf");
    /// foreach (PdfTextMatch match in pdf.FindText("Confidential", new PdfFindOptions { MatchCase = false }))
    /// {
    ///     Console.WriteLine($"{match.PageIndex + 1}: {match.Box}");
    /// }
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or empty.</exception>
    public IReadOnlyList<PdfTextMatch> FindText(string text, PdfFindOptions? options = null, CancellationToken cancellationToken = default)
    {
        return FindText(TextSearch.Pattern(text, options ?? PdfFindOptions.Default), options, cancellationToken);
    }

    /// <summary>Finds every match of a regular expression, page by page; its own options decide case and the like.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    public IReadOnlyList<PdfTextMatch> FindText(Regex pattern, PdfFindOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        options ??= PdfFindOptions.Default;
        List<PdfTextMatch> matches = [];
        foreach (int index in (options.Pages ?? PdfPageRange.All).Resolve(Pages.Count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            matches.AddRange(TextSearch.Find(Pages[index], index, pattern, options));
        }

        return matches;
    }
}

/// <summary>Finding text on a page.</summary>
public sealed partial class PdfPage
{
    /// <summary>
    /// Finds every place a phrase is shown on the page. The page's lines (as <see cref="GetText"/> builds them) are searched
    /// with words joined by single spaces, and lines that follow each other in a block of text are joined too, so a phrase
    /// is found where it wraps onto the next line; any run of white space in the phrase matches any run on the page.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> is null or empty.</exception>
    public IReadOnlyList<PdfTextMatch> FindText(string text, PdfFindOptions? options = null)
    {
        options ??= PdfFindOptions.Default;
        return TextSearch.Find(this, Document.Pages.IndexOf(this), TextSearch.Pattern(text, options), options);
    }

    /// <summary>Finds every match of a regular expression on the page, searched as <see cref="FindText(string, PdfFindOptions?)"/> searches.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="pattern"/> is null.</exception>
    public IReadOnlyList<PdfTextMatch> FindText(Regex pattern, PdfFindOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return TextSearch.Find(this, Document.Pages.IndexOf(this), pattern, options ?? PdfFindOptions.Default);
    }
}
