using System.Globalization;
using System.Text.RegularExpressions;

using Egap.Documents;

namespace Egap.Pdf.Layout;

/// <summary>A list marker read apart: bullet or numbering, the number's value and the text around it ("(" and ")" in "(a)").</summary>
internal readonly record struct Marker(NumberStyle Style, int Value, string Prefix, string Suffix, string Bullet, int Depth)
{
    /// <summary>The level text the marker stands for at level <paramref name="level"/>: "%1." for "3.", "(%2)" for "(b)", the bullet itself.</summary>
    public string LevelText(int level)
    {
        if (Style == NumberStyle.Bullet)
        {
            return Bullet;
        }

        if (Depth > 1)
        {
            return string.Join('.', Enumerable.Range(level - Depth + 2, Depth).Select(i => "%" + i.ToString(CultureInfo.InvariantCulture))) + Suffix;
        }

        return Prefix + "%" + (level + 1).ToString(CultureInfo.InvariantCulture) + Suffix;
    }

    /// <summary>Whether two markers are written the same way, so items with them can share a list level.</summary>
    public bool SameKind(Marker other)
    {
        return Style == other.Style && Prefix == other.Prefix && Suffix == other.Suffix && Depth == other.Depth && (Style != NumberStyle.Bullet || Bullet == other.Bullet);
    }
}

/// <summary>
/// List items to the model's lists (design Q4, Q6): each marker read as a bullet or a number in decimal, letters or roman
/// numerals with its punctuation, a list per run of items that carry on one numbering, a level per nesting depth with the
/// marker's level text, start number and the indents the items are set with.
/// </summary>
internal sealed partial class ListBuilder(Document document)
{
    private readonly List<State> lists = [];
    private State? current;

    /// <summary>Reads a marker; null for one that is neither a bullet nor a number.</summary>
    public static Marker? Parse(string marker, bool romanFirst)
    {
        if (marker.Length == 0)
        {
            return null;
        }

        if (!marker.Any(char.IsLetterOrDigit))
        {
            return new Marker(NumberStyle.Bullet, 0, "", "", marker, 1);
        }

        Match legal = Legal().Match(marker);
        if (legal.Success)
        {
            string[] parts = legal.Groups[1].Value.Split('.');
            return new Marker(NumberStyle.Decimal, int.Parse(parts[^1], CultureInfo.InvariantCulture), "", legal.Groups[2].Value, "", parts.Length);
        }

        Match match = Numbered().Match(marker);
        if (!match.Success)
        {
            return null;
        }

        string prefix = match.Groups[1].Value, body = match.Groups[2].Value, suffix = match.Groups[3].Value;
        if (body.All(char.IsAsciiDigit))
        {
            return body.Length <= 4 ? new Marker(NumberStyle.Decimal, int.Parse(body, CultureInfo.InvariantCulture), prefix, suffix, "", 1) : null;
        }

        bool lower = char.IsLower(body[0]);
        if (Roman(body.ToUpperInvariant()) is int roman && (body.Length > 1 || romanFirst))
        {
            return new Marker(lower ? NumberStyle.LowerRoman : NumberStyle.UpperRoman, roman, prefix, suffix, "", 1);
        }

        return body.Length == 1 ? new Marker(lower ? NumberStyle.LowerLetter : NumberStyle.UpperLetter, char.ToUpperInvariant(body[0]) - 'A' + 1, prefix, suffix, "", 1) : null;
    }

    /// <summary>
    /// Puts a list item's paragraph in a list: the open list when its marker carries on that list's numbering at its level
    /// (or is a bullet like it), a list from before when the numbering picks up where that one stopped, else a new list.
    /// </summary>
    /// <param name="paragraph">The item's paragraph.</param>
    /// <param name="marker">The item's marker.</param>
    /// <param name="level">The nesting level, from 0.</param>
    /// <param name="markerLeft">The marker's left on the page, from the item's frame.</param>
    /// <param name="textLeft">Where the item's text starts, from the frame.</param>
    public void Add(Paragraph paragraph, Marker marker, int level, double markerLeft, double textLeft)
    {
        level = Math.Clamp(marker.Depth > 1 ? marker.Depth - 1 : level, 0, 8);
        State? target = Fits(current, marker, level) ? current : lists.LastOrDefault(l => Continues(l, marker, level));
        if (target is null)
        {
            target = new State(document.Lists.Add(document.Lists.AddDefinition()));
            lists.Add(target);
        }

        (List list0, Dictionary<int, Marker> levels, Dictionary<int, int> last) = (target.List, target.Levels, target.Last);
        if (!levels.ContainsKey(level))
        {
            levels[level] = marker;
            ListLevel format = list0.GetLevel(level);
            format.NumberStyle = marker.Style;
            format.LevelText = marker.LevelText(level);
            format.StartAt = marker.Style == NumberStyle.Bullet ? 1 : Math.Max(marker.Value, 0);
            format.IsLegal = marker.Depth > 1;
            format.Suffix = ListLabelSuffix.Tab;
            format.ParagraphFormat.LeftIndent = StyleSheet.Indent(textLeft);
            format.ParagraphFormat.FirstLineIndent = StyleSheet.Indent(markerLeft - textLeft);
            if (marker.Style == NumberStyle.Bullet && marker.Bullet.Length == 1 && marker.Bullet[0] is '\uF0B7' or '\u2022' or '\u25CF')
            {
                format.LevelText = "\u2022";
            }

            if (levels.Count > 1)
            {
                list0.Definition.Kind = ListKind.Multilevel;
            }
        }

        last[level] = marker.Value;
        foreach (int deeper in last.Keys.Where(k => k > level).ToList())
        {
            last.Remove(deeper);
        }

        current = target;
        paragraph.SetList(list0, level);
    }

    /// <summary>Ends the open list: the next item starts a new one unless its numbering carries on an earlier list.</summary>
    public void Break()
    {
        current = null;
    }

    private static bool Fits(State? l, Marker marker, int level)
    {
        if (l is null)
        {
            return false;
        }

        if (!l.Levels.TryGetValue(level, out Marker known))
        {
            return l.Levels.Keys.Any(k => k < level) || l.Levels.Count == 0;
        }

        if (!known.SameKind(marker))
        {
            return false;
        }

        return marker.Style == NumberStyle.Bullet || !l.Last.TryGetValue(level, out int last) || marker.Value == last + 1;
    }

    private static bool Continues(State list, Marker marker, int level)
    {
        return marker.Style != NumberStyle.Bullet && marker.Value > 1 && list.Levels.TryGetValue(level, out Marker known) && known.SameKind(marker)
            && list.Last.TryGetValue(level, out int last) && marker.Value == last + 1;
    }

    /// <summary>A list being built: the model's list, the marker each level was first written with, and each level's last number.</summary>
    private sealed class State(List list)
    {
        public List List { get; } = list;

        public Dictionary<int, Marker> Levels { get; } = [];

        public Dictionary<int, int> Last { get; } = [];
    }

    private static int? Roman(string text)
    {
        int total = 0, previous = 0;
        for (int i = text.Length - 1; i >= 0; i--)
        {
            int value = text[i] switch
            {
                'I' => 1,
                'V' => 5,
                'X' => 10,
                'L' => 50,
                'C' => 100,
                'D' => 500,
                'M' => 1000,
                _ => 0,
            };

            if (value == 0)
            {
                return null;
            }

            total += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }

        return total;
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)+)(\.?)$")]
    private static partial Regex Legal();

    [GeneratedRegex(@"^([(\[]?)([0-9]+|[A-Za-z]+)([.):\]]?)$")]
    private static partial Regex Numbered();
}
