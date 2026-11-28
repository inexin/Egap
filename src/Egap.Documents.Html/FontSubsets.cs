using System.Text;

using Egami.Fonts;

namespace Egap.Documents.Html;

/// <summary>
/// The subsets EPUB and MHTML embed of a document's fonts: the glyphs the shaper draws the document's characters with,
/// renumbered densely, with GSUB, GPOS, GDEF, kern and MATH cut down to them, so a reader shapes the text as the full font
/// would (ligatures, contextual forms, mark positions, kerning).
/// </summary>
internal static class FontSubsets
{
    /// <summary>The characters of the document's paragraphs, and the space.</summary>
    public static HashSet<int> Characters(Document document)
    {
        HashSet<int> characters = [' '];
        foreach (Paragraph paragraph in document.Descendants<Paragraph>())
        {
            foreach (Rune rune in paragraph.GetText().EnumerateRunes())
            {
                characters.Add(rune.Value);
            }
        }

        return characters;
    }

    /// <summary>
    /// The font cut down to <paramref name="characters"/>; the whole font when its embedding rights forbid subsetting, or
    /// <see langword="null"/> when it cannot be read or subset.
    /// </summary>
    public static byte[]? Subset(ReadOnlyMemory<byte> data, IReadOnlyCollection<int> characters)
    {
        try
        {
            Typeface face = Typeface.FromMemory(data);
            if ((face.Embedding & FontEmbedding.NoSubsetting) != 0)
            {
                return data.ToArray();
            }

            FontSubset subset = FontSubsetter.Subset(face, [0, .. FontSubsetter.GlyphsFor(face, characters)], new SubsetOptions
            {
                KeepCmap = true,
                LayoutClosure = true,
                Characters = characters,
            });
            return subset.Data.ToArray();
        }
        catch (Exception e) when (e is NotSupportedException or InvalidDataException or FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
