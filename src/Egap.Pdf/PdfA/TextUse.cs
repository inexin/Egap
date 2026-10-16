namespace Egap.Pdf.PdfA;

/// <summary>
/// The codes each font shows (Phase 5 task 15): the text operators of pages, form XObjects, appearances and patterns, with
/// the font that <c>Tf</c> set, kept across <c>q</c> and <c>Q</c> as the graphics state is. PDF/A's font rules apply to the
/// glyphs used for rendering, so the converter checks those rather than every code a font could show.
/// </summary>
internal static class TextUse
{
    /// <summary>The codes shown, by font dictionary.</summary>
    public static Dictionary<PdfDictionary, HashSet<int>> Scan(PdfDocument document, IEnumerable<PdfStream> streams)
    {
        Dictionary<PdfDictionary, HashSet<int>> used = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            if (Parse(page.GetContent) is PdfContent content)
            {
                Walk(content, page.Resources, used);
            }
        }

        foreach (PdfStream stream in streams)
        {
            if (Parse(() => PdfContent.Parse(stream.GetDecodedBytes())) is PdfContent content)
            {
                Walk(content, stream.Dictionary.Get<PdfDictionary>("Resources"), used);
            }
        }

        return used;
    }

    private static void Walk(PdfContent content, PdfDictionary? resources, Dictionary<PdfDictionary, HashSet<int>> used)
    {
        Stack<PdfDictionary?> saved = [];
        PdfDictionary? font = null;
        foreach (PdfOperation operation in content)
        {
            switch (operation.Operator)
            {
                case "q":
                    saved.Push(font);
                    break;
                case "Q":
                    font = saved.Count > 0 ? saved.Pop() : font;
                    break;
                case "Tf":
                    font = operation.GetName(0) is PdfName name ? resources?.Get<PdfDictionary>("Font")?.Get<PdfDictionary>(name) : null;
                    break;
                case "Tj" or "'" when font is not null && operation.GetString(0) is PdfString text:
                    Add(font, text, used);
                    break;
                case "\"" when font is not null && operation.GetString(2) is PdfString text:
                    Add(font, text, used);
                    break;
                case "TJ" when font is not null && operation.Operands.Count > 0 && operation.Operands[0].Resolve() is PdfArray array:
                    foreach (PdfObject item in array)
                    {
                        if (item.Resolve() is PdfString part)
                        {
                            Add(font, part, used);
                        }
                    }

                    break;
            }
        }
    }

    private static void Add(PdfDictionary font, PdfString text, Dictionary<PdfDictionary, HashSet<int>> used)
    {
        if (!used.TryGetValue(font, out HashSet<int>? codes))
        {
            codes = [];
            used[font] = codes;
        }

        if (codes.Count < 65536)
        {
            codes.UnionWith(PdfFont.FromDictionary(font).GetCodes(text.Bytes));
        }
    }

    private static PdfContent? Parse(Func<PdfContent> parse)
    {
        try
        {
            return parse();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return null;
        }
    }
}
