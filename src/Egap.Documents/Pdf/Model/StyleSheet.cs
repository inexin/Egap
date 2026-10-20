
using Egap.Pdf;

namespace Egap.Documents.Pdf;

/// <summary>A paragraph's measured look: alignment, left and first-line indents and line spacing as a multiple; null where a block cannot say.</summary>
internal readonly record struct Look(ParagraphAlignment? Alignment, double? Left, double? FirstLine, double? Spacing);

/// <summary>A paragraph style made from measured blocks: the model's style, the character formatting and look it holds, and its spacing.</summary>
internal sealed class StyleEntry(ParagraphStyle style, GlyphStyle font, Look look)
{
    public ParagraphStyle Style { get; } = style;

    public GlyphStyle Font { get; } = font;

    public Look Look { get; } = look;

    public double SpaceBefore { get; set; }

    public double SpaceAfter { get; set; }
}

/// <summary>
/// Paragraph styles from measured formatting (design Q6): blocks are grouped by role (title, heading level, caption, quote,
/// code, list item, note, header, footer, text), each group's most common font and look becomes its style (a built-in one
/// where Word has it), and text whose font differs from the body's often enough gets a body text style of its own. What a
/// paragraph does not share with its style stays direct formatting.
/// </summary>
internal sealed class StyleSheet
{
    private readonly Document document;
    private readonly Dictionary<BlockDraft, StyleEntry> styles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<BlockDraft, Look> looks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, StyleEntry> byKey = [];

    public StyleSheet(Document document, IReadOnlyList<(BlockDraft Block, Frame Frame)> blocks)
    {
        this.document = document;
        foreach ((BlockDraft block, Frame frame) in blocks)
        {
            looks[block] = Measure(block, frame);
        }

        List<(BlockDraft Block, string Key)> keyed = [.. blocks.Select(b => (b.Block, Key(b.Block)))];

        // The body: the font most text is set in, by characters.
        List<BlockDraft> text = [.. keyed.Where(k => k.Key == "Text").Select(k => k.Block)];
        GlyphStyle body = RunWriter.Dominant(text.Count > 0 ? text.SelectMany(b => b.Lines) : blocks.SelectMany(b => b.Block.Lines));
        Dictionary<GlyphStyle, List<BlockDraft>> variants = [];
        foreach (BlockDraft block in text)
        {
            GlyphStyle font = RunWriter.Dominant(block.Lines);
            (variants.TryGetValue(font, out List<BlockDraft>? list) ? list : variants[font] = []).Add(block);
        }

        int name = 0;
        for (int i = 0; i < keyed.Count; i++)
        {
            if (keyed[i].Key != "Text")
            {
                continue;
            }

            GlyphStyle font = RunWriter.Dominant(keyed[i].Block.Lines);
            if (font != body && variants[font].Count >= 3 && variants[font].Count >= text.Count / 20)
            {
                string key = "Text:" + font;
                if (!byKey.ContainsKey(key))
                {
                    name++;
                    byKey[key] = null!;
                    variantNames[key] = name == 1 ? "Body Text" : $"Body Text {name}";
                }

                keyed[i] = (keyed[i].Block, key);
            }
        }

        foreach (IGrouping<string, BlockDraft> group in keyed.GroupBy(k => k.Key, k => k.Block))
        {
            List<BlockDraft> members = [.. group];
            GlyphStyle font = group.Key == "Text" ? body : RunWriter.Dominant(members.SelectMany(b => b.Lines));
            Look look = new(Mode(members.Select(b => looks[b].Alignment)) ?? ParagraphAlignment.Left, Mode(members.Select(b => looks[b].Left)) ?? 0,
                Mode(members.Select(b => looks[b].FirstLine)) ?? 0, Mode(members.Select(b => looks[b].Spacing)) ?? 1);
            ParagraphStyle style = Make(group.Key, members[0]);
            font.ApplyTo(style.RunFormat);
            ParagraphFormat format = style.ParagraphFormat;
            format.Alignment = look.Alignment;
            if (group.Key != "List")
            {
                format.LeftIndent = look.Left;
                format.FirstLineIndent = look.FirstLine;
            }

            format.RightIndent = 0;
            format.LineSpacing = look.Spacing;
            format.LineSpacingRule = LineSpacingRule.Multiple;
            StyleEntry entry = new(style, font, look);
            byKey[group.Key] = entry;
            foreach (BlockDraft member in members)
            {
                styles[member] = entry;
            }
        }

        Normal = byKey.TryGetValue("Text", out StyleEntry? normal) ? normal
            : new StyleEntry((ParagraphStyle)document.Styles.AddBuiltIn(BuiltInStyle.Normal), body, new Look(ParagraphAlignment.Left, 0, 0, 1));
        body.ApplyTo(Normal.Style.RunFormat);
    }

    private readonly Dictionary<string, string> variantNames = [];

    /// <summary>Normal: body text's style.</summary>
    public StyleEntry Normal { get; }

    /// <summary>The style of <paramref name="block"/>.</summary>
    public StyleEntry For(BlockDraft block)
    {
        return styles.GetValueOrDefault(block) ?? Normal;
    }

    /// <summary>What a block measured as.</summary>
    public Look LookOf(BlockDraft block)
    {
        return looks.GetValueOrDefault(block);
    }

    /// <summary>All the styles made, with the role key they stand for.</summary>
    public IEnumerable<(string Key, StyleEntry Entry)> Entries => byKey.Where(e => e.Value is not null).Select(e => (e.Key, e.Value));

    /// <summary>Gives <paramref name="paragraph"/> direct formatting where <paramref name="block"/>'s look differs from its style's.</summary>
    public void ApplyDifferences(Paragraph paragraph, BlockDraft block, bool list)
    {
        StyleEntry entry = For(block);
        Look look = LookOf(block);
        ParagraphFormat format = paragraph.Format;
        if (look.Alignment is ParagraphAlignment alignment && alignment != entry.Look.Alignment)
        {
            format.Alignment = alignment;
        }

        if (!list && look.Left is double left && Math.Abs(left - (entry.Look.Left ?? 0)) > 1.5)
        {
            format.LeftIndent = left;
        }

        if (!list && look.FirstLine is double first && Math.Abs(first - (entry.Look.FirstLine ?? 0)) > 1.5)
        {
            format.FirstLineIndent = first;
        }

        if (look.Spacing is double spacing && Math.Abs(spacing - (entry.Look.Spacing ?? 1)) > 0.08)
        {
            format.LineSpacing = spacing;
            format.LineSpacingRule = LineSpacingRule.Multiple;
        }
    }

    /// <summary>The role key a block's style comes from.</summary>
    internal static string Key(BlockDraft block)
    {
        return block.Role switch
        {
            LayoutRole.Title => "Title",
            LayoutRole.Heading => "Heading" + Math.Clamp(block.Level, 1, 9),
            LayoutRole.Caption => "Caption",
            LayoutRole.Quote => "Quote",
            LayoutRole.Code => "Code",
            LayoutRole.ListItem => "List",
            LayoutRole.Footnote => "Note",
            LayoutRole.PageHeader => "Header",
            LayoutRole.PageFooter => "Footer",
            LayoutRole.PageNumber => block.Box.Top < block.Lines[0].Size * 20 ? "Header" : "Footer",
            _ => "Text",
        };
    }

    private ParagraphStyle Make(string key, BlockDraft sample)
    {
        BuiltInStyle? builtIn = key switch
        {
            "Text" => BuiltInStyle.Normal,
            "Title" => BuiltInStyle.Title,
            "Caption" => BuiltInStyle.Caption,
            "Quote" => BuiltInStyle.Quote,
            "List" => BuiltInStyle.ListParagraph,
            "Note" => BuiltInStyle.FootnoteText,
            "Header" => BuiltInStyle.Header,
            "Footer" => BuiltInStyle.Footer,
            _ when key.StartsWith("Heading", StringComparison.Ordinal) => BuiltInStyle.Heading1 + (int.Parse(key[7..], System.Globalization.CultureInfo.InvariantCulture) - 1),
            _ => null,
        };

        if (builtIn is BuiltInStyle known)
        {
            return (ParagraphStyle)document.Styles.AddBuiltIn(known);
        }

        string name = key == "Code" ? "Code" : variantNames.GetValueOrDefault(key, key);
        string id = name.Replace(" ", "", StringComparison.Ordinal);
        if (document.Styles[id] is ParagraphStyle existing)
        {
            return existing;
        }

        ParagraphStyle style = document.Styles.AddParagraphStyle(id, name);
        style.BasedOn = document.Styles.AddBuiltIn(BuiltInStyle.Normal);
        _ = sample;
        return style;
    }

    /// <summary>
    /// A block's look in its frame: alignment from where its lines start and end, the indents of its body and first line,
    /// and its line spacing as a multiple of the font's own line height, rounded to 0.05.
    /// </summary>
    internal static Look Measure(BlockDraft block, Frame frame)
    {
        List<PageLine> lines = [.. block.Lines.Where(l => l.IsHorizontal)];
        if (lines.Count == 0)
        {
            return default;
        }

        double size = lines[0].Size;
        double tol = Math.Max(1.5, size * 0.15);
        double frameCenter = (frame.Left + frame.Right) / 2;
        if (lines.Count == 1)
        {
            PageBox only = lines[0].Box;
            ParagraphAlignment? single = Math.Abs(((only.Left + only.Right) / 2) - frameCenter) <= tol * 1.5 && only.Left - frame.Left > size * 3 ? ParagraphAlignment.Center
                : Math.Abs(only.Right - frame.Right) <= tol && only.Left - frame.Left > frame.Width * 0.3 ? ParagraphAlignment.Right
                : null;
            return new Look(single, null, null, null);
        }

        List<PageLine> rest = [.. lines.Skip(1)];
        double restLeft = rest.Min(l => l.Box.Left);
        double maxRight = lines.Max(l => l.Box.Right);
        List<PageLine> nonLast = [.. lines.Take(lines.Count - 1)];
        bool leftAligned = rest.All(l => Math.Abs(l.Box.Left - restLeft) <= tol);
        bool rightAligned = nonLast.All(l => Math.Abs(l.Box.Right - maxRight) <= tol);
        double leftSpread = lines.Max(l => l.Box.Left) - lines.Min(l => l.Box.Left);
        bool centered = lines.All(l => Math.Abs(((l.Box.Left + l.Box.Right) / 2) - frameCenter) <= tol * 1.5);
        ParagraphAlignment? alignment = centered && leftSpread > tol * 2 ? ParagraphAlignment.Center
            : rightAligned && leftSpread > tol * 2 && Math.Abs(maxRight - frame.Right) <= tol * 2 && lines.All(l => Math.Abs(l.Box.Right - maxRight) <= tol) ? ParagraphAlignment.Right
            : leftAligned && rightAligned && nonLast.Count >= 2 ? ParagraphAlignment.Justify
            : leftAligned && rightAligned ? null
            : ParagraphAlignment.Left;

        double? left = alignment is ParagraphAlignment.Center or ParagraphAlignment.Right ? 0 : Indent(restLeft - frame.Left);
        double? first = alignment is ParagraphAlignment.Center or ParagraphAlignment.Right ? 0 : Indent(lines[0].Box.Left - restLeft);
        List<double> pitches = [];
        for (int i = 1; i < lines.Count; i++)
        {
            double pitch = lines[i].Baseline - lines[i - 1].Baseline;
            if (pitch > size * 0.5)
            {
                pitches.Add(pitch);
            }
        }

        double? spacing = null;
        if (pitches.Count > 0)
        {
            pitches.Sort();
            PdfFont? font = lines[0].Words[0].Font;
            double height = font is null ? 0 : (font.Ascent - font.Descent) / 1000;
            double factor = height is > 0.9 and < 1.6 ? height : 1.15;
            double multiple = Math.Round(pitches[pitches.Count / 2] / (size * factor) * 20) / 20;
            spacing = multiple is >= 0.95 and <= 1.07 ? 1 : Math.Clamp(multiple, 0.5, 5);
        }

        return new Look(alignment, left, first, spacing);
    }

    /// <summary>An indent rounded to the half point, nothing when under two points.</summary>
    internal static double Indent(double value)
    {
        return Math.Abs(value) < 2 ? 0 : Math.Round(value * 2) / 2;
    }

    private static T? Mode<T>(IEnumerable<T?> values)
        where T : struct
    {
        return values.Where(v => v.HasValue).GroupBy(v => v!.Value).OrderByDescending(g => g.Count()).Select(g => (T?)g.Key).FirstOrDefault();
    }
}
