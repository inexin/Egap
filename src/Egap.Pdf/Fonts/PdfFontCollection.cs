using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Egap.Pdf;

/// <summary>
/// The fonts a page's resources name (<c>/Font</c>), by resource name: <c>page.Fonts["F1"]</c> is the font <c>/F1 12 Tf</c>
/// selects. Read when asked, so it always shows the resources as they are.
/// </summary>
public sealed class PdfFontCollection : IReadOnlyDictionary<string, PdfFont>
{
    private readonly PdfPage page;

    internal PdfFontCollection(PdfPage page)
    {
        this.page = page;
    }

    /// <inheritdoc/>
    public int Count => Entries().Count();

    /// <inheritdoc/>
    public IEnumerable<string> Keys => Entries().Select(e => e.Key);

    /// <inheritdoc/>
    public IEnumerable<PdfFont> Values => Entries().Select(e => e.Value);

    /// <inheritdoc/>
    /// <exception cref="KeyNotFoundException">The resources name no font by that name.</exception>
    public PdfFont this[string key] => TryGetValue(key, out PdfFont? font) ? font : throw new KeyNotFoundException($"The page has no font named {key}.");

    /// <inheritdoc/>
    public bool ContainsKey(string key)
    {
        return TryGetValue(key, out _);
    }

    /// <inheritdoc/>
    public bool TryGetValue(string key, [MaybeNullWhen(false)] out PdfFont value)
    {
        ArgumentNullException.ThrowIfNull(key);
        value = page.Resources?.Get<PdfDictionary>("Font")?.Get<PdfDictionary>(key) is PdfDictionary font ? PdfFont.FromDictionary(font) : null;
        return value is not null;
    }

    /// <inheritdoc/>
    public IEnumerator<KeyValuePair<string, PdfFont>> GetEnumerator()
    {
        return Entries().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private IEnumerable<KeyValuePair<string, PdfFont>> Entries()
    {
        if (page.Resources?.Get<PdfDictionary>("Font") is not PdfDictionary fonts)
        {
            yield break;
        }

        foreach (KeyValuePair<PdfName, PdfObject> entry in fonts.Entries)
        {
            if (entry.Value.Resolve() is PdfDictionary font)
            {
                yield return new(entry.Key.Value, PdfFont.FromDictionary(font));
            }
        }
    }
}
