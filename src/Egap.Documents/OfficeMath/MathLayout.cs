using System.Numerics;
using System.Text;

using Egami.Fonts;

namespace Egap.Documents.OfficeMath;

/// <summary>The four math styles, from TeX: display and text set at full size, script and script-script smaller.</summary>
internal enum MathStyle
{
    /// <summary>A display equation's main line.</summary>
    Display,

    /// <summary>An inline equation, and the numerator and denominator of a display fraction.</summary>
    Text,

    /// <summary>First-level scripts and limits.</summary>
    Script,

    /// <summary>Second-level scripts and radical degrees.</summary>
    ScriptScript,
}

/// <summary>
/// Lays out Office Math trees into <see cref="MathBox"/>es with a face's OpenType <c>MATH</c> constants, after the rules the
/// OpenType MATH specification describes (which follow TeX's Appendix G): styles and cramping, atom spacing, fractions,
/// scripts, limits, radicals, accents, bars, delimiters and large operators that grow through glyph variants and assemblies.
/// </summary>
internal sealed class MathLayout
{
    private const int MaxDepth = 64;

    // TeX's inter-atom spacing: rows are the left atom's class, columns the right's, in MathClass order (Ordinary, Operator,
    // Binary, Relation, Open, Close, Punctuation, Inner). 0 none, 1 thin, 2 medium, 3 thick; a, b, c are thin, medium and
    // thick in display and text style only.
    private static readonly string[] Spacing =
    [
        "01bc000a",
        "110c000a",
        "bb00b00b",
        "cc00c00c",
        "00000000",
        "01bc000a",
        "aa0aaaaa",
        "a1bca0aa",
    ];

    private readonly Typeface face;
    private readonly TypefaceMath math;
    private readonly MathConstants k;
    private readonly MathSettings settings;
    private readonly float size;
    private readonly float unitsPerEm;
    private int depth;
    private bool hidePlaceholders;
    private bool wideSpaces;

    /// <summary>A layout in <paramref name="face"/> at <paramref name="size"/> points.</summary>
    public MathLayout(Typeface face, TypefaceMath math, MathSettings settings, float size)
    {
        this.face = face;
        this.math = math;
        k = math.Constants;
        this.settings = settings;
        this.size = size;
        unitsPerEm = face.UnitsPerEm;
    }

    /// <summary>The face the layout sets glyphs in.</summary>
    public Typeface Face => face;

    /// <summary>
    /// Lays out an equation: inline in text style, or a display equation in display style, broken into lines at its manual
    /// breaks (<c>m:brk</c>) and, when it is wider than <paramref name="maxWidth"/> points, at its operators.
    /// </summary>
    public MathBox Zone(MathZone zone, bool display, float maxWidth = float.PositiveInfinity)
    {
        Context ctx = new(display ? MathStyle.Display : MathStyle.Text, false);
        if (!display || zone.Content.Items.Count == 0)
        {
            return Row(zone.Content, ctx);
        }

        List<MathBox> atoms = Atoms(zone.Content, ctx);
        MathBox whole = Join(atoms, ctx);
        if (!atoms.Exists(a => a.BreakBefore) && whole.Width <= maxWidth)
        {
            return whole;
        }

        return Wrap(atoms, whole, maxWidth, ctx);
    }

    /// <summary>Lays out each equation of a display paragraph, one line each (or more, where it breaks).</summary>
    public List<MathBox> Lines(MathParagraph paragraph, float maxWidth = float.PositiveInfinity)
    {
        List<MathBox> lines = [];
        foreach (MathNode item in paragraph.Items)
        {
            if (item is MathZone zone)
            {
                lines.Add(Zone(zone, display: true, maxWidth));
            }
        }

        return lines;
    }

    /// <summary>
    /// Breaks a display equation into lines (ECMA-376 Part 1, §22.1.2): before a run marked <c>m:brk</c>, and otherwise at the
    /// last binary operator or relation that keeps a line within <paramref name="maxWidth"/>. A binary operator starts the next
    /// line, ends this one or does both, by <c>m:brkBin</c> (with <c>m:brkBinSub</c> choosing the signs of a repeated minus); a
    /// relation starts the next line. Lines after the first indent by <c>m:wrapIndent</c>, align right with <c>m:wrapRight</c>,
    /// or align under an operator of the first line by <c>m:brk m:alnAt</c>.
    /// </summary>
    private MathBox Wrap(List<MathBox> atoms, MathBox whole, float maxWidth, Context ctx)
    {
        List<List<MathBox>> lines = [];
        List<int> alignments = [];
        List<MathBox> line = [];
        int alignAt = 0;
        float indent = settings.WrapIndent / 20f;
        float limit = maxWidth;
        int lastBreak = -1;

        void Flush()
        {
            lines.Add(line);
            alignments.Add(alignAt);
            line = [];
            lastBreak = -1;
            limit = maxWidth - indent;
        }

        for (int i = 0; i < atoms.Count; i++)
        {
            MathBox atom = atoms[i];
            if (atom.BreakBefore && line.Count > 0)
            {
                Flush();
                alignAt = atom.AlignAt;
            }

            if (line.Count > 0 && Breakable(atoms, i))
            {
                lastBreak = line.Count;
            }

            line.Add(atom);
            if (lastBreak > 0 && float.IsFinite(limit) && Width(line, ctx) > limit)
            {
                // Break at the last operator that fits; what follows it starts the next line.
                List<MathBox> rest = line[lastBreak..];
                line = line[..lastBreak];
                MathBox op = rest[0];
                string rule = op.Class == MathClass.Binary ? settings.BreakBinary : "before";
                if (rule is "after" or "repeat")
                {
                    line.Add(op);
                    rest.RemoveAt(0);
                }

                if (rule == "repeat")
                {
                    (MathBox end, MathBox start) = RepeatedOperator(op, ctx);
                    line[^1] = end;
                    rest.Insert(0, start);
                }

                Flush();
                alignAt = 0;
                line.AddRange(rest);
                lastBreak = -1;
            }
        }

        if (line.Count > 0)
        {
            Flush();
        }

        if (lines.Count < 2)
        {
            return whole;
        }

        List<MathBox> boxes = [.. lines.Select(l => Join(l, ctx))];
        float gap = settings.SpaceWithin / 20f;
        float width = boxes.Max(b => b.Width);
        MathBox box = new() { Class = MathClass.Ordinary };
        float y = 0;
        for (int i = 0; i < boxes.Count; i++)
        {
            if (i > 0)
            {
                // Broken lines keep at least single spacing (1.17 em, as Word's arrays), plus m:intraSp.
                y += Math.Max(boxes[i - 1].Descent + (Size(ctx) * 0.15f) + boxes[i].Ascent, Size(ctx) * 1.17f) + gap;
            }

            float x = i == 0 ? 0
                : alignments[i] > 0 ? OperatorX(lines[0], alignments[i], ctx)
                : settings.WrapRight ? Math.Max(0, width - boxes[i].Width)
                : indent;
            box.Add(boxes[i], x, y);
            box.Width = Math.Max(box.Width, x + boxes[i].Width);
        }

        return Finish(box);
    }

    /// <summary>Whether a display line may break at atom <paramref name="i"/>: a binary operator or relation after an operand.</summary>
    private static bool Breakable(List<MathBox> atoms, int i) =>
        i > 0 && atoms[i].Class is MathClass.Binary or MathClass.Relation
        && atoms[i - 1].Class is not (MathClass.Binary or MathClass.Relation or MathClass.Open or MathClass.Operator or MathClass.Punctuation);

    /// <summary>The signs a repeated binary operator shows at the end of a line and the start of the next.</summary>
    private (MathBox End, MathBox Start) RepeatedOperator(MathBox atom, Context ctx)
    {
        ushort minus = face.GetGlyph(0x2212);
        if (atom.Glyph == 0 || atom.Glyph != minus)
        {
            return (atom, atom);
        }

        MathBox Plus()
        {
            MathBox box = GlyphBox(face.GetGlyph('+'), Size(ctx));
            box.Class = MathClass.Binary;
            return box;
        }

        return settings.BreakBinarySubtraction switch
        {
            "-+" => (atom, Plus()),
            "+-" => (Plus(), atom),
            _ => (atom, atom),
        };
    }

    /// <summary>The x of the <paramref name="n"/>th (1-based) operator of a line, for a break that aligns under it.</summary>
    private float OperatorX(List<MathBox> atoms, int n, Context ctx)
    {
        int seen = 0;
        for (int i = 1; i < atoms.Count; i++)
        {
            if (Breakable(atoms, i) && ++seen == n)
            {
                return Width(atoms[..i], ctx);
            }
        }

        return settings.WrapIndent / 20f;
    }

    /// <summary>Lays out any node of a tree as a display or inline equation.</summary>
    public MathBox Node(MathNode node, bool display) => node switch
    {
        MathZone zone => Zone(zone, display),
        MathParagraph paragraph => Stack(Lines(paragraph)),
        MathRow row => Row(row, new Context(display ? MathStyle.Display : MathStyle.Text, false)),
        _ => new MathBox(),
    };

    // Word sets scripts two points of a hundred smaller than the font asks: 8.52 pt and 6.96 pt at 12 pt in Cambria Math (73 and 60).
    private float Size(Context ctx) => ctx.Style switch
    {
        MathStyle.Script => size * ((k.ScriptPercentScaleDown is > 2 and <= 100 ? k.ScriptPercentScaleDown : 73) - 2) / 100f,
        MathStyle.ScriptScript => size * ((k.ScriptScriptPercentScaleDown is > 2 and <= 100 ? k.ScriptScriptPercentScaleDown : 52) - 2) / 100f,
        _ => size,
    };

    private float U(float fontUnits, Context ctx) => fontUnits * Size(ctx) / unitsPerEm;

    private MathBox Stack(List<MathBox> lines)
    {
        MathBox box = new();
        float y = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                y += lines[i - 1].Descent + lines[i].Ascent + size * 0.2f;
            }

            box.Add(lines[i], 0, y);
            box.Width = Math.Max(box.Width, lines[i].Width);
        }

        return Finish(box);
    }

    private MathBox Row(MathRow row, Context ctx)
    {
        // An object's empty argument keeps the room of Word's placeholder (a dotted square, not printed) unless hidden.
        if (row.Items.Count == 0 && depth > 0 && !hidePlaceholders)
        {
            return Placeholder(ctx);
        }

        return Join(Atoms(row, ctx), ctx);
    }

    private MathBox Placeholder(Context ctx)
    {
        ushort glyph = face.GetGlyph(0x2B1A);
        MathBox box = glyph != 0 ? GlyphBox(glyph, Size(ctx)) : new MathBox { Width = Size(ctx) * 0.6f, Ascent = Size(ctx) * 0.6f };
        box.Glyph = 0;
        box.Italic = 0;
        box.Hidden = true;
        return box;
    }

    private List<MathBox> Atoms(MathRow row, Context ctx)
    {
        int argSz = row.Properties?.Int("argSz", 0) ?? 0;
        for (int i = argSz; i < 0; i++)
        {
            ctx = ctx.Script;
        }

        for (int i = 0; i < argSz; i++)
        {
            ctx = ctx.Larger;
        }

        List<MathBox> atoms = [];
        if (++depth > MaxDepth)
        {
            depth--;
            return atoms;
        }

        foreach (MathNode node in row.Items)
        {
            switch (node)
            {
                case MathRun run:
                    Run(run, ctx, atoms);
                    break;
                case MathObject obj:
                    atoms.Add(Object(obj, ctx));
                    break;
                case MathZone zone:
                    atoms.AddRange(Atoms(zone.Content, ctx));
                    break;
            }
        }

        depth--;
        return atoms;
    }

    private void Run(MathRun run, Context ctx, List<MathBox> atoms)
    {
        int first = atoms.Count;
        RunAtoms(run, ctx, atoms);
        // Word honours a manual break only before an operator, where an automatic break could also fall.
        if (atoms.Count > first && atoms[first].Class is MathClass.Binary or MathClass.Relation && Break(run) is int alignAt)
        {
            atoms[first].BreakBefore = true;
            atoms[first].AlignAt = alignAt;
        }
    }

    /// <summary>A run's manual break (<c>m:brk</c>): the operator its next line aligns under, 0 for none given; null for no break.</summary>
    private static int? Break(MathRun run)
    {
        foreach (object item in run.Properties?.Items ?? [])
        {
            switch (item)
            {
                case MathProperty { Name: "brk" }:
                    return 0;
                case System.Xml.Linq.XElement { Name.LocalName: "brk" } element:
                    string? value = element.Attributes().FirstOrDefault(a => a.Name.LocalName == "alnAt")?.Value;
                    return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int at) && at is > 0 and < 256 ? at : 0;
            }
        }

        return null;
    }

    private void RunAtoms(MathRun run, Context ctx, List<MathBox> atoms)
    {
        bool normal = run.Normal;
        string? style = normal ? "p" : run.Style;
        string? script = normal ? null : run.Script;
        foreach (Rune rune in run.Text.EnumerateRunes())
        {
            int c = rune.Value;
            if (c == '&')
            {
                atoms.Add(new MathBox { Alignment = true });
                continue;
            }

            if (c == ' ' && wideSpaces && !normal)
            {
                atoms.Add(new MathBox { Width = Size(ctx), Class = MathClass.Ordinary });
                continue;
            }

            int mapped = MathAlphabets.Map(c, style, script);
            ushort glyph = face.GetGlyph(mapped);
            if (glyph == 0 && mapped != c)
            {
                mapped = c;
                glyph = face.GetGlyph(c);
            }

            if (ctx.Style >= MathStyle.Script)
            {
                glyph = ScriptStyle(mapped, glyph, ctx.Style == MathStyle.Script ? 1u : 2u);
            }

            MathBox box = GlyphBox(glyph, Size(ctx));
            box.Class = MathAlphabets.Classify(mapped);
            if (box.Class == MathClass.Operator && ctx.Style == MathStyle.Display)
            {
                box = Center(Large(glyph, ctx), ctx);
                box.Class = MathClass.Operator;
            }

            atoms.Add(box);
        }
    }

    private MathBox Join(List<MathBox> atoms, Context ctx)
    {
        List<MathBox> segments = Segments(atoms, ctx);
        if (segments.Count == 1)
        {
            return segments[0];
        }

        MathBox box = new();
        foreach (MathBox segment in segments)
        {
            box.Add(segment, box.Width, 0);
            box.Width += segment.Width;
        }

        box.Italic = segments[^1].Italic;
        return Finish(box);
    }

    /// <summary>
    /// Spaces <paramref name="atoms"/> by their classes and joins them into boxes, one for each run between alignment points;
    /// the space before an atom that starts a segment begins that segment.
    /// </summary>
    private List<MathBox> Segments(List<MathBox> atoms, Context ctx)
    {
        List<MathBox> segments = [];
        Space(atoms, ctx, segments);
        return segments;
    }

    /// <summary>The width <see cref="Join"/> gives <paramref name="atoms"/>, without building the boxes.</summary>
    private float Width(List<MathBox> atoms, Context ctx) => Space(atoms, ctx, null);

    /// <summary>
    /// Spaces <paramref name="atoms"/> and returns their joined width; with <paramref name="segments"/>, also adds a box for each
    /// run between alignment points.
    /// </summary>
    private float Space(List<MathBox> atoms, Context ctx, List<MathBox>? segments)
    {
        Span<MathClass> classes = atoms.Count <= 128 ? stackalloc MathClass[atoms.Count] : new MathClass[atoms.Count];
        int previous = -1;
        for (int i = 0; i < atoms.Count; i++)
        {
            if (atoms[i].Alignment)
            {
                continue;
            }

            MathClass c = atoms[i].Class;
            if (c == MathClass.Binary && (previous < 0 || classes[previous] is MathClass.Binary or MathClass.Operator or MathClass.Relation or MathClass.Open or MathClass.Punctuation))
            {
                c = MathClass.Ordinary;
            }

            if (c is MathClass.Relation or MathClass.Close or MathClass.Punctuation && previous >= 0 && classes[previous] == MathClass.Binary)
            {
                classes[previous] = MathClass.Ordinary;
            }

            classes[i] = c;
            previous = i;
        }

        if (previous >= 0 && classes[previous] == MathClass.Binary)
        {
            classes[previous] = MathClass.Ordinary;
        }

        float mu = Size(ctx) / 18;
        bool tight = ctx.Style >= MathStyle.Script;
        List<(MathBox Box, float X)>? current = segments is null ? null : [];
        float total = 0;
        float x = 0;
        bool crossed = false;
        previous = -1;
        for (int i = 0; i < atoms.Count; i++)
        {
            MathBox atom = atoms[i];
            if (atom.Alignment)
            {
                // A slanted last glyph keeps its italic correction in its own segment.
                if (!crossed && previous >= 0 && atoms[previous] is { Glyph: not 0, Italic: > 0 } last)
                {
                    x += last.Italic;
                }

                if (segments is not null)
                {
                    segments.Add(Segment(current!, x));
                    current = [];
                }

                total += x;
                x = 0;
                crossed = true;
                continue;
            }

            if (previous >= 0)
            {
                MathBox before = atoms[previous];
                // Word adds a slanted glyph's italic correction before what follows, but not before an opening fence: f(x).
                if (!crossed && before.Glyph != 0 && before.Italic > 0 && atom.Italic == 0 && !Opens(atom))
                {
                    x += before.Italic;
                }

                x += Spacing[(int)classes[previous]][(int)classes[i]] switch
                {
                    '1' => 3 * mu,
                    '2' => 4 * mu,
                    '3' => 5 * mu,
                    'a' when !tight => 3 * mu,
                    'b' when !tight => 4 * mu,
                    'c' when !tight => 5 * mu,
                    _ => 0,
                };
            }

            current?.Add((atom, x));
            x += atom.Width;
            previous = i;
            crossed = false;
        }

        segments?.Add(Segment(current!, x));
        return total + x;
    }

    private static MathBox Segment(List<(MathBox Box, float X)> atoms, float width)
    {
        if (atoms.Count == 1 && atoms[0].X == 0)
        {
            return atoms[0].Box;
        }

        MathBox box = new() { Width = width };
        foreach ((MathBox atom, float x) in atoms)
        {
            box.Add(atom, x, 0);
        }

        if (atoms.Count > 0)
        {
            box.Italic = atoms[^1].Box.Italic;
        }

        return Finish(box);
    }

    private MathBox Object(MathObject o, Context ctx)
    {
        if (++depth > MaxDepth)
        {
            depth--;
            return new MathBox();
        }

        MathBox box = o.Kind switch
        {
            MathKind.Fraction => Fraction(o, ctx),
            MathKind.Radical => Radical(o, ctx),
            MathKind.Subscript or MathKind.Superscript or MathKind.SubSuperscript => Scripts(o, ctx),
            MathKind.PreSubSuperscript => PreScripts(o, ctx),
            MathKind.Nary => Nary(o, ctx),
            MathKind.Delimiter => Delimiter(o, ctx),
            MathKind.Accent => Accent(o, ctx),
            MathKind.Bar => Bar(o, ctx),
            MathKind.Box => BoxObject(o, ctx),
            MathKind.BorderBox => BorderBox(o, ctx),
            MathKind.EquationArray => EquationArray(o, ctx),
            MathKind.Matrix => Matrix(o, ctx),
            MathKind.Function => Function(o, ctx),
            MathKind.GroupCharacter => GroupCharacter(o, ctx),
            MathKind.LowerLimit or MathKind.UpperLimit => Limit(o, ctx),
            MathKind.Phantom => Phantom(o, ctx),
            _ => new MathBox(),
        };
        depth--;
        return box;
    }

    private MathBox Fraction(MathObject o, Context ctx)
    {
        string type = o.Properties?.Get("type") ?? "bar";
        if (settings.SmallFractions && ctx.Style == MathStyle.Display)
        {
            ctx = ctx with { Style = MathStyle.Text };
        }

        if (type == "lin")
        {
            MathBox slash = GlyphBox(face.GetGlyph('/'), Size(ctx));
            slash.Class = MathClass.Ordinary;
            List<MathBox> atoms = [Row(o.Argument("num"), ctx), slash, Row(o.Argument("den"), ctx)];
            MathBox linear = Join(atoms, ctx);
            linear.Class = MathClass.Inner;
            return linear;
        }

        bool skewed = type == "skw";
        MathBox num = Row(o.Argument("num"), skewed ? ctx : ctx.Numerator);
        MathBox den = Row(o.Argument("den"), skewed ? ctx.Cramp : ctx.Denominator);
        float axis = U(k.AxisHeight, ctx);
        bool display = ctx.Style == MathStyle.Display;
        MathBox box = new() { Class = MathClass.Inner };

        if (skewed)
        {
            float hGap = U(k.SkewedFractionHorizontalGap, ctx);
            float vGap = U(k.SkewedFractionVerticalGap, ctx);
            float rule = RuleWidth(k.FractionRuleThickness, ctx);
            float numShift = axis + vGap / 2 + num.Descent;
            float denShift = vGap / 2 - axis + den.Ascent;
            box.Add(num, 0, -numShift);
            box.Add(den, num.Width + hGap, denShift);
            float top = numShift + num.Ascent;
            float bottom = denShift + den.Descent;
            box.Items.Add(new MathItem.Line(new Vector2(num.Width + hGap * 0.2f, bottom), new Vector2(num.Width + hGap * 0.8f, -top), rule));
            box.Width = num.Width + hGap + den.Width;
            return Finish(box);
        }

        float shiftUp;
        float shiftDown;
        float thickness = type == "noBar" ? 0 : RuleWidth(k.FractionRuleThickness, ctx);
        if (type == "noBar")
        {
            shiftUp = U(display ? k.StackTopDisplayStyleShiftUp : k.StackTopShiftUp, ctx);
            shiftDown = U(display ? k.StackBottomDisplayStyleShiftDown : k.StackBottomShiftDown, ctx);
            float gap = U(display ? k.StackDisplayStyleGapMin : k.StackGapMin, ctx);
            float clearance = gap - (shiftUp - num.Descent - (den.Ascent - shiftDown));
            if (clearance > 0)
            {
                shiftUp += clearance / 2;
                shiftDown += clearance / 2;
            }
        }
        else
        {
            shiftUp = Math.Max(U(display ? k.FractionNumeratorDisplayStyleShiftUp : k.FractionNumeratorShiftUp, ctx),
                U(display ? k.FractionNumDisplayStyleGapMin : k.FractionNumeratorGapMin, ctx) + num.Descent + axis + thickness / 2);
            shiftDown = Math.Max(U(display ? k.FractionDenominatorDisplayStyleShiftDown : k.FractionDenominatorShiftDown, ctx),
                U(display ? k.FractionDenomDisplayStyleGapMin : k.FractionDenominatorGapMin, ctx) + den.Ascent - axis + thickness / 2);
        }

        // Word's bar is as wide as the wider argument with its italic correction, with no padding at the sides.
        float pad = 0;
        float numWidth = num.Width + num.Italic;
        float denWidth = den.Width + den.Italic;
        float width = Math.Max(numWidth, denWidth);
        box.Add(num, pad + (width - numWidth) / 2, -shiftUp);
        box.Add(den, pad + (width - denWidth) / 2, shiftDown);
        if (thickness > 0)
        {
            box.Items.Add(new MathItem.Rule(pad, -axis - thickness / 2, width, thickness));
        }

        box.Width = width + 2 * pad;
        return Finish(box);
    }

    private MathBox Radical(MathObject o, Context ctx)
    {
        MathBox body = Row(o.Argument("e"), ctx.Cramp);
        bool display = ctx.Style == MathStyle.Display;
        float thickness = RuleWidth(k.RadicalRuleThickness, ctx);
        float gap = U(display ? k.RadicalDisplayStyleVerticalGap : k.RadicalVerticalGap, ctx);
        float extra = U(k.RadicalExtraAscender, ctx);
        float need = body.Height + gap + thickness;
        MathBox sign = StretchVertical(face.GetGlyph(0x221A), need, Size(ctx));
        if (sign.Height > need)
        {
            gap += (sign.Height - need) / 2;
        }

        float ruleTop = body.Ascent + gap + thickness;
        float signY = sign.Ascent - ruleTop;

        MathBox box = new();
        float x = 0;
        MathRow degreeRow = o.Argument("deg");
        if (!(o.Properties?.On("degHide") ?? false) && degreeRow.Items.Count > 0)
        {
            MathBox degree = Row(degreeRow, new Context(MathStyle.ScriptScript, false));
            float before = U(k.RadicalKernBeforeDegree, ctx);
            float after = U(k.RadicalKernAfterDegree, ctx);
            float bottom = sign.Height * k.RadicalDegreeBottomRaisePercent / 100f - (signY + sign.Descent);
            box.Add(degree, before, -(bottom + degree.Descent));
            x = Math.Max(0, before + degree.Width + after);
        }

        box.Add(sign, x, signY);
        // Word's radical rule covers the body's italic correction too.
        box.Items.Add(new MathItem.Rule(x + sign.Width, -ruleTop, body.Width + body.Italic, thickness));
        box.Add(body, x + sign.Width, 0);
        box.Width = x + sign.Width + body.Width + body.Italic;
        box.Ascent = ruleTop + extra;
        return Finish(box);
    }

    private MathBox Scripts(MathObject o, Context ctx)
    {
        MathBox nucleus = Row(o.Argument("e"), ctx);
        MathBox? sub = o.Kind is MathKind.Subscript or MathKind.SubSuperscript ? Row(o.Argument("sub"), ctx.Sub) : null;
        MathBox? sup = o.Kind is MathKind.Superscript or MathKind.SubSuperscript ? Row(o.Argument("sup"), ctx.Script) : null;
        MathBox box = Attach(nucleus, sub, sup, ctx);
        box.Class = nucleus.Class;
        return box;
    }

    /// <summary>Attaches scripts to the right of a base, by the MATH table's script constants.</summary>
    private MathBox Attach(MathBox nucleus, MathBox? sub, MathBox? sup, Context ctx)
    {
        (float shiftUp, float shiftDown) = ScriptShifts(nucleus, sub, sup, ctx);
        MathBox box = new();
        box.Add(nucleus, 0, 0);
        float width = nucleus.Width;
        // On a large operator Word sets the superscript at the glyph's advance and the subscript its italic correction to the
        // left, and lets the operator's body follow its limits directly.
        bool op = nucleus.Class == MathClass.Operator;
        float space = op ? 0 : U(k.SpaceAfterScript, ctx);
        if (sup is not null)
        {
            float x = nucleus.Width + (op ? 0 : nucleus.Italic) + Kern(nucleus, MathKernCorner.TopRight, shiftUp - sup.Descent, ctx);
            box.Add(sup, x, -shiftUp);
            width = Math.Max(width, x + sup.Width + space);
        }

        if (sub is not null)
        {
            float x = nucleus.Width + Kern(nucleus, MathKernCorner.BottomRight, sub.Ascent - shiftDown, ctx) - (nucleus.Class == MathClass.Operator ? nucleus.Italic : 0);
            box.Add(sub, x, shiftDown);
            width = Math.Max(width, x + sub.Width + space);
        }

        box.Width = width;
        return Finish(box);
    }

    private (float Up, float Down) ScriptShifts(MathBox nucleus, MathBox? sub, MathBox? sup, Context ctx)
    {
        // Word shifts scripts on a base no taller than a letter (cos θ, a function) as it does on a single glyph.
        // Word hangs an integral's limits from its top and bottom; sums and products take ordinary script shifts.
        bool glyph = !nucleus.Integral
            && (nucleus.Glyph != 0 || nucleus.Ascent <= Size(ctx) * 0.75f && nucleus.Descent <= Size(ctx) * 0.3f);
        Context scriptCtx = ctx.Script;
        float up = 0;
        float down = 0;
        if (sup is not null)
        {
            up = Math.Max(glyph ? 0 : nucleus.Ascent - U(k.SuperscriptBaselineDropMax, scriptCtx),
                Math.Max(U(ctx.Cramped ? k.SuperscriptShiftUpCramped : k.SuperscriptShiftUp, ctx), sup.Descent + U(k.SuperscriptBottomMin, ctx)));
        }

        if (sub is not null)
        {
            down = Math.Max(glyph ? 0 : nucleus.Descent + U(k.SubscriptBaselineDropMin, scriptCtx),
                Math.Max(U(k.SubscriptShiftDown, ctx), sub.Ascent - U(k.SubscriptTopMax, ctx)));
        }

        if (sub is not null && sup is not null)
        {
            float gap = up - sup.Descent - (sub.Ascent - down);
            float need = U(k.SubSuperscriptGapMin, ctx) - gap;
            if (need > 0)
            {
                float raise = Math.Min(need, U(k.SuperscriptBottomMaxWithSubscript, ctx) - (up - sup.Descent));
                if (raise > 0)
                {
                    up += raise;
                    need -= raise;
                }

                down += Math.Max(0, need);
            }
        }

        return (up, down);
    }

    private float Kern(MathBox nucleus, MathKernCorner corner, float height, Context ctx)
    {
        if (nucleus.Glyph == 0)
        {
            return 0;
        }

        float scale = nucleus.GlyphSize / unitsPerEm;
        return math.GetKerning(nucleus.Glyph, corner, height / scale) * scale;
    }

    private MathBox PreScripts(MathObject o, Context ctx)
    {
        MathBox nucleus = Row(o.Argument("e"), ctx);
        MathBox sub = Row(o.Argument("sub"), ctx.Sub);
        MathBox sup = Row(o.Argument("sup"), ctx.Script);
        (float up, float down) = ScriptShifts(new MathBox { Glyph = nucleus.Glyph, Ascent = nucleus.Ascent, Descent = nucleus.Descent }, sub, sup, ctx);
        float space = U(k.SpaceAfterScript, ctx);
        float width = Math.Max(sub.Width, sup.Width) + space;
        MathBox box = new() { Class = nucleus.Class };
        box.Add(sup, width - space - sup.Width, -up);
        box.Add(sub, width - space - sub.Width, down);
        box.Add(nucleus, width, 0);
        box.Width = width + nucleus.Width;
        box.Italic = nucleus.Italic;
        return Finish(box);
    }

    private MathBox Nary(MathObject o, Context ctx)
    {
        string chr = o.Properties?.Char("chr", null) ?? char.ConvertFromUtf32(0x222B);
        int c = chr.Length > 0 ? char.ConvertToUtf32(chr, 0) : 0;
        bool integral = MathAlphabets.IsIntegral(c);
        string limits = o.Properties?.Get("limLoc") ?? (integral ? settings.IntegralLimits : settings.NaryLimits);
        MathBox body = Row(o.Argument("e"), ctx);

        MathBox op;
        if (c == 0)
        {
            op = new MathBox();
        }
        else
        {
            ushort glyph = face.GetGlyph(c);
            op = ctx.Style == MathStyle.Display ? Large(glyph, ctx) : GlyphBox(glyph, Size(ctx));
            if (o.Properties?.On("grow") ?? false)
            {
                float axis = U(k.AxisHeight, ctx);
                float need = 2 * Math.Max(body.Ascent - axis, body.Descent + axis);
                if (need > op.Height)
                {
                    op = StretchVertical(glyph, need, Size(ctx));
                }
            }

            op = Center(op, ctx);
        }

        MathBox? sub = o.Properties?.On("subHide") ?? false ? null : Row(o.Argument("sub"), ctx.Sub);
        MathBox? sup = o.Properties?.On("supHide") ?? false ? null : Row(o.Argument("sup"), ctx.Script);
        MathBox opWithLimits;
        if (limits == "undOvr" && ctx.Style == MathStyle.Display)
        {
            opWithLimits = UnderOver(op, sub, sup, ctx);
        }
        else
        {
            MathBox nucleus = op;
            if (op.Items.Count == 1 && op.Items[0] is MathItem.Child { Box.Glyph: not 0 } child)
            {
                nucleus.Glyph = child.Box.Glyph;
                nucleus.GlyphSize = child.Box.GlyphSize;
                nucleus.Italic = child.Box.Italic;
            }

            nucleus.Class = MathClass.Operator;
            nucleus.Integral = integral;
            opWithLimits = sub is null && sup is null ? op : Attach(nucleus, sub, sup, ctx);
        }

        // In a display equation Word sets the body right after an operator's side limits, with no thin space.
        opWithLimits.Class = ctx.Style == MathStyle.Display && limits != "undOvr" && (sub is not null || sup is not null) ? MathClass.Ordinary : MathClass.Operator;
        MathBox box = Join([opWithLimits, body], ctx);
        box.Class = MathClass.Operator;
        return box;
    }

    /// <summary>Places limits centred over and under an operator, by the MATH table's limit constants.</summary>
    private MathBox UnderOver(MathBox nucleus, MathBox? under, MathBox? over, Context ctx)
    {
        float italic = nucleus.Italic;
        if (nucleus.Items.Count == 1 && nucleus.Items[0] is MathItem.Child child)
        {
            italic = child.Box.Italic;
        }

        float width = Math.Max(nucleus.Width, Math.Max(under?.Width ?? 0, over?.Width ?? 0));
        MathBox box = new() { Width = width, Class = nucleus.Class };
        box.Add(nucleus, (width - nucleus.Width) / 2, 0);
        if (over is not null)
        {
            float gap = Math.Max(U(k.UpperLimitGapMin, ctx), U(k.UpperLimitBaselineRiseMin, ctx) - over.Descent);
            box.Add(over, (width - over.Width + italic) / 2, -(nucleus.Ascent + gap + over.Descent));
        }

        if (under is not null)
        {
            float gap = Math.Max(U(k.LowerLimitGapMin, ctx), U(k.LowerLimitBaselineDropMin, ctx) - under.Ascent);
            box.Add(under, (width - under.Width - italic) / 2, nucleus.Descent + gap + under.Ascent);
        }

        return Finish(box);
    }

    private MathBox Delimiter(MathObject o, Context ctx)
    {
        string open = o.Properties?.Char("begChr", "(") ?? "(";
        string close = o.Properties?.Char("endChr", ")") ?? ")";
        string separator = o.Properties?.Char("sepChr", "|") ?? "|";
        bool grow = o.Properties?.On("grow", absent: true) ?? true;
        bool match = o.Properties?.Get("shp") == "match";

        List<MathBox> atoms = [];
        List<MathBox> parts = [.. o.All("e").Select(e => Row(e, ctx))];
        float ascent = 0;
        float descent = 0;
        foreach (MathBox part in parts)
        {
            ascent = Math.Max(ascent, part.Ascent);
            descent = Math.Max(descent, part.Descent);
        }

        float axis = U(k.AxisHeight, ctx);
        // Word sizes a fence by its content's height, not by the content's reach either side of the axis.
        float target = ascent + descent;
        target = Math.Max(target * 0.901f, target - Size(ctx) / 10);
        bool stretch = grow && ascent + descent > U(k.DelimitedSubFormulaMinHeight, ctx) * 0.5f;

        MathBox? Fence(string text, MathClass cls)
        {
            if (text.Length == 0)
            {
                return null;
            }

            ushort glyph = face.GetGlyph(char.ConvertToUtf32(text, 0));
            MathBox fence = stretch ? StretchVertical(glyph, target, Size(ctx)) : GlyphBox(glyph, Size(ctx));
            // Word centres fences on the math axis; a fence matched to its content keeps the content's baseline.
            fence = stretch || !match ? Center(fence, ctx) : fence;
            fence.Class = cls;
            return fence;
        }

        if (Fence(open, MathClass.Open) is { } left)
        {
            atoms.Add(left);
        }

        for (int i = 0; i < parts.Count; i++)
        {
            if (i > 0 && Fence(separator, MathClass.Ordinary) is { } bar)
            {
                atoms.Add(bar);
            }

            MathBox part = parts[i];
            part.Class = MathClass.Ordinary;
            atoms.Add(part);
        }

        if (Fence(close, MathClass.Close) is { } right)
        {
            atoms.Add(right);
        }

        // Word spaces a delimiter like an ordinary atom: no thin space between f and (x).
        MathBox box = Join(atoms, ctx);
        box.Class = MathClass.Ordinary;
        if (box.Glyph != 0)
        {
            box = Wrap(box);
            box.Class = MathClass.Ordinary;
        }

        return box;
    }

    private MathBox Accent(MathObject o, Context ctx)
    {
        string chr = o.Properties?.Char("chr", null) ?? char.ConvertFromUtf32(0x302);
        MathBox nucleus = Row(o.Argument("e"), ctx.Cramp);
        if (chr.Length == 0)
        {
            return nucleus;
        }

        ushort glyph = face.GetGlyph(char.ConvertToUtf32(chr, 0));
        float sz = Size(ctx);
        float scale = sz / unitsPerEm;
        foreach (MathGlyphVariant variant in math.GetVariants(glyph, MathDirection.Horizontal))
        {
            if (variant.Advance * scale <= nucleus.Width * 1.1f || variant.Glyph == glyph)
            {
                glyph = variant.Glyph;
            }
        }

        MathBox accent = GlyphBox(glyph, sz);
        GlyphExtents ink = face.GetGlyphExtents(glyph);
        float accentAttach = (math.GetTopAccentAttachment(glyph) ?? (ink.XMin + ink.XMax) / 2) * scale;
        float baseAttach = nucleus.TopAccent ?? (nucleus.Glyph != 0 ? (nucleus.Width + nucleus.Italic) / 2 : nucleus.Width / 2);
        float lift = Math.Max(0, nucleus.Ascent - U(k.AccentBaseHeight, ctx));

        MathBox box = new() { Width = nucleus.Width, Italic = nucleus.Italic, Class = MathClass.Ordinary };
        box.Add(nucleus, 0, 0);
        box.Add(accent, baseAttach - accentAttach, -lift);
        return Finish(box);
    }

    private MathBox Bar(MathObject o, Context ctx)
    {
        bool top = o.Properties?.Get("pos") == "top";
        MathBox nucleus = Row(o.Argument("e"), top ? ctx.Cramp : ctx);
        MathBox box = new() { Width = nucleus.Width, Class = MathClass.Ordinary };
        box.Add(nucleus, 0, 0);
        if (top)
        {
            float t = RuleWidth(k.OverbarRuleThickness, ctx);
            float y = nucleus.Ascent + U(k.OverbarVerticalGap, ctx) + t;
            box.Items.Add(new MathItem.Rule(0, -y, nucleus.Width, t));
            box.Ascent = y + U(k.OverbarExtraAscender, ctx);
        }
        else
        {
            float t = RuleWidth(k.UnderbarRuleThickness, ctx);
            float y = nucleus.Descent + U(k.UnderbarVerticalGap, ctx);
            box.Items.Add(new MathItem.Rule(0, y, nucleus.Width, t));
            box.Descent = y + t + U(k.UnderbarExtraDescender, ctx);
        }

        return Finish(box);
    }

    private MathBox BoxObject(MathObject o, Context ctx)
    {
        MathBox box = Wrap(Row(o.Argument("e"), ctx));
        box.Class = o.Properties?.On("opEmu") ?? false ? MathClass.Operator : MathClass.Ordinary;
        return box;
    }

    private MathBox BorderBox(MathObject o, Context ctx)
    {
        MathBox nucleus = Row(o.Argument("e"), ctx);
        MathProperties? p = o.Properties;
        // Word draws the border inside the box at the font's rule thickness rounded down to 1/600 inch, 0.17 em from the content (with the content's italic
        // correction on the right); a hidden top or bottom takes its padding with it.
        float t = MathF.Max(1, MathF.Floor(U(k.OverbarRuleThickness, ctx) / 0.12f + 0.01f)) * 0.12f;
        float pad = Size(ctx) * 0.17f;
        bool hideTop = p?.On("hideTop") ?? false;
        bool hideBot = p?.On("hideBot") ?? false;
        float left = t / 2;
        float right = t + pad + nucleus.Width + nucleus.Italic + pad + t / 2;
        float top = -(nucleus.Ascent + (hideTop ? 0 : pad + t / 2));
        float bottom = nucleus.Descent + (hideBot ? 0 : pad + t / 2);
        MathBox box = new() { Width = right + t / 2, Class = MathClass.Ordinary };
        box.Add(nucleus, t + pad, 0);

        void Line(float x1, float y1, float x2, float y2) => box.Items.Add(new MathItem.Line(new Vector2(x1, y1), new Vector2(x2, y2), t));

        if (!hideTop)
        {
            Line(left - t / 2, top, right + t / 2, top);
        }

        if (!hideBot)
        {
            Line(left - t / 2, bottom, right + t / 2, bottom);
        }

        if (!(p?.On("hideLeft") ?? false))
        {
            Line(left, top, left, bottom);
        }

        if (!(p?.On("hideRight") ?? false))
        {
            Line(right, top, right, bottom);
        }

        if (p?.On("strikeH") ?? false)
        {
            float axis = -U(k.AxisHeight, ctx);
            Line(left, axis, right, axis);
        }

        if (p?.On("strikeV") ?? false)
        {
            Line(right / 2, top, right / 2, bottom);
        }

        if (p?.On("strikeBLTR") ?? false)
        {
            Line(left, bottom, right, top);
        }

        if (p?.On("strikeTLBR") ?? false)
        {
            Line(left, top, right, bottom);
        }

        box.Ascent = -top + t / 2;
        box.Descent = bottom + t / 2;
        return Finish(box);
    }

    private MathBox EquationArray(MathObject o, Context ctx)
    {
        // In a display equation Word widens each space in an equation array to an em, so "x,  x≥0" makes columns.
        bool wide = wideSpaces;
        wideSpaces = ctx.Style == MathStyle.Display;
        List<List<MathBox>> rows = [.. o.All("e").Select(e => Segments(Atoms(e, ctx), ctx))];
        wideSpaces = wide;
        int columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        float[] widths = new float[columns];
        foreach (List<MathBox> row in rows)
        {
            for (int i = 0; i < row.Count; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Width);
            }
        }

        // Alignment points pair up: the column before an odd point aligns right, the one after it left.
        List<MathBox> lines = [];
        foreach (List<MathBox> row in rows)
        {
            MathBox line = new();
            float x = 0;
            for (int i = 0; i < row.Count; i++)
            {
                float offset = row.Count == 1 ? (widths[i] - row[i].Width) / 2 : i % 2 == 0 ? widths[i] - row[i].Width : 0;
                line.Add(row[i], x + offset, 0);
                x += widths[i];
            }

            line.Width = x;
            lines.Add(Finish(line));
        }

        return Grid(lines, o.Properties, ctx);
    }

    private MathBox Matrix(MathObject o, Context ctx)
    {
        bool hidden = hidePlaceholders;
        hidePlaceholders |= o.Properties?.On("plcHide") ?? false;
        List<List<MathBox>> cells = [.. o.Rows.Select(r => r.Select(c => Row(c, ctx)).ToList())];
        hidePlaceholders = hidden;
        int columns = cells.Count == 0 ? 0 : cells.Max(r => r.Count);
        float[] widths = new float[columns];
        foreach (List<MathBox> row in cells)
        {
            for (int i = 0; i < row.Count; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Width);
            }
        }

        string[] justify = ColumnJustification(o.Properties, columns);

        // m:cGpRule: single (an em), one and a half, double, exactly m:cGp twips, or m:cGp half-ems; m:cSp is the least
        // width a column takes, in twips.
        float em = Size(ctx);
        int gapValue = o.Properties?.Int("cGp", 0) ?? 0;
        float gap = (o.Properties?.Int("cGpRule", 0) ?? 0) switch
        {
            1 => 1.5f * em,
            2 => 2 * em,
            3 => gapValue / 20f,
            4 => gapValue * 0.5f * em,
            _ => em,
        };
        float least = (o.Properties?.Int("cSp", 0) ?? 0) / 20f;
        for (int i = 0; i < columns; i++)
        {
            widths[i] = Math.Max(widths[i], least);
        }

        List<MathBox> lines = [];
        foreach (List<MathBox> row in cells)
        {
            MathBox line = new();
            float x = 0;
            for (int i = 0; i < columns; i++)
            {
                if (i < row.Count)
                {
                    float offset = justify[i] switch
                    {
                        "left" => 0,
                        "right" => widths[i] - row[i].Width,
                        _ => (widths[i] - row[i].Width) / 2,
                    };
                    line.Add(row[i], x + offset, 0);
                }

                x += widths[i] + (i < columns - 1 ? gap : 0);
            }

            line.Width = x;
            lines.Add(Finish(line));
        }

        return Grid(lines, o.Properties, ctx);
    }

    private static string[] ColumnJustification(MathProperties? properties, int columns)
    {
        string[] justify = [.. Enumerable.Repeat("center", columns)];
        if (properties?.Element("mcs") is not { } mcs)
        {
            return justify;
        }

        int column = 0;
        foreach (System.Xml.Linq.XElement mc in mcs.Elements())
        {
            System.Xml.Linq.XElement? mcPr = mc.Elements().FirstOrDefault(e => e.Name.LocalName == "mcPr");
            int count = 1;
            string jc = "center";
            foreach (System.Xml.Linq.XElement p in mcPr?.Elements() ?? [])
            {
                string? value = p.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value;
                if (p.Name.LocalName == "count" && int.TryParse(value, out int n))
                {
                    count = Math.Clamp(n, 1, 64);
                }
                else if (p.Name.LocalName == "mcJc" && value is not null)
                {
                    jc = value;
                }
            }

            for (int i = 0; i < count && column < columns; i++)
            {
                justify[column++] = jc;
            }
        }

        return justify;
    }

    /// <summary>Stacks the lines of an equation array or matrix and places the stack on the baseline per <c>m:baseJc</c>.</summary>
    private MathBox Grid(List<MathBox> lines, MathProperties? properties, Context ctx)
    {
        float leading = Math.Max(U(k.MathLeading, ctx), Size(ctx) * 0.15f);

        // m:rSpRule: single, one and a half, double, exactly m:rSp twips, or m:rSp half-lines, baseline to baseline. Word's
        // single pitch is 1.17 em (14.04 pt at 12 pt).
        float single = Size(ctx) * 1.17f;
        int rule = properties?.Int("rSpRule", 0) ?? 0;
        int spacing = properties?.Int("rSp", 0) ?? 0;
        float pitch = rule switch
        {
            1 => 1.5f * single,
            2 => 2 * single,
            3 => spacing / 20f,
            4 => spacing * 0.5f * single,
            _ => single,
        };
        MathBox box = new() { Class = MathClass.Ordinary };
        float y = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                float natural = lines[i - 1].Descent + leading + lines[i].Ascent;
                y += rule == 3 ? pitch : Math.Max(pitch, natural);
            }

            box.Add(lines[i], 0, y);
            box.Width = Math.Max(box.Width, lines[i].Width);
        }

        if (lines.Count == 0)
        {
            return box;
        }

        float top = -lines[0].Ascent;
        float bottom = y + lines[^1].Descent;
        string justify = properties?.Get("baseJc") ?? "center";
        float shift = justify switch
        {
            "top" => 0,
            "bottom" or "bot" => -y,
            _ => -((top + bottom) / 2) - U(k.AxisHeight, ctx),
        };

        MathBox placed = new() { Width = box.Width, Class = MathClass.Ordinary };
        placed.Add(Finish(box), 0, shift);
        return Finish(placed);
    }

    private MathBox Function(MathObject o, Context ctx)
    {
        MathBox name = Row(o.Argument("fName"), ctx);
        if (name.Glyph != 0)
        {
            name = Wrap(name);
        }

        name.Class = MathClass.Operator;
        MathBox argument = Row(o.Argument("e"), ctx);
        MathBox box = Join([name, argument], ctx);
        if (box == name || box == argument)
        {
            box = Wrap(box);
        }

        box.Class = MathClass.Ordinary;
        return box;
    }

    private MathBox GroupCharacter(MathObject o, Context ctx)
    {
        string chr = o.Properties?.Char("chr", null) ?? char.ConvertFromUtf32(0x23DF);
        bool top = o.Properties?.Get("pos") == "top";
        MathBox nucleus = Row(o.Argument("e"), ctx);
        MathBox box = new() { Width = nucleus.Width, Class = MathClass.Ordinary };
        box.Add(nucleus, 0, 0);
        if (chr.Length > 0)
        {
            ushort glyph = face.GetGlyph(char.ConvertToUtf32(chr, 0));
            MathBox group = StretchHorizontal(glyph, nucleus.Width, Size(ctx));
            float x = (nucleus.Width - group.Width) / 2;
            if (top)
            {
                float y = nucleus.Ascent + U(k.StretchStackGapAboveMin, ctx) + group.Descent;
                box.Add(group, x, -y);
            }
            else
            {
                // The brace's glyphs are drawn below the baseline already: Word leaves them there when they clear the content.
                float inkTop = float.MinValue;
                foreach (MathItem item in group.Items)
                {
                    if (item is MathItem.Glyphs glyphs)
                    {
                        foreach (ushort id in glyphs.Ids)
                        {
                            inkTop = Math.Max(inkTop, face.GetGlyphExtents(id).YMax * glyphs.Size / unitsPerEm);
                        }
                    }
                }

                inkTop = inkTop == float.MinValue ? group.Ascent : inkTop;
                float y = Math.Max(0, nucleus.Descent + U(k.StretchStackGapBelowMin, ctx) + inkTop);
                box.Add(group, x, y);
            }

            box.Width = Math.Max(nucleus.Width, group.Width);
        }

        return Finish(box);
    }

    private MathBox Limit(MathObject o, Context ctx)
    {
        MathBox nucleus = Row(o.Argument("e"), ctx);
        MathBox limit = Row(o.Argument("lim"), o.Kind == MathKind.LowerLimit ? ctx.Sub : ctx.Script);
        MathBox box = o.Kind == MathKind.LowerLimit ? UnderOver(nucleus, limit, null, ctx) : UnderOver(nucleus, null, limit, ctx);
        box.Class = nucleus.Class == MathClass.Operator ? MathClass.Operator : MathClass.Ordinary;
        return box;
    }

    private MathBox Phantom(MathObject o, Context ctx)
    {
        MathBox nucleus = Row(o.Argument("e"), ctx);
        MathProperties? p = o.Properties;
        MathBox box = Wrap(nucleus);
        box.Hidden = !(p?.On("show", absent: true) ?? true);
        if (p?.On("zeroWid") ?? false)
        {
            box.Width = 0;
        }

        if (p?.On("zeroAsc") ?? false)
        {
            box.Ascent = 0;
        }

        if (p?.On("zeroDesc") ?? false)
        {
            box.Descent = 0;
        }

        return box;
    }

    /// <summary>
    /// A rule's thickness. Word rounds fraction bars, radical and bar rules up to a whole 1/600 inch (0.12 pt): 0.84 pt for
    /// Cambria Math's 133 units at 12 pt, 0.6 pt in scripts.
    /// </summary>
    private float RuleWidth(float value, Context ctx) => MathF.Ceiling(U(value, ctx) / 0.12f - 0.01f) * 0.12f;

    private readonly Dictionary<(int, uint), ushort> scriptGlyphs = [];

    /// <summary>
    /// The script-style form of a character through the font's <c>ssty</c> feature (1 for scripts, 2 for second-level scripts),
    /// as Word draws them: wider, sturdier glyphs that read well small.
    /// </summary>
    private ushort ScriptStyle(int c, ushort glyph, uint level)
    {
        if (scriptGlyphs.TryGetValue((c, level), out ushort cached))
        {
            return cached;
        }

        // The feature sits under the font's math script, which shaping never selects, so the forms come from the feature's
        // closure: Cambria Math and STIX Two Math number the first-level forms before the second-level ones.
        ushort result = glyph;
        if (face.Layout is { } layout)
        {
            List<ushort> forms = [.. layout.GetClosure([glyph], [new FontTag("ssty")]).Where(g => g != glyph).Order()];
            if (forms.Count > 0)
            {
                result = forms[Math.Min((int)level, forms.Count) - 1];
            }
        }

        scriptGlyphs[(c, level)] = result;
        return result;
    }

    /// <summary>Whether a box begins with an opening fence: a fence glyph, or a delimiter object.</summary>
    private static bool Opens(MathBox box)
    {
        for (int depth = 0; depth < 8; depth++)
        {
            if (box.Class == MathClass.Open)
            {
                return true;
            }

            if (box.Glyph != 0 || box.Items.Count == 0 || box.Items[0] is not MathItem.Child child)
            {
                return false;
            }

            box = child.Box;
        }

        return false;
    }

    /// <summary>
    /// A large operator for display style: the first variant at least the display operator height. Word asks a quarter more
    /// than the constant: with Cambria Math its display integrals take the 2.15 em variant, not the 1.35 em one just over it,
    /// while sums and products keep their 1.9 em variants.
    /// </summary>
    private MathBox Large(ushort glyph, Context ctx)
    {
        float sz = Size(ctx);
        float scale = sz / unitsPerEm;
        float min = k.DisplayOperatorMinHeight * 1.25f * scale;
        ushort chosen = glyph;
        foreach (MathGlyphVariant variant in math.GetVariants(glyph, MathDirection.Vertical))
        {
            chosen = variant.Glyph;
            if (variant.Advance * scale >= min)
            {
                break;
            }
        }

        return GlyphBox(chosen, sz);
    }

    /// <summary>Wraps a box so the center of its height sits on the math axis.</summary>
    private MathBox Center(MathBox box, Context ctx)
    {
        float shift = (box.Ascent - box.Descent) / 2 - U(k.AxisHeight, ctx);
        MathBox centered = new() { Width = box.Width, Italic = box.Italic, Class = box.Class };
        centered.Add(box, 0, shift);
        return Finish(centered);
    }

    private static MathBox Wrap(MathBox box)
    {
        MathBox wrapped = new() { Width = box.Width, Italic = box.Italic, Class = box.Class, TopAccent = box.TopAccent };
        wrapped.Add(box, 0, 0);
        return Finish(wrapped);
    }

    /// <summary>One glyph as a box, measured by its ink, at <paramref name="sz"/> points.</summary>
    private MathBox GlyphBox(ushort glyph, float sz)
    {
        float scale = sz / unitsPerEm;
        GlyphExtents ink = face.GetGlyphExtents(glyph);
        MathBox box = new()
        {
            Width = face.GetAdvance(glyph) * scale,
            Ascent = Math.Max(0, ink.YMax * scale),
            Descent = Math.Max(0, -ink.YMin * scale),
            Italic = (math.GetItalicsCorrection(glyph) ?? 0) * scale,
            Glyph = glyph,
            GlyphSize = sz,
        };
        if (math.GetTopAccentAttachment(glyph) is float attach)
        {
            box.TopAccent = attach * scale;
        }

        box.Items.Add(new MathItem.Glyphs(face, sz, [glyph], [Vector2.Zero]));
        return box;
    }

    /// <summary>
    /// A glyph at least <paramref name="target"/> points tall: the first vertical variant that reaches it, else the glyph
    /// assembly, else the largest variant. The box's baseline is the glyph's own (variants) or its bottom (assemblies).
    /// </summary>
    public MathBox StretchVertical(ushort glyph, float target, float sz)
    {
        float scale = sz / unitsPerEm;
        ushort largest = glyph;
        foreach (MathGlyphVariant variant in math.GetVariants(glyph, MathDirection.Vertical))
        {
            largest = variant.Glyph;
            if (variant.Advance * scale >= target)
            {
                return GlyphBox(variant.Glyph, sz);
            }
        }

        if (math.GetAssembly(glyph, MathDirection.Vertical) is { } assembly && Assemble(assembly, target / scale) is { } parts)
        {
            MathBox box = new() { Glyph = 0, Italic = assembly.ItalicsCorrection * scale };
            ushort[] ids = new ushort[parts.Count];
            Vector2[] positions = new Vector2[parts.Count];
            float top = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                GlyphExtents ink = face.GetGlyphExtents(parts[i].Glyph);
                ids[i] = parts[i].Glyph;
                positions[i] = new Vector2(0, -(parts[i].At - ink.YMin) * scale);
                box.Width = Math.Max(box.Width, face.GetAdvance(parts[i].Glyph) * scale);
                top = Math.Max(top, parts[i].At + ink.Height);
            }

            box.Ascent = top * scale;
            box.Items.Add(new MathItem.Glyphs(face, sz, ids, positions));
            return box;
        }

        return GlyphBox(largest, sz);
    }

    /// <summary>A glyph at least <paramref name="target"/> points wide, like <see cref="StretchVertical"/> across.</summary>
    public MathBox StretchHorizontal(ushort glyph, float target, float sz)
    {
        float scale = sz / unitsPerEm;
        ushort largest = glyph;
        foreach (MathGlyphVariant variant in math.GetVariants(glyph, MathDirection.Horizontal))
        {
            largest = variant.Glyph;
            if (variant.Advance * scale >= target)
            {
                return GlyphBox(variant.Glyph, sz);
            }
        }

        if (math.GetAssembly(glyph, MathDirection.Horizontal) is { } assembly && Assemble(assembly, target / scale, horizontal: true) is { } parts)
        {
            MathBox box = new();
            ushort[] ids = new ushort[parts.Count];
            Vector2[] positions = new Vector2[parts.Count];
            float right = 0;
            for (int i = 0; i < parts.Count; i++)
            {
                GlyphExtents ink = face.GetGlyphExtents(parts[i].Glyph);
                ids[i] = parts[i].Glyph;
                positions[i] = new Vector2(parts[i].At * scale, 0);
                box.Ascent = Math.Max(box.Ascent, ink.YMax * scale);
                box.Descent = Math.Max(box.Descent, -ink.YMin * scale);
                right = Math.Max(right, parts[i].At + parts[i].Advance);
            }

            box.Width = right * scale;
            box.Items.Add(new MathItem.Glyphs(face, sz, ids, positions));
            return box;
        }

        return GlyphBox(largest, sz);
    }

    /// <summary>
    /// The parts of <paramref name="assembly"/> repeated and overlapped to span <paramref name="target"/> font units, each with
    /// its start along the assembly; null when the assembly has no parts.
    /// </summary>
    /// <remarks>
    /// Horizontally (braces, brackets over and under) Word uses no extender when the end and middle parts can overlap down to the
    /// target, however far they overlap, so a brace spans exactly its content.
    /// </remarks>
    internal List<(ushort Glyph, float At, float Advance)>? Assemble(MathGlyphAssembly assembly, float target, bool horizontal = false)
    {
        IReadOnlyList<MathGlyphPart> parts = assembly.Parts;
        if (parts.Count == 0)
        {
            return null;
        }

        float minOverlap = math.MinConnectorOverlap;
        List<MathGlyphPart> list = [];
        for (int repeats = horizontal ? 0 : 1; repeats <= 256; repeats++)
        {
            list.Clear();
            foreach (MathGlyphPart part in parts)
            {
                for (int i = 0; i < (part.IsExtender ? repeats : 1); i++)
                {
                    list.Add(part);
                }
            }

            float full = list.Sum(p => p.FullAdvance);
            float longest = full - minOverlap * (list.Count - 1);
            float shortest = list.Count > 0 ? list.Max(p => p.FullAdvance) : 0;
            if (horizontal && repeats == 0 && list.Count > 0 && (longest < target || shortest > target))
            {
                continue;
            }

            if (longest >= target || repeats == 256 || !parts.Any(p => p.IsExtender))
            {
                break;
            }
        }

        float total = list.Sum(p => p.FullAdvance);
        float overlap = list.Count > 1 ? Math.Max(minOverlap, (total - target) / (list.Count - 1)) : 0;
        List<(ushort Glyph, float At, float Advance)> placed = new(list.Count);
        float at = 0;
        for (int i = 0; i < list.Count; i++)
        {
            placed.Add((list[i].Glyph, at, list[i].FullAdvance));
            if (i + 1 < list.Count)
            {
                float most = Math.Max(minOverlap, Math.Min(list[i].EndConnectorLength, list[i + 1].StartConnectorLength));
                at += list[i].FullAdvance - (horizontal ? overlap : Math.Clamp(overlap, minOverlap, most));
            }
        }

        return placed;
    }

    /// <summary>Sets a box's ascent and descent to cover what it holds.</summary>
    private static MathBox Finish(MathBox box)
    {
        float ascent = box.Ascent;
        float descent = box.Descent;
        foreach (MathItem item in box.Items)
        {
            switch (item)
            {
                case MathItem.Child child:
                    ascent = Math.Max(ascent, child.Box.Ascent - child.Y);
                    descent = Math.Max(descent, child.Box.Descent + child.Y);
                    break;
                case MathItem.Rule rule:
                    ascent = Math.Max(ascent, -rule.Y);
                    descent = Math.Max(descent, rule.Y + rule.Height);
                    break;
                case MathItem.Line line:
                    ascent = Math.Max(ascent, -Math.Min(line.Start.Y, line.End.Y));
                    descent = Math.Max(descent, Math.Max(line.Start.Y, line.End.Y));
                    break;
            }
        }

        box.Ascent = ascent;
        box.Descent = descent;
        return box;
    }

    private readonly record struct Context(MathStyle Style, bool Cramped, bool InFraction = false)
    {
        public Context Script => new(Style <= MathStyle.Text ? MathStyle.Script : MathStyle.ScriptScript, Cramped, InFraction);

        public Context Sub => Script with { Cramped = true };

        // In a display equation Word shrinks fractions to text size once: a text-size fraction inside a fraction stays text size.
        public Context Numerator => new(InFraction && Style == MathStyle.Text ? Style : Style switch
        {
            MathStyle.Display => MathStyle.Text,
            MathStyle.Text => MathStyle.Script,
            _ => MathStyle.ScriptScript,
        }, Cramped, true);

        public Context Denominator => Numerator with { Cramped = true };

        public Context Cramp => this with { Cramped = true };

        public Context Larger => this with
        {
            Style = Style switch
            {
                MathStyle.ScriptScript => MathStyle.Script,
                MathStyle.Script => MathStyle.Text,
                _ => Style,
            },
        };
    }
}
