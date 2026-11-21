using System.Globalization;
using System.Text;

using Egami.Fonts.Unicode;

namespace Egap.Documents.Comparing;

/// <summary>What a <see cref="ParagraphPiece"/> of a paragraph is.</summary>
internal enum PieceKind
{
    /// <summary>A run of text; its characters are in the paragraph's text.</summary>
    Text,

    /// <summary>One or more inlines compared as one token: a tab, break, picture, note, whole field and so on.</summary>
    Atom,

    /// <summary>An inline with no text that marks a place, such as a bookmark edge; not compared.</summary>
    Marker,
}

/// <summary>
/// A stretch of a paragraph's inlines and where it sits in the paragraph's comparison text: a run (all its characters), an
/// atom (one placeholder character) or a marker (no characters).
/// </summary>
internal readonly record struct ParagraphPiece(PieceKind Kind, Inline First, int Count, int Start, int Length);

/// <summary>
/// A paragraph cut into tokens for comparing: UAX #29 words and the spaces and punctuation between them (or grapheme
/// clusters), with one token for each atomic inline. Token ids come from a <see cref="TokenTable"/> shared by both documents,
/// so equal tokens have equal ids.
/// </summary>
internal sealed class ParagraphTokens
{
    /// <summary>The character that stands for an atom in <see cref="Text"/>.</summary>
    public const char AtomChar = '\uFFFC';

    private ParagraphTokens(Paragraph paragraph, string text, ParagraphPiece[] pieces, int[] ids, int[] starts, bool[] words, ulong key)
    {
        Paragraph = paragraph;
        Text = text;
        Pieces = pieces;
        Ids = ids;
        Starts = starts;
        Words = words;
        Key = key;
    }

    /// <summary>The paragraph.</summary>
    public Paragraph Paragraph { get; }

    /// <summary>The text compared: the runs' characters with <see cref="AtomChar"/> for each atom.</summary>
    public string Text { get; }

    /// <summary>The paragraph's inlines in order, each with its place in <see cref="Text"/>.</summary>
    public ParagraphPiece[] Pieces { get; }

    /// <summary>The token ids in order.</summary>
    public int[] Ids { get; }

    /// <summary>Where each token starts in <see cref="Text"/>, with the text's length after the last.</summary>
    public int[] Starts { get; }

    /// <summary>Whether each token is a word (letters or digits) rather than spaces, punctuation or an atom.</summary>
    public bool[] Words { get; }

    /// <summary>A 64-bit hash of the token ids, equal for paragraphs that compare equal.</summary>
    public ulong Key { get; }

    /// <summary>How many tokens there are.</summary>
    public int Count => Ids.Length;

    /// <summary>The text of tokens <paramref name="start"/> to <paramref name="end"/>, atoms shown as their plain text.</summary>
    public string TextOf(int start, int end)
    {
        if (start >= end)
        {
            return "";
        }

        StringBuilder text = new();
        int from = Starts[start], to = Starts[end];
        foreach (ParagraphPiece piece in Pieces)
        {
            if (piece.Start + piece.Length <= from || piece.Start >= to || piece.Kind == PieceKind.Marker)
            {
                continue;
            }

            if (piece.Kind == PieceKind.Text)
            {
                int s = Math.Max(from, piece.Start), e = Math.Min(to, piece.Start + piece.Length);
                text.Append(((Run)piece.First).Text, s - piece.Start, e - s);
            }
            else
            {
                text.Append(AtomText(piece));
            }
        }

        return text.ToString();
    }

    /// <summary>Cuts <paramref name="paragraph"/> into tokens.</summary>
    public static ParagraphTokens Create(Paragraph paragraph, TokenTable table)
    {
        StringBuilder text = new();
        List<ParagraphPiece> pieces = [];
        List<string?> atomKeys = [];
        NodeCollection<Inline> inlines = paragraph.Inlines;
        for (int i = 0; i < inlines.Count; i++)
        {
            Inline inline = inlines[i];
            switch (inline)
            {
                case Run run:
                    pieces.Add(new ParagraphPiece(PieceKind.Text, run, 1, text.Length, run.Text.Length));
                    text.Append(run.Text);
                    break;
                case BookmarkStart or BookmarkEnd or CommentRangeStart or CommentRangeEnd or EditableRangeStart or EditableRangeEnd or Comment:
                    pieces.Add(new ParagraphPiece(PieceKind.Marker, inline, 1, text.Length, 0));
                    break;
                case FieldStart when FieldEndIndex(inlines, i) is int end:
                    pieces.Add(new ParagraphPiece(PieceKind.Atom, inline, end - i + 1, text.Length, 1));
                    text.Append(AtomChar);
                    i = end;
                    break;
                default:
                    pieces.Add(new ParagraphPiece(PieceKind.Atom, inline, 1, text.Length, 1));
                    text.Append(AtomChar);
                    break;
            }
        }

        string all = text.ToString();
        int[] atomAt = new int[all.Length];
        Array.Fill(atomAt, -1);
        for (int p = 0; p < pieces.Count; p++)
        {
            if (pieces[p].Kind == PieceKind.Atom)
            {
                atomAt[pieces[p].Start] = p;
            }
        }

        List<int> ids = [];
        List<int> starts = [];
        List<bool> words = [];
        int start = 0;
        while (start < all.Length)
        {
            if (atomAt[start] >= 0)
            {
                starts.Add(start);
                ids.Add(table.Atom(AtomKey(pieces[atomAt[start]], table.FieldCodes)));
                words.Add(false);
                start++;
                continue;
            }

            // A stretch of text up to the next atom, segmented on its own so an atom always stands alone.
            int end = start;
            while (end < all.Length && atomAt[end] < 0)
            {
                end++;
            }

            ReadOnlySpan<char> stretch = all.AsSpan(start, end - start);
            if (table.Characters)
            {
                foreach (TextSegment segment in TextSegmenter.Graphemes(stretch))
                {
                    AddText(stretch.Slice(segment.Start, segment.Length), start + segment.Start);
                }
            }
            else
            {
                foreach (TextSegment segment in TextSegmenter.Words(stretch))
                {
                    AddText(stretch.Slice(segment.Start, segment.Length), start + segment.Start);
                }
            }

            start = end;
        }

        starts.Add(all.Length);
        int[] idArray = [.. ids];
        return new ParagraphTokens(paragraph, all, [.. pieces], idArray, [.. starts], [.. words], Hash(idArray));

        void AddText(ReadOnlySpan<char> token, int at)
        {
            starts.Add(at);
            ids.Add(table.Text(token));
            words.Add(IsWord(token));
        }
    }

    /// <summary>The plain text an atom stands for, for change lists.</summary>
    public static string AtomText(ParagraphPiece piece)
    {
        StringBuilder text = new();
        Node? node = piece.First;
        for (int i = 0; i < piece.Count && node is not null; i++, node = node.NextSibling)
        {
            text.Append(node.GetText());
        }

        return text.ToString();
    }

    /// <summary>The inlines a piece covers, in order.</summary>
    public static IEnumerable<Inline> InlinesOf(ParagraphPiece piece)
    {
        Node? node = piece.First;
        for (int i = 0; i < piece.Count && node is Inline inline; i++)
        {
            Node? next = node.NextSibling;
            yield return inline;
            node = next;
        }
    }

    /// <summary>The 64-bit FNV-1a hash of a list of ids.</summary>
    public static ulong Hash(ReadOnlySpan<int> ids, ulong seed = 14695981039346656037UL)
    {
        ulong hash = seed;
        foreach (int id in ids)
        {
            hash = (hash ^ (uint)id) * 1099511628211UL;
        }

        return hash;
    }

    private static bool IsWord(ReadOnlySpan<char> token)
    {
        foreach (char c in token)
        {
            if (char.IsLetterOrDigit(c))
            {
                return true;
            }
        }

        return false;
    }

    private static int? FieldEndIndex(NodeCollection<Inline> inlines, int start)
    {
        int depth = 0;
        for (int i = start; i < inlines.Count; i++)
        {
            switch (inlines[i])
            {
                case FieldStart:
                    depth++;
                    break;
                case FieldEnd:
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return null;
    }

    /// <summary>
    /// What an atom is compared by: its kind, its settings that show, and its text. A note goes by its kind and mark and a
    /// shape by its kind and size, as their text is compared as a story of its own; a picture by its image and size; a field
    /// by its code and result, or by its result only.
    /// </summary>
    private static string AtomKey(ParagraphPiece atom, bool fieldCodes)
    {
        StringBuilder key = new();
        bool skipping = !fieldCodes && atom.First is FieldStart;
        int depth = 0;
        foreach (Inline inline in InlinesOf(atom))
        {
            if (skipping)
            {
                // A field's result only: what follows its own separator, nested fields' codes skipped likewise.
                depth += inline switch
                {
                    FieldStart => 1,
                    FieldEnd => -1,
                    _ => 0,
                };
                if (inline is FieldSeparator && depth == 1)
                {
                    skipping = false;
                }

                continue;
            }

            key.Append(inline.GetType().Name).Append(':');
            switch (inline)
            {
                case Break line:
                    key.Append(line.Kind);
                    break;
                case Picture picture:
                    key.Append(Hash(picture.Image.Bytes.Span).ToString("x16", CultureInfo.InvariantCulture))
                        .Append(':').Append(picture.Width.ToString("R", CultureInfo.InvariantCulture))
                        .Append('x').Append(picture.Height.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case Shape shape:
                    key.Append(shape.Kind).Append(':').Append(shape.Width.ToString("R", CultureInfo.InvariantCulture))
                        .Append('x').Append(shape.Height.ToString("R", CultureInfo.InvariantCulture)).Append(':')
                        .Append(shape.TextBox is null ? '-' : '+');
                    break;
                case Footnote note:
                    key.Append(note.Kind).Append(':').Append(note.CustomMark);
                    break;
                case Hyperlink link:
                    key.Append(link.Address).Append('#').Append(link.Anchor).Append(':').Append(link.GetText());
                    break;
                case Island island:
                    key.Append(island.Kind).Append(':').Append(island.Format).Append(':')
                        .Append(Hash(Encoding.UTF8.GetBytes(island.Markup)).ToString("x16", CultureInfo.InvariantCulture));
                    break;
                default:
                    key.Append(inline.GetText());
                    break;
            }

            key.Append('\u0001');
        }

        return key.ToString();
    }

    private static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in bytes)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return hash;
    }
}

/// <summary>
/// Token ids shared by the two documents of a comparison: equal text (after the comparison's case and white-space
/// folding) or equal atoms get equal ids.
/// </summary>
internal sealed class TokenTable(bool characters, bool ignoreCase, bool ignoreWhitespace, bool fieldCodes = true)
{
    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);

    /// <summary>Whether tokens are grapheme clusters rather than words.</summary>
    public bool Characters => characters;

    /// <summary>Whether a field is compared by its code as well as its result.</summary>
    public bool FieldCodes => fieldCodes;

    /// <summary>The id of a text token.</summary>
    public int Text(ReadOnlySpan<char> token)
    {
        if (ignoreWhitespace && IsWhitespace(token))
        {
            token = " ";
        }

        if (ignoreCase)
        {
            Span<char> lower = token.Length <= 64 ? stackalloc char[token.Length] : new char[token.Length];
            token.ToLowerInvariant(lower);
            return Id(lower);
        }

        return Id(token);
    }

    /// <summary>The id of an atom with <paramref name="key"/>.</summary>
    public int Atom(string key)
    {
        return Id(("\u0000" + key).AsSpan());
    }

    private int Id(ReadOnlySpan<char> key)
    {
        Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> table = ids.GetAlternateLookup<ReadOnlySpan<char>>();
        if (!table.TryGetValue(key, out int id))
        {
            id = ids.Count;
            table[key] = id;
        }

        return id;
    }

    private static bool IsWhitespace(ReadOnlySpan<char> token)
    {
        foreach (char c in token)
        {
            if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return token.Length > 0;
    }
}
