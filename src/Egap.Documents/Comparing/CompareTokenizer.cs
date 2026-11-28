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
        int spaceEnd = -1;
        int start = 0;
        while (start < all.Length)
        {
            if (atomAt[start] >= 0)
            {
                starts.Add(start);
                ParagraphPiece atom = pieces[atomAt[start]];
                if (table.AcrossFormats && ShownText(atom) is string shown && IsSingleWord(shown))
                {
                    // Across formats a field that shows one word, such as a page number in a table of contents, is that
                    // word, as another format may keep it as plain text.
                    ids.Add(table.Text(shown));
                    words.Add(true);
                }
                else
                {
                    ids.Add(table.Atom(AtomKey(atom, table.FieldCodes, table.AcrossFormats)));
                    words.Add(false);
                }

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
                foreach (TextSegment segment in TextSegmenter.Words(stretch, null))
                {
                    AddWord(stretch.Slice(segment.Start, segment.Length), start + segment.Start);
                }
            }

            start = end;
        }

        starts.Add(all.Length);
        int[] idArray = [.. ids];
        return new ParagraphTokens(paragraph, all, [.. pieces], idArray, [.. starts], [.. words], Hash(idArray));

        // UAX #29 keeps a full stop between letters inside a word ("one.This", where a space is missing); Word reads two
        // words and the stop, so the word is cut there.
        void AddWord(ReadOnlySpan<char> token, int at)
        {
            int from = 0;
            for (int k = 1; k + 1 < token.Length; k++)
            {
                if (token[k] == '.' && char.IsLetter(token[k - 1]) && char.IsLetter(token[k + 1]))
                {
                    AddText(token[from..k], at + from);
                    AddText(token.Slice(k, 1), at + k);
                    from = k + 1;
                }
            }

            AddText(token[from..], at + from);
        }

        void AddText(ReadOnlySpan<char> token, int at)
        {
            if (table.AcrossFormats && token.Length > 0 && token.Trim().IsEmpty)
            {
                // Across formats a stretch of white space is one, whatever it is made of: HTML keeps runs of spaces and tabs
                // as no-break spaces, so only that there is space counts.
                if (spaceEnd == at && starts.Count > 0)
                {
                    spaceEnd = at + token.Length;
                    return;
                }

                starts.Add(at);
                ids.Add(table.Text(" "));
                words.Add(false);
                spaceEnd = at + token.Length;
                return;
            }

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

    /// <summary>The text a whole field shows (its result, nested fields' codes left out); null for other atoms.</summary>
    public static string? ShownText(ParagraphPiece atom)
    {
        if (atom.First is not FieldStart || atom.Count < 2 || InlinesOf(atom).Last() is not FieldEnd)
        {
            return null;
        }

        StringBuilder text = new();
        List<bool> inResult = [];
        foreach (Inline inline in InlinesOf(atom))
        {
            switch (inline)
            {
                case FieldStart:
                    inResult.Add(false);
                    break;
                case FieldSeparator when inResult.Count > 0:
                    inResult[^1] = true;
                    break;
                case FieldEnd when inResult.Count > 0:
                    inResult.RemoveAt(inResult.Count - 1);
                    break;
                default:
                    if (!inResult.Contains(false))
                    {
                        text.Append(inline.GetText());
                    }

                    break;
            }
        }

        return text.ToString();
    }

    /// <summary>Whether <paramref name="text"/> is one word, as the tokenizer would cut it.</summary>
    private static bool IsSingleWord(string text)
    {
        if (text.Length == 0 || !IsWord(text) || text.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (TextSegment segment in TextSegmenter.Words(text, null))
        {
            return segment.Start == 0 && segment.Length == text.Length;
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
    private static string AtomKey(ParagraphPiece atom, bool fieldCodes, bool acrossFormats)
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
                case Hyperlink link when acrossFormats:
                    // HTML drops the white space at a line's end, and keeps runs of it as one.
                    key.Append(link.Address).Append('#').Append(link.Anchor).Append(':')
                        .AppendJoin(' ', link.GetText().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                    break;
                case Hyperlink link:
                    key.Append(link.Address).Append('#').Append(link.Anchor).Append(':').Append(link.GetText());
                    break;
                case Island island when acrossFormats:
                    // Each format keeps its own markup for what it cannot read, so across formats an island is known only
                    // by its kind, and a drawing (a VML shape, a shape group, an embedded object) as one, as one format
                    // keeps as a VML shape what another keeps as a group or an object.
                    key.Append(island.Kind is IslandKind.Math or IslandKind.Chart or IslandKind.SmartArt or IslandKind.Ink ? island.Kind : IslandKind.Other);
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
internal sealed class TokenTable(bool characters, bool ignoreCase, bool ignoreWhitespace, bool fieldCodes = true, bool acrossFormats = false)
{
    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);

    /// <summary>Whether tokens are grapheme clusters rather than words.</summary>
    public bool Characters => characters;

    /// <summary>Whether a field is compared by its code as well as its result.</summary>
    public bool FieldCodes => fieldCodes;

    /// <summary>
    /// Whether the documents come from different formats: a field that shows one word then equals that word as plain text, as
    /// one format keeps as a field (a page number in a table of contents) what another keeps as text.
    /// </summary>
    public bool AcrossFormats => acrossFormats;

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
