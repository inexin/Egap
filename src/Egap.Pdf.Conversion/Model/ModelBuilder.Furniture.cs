using System.Globalization;
using System.Text.RegularExpressions;

using Egap.Documents;

namespace Egap.Pdf.Conversion;

internal sealed partial class ModelBuilder
{
    /// <summary>
    /// Running headers and footers for a run of same-sized pages (design Q4): the text most of its pages repeat (numbers aside)
    /// becomes the primary header or footer, a first page that differs gets its own, and pages that alternate get even and
    /// odd ones. A page number becomes a PAGE field, a page count a NUMPAGES field, and numbering that does not start at the
    /// first page's place sets where the section's numbering starts.
    /// </summary>
    private void Headers(Section section, SectionPlan plan, List<SectionPlan> plans)
    {
        int first = plan.FirstPage.Content.Index;
        int end = plans.FirstOrDefault(p => p.Group > plan.Group)?.FirstPage.Content.Index ?? pages[^1].Content.Index + 1;
        List<PageDraft> run = [.. pages.Where(p => p.Content.Index >= first && p.Content.Index < end)];
        if (run.Count == 0)
        {
            return;
        }

        (int Offset, NumberStyle Style)? numbering = Numbering(run);
        if (numbering is (int offset, NumberStyle style))
        {
            if (offset != 0)
            {
                section.PageSetup.PageNumberStart = run[0].Content.Index + 1 + offset;
            }

            if (style != NumberStyle.Decimal)
            {
                section.PageSetup.PageNumberStyle = style;
            }
        }

        Frame frame = new(plan.Left, plan.Width - plan.Right);
        foreach (bool header in (bool[])[true, false])
        {
            List<List<BlockDraft>> furniture = [.. run.Select(p => p.Blocks.Where(b => Furniture(b) && (b.Box.Top < p.Content.Height / 2) == header).OrderBy(b => b.Box.Top).ToList())];
            List<string> keys = [.. furniture.Select(Key)];
            string? primary = keys.Where(k => k.Length > 0).GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            if (primary is null)
            {
                continue;
            }

            double distance = header ? furniture.Where(f => f.Count > 0).Min(f => f[0].Box.Top)
                : run.Zip(furniture).Where(p => p.Second.Count > 0).Min(p => p.First.Content.Height - p.Second.Max(b => b.Box.Bottom));
            if (header)
            {
                section.PageSetup.HeaderDistance = Math.Max(0, Math.Round(distance * 2) / 2);
            }
            else
            {
                section.PageSetup.FooterDistance = Math.Max(0, Math.Round(distance * 2) / 2);
            }

            // Even and odd pages alternate when each repeats its own text.
            int Number(int i) => run[i].Content.Index + 1 + (numbering?.Offset ?? 0);
            List<int> rest = [.. Enumerable.Range(1, run.Count - 1)];
            string? even = Most(rest.Where(i => Number(i) % 2 == 0).Select(i => keys[i]));
            string? odd = Most(rest.Where(i => Number(i) % 2 == 1).Select(i => keys[i]));
            bool alternate = even is not null && odd is not null && even != odd && rest.Count(i => keys[i] == even) >= 2 && rest.Count(i => keys[i] == odd) >= 2;
            if (alternate)
            {
                document.Settings.EvenAndOddHeaders = true;
                Fill(section, header ? HeaderFooterKind.HeaderEven : HeaderFooterKind.FooterEven, furniture[rest.First(i => keys[i] == even)], frame, numbering, run.Count);
                Fill(section, header ? HeaderFooterKind.HeaderPrimary : HeaderFooterKind.FooterPrimary, furniture[rest.First(i => keys[i] == odd)], frame, numbering, run.Count);
            }
            else
            {
                Fill(section, header ? HeaderFooterKind.HeaderPrimary : HeaderFooterKind.FooterPrimary, furniture[keys.IndexOf(primary)], frame, numbering, run.Count);
            }

            if (keys[0] != primary && !(alternate && (keys[0] == even || keys[0] == odd)) && run.Count > 1)
            {
                section.PageSetup.DifferentFirstPage = true;
                Fill(section, header ? HeaderFooterKind.HeaderFirst : HeaderFooterKind.FooterFirst, furniture[0], frame, numbering, run.Count);
            }
        }
    }

    /// <summary>Fills a header or footer from one page's furniture: a paragraph per line, blocks on one line apart by centre and right tabs.</summary>
    private void Fill(Section section, HeaderFooterKind kind, List<BlockDraft> blocks, Frame frame, (int Offset, NumberStyle Style)? numbering, int count)
    {
        HeaderFooter part = section.HeadersFooters.GetOrAdd(kind);
        foreach (Block old in part.Blocks.ToList())
        {
            old.Remove();
        }

        int page = blocks.Count > 0 && pageOf.TryGetValue(blocks[0], out PageDraft? draft) ? draft.Content.Index : 0;
        string? number = numbering is (int offset, NumberStyle style) ? Label(page + 1 + offset, style) : null;
        string total = count.ToString(CultureInfo.InvariantCulture);
        writer.Fields = w => Core(w.Text) is string core ? core == number ? "PAGE" : core == total && core != number ? "NUMPAGES" : null : null;
        try
        {
            List<List<BlockDraft>> rows = [];
            foreach (BlockDraft block in blocks.OrderBy(b => b.Lines[0].Baseline))
            {
                if (rows.Count > 0 && Math.Abs(rows[^1][0].Lines[0].Baseline - block.Lines[0].Baseline) < block.Lines[0].Size * 0.5)
                {
                    rows[^1].Add(block);
                }
                else
                {
                    rows.Add([block]);
                }
            }

            double center = (frame.Left + frame.Right) / 2;
            foreach (List<BlockDraft> row in rows)
            {
                row.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));
                StyleEntry entry = styles.For(row[0]);
                Paragraph paragraph = new(document) { Style = entry.Style };
                paragraph.Format.SpaceBefore = 0;
                paragraph.Format.SpaceAfter = 0;
                paragraph.Format.LeftIndent = 0;
                paragraph.Format.FirstLineIndent = 0;
                if (row.Count == 1)
                {
                    PageBox box = row[0].Box;
                    paragraph.Format.Alignment = Math.Abs(((box.Left + box.Right) / 2) - center) < frame.Width * 0.05 ? ParagraphAlignment.Center
                        : box.Left > center && frame.Right - box.Right < frame.Width * 0.1 ? ParagraphAlignment.Right : ParagraphAlignment.Left;
                    writer.Write(paragraph, row[0].Lines, entry.Font);
                }
                else
                {
                    paragraph.Format.Alignment = ParagraphAlignment.Left;
                    paragraph.Format.TabStops = [new TabStop(Math.Round(frame.Width / 2), TabAlignment.Center), new TabStop(Math.Round(frame.Width), TabAlignment.Right)];
                    int stop = 0;
                    foreach (BlockDraft block in row)
                    {
                        PageBox box = block.Box;
                        int at = Math.Abs(((box.Left + box.Right) / 2) - center) < frame.Width * 0.1 ? 1 : box.Left > center ? 2 : 0;
                        for (; stop < at; stop++)
                        {
                            paragraph.AddTab();
                        }

                        writer.Write(paragraph, [block.Lines[0]], entry.Font);
                    }
                }

                part.Blocks.Add(paragraph);
            }
        }
        finally
        {
            writer.Fields = null;
        }

        if (part.Blocks.Count == 0)
        {
            part.Blocks.Add(new Paragraph(document));
        }
    }

    /// <summary>A page's furniture as text with digits as one sign, to compare across pages.</summary>
    private static string Key(List<BlockDraft> blocks)
    {
        return string.Join("|", blocks.Select(b => Digits().Replace(b.Text, "#")));
    }

    private static string? Most(IEnumerable<string> keys)
    {
        return keys.Where(k => k.Length > 0).GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
    }

    /// <summary>
    /// How the pages are numbered: the offset between the number a page shows in its furniture and its place in the run that
    /// most pages agree on, and whether the numbers are arabic or roman. Null when no page shows its number.
    /// </summary>
    private static (int Offset, NumberStyle Style)? Numbering(List<PageDraft> run)
    {
        Dictionary<(int, NumberStyle), int> votes = [];
        foreach (PageDraft page in run)
        {
            HashSet<(int, NumberStyle)> seen = [];
            foreach (PageWord word in page.Blocks.Where(Furniture).SelectMany(b => b.Lines).SelectMany(l => l.Words))
            {
                if (Core(word.Text) is not string core)
                {
                    continue;
                }

                (int Value, NumberStyle Style)? number = core.All(char.IsAsciiDigit) && core.Length <= 5 ? (int.Parse(core, CultureInfo.InvariantCulture), NumberStyle.Decimal)
                    : ListBuilder.Parse(core, true) is { Style: NumberStyle.LowerRoman or NumberStyle.UpperRoman } roman ? (roman.Value, roman.Style) : null;
                if (number is (int value, NumberStyle style) && seen.Add((value - page.Content.Index - 1, style)))
                {
                    votes[(value - page.Content.Index - 1, style)] = votes.GetValueOrDefault((value - page.Content.Index - 1, style)) + 1;
                }
            }
        }

        if (votes.Count == 0)
        {
            return null;
        }

        KeyValuePair<(int, NumberStyle), int> best = votes.MaxBy(v => v.Value);
        return best.Value >= Math.Min(2, run.Count) ? best.Key : null;
    }

    /// <summary>A word's letters and digits without the punctuation around them ("- 3 -" gives "3"); null when that leaves nothing.</summary>
    private static string? Core(string text)
    {
        string core = text.Trim().Trim('-', '\u2013', '\u2014', '(', ')', '[', ']', '.', '|', '/', ',');
        return core.Length == 0 || !core.All(char.IsLetterOrDigit) ? null : core;
    }

    private static string Label(int number, NumberStyle style)
    {
        if (style is not (NumberStyle.LowerRoman or NumberStyle.UpperRoman) || number <= 0)
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        string[] thousands = ["", "M", "MM", "MMM"], hundreds = ["", "C", "CC", "CCC", "CD", "D", "DC", "DCC", "DCCC", "CM"];
        string[] tens = ["", "X", "XX", "XXX", "XL", "L", "LX", "LXX", "LXXX", "XC"], ones = ["", "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
        string roman = thousands[Math.Min(number / 1000, 3)] + hundreds[number / 100 % 10] + tens[number / 10 % 10] + ones[number % 10];
        return style == NumberStyle.LowerRoman ? roman.ToLowerInvariant() : roman;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();
}
