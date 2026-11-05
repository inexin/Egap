using System.Text;

namespace Egap.Documents.Word;

/// <summary>Makes style ids from Word style names, as the Word readers need them: unique within a document.</summary>
internal sealed class WordStyleIds
{
    private readonly Dictionary<string, int> suffixes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A style id from a style's name: Word's own id for a built-in style, else the name without spaces, unique.</summary>
    public string Make(string name, HashSet<string> used)
    {
        string id = "";
        foreach (BuiltInStyle builtIn in Enum.GetValues<BuiltInStyle>())
        {
            if (BuiltInStyles.Name(builtIn).Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                id = BuiltInStyles.Id(builtIn);
                break;
            }
        }

        if (id.Length == 0)
        {
            StringBuilder text = new();
            bool upper = true;
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c))
                {
                    text.Append(upper ? char.ToUpperInvariant(c) : c);
                    upper = false;
                }
                else
                {
                    upper = true;
                }
            }

            id = text.Length > 0 ? text.ToString() : "Style";
        }

        // Many styles with one name try each suffix once: the next one to try is kept per id.
        string unique = id;
        int n = suffixes.GetValueOrDefault(id, 1);
        while (!used.Add(unique))
        {
            unique = id + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            n++;
        }

        suffixes[id] = n;
        return unique;
    }
}
