using System.Globalization;
using System.Text;

namespace Egap.Documents;

/// <summary>Where lines may break inside words.</summary>
public enum WordBreak
{
    /// <summary>Only where the language's line breaking rules allow, as text usually breaks.</summary>
    Normal,

    /// <summary>Between any two characters, so a long word, number or address never runs past the line (CSS <c>word-break: break-all</c>).</summary>
    BreakAll,

    /// <summary>Not inside words, also in Chinese, Japanese and Korean text (CSS <c>word-break: keep-all</c>).</summary>
    KeepAll,

    /// <summary>A word too long for the line breaks where it must, others as normal (CSS <c>overflow-wrap: anywhere</c>).</summary>
    Anywhere,
}

/// <summary>
/// OpenType features turned on or off for some text, such as <c>liga=0</c> for no standard ligatures or <c>tnum</c> for
/// tabular figures. A feature's value is 1 for on, 0 for off, or a number for features that choose among alternates. The set
/// is compared by value, so two runs with the same features share formatting.
/// </summary>
/// <example>
/// <code>
/// builder.Font.Features = FontFeatures.Parse("liga=0, tnum, ss01");
/// </code>
/// </example>
public sealed class FontFeatures : IReadOnlyList<(string Tag, int Value)>, IEquatable<FontFeatures>
{
    private readonly (string Tag, int Value)[] features;

    private FontFeatures((string Tag, int Value)[] features)
    {
        this.features = features;
    }

    /// <summary>Gets an empty set.</summary>
    public static FontFeatures None { get; } = new([]);

    /// <inheritdoc/>
    public int Count => features.Length;

    /// <inheritdoc/>
    public (string Tag, int Value) this[int index] => features[index];

    /// <summary>
    /// A set from tags and values: the four-letter tag, then optionally <c>=</c> and a value (<c>"liga=0, tnum"</c>), or a
    /// leading <c>+</c> or <c>-</c> for on or off (<c>"-liga, +onum"</c>). Items are separated by commas or spaces; a later
    /// item for the same tag wins.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="FormatException">An item is not a feature tag.</exception>
    public static FontFeatures Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<(string Tag, int Value)> list = [];
        foreach (string raw in text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string item = raw.Trim();
            int value = 1;
            if (item[0] is '+' or '-')
            {
                value = item[0] == '+' ? 1 : 0;
                item = item[1..];
            }

            int equals = item.IndexOf('=');
            if (equals >= 0)
            {
                string number = item[(equals + 1)..].Trim();
                value = number.ToUpperInvariant() switch
                {
                    "ON" => 1,
                    "OFF" => 0,
                    _ when int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0 => parsed,
                    _ => throw new FormatException($"'{raw}' does not give a feature value such as 0, 1 or 2."),
                };
                item = item[..equals].Trim();
            }

            list.Add((Tag(item, raw), value));
        }

        return Of(list);
    }

    /// <summary>A set of the given features; a later one for the same tag wins.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="features"/> is null.</exception>
    /// <exception cref="FormatException">A tag is not four letters, digits or spaces.</exception>
    public static FontFeatures Of(IEnumerable<(string Tag, int Value)> features)
    {
        ArgumentNullException.ThrowIfNull(features);
        List<(string Tag, int Value)> list = [];
        foreach ((string tag, int value) in features)
        {
            string checkedTag = Tag(tag, tag);
            list.RemoveAll(f => f.Tag == checkedTag);
            list.Add((checkedTag, Math.Max(0, value)));
        }

        return list.Count == 0 ? None : new FontFeatures([.. list]);
    }

    /// <summary>This set with <paramref name="other"/>'s features added, its values winning where both have a tag.</summary>
    public FontFeatures With(FontFeatures other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Of([.. features, .. other.features]);
    }

    /// <summary>The value set for a tag, or null when the set does not name it.</summary>
    public int? ValueOf(string tag)
    {
        foreach ((string t, int v) in features)
        {
            if (t == tag)
            {
                return v;
            }
        }

        return null;
    }

    /// <inheritdoc/>
    public IEnumerator<(string Tag, int Value)> GetEnumerator() => ((IEnumerable<(string Tag, int Value)>)features).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <inheritdoc/>
    public bool Equals(FontFeatures? other) => other is not null && features.AsSpan().SequenceEqual(other.features);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as FontFeatures);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        foreach ((string tag, int value) in features)
        {
            hash.Add(tag);
            hash.Add(value);
        }

        return hash.ToHashCode();
    }

    /// <summary>The set as <see cref="Parse"/> reads it, such as <c>liga=0, tnum</c>.</summary>
    public override string ToString()
    {
        StringBuilder text = new();
        foreach ((string tag, int value) in features)
        {
            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append(tag);
            if (value != 1)
            {
                text.Append('=').Append(value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }

    private static string Tag(string tag, string item)
    {
        tag = tag.Trim().Trim('"', '\'');
        if (tag.Length is 0 or > 4 || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == ' '))
        {
            throw new FormatException($"'{item}' is not an OpenType feature tag such as liga or tnum.");
        }

        return tag.PadRight(4);
    }
}
