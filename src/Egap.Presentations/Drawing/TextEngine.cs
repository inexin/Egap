using System.Numerics;
using System.Text;
using System.Xml.Linq;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Fonts.Layout;
using Egami.Fonts.Unicode;

using Egap.Documents.DrawingML;
using Egap.Presentations.Pptx;

using Path = Egami.Drawing.Path;

namespace Egap.Presentations.Drawing;

/// <summary>
/// Lays out and draws a shape's text (ECMA-376 Part 1, 21.1.2): the body's insets, anchor, wrapping, columns, vertical text and
/// stored autofit; each paragraph's alignment, margins and indent, level, spacing, tab stops, direction and bullet or number;
/// each run as <see cref="TextRuns"/> sets it. Lines break through Egami's text layout, with shaping, bidi and line breaking
/// for every script and no automatic hyphenation.
/// </summary>
internal static class TextEngine
{
    private static readonly XNamespace A = PptxNames.A;

    /// <summary>The default tab stop spacing when the chain sets none: one inch.</summary>
    private const double DefaultTab = 914400;

    /// <summary>Draws <paramref name="frame"/>'s text into <paramref name="rect"/> (before the insets), turned by <paramref name="rotation"/> degrees about its centre.</summary>
    public static void Draw(Canvas canvas, RectF rect, double rotation, TextFrame frame)
    {
        if (!frame.Paragraphs.Any())
        {
            return;
        }

        double rot = rotation + ((frame.BodyNumber("rot") ?? 0) / 60000);
        string vert = frame.BodyAttribute("vert") ?? "horz";
        double turn = vert switch
        {
            "vert" => 90,
            "vert270" => 270,
            _ => 0,
        };

        float left = Emu(frame.BodyNumber("lIns"), 91440), top = Emu(frame.BodyNumber("tIns"), 45720);
        float right = Emu(frame.BodyNumber("rIns"), 91440), bottom = Emu(frame.BodyNumber("bIns"), 45720);
        RectF inner = new(rect.X + left, rect.Y + top, Math.Max(0, rect.Width - left - right), Math.Max(0, rect.Height - top - bottom));
        Vector2 center = new(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
        if (turn != 0)
        {
            // The text runs down the box: it is laid out across the box's height, then turned.
            RectF across = new(rect.X - ((rect.Height - rect.Width) / 2), rect.Y + ((rect.Height - rect.Width) / 2), rect.Height, rect.Width);
            inner = turn == 90
                ? new RectF(across.X + top, across.Y + right, Math.Max(0, across.Width - top - bottom), Math.Max(0, across.Height - left - right))
                : new RectF(across.X + bottom, across.Y + left, Math.Max(0, across.Width - top - bottom), Math.Max(0, across.Height - left - right));
        }

        XElement? autofit = frame.Autofit;
        float fontScale = 1, spacingCut = 0;
        if (autofit?.Name.LocalName == "normAutofit")
        {
            fontScale = (float)((PptxNames.Long(autofit, "fontScale") ?? 100000) / 100000.0);
            spacingCut = (float)((PptxNames.Long(autofit, "lnSpcReduction") ?? 0) / 100000.0);
        }

        int saved = canvas.Save();
        try
        {
            if (rot + turn != 0)
            {
                canvas.Translate(center.X, center.Y);
                canvas.Rotate((float)(rot + turn));
                canvas.Translate(-center.X, -center.Y);
            }

            Settings settings = new(frame, inner, frame.BodyAttribute("wrap") != "none", fontScale, spacingCut);
            WritingMode? mode = vert switch
            {
                "eaVert" or "wordArtVertRtl" => WritingMode.VerticalRl,
                "mongolianVert" or "wordArtVert" => WritingMode.VerticalLr,
                _ => null,
            };
            if (mode is WritingMode writing)
            {
                DrawVertical(canvas, settings, writing, vert.StartsWith("wordArt", StringComparison.Ordinal) ? TextOrientation.Upright : TextOrientation.Mixed);
            }
            else
            {
                DrawHorizontal(canvas, settings);
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }
    }

    /// <summary>The body-wide settings a paragraph is laid out with.</summary>
    private sealed record Settings(TextFrame Frame, RectF Inner, bool Wrap, float FontScale, float SpacingCut);

    /// <summary>A paragraph's lines, as one or two layouts: the first line on its own when it starts somewhere the others do not.</summary>
    private sealed class Block
    {
        public List<Segment> Segments { get; } = [];

        public float Before { get; init; }

        public float After { get; init; }

        public Mark? Bullet { get; init; }

        public List<TextAtom> Atoms { get; init; } = [];

        public bool Outlined { get; init; }
    }

    /// <summary>A layout and where it starts across the column, from the column's left edge.</summary>
    private sealed record Segment(TextLayout Layout, float X, List<TextAtom> Atoms, bool FirstOnly)
    {
        /// <summary>How far down the lines are drawn from where Egami sets their baselines, to sit where PowerPoint sets them.</summary>
        public float Shift { get; init; }
    }

    /// <summary>A bullet or number: its text laid out, or its picture, and where it starts across the column.</summary>
    private sealed record Mark(TextLayout? Text, BlipImage? Picture, float Size, float X);

    /// <summary>A run of lines of one segment placed in a column.</summary>
    private sealed record Band(Block Block, Segment Segment, int First, int Last, int Column, float Y, bool FirstOfBlock);

    private static void DrawHorizontal(Canvas canvas, Settings settings)
    {
        TextFrame frame = settings.Frame;
        RectF inner = settings.Inner;
        int columns = (int)Math.Clamp(frame.BodyNumber("numCol") ?? 1, 1, 16);
        float gap = Emu(frame.BodyNumber("spcCol"), 0);
        float columnWidth = columns == 1 ? inner.Width : Math.Max(1, (inner.Width - (gap * (columns - 1))) / columns);

        List<Block> blocks = Lay(settings, columnWidth, null, TextOrientation.Mixed);
        if (blocks.Count == 0)
        {
            return;
        }

        // Lines fill the first column to the bottom of the box, then the next; the last column takes what is left.
        List<Band> bands = [];
        int column = 0;
        float y = 0, used = 0;
        bool firstInColumn = true;
        foreach (Block block in blocks)
        {
            bool firstOfBlock = true;
            if (!firstInColumn)
            {
                y += block.Before;
            }

            foreach (Segment segment in block.Segments)
            {
                IReadOnlyList<TextLine> lines = segment.Layout.Lines;
                int count = segment.FirstOnly ? Math.Min(1, lines.Count) : lines.Count;
                int start = 0;
                for (int i = 0; i < count; i++)
                {
                    float height = lines[i].Bottom - lines[i].Top;
                    if (column < columns - 1 && !firstInColumn && y + height > inner.Height + 0.01f)
                    {
                        if (i > start)
                        {
                            bands.Add(Place(block, segment, start, i - 1, column, y - (lines[i - 1].Bottom - lines[start].Top), firstOfBlock && start == 0));
                            firstOfBlock = false;
                        }

                        used = Math.Max(used, y);
                        column++;
                        y = 0;
                        start = i;
                    }

                    y += height;
                    firstInColumn = false;
                }

                if (count > start)
                {
                    bands.Add(Place(block, segment, start, count - 1, column, y - (lines[count - 1].Bottom - lines[start].Top), firstOfBlock && start == 0));
                    firstOfBlock = false;
                }
            }

            y += block.After;
        }

        used = Math.Max(used, y - blocks[^1].After);

        // The anchor places the text in the box: a box filled past one column keeps its text at the top.
        string anchor = frame.BodyAttribute("anchor") ?? "t";
        float shift = column > 0 ? 0 : anchor switch
        {
            "ctr" => (inner.Height - used) / 2,
            "b" => inner.Height - used,
            _ => 0,
        };

        float across = 0;
        if (frame.BodyAttribute("anchorCtr") is "1" or "true" && bands.Count > 0)
        {
            // The block as a whole centred across the box, its lines keeping their alignment within it.
            float minX = float.MaxValue, maxX = float.MinValue;
            foreach (Band band in bands)
            {
                for (int i = band.First; i <= band.Last; i++)
                {
                    TextLine line = band.Segment.Layout.Lines[i];
                    minX = Math.Min(minX, band.Segment.X + line.Left);
                    maxX = Math.Max(maxX, band.Segment.X + line.Left + line.Width);
                }
            }

            if (minX <= maxX)
            {
                across = ((columnWidth - (maxX - minX)) / 2) - minX;
            }
        }

        foreach (Band band in bands)
        {
            float columnX = inner.X + across + ((columnWidth + gap) * band.Column);
            DrawBand(canvas, band, columnX, inner.Y + shift, frame);
        }

        static Band Place(Block block, Segment segment, int first, int last, int col, float bandTop, bool firstOfBlock) =>
            new(block, segment, first, last, col, bandTop, firstOfBlock);
    }

    /// <summary>Draws a band of lines: the whole layout when the band is all of it, else the band's lines clipped from it.</summary>
    private static void DrawBand(Canvas canvas, Band band, float columnX, float top, TextFrame frame)
    {
        Segment segment = band.Segment;
        TextLayout layout = segment.Layout;
        float x = columnX + segment.X;
        TextLine first = layout.Lines[band.First];
        TextLine last = layout.Lines[band.Last];
        Vector2 origin = new(x, top + band.Y - first.Top + segment.Shift);
        bool whole = band.First == 0 && band.Last == layout.Lines.Count - 1;
        int saved = canvas.Save();
        try
        {
            if (!whole)
            {
                canvas.ClipRect(RectF.FromEdges(-1e5f, origin.Y + first.Top, 1e5f, origin.Y + last.Bottom));
            }

            canvas.DrawTextLayout(layout, origin);
            if (band.Block.Outlined)
            {
                Outline(canvas, layout, band.First, band.Last, origin, segment, frame);
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
        }

        if (band.FirstOfBlock && band.Block.Bullet is { } bullet && layout.Lines.Count > 0)
        {
            float baseline = origin.Y + first.Baseline;
            float bulletX = columnX + bullet.X;
            if (bullet.Text is { Lines.Count: > 0 } text)
            {
                canvas.DrawTextLayout(text, new Vector2(bulletX, baseline - text.Lines[0].Baseline));
            }
            else if (bullet.Picture is { } picture)
            {
                picture.Draw(canvas, new RectF(bulletX, baseline - (bullet.Size * 0.8f), bullet.Size, bullet.Size), null, frame.Theme);
            }
        }
    }

    /// <summary>Strokes the outlines (<c>a:ln</c>) of the runs that have one, over their fill.</summary>
    private static void Outline(Canvas canvas, TextLayout layout, int first, int last, Vector2 origin, Segment segment, TextFrame frame)
    {
        for (int l = first; l <= last; l++)
        {
            foreach (LayoutRun run in layout.Lines[l].Runs)
            {
                if (run.Glyphs is not { Count: > 0 } glyphs || AtomAt(segment, run.Start)?.Look.Outline is not { } ln)
                {
                    continue;
                }

                float width = (float)((PptxNames.Long(ln, "w") ?? 9525) / (double)ShapeTransform.EmusPerPoint);
                PathSink sink = new(origin);
                float scale = glyphs.Font.Scale;
                ReadOnlySpan<Vector2> positions = glyphs.Positions;
                for (int g = 0; g < glyphs.Count; g++)
                {
                    sink.Matrix = new Matrix3x2(scale, 0, 0, -scale, positions[g].X, positions[g].Y);
                    glyphs.Font.Typeface.GetGlyphOutline(glyphs.Glyphs[g], sink);
                }

                Path path = sink.ToPath();
                XElement? fill = ln.Elements().FirstOrDefault(DrawingFills.IsFill);
                if (fill is not null && ShapePainter.PaintOf(fill, path.Bounds, frame.Theme, null) is { } paint)
                {
                    canvas.StrokePath(path, new Stroke(Math.Max(0.1f, width)) { Join = LineJoin.Round }, paint);
                }
            }
        }
    }

    private static TextAtom? AtomAt(Segment segment, int index)
    {
        foreach (TextAtom atom in segment.Atoms)
        {
            if (index < atom.Text.Length)
            {
                return atom;
            }

            index -= atom.Text.Length;
        }

        return null;
    }

    /// <summary>Vertical text (<c>eaVert</c>, <c>mongolianVert</c>, <c>wordArtVert</c>): each paragraph a block of vertical lines, blocks side by side.</summary>
    private static void DrawVertical(Canvas canvas, Settings settings, WritingMode mode, TextOrientation orientation)
    {
        // The lines run down the box: laid out at its height, with the box's width across them.
        RectF inner = settings.Inner;
        List<Block> blocks = Lay(settings with { Inner = new RectF(inner.X, inner.Y, inner.Height, inner.Width) }, inner.Height, mode, orientation);
        if (blocks.Count == 0)
        {
            return;
        }

        float total = blocks.Sum(b => b.Segments[0].Layout.Width + b.Before + b.After) - blocks[0].Before - blocks[^1].After;
        string anchor = settings.Frame.BodyAttribute("anchor") ?? "t";
        float slack = inner.Width - total;
        float start = anchor switch
        {
            "ctr" => slack / 2,
            "b" => mode == WritingMode.VerticalRl ? 0 : slack,
            _ => mode == WritingMode.VerticalRl ? slack : 0,
        };

        float x = mode == WritingMode.VerticalRl ? inner.X + start + total : inner.X + start;
        for (int i = 0; i < blocks.Count; i++)
        {
            Block block = blocks[i];
            Segment segment = block.Segments[0];
            float before = i == 0 ? 0 : block.Before;
            float width = segment.Layout.Width;
            if (mode == WritingMode.VerticalRl)
            {
                x -= before + width;
                canvas.DrawTextLayout(segment.Layout, new Vector2(x, inner.Y + segment.X));
                x -= block.After;
            }
            else
            {
                x += before;
                canvas.DrawTextLayout(segment.Layout, new Vector2(x, inner.Y + segment.X));
                x += width + block.After;
            }
        }
    }

    /// <summary>Lays out every paragraph of the body at <paramref name="width"/>, numbering the numbered ones as it goes.</summary>
    private static List<Block> Lay(Settings settings, float width, WritingMode? mode, TextOrientation orientation)
    {
        TextFrame frame = settings.Frame;
        List<Block> blocks = [];
        string?[] schemes = new string?[9];
        int[] counts = new int[9];
        foreach (XElement p in frame.Paragraphs)
        {
            XElement? pPr = p.Element(A + "pPr");
            int level = (int)Math.Clamp(PptxNames.Long(pPr, "lvl") ?? 0, 0, 8);
            XElement?[] chain = frame.Chain(pPr, level);
            for (int deeper = level + 1; deeper < 9; deeper++)
            {
                schemes[deeper] = null;
            }

            blocks.Add(LayParagraph(p, chain, settings, width, mode, orientation, level, schemes, counts));
        }

        return blocks;
    }

    private static Block LayParagraph(XElement p, XElement?[] chain, Settings settings, float width, WritingMode? mode, TextOrientation orientation, int level, string?[] schemes, int[] counts)
    {
        TextFrame frame = settings.Frame;
        float fontScale = settings.FontScale;

        // Line spacing: a share of each font's own line, less the autofit's reduction, or a height in points.
        XElement? lineSpacing = Bullets.First(chain, "lnSpc");
        double? points = PptxNames.Long(lineSpacing?.Element(A + "spcPts"), "val") / 100.0;
        double share = ((PptxNames.Long(lineSpacing?.Element(A + "spcPct"), "val") ?? 100000) / 100000.0) - settings.SpacingCut;
        LineHeight? LineHeightOf(RunLook look, string font)
        {
            if (points is double exact)
            {
                return Egami.Fonts.Layout.LineHeight.Absolute((float)Math.Max(0, exact));
            }

            return Egami.Fonts.Layout.LineHeight.Multiple((float)Math.Max(0, share * frame.Fonts.SingleLine(font, look.FontStyle)));
        }

        List<TextAtom> atoms = [];
        RunLook? first = null;
        bool hasText = false;
        foreach (XElement child in p.Elements())
        {
            string? text = child.Name.LocalName switch
            {
                "r" => (string?)child.Element(A + "t") ?? "",
                "br" => "\n",
                "fld" => null,
                _ => null,
            };
            if (child.Name.Namespace != A || (text is null && child.Name.LocalName != "fld"))
            {
                continue;
            }

            RunLook look = TextRuns.Look(child.Element(A + "rPr"), chain, frame, fontScale);
            if (child.Name.LocalName == "fld")
            {
                text = Fields.Text((string?)child.Attribute("type"), (string?)child.Element(A + "t") ?? "", frame.Fields, TextRuns.Culture(look.Language));
            }

            if (child.Name.LocalName != "br")
            {
                first ??= look;
                hasText |= text!.Length > 0;
            }

            TextRuns.Add(atoms, text!, look, frame, LineHeightOf, settings.Inner);
        }

        // An empty paragraph keeps the height of its end mark.
        RunLook mark = TextRuns.Look(p.Element(A + "endParaRPr"), chain, frame, fontScale);
        if (atoms.Count == 0 || !atoms.Exists(a => a.Text.Any(c => c != '\n')))
        {
            atoms.Add(new TextAtom("​", TextRuns.Style(mark, mark.Latin, frame, LineHeightOf(mark, mark.Latin), settings.Inner), mark));
        }

        RunLook lead = first ?? mark;
        float before = Spacing(chain, "spcBef", lead) ?? 0;
        float after = Spacing(chain, "spcAft", lead) ?? 0;

        // The bullet or number: none on an empty paragraph, which neither shows nor breaks a numbered list.
        XElement? kind = hasText ? Bullets.Kind(chain) : null;
        if (hasText)
        {
            if (kind?.Name.LocalName == "buAutoNum")
            {
                string scheme = (string?)kind.Attribute("type") ?? "arabicPeriod";
                counts[level] = schemes[level] == scheme ? counts[level] + 1 : (int)(PptxNames.Long(kind, "startAt") ?? 1);
                schemes[level] = scheme;
            }
            else
            {
                schemes[level] = null;
            }
        }

        bool rtl = Attribute(chain, "rtl") is "1" or "true";
        float marL = Emu(Number(chain, "marL"), 0);
        float marR = Emu(Number(chain, "marR"), 0);
        float indent = Emu(Number(chain, "indent"), 0);
        Mark? bullet = kind is null ? null : MakeBullet(kind, chain, lead, frame, fontScale, counts[level], settings.Inner);
        if (mode is not null)
        {
            // Vertical text sets its bullet at the head of the line.
            if (bullet?.Text is { } bulletText)
            {
                atoms.Insert(0, new TextAtom(bulletText.Paragraph.Text + " ", bulletText.Paragraph.GetStyle(0), lead));
            }

            float room = Math.Max(1, width - marL - marR);
            TextLayout layout = Layout(atoms, 0, ParagraphStyleOf(chain, frame, mark, rtl, mode, orientation, 0, room, settings, LineHeightOf(mark, mark.Latin)), room, true);
            Block vertical = new() { Before = before, After = after, Atoms = atoms };
            vertical.Segments.Add(new Segment(layout, marL, atoms, false));
            return vertical;
        }

        // Where the first line and the rest start, from the column's start edge: the bullet hangs in the indent when it fits.
        float bulletAt = Math.Max(0, marL + indent);
        float firstAt = Math.Max(0, marL + indent);
        if (bullet is not null)
        {
            float bulletEnd = bulletAt + BulletWidth(bullet);
            firstAt = indent < 0 && bulletEnd <= marL + 0.01f ? marL : bulletEnd;
        }

        float restAt = marL;
        Block block = new()
        {
            Before = before,
            After = after,
            Atoms = atoms,
            Outlined = atoms.Exists(a => a.Look.Outline is not null),
            Bullet = bullet is null ? null : bullet with { X = rtl ? width - bulletAt - BulletWidth(bullet) : bulletAt },
        };

        float firstRoom = Math.Max(1, width - firstAt - marR);
        TextLayout firstLayout = Layout(atoms, 0, ParagraphStyleOf(chain, frame, mark, rtl, null, orientation, firstAt, firstRoom, settings, LineHeightOf(mark, mark.Latin)), firstRoom, settings.Wrap);
        block.Segments.Add(new Segment(firstLayout, Across(firstLayout, firstAt, firstRoom, rtl, width, settings.Wrap), atoms, false) { Shift = Shift(firstLayout, atoms, mark, frame) });
        if (Math.Abs(firstAt - restAt) > 0.01f && firstLayout.Lines.Count > 1)
        {
            // The first line starts elsewhere than the rest: the rest of the text is laid out again from where it broke.
            int offset = firstLayout.Lines[0].End;
            float restRoom = Math.Max(1, width - restAt - marR);
            TextLayout rest = Layout(atoms, offset, ParagraphStyleOf(chain, frame, mark, rtl, null, orientation, restAt, restRoom, settings, LineHeightOf(mark, mark.Latin)), restRoom, settings.Wrap);
            block.Segments[0] = block.Segments[0] with { FirstOnly = true };
            List<TextAtom> restAtoms = Slice(atoms, offset);
            block.Segments.Add(new Segment(rest, Across(rest, restAt, restRoom, rtl, width, settings.Wrap), restAtoms, false) { Shift = Shift(rest, restAtoms, mark, frame) });
        }

        return block;
    }

    /// <summary>
    /// Where a layout starts across the column: at its start edge, from the right for right-to-left text; text that does not
    /// wrap is laid out as wide as its longest line and aligned in the room it has, overhanging both sides when centred.
    /// </summary>
    private static float Across(TextLayout layout, float at, float room, bool rtl, float width, bool wrap)
    {
        float x = rtl ? width - at - room : at;
        if (wrap || layout.Lines.Count == 0)
        {
            return x;
        }

        TextAlign align = layout.Paragraph.Style.Align;
        float slack = room - layout.Width;
        return align switch
        {
            TextAlign.Center => x + (slack / 2),
            TextAlign.Right => x + slack,
            _ => rtl ? x + slack : x,
        };
    }

    private static float BulletWidth(Mark bullet) => bullet.Text is { } text ? text.LongestLine : bullet.Size;

    private static Mark? MakeBullet(XElement kind, XElement?[] chain, RunLook lead, TextFrame frame, float fontScale, int number, RectF bounds)
    {
        switch (kind.Name.LocalName)
        {
            case "buChar" when (string?)kind.Attribute("char") is { Length: > 0 } character:
                {
                    RunLook look = Bullets.Look(chain, lead, frame, fontScale, character: true);
                    return Text(Bullets.Character(character, look.Latin), look);
                }

            case "buAutoNum":
                {
                    RunLook look = Bullets.Look(chain, lead, frame, fontScale, character: false);
                    return Text(Bullets.Number((string?)kind.Attribute("type") ?? "arabicPeriod", number), look);
                }

            case "buBlip" when (string?)kind.Element(A + "blip")?.Attribute(PptxNames.R + "embed") is { } id && frame.Pictures?.Invoke(kind, id) is { } picture:
                {
                    RunLook look = Bullets.Look(chain, lead, frame, fontScale, character: false);
                    return new Mark(null, picture, look.Size, 0);
                }

            default:
                return null;
        }

        Mark Text(string text, RunLook look)
        {
            TextStyle style = TextRuns.Style(look, look.Latin, frame, null, bounds);
            RichTextBuilder builder = new();
            builder.Push(style);
            builder.Append(text);
            builder.Pop();
            TextLayout layout = Paragraph.Create(builder.ToRichText(), new ParagraphStyle { DefaultStyle = style, Fallback = frame.Fonts.Fallback }).Layout(float.PositiveInfinity);
            return new Mark(layout, null, look.Size, 0);
        }
    }

    private static ParagraphStyle ParagraphStyleOf(XElement?[] chain, TextFrame frame, RunLook mark, bool rtl, WritingMode? mode, TextOrientation orientation, float at, float room, Settings settings, LineHeight? lineHeight)
    {
        string algn = Attribute(chain, "algn") ?? "l";
        (TextAlign align, TextAlignLast last) = algn switch
        {
            "ctr" => (TextAlign.Center, TextAlignLast.Auto),
            "r" => (TextAlign.Right, TextAlignLast.Auto),
            "just" or "justLow" => (TextAlign.Justify, TextAlignLast.Auto),
            "dist" or "thaiDist" => (TextAlign.Justify, TextAlignLast.Justify),
            _ => (TextAlign.Left, TextAlignLast.Auto),
        };

        return new ParagraphStyle
        {
            // The default style's line acts as a strut on every line, so it takes the same line height as the runs.
            DefaultStyle = TextRuns.Style(mark, mark.Latin, frame, lineHeight, settings.Inner),
            Align = align,
            AlignLast = last,
            Direction = rtl ? TextDirection.RightToLeft : TextDirection.LeftToRight,
            Hyphens = Hyphens.Manual,
            TabStops = mode is null ? TabStops(chain, at, room) : null,
            WritingMode = mode ?? WritingMode.HorizontalTb,
            TextOrientation = orientation,
            Fallback = frame.Fonts.Fallback,
        };
    }

    /// <summary>
    /// The paragraph's tab stops (<c>a:tabLst</c>) and then the default ones every <c>defTabSz</c>, measured from the text box's
    /// edge and given from the layout's start at <paramref name="at"/>.
    /// </summary>
    private static List<TabStop> TabStops(XElement?[] chain, float at, float room)
    {
        List<TabStop> stops = [];
        float lastStop = 0;
        if (Bullets.First(chain, "tabLst") is { } list)
        {
            foreach (XElement tab in list.Elements(A + "tab"))
            {
                float position = Emu(PptxNames.Long(tab, "pos"), 0);
                TabAlignment alignment = (string?)tab.Attribute("algn") switch
                {
                    "r" => TabAlignment.End,
                    "ctr" => TabAlignment.Center,
                    "dec" => TabAlignment.Decimal,
                    _ => TabAlignment.Start,
                };
                if (position - at > 0.01f)
                {
                    stops.Add(new TabStop(position - at, alignment));
                }

                lastStop = Math.Max(lastStop, position);
            }
        }

        float every = Emu(Number(chain, "defTabSz"), DefaultTab);
        if (every < 1)
        {
            every = 72;
        }

        float next = (MathF.Floor(Math.Max(lastStop, at) / every) + 1) * every;
        float end = at + Math.Min(room, 1e5f) + every;
        for (int i = 0; next < end && i < 200; i++, next += every)
        {
            stops.Add(new TabStop(next - at, TabAlignment.Start));
        }

        stops.Sort((x, y) => x.Position.CompareTo(y.Position));
        return stops;
    }

    /// <summary>The atoms from character <paramref name="offset"/> on, the one it falls in cut there.</summary>
    private static List<TextAtom> Slice(List<TextAtom> atoms, int offset)
    {
        List<TextAtom> slice = [];
        foreach (TextAtom atom in atoms)
        {
            if (offset >= atom.Text.Length)
            {
                offset -= atom.Text.Length;
                continue;
            }

            slice.Add(offset > 0 ? atom with { Text = atom.Text[offset..] } : atom);
            offset = 0;
        }

        return slice;
    }

    /// <summary>
    /// How far to move a segment's lines down so their baselines sit where PowerPoint puts them. PowerPoint sets a line's
    /// baseline its font's descent above the line's foot, where the line is no taller than a single line (1.2 times the size),
    /// and that far above a single line's foot in taller lines, but never above three quarters of the way down a short line;
    /// Egami shares the leading out evenly above and below. Measured on PowerPoint's output for Calibri and Arial lines from 70 %
    /// to 200 % and from 40 to 150 points.
    /// </summary>
    private static float Shift(TextLayout layout, List<TextAtom> atoms, RunLook mark, TextFrame frame)
    {
        if (layout.Lines.Count == 0)
        {
            return 0;
        }

        TextLine line = layout.Lines[0];
        RunLook tallest = mark;
        foreach (TextAtom atom in atoms)
        {
            if (atom.Look.Size > tallest.Size && atom.Text != "\n")
            {
                tallest = atom.Look;
            }
        }

        float size = tallest.Size;
        float height = line.Bottom - line.Top;
        float single = frame.Fonts.SingleLine(tallest.Latin, tallest.FontStyle) * size;
        float descent = frame.Fonts.Metrics(tallest.Latin, tallest.FontStyle).Descent * size;
        float baseline = Math.Max(0.75f * height, Math.Min(height, single) - descent);
        return baseline - (line.Baseline - line.Top);
    }

    private static TextLayout Layout(List<TextAtom> atoms, int offset, ParagraphStyle style, float room, bool wrap)
    {
        RichTextBuilder builder = new();
        foreach (TextAtom atom in offset == 0 ? atoms : Slice(atoms, offset))
        {
            builder.Push(atom.Style);
            builder.Append(atom.Text);
            builder.Pop();
        }

        RichText text = builder.ToRichText();
        if (text.Text.Length == 0)
        {
            builder.Push(style.DefaultStyle);
            builder.Append("​");
            builder.Pop();
            text = builder.ToRichText();
        }

        Paragraph paragraph = Paragraph.Create(text, style);
        if (wrap)
        {
            return paragraph.Layout(room);
        }

        TextLayout natural = paragraph.Layout(float.PositiveInfinity);
        return style.Align == TextAlign.Left && style.Direction != TextDirection.RightToLeft ? natural : paragraph.Layout(Math.Max(natural.LongestLine, 0.01f));
    }

    /// <summary>Paragraph spacing before or after: <c>spcPts</c> in hundredths of a point, <c>spcPct</c> as a share of the first run's line.</summary>
    private static float? Spacing(XElement?[] chain, string name, RunLook look)
    {
        if (Bullets.First(chain, name) is not { } spacing)
        {
            return null;
        }

        if (PptxNames.Long(spacing.Element(A + "spcPts"), "val") is long points)
        {
            return points / 100f;
        }

        if (PptxNames.Long(spacing.Element(A + "spcPct"), "val") is long percent)
        {
            return (float)(percent / 100000.0 * look.Size * 1.2);
        }

        return null;
    }

    private static string? Attribute(XElement?[] chain, string name)
    {
        foreach (XElement? element in chain)
        {
            if ((string?)element?.Attribute(name) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    private static double? Number(XElement?[] chain, string name) =>
        double.TryParse(Attribute(chain, name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) ? value : null;

    private static float Emu(double? value, double fallback) => (float)((value ?? fallback) / ShapeTransform.EmusPerPoint);

    /// <summary>Collects glyph outlines into a path, each glyph placed by <see cref="Matrix"/> and then by the layout's origin.</summary>
    private sealed class PathSink(Vector2 origin) : IGlyphOutlineSink
    {
        private readonly PathBuilder builder = new();

        public Matrix3x2 Matrix { get; set; } = Matrix3x2.Identity;

        public void MoveTo(float x, float y) => builder.MoveTo(Map(x, y));

        public void LineTo(float x, float y) => builder.LineTo(Map(x, y));

        public void QuadTo(float cx, float cy, float x, float y) => builder.QuadTo(Map(cx, cy), Map(x, y));

        public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y) => builder.CubicTo(Map(c1x, c1y), Map(c2x, c2y), Map(x, y));

        public void Close() => builder.Close();

        public Path ToPath() => builder.ToPath();

        private Vector2 Map(float x, float y) => Vector2.Transform(new Vector2(x, y), Matrix) + origin;
    }
}
