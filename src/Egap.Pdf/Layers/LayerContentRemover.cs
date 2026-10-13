using Egap.Pdf.Objects;

namespace Egap.Pdf.Layers;

/// <summary>
/// Removes the content an optional content group hides (8.11.3), on every page and in every form XObject they draw: what
/// lies between <c>/OC … BDC</c> and its <c>EMC</c>, and XObjects whose own <c>/OC</c> names the group. Content counts as the
/// group's when it is shown with the group on and hidden with it off, other groups as the default configuration sets them, so
/// membership dictionaries (<c>/OCMD</c>) are followed too.
/// </summary>
/// <remarks>
/// Hidden content still sets the graphics state (8.11.3.2), so a marked sequence is dropped whole only when nothing in it
/// outlives it: every state change sits inside its own <c>q</c> and <c>Q</c>, and it shows no text in an open text object.
/// Otherwise its painting is taken out and its state changes are kept: paths end with <c>n</c>, images, shadings and
/// XObjects go, and text is shown in the invisible rendering mode, so the page renders exactly as with the layer hidden.
/// </remarks>
internal static class LayerContentRemover
{
    private static readonly HashSet<string> PathConstruction = ["m", "l", "c", "v", "y", "h", "re"];
    private static readonly HashSet<string> PathPainting = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*"];
    private static readonly HashSet<string> TextShowing = ["Tj", "TJ", "'", "\""];
    private static readonly HashSet<string> Marking = ["BDC", "BMC", "EMC", "MP", "DP"];

    /// <summary>Removes the group's content; returns how many marked sequences and XObject draws were taken out.</summary>
    public static int Remove(PdfDocument document, PdfDictionary group)
    {
        Visibility visibility = new(document, group);
        int removed = 0;
        Dictionary<PdfStream, PdfDictionary?> forms = new(ReferenceEqualityComparer.Instance);
        foreach (PdfPage page in document.Pages)
        {
            PdfContent content = page.GetContent();
            int count = Edit(content, page.Resources, visibility);
            if (count > 0)
            {
                page.SetContent(content);
                removed += count;
            }

            CollectForms(page.Resources, forms, 0);
        }

        foreach ((PdfStream form, PdfDictionary? inherited) in forms)
        {
            PdfContent content;
            try
            {
                content = PdfContent.Read(form);
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                continue;
            }

            int count = Edit(content, form.Dictionary.Get<PdfDictionary>("Resources") ?? inherited, visibility);
            if (count > 0)
            {
                form.SetDecodedBytes(content.ToBytes(), compress: form.Filters.Count > 0 || form.EncodedLength == 0);
                removed += count;
            }
        }

        return removed;
    }

    /// <summary>Every form XObject the resources draw, nested ones included, with the resources a form without its own inherits.</summary>
    private static void CollectForms(PdfDictionary? resources, Dictionary<PdfStream, PdfDictionary?> forms, int depth)
    {
        if (depth > 64 || resources?.Get<PdfDictionary>("XObject") is not PdfDictionary xObjects)
        {
            return;
        }

        foreach ((PdfName _, PdfObject value) in xObjects.Entries)
        {
            if (value.Resolve() is PdfStream form && form.Dictionary.GetName("Subtype")?.Value == "Form" && forms.TryAdd(form, resources))
            {
                CollectForms(form.Dictionary.Get<PdfDictionary>("Resources") ?? resources, forms, depth + 1);
            }
        }
    }

    /// <summary>Takes the group's content out of one content stream; returns how many sequences and draws went.</summary>
    private static int Edit(PdfContent content, PdfDictionary? resources, Visibility visibility)
    {
        List<(int Start, int End, bool Closed)> regions = [];
        Stack<(int Start, bool Ours)> open = new();
        int outer = -1;
        for (int i = 0; i < content.Count; i++)
        {
            PdfOperation op = content[i];
            switch (op.Operator)
            {
                case "BDC" or "BMC":
                    bool ours = op.Operator == "BDC" && op.GetName(0)?.Value == "OC" && Properties(op, resources) is PdfDictionary oc && visibility.Affected(oc);
                    open.Push((i, ours));
                    if (ours && outer < 0)
                    {
                        outer = i;
                    }

                    break;
                case "EMC" when open.Count > 0:
                    (int start, bool closesOurs) = open.Pop();
                    if (closesOurs && start == outer)
                    {
                        regions.Add((outer, i, true));
                        outer = -1;
                    }

                    break;
                case "Do" when outer < 0 && op.GetName(0) is PdfName name
                    && resources?.Get<PdfDictionary>("XObject")?.Get<PdfStream>(name)?.Dictionary.Get<PdfDictionary>("OC") is PdfDictionary drawn
                    && visibility.Affected(drawn):
                    regions.Add((i, i, false));
                    break;
            }
        }

        if (outer >= 0)
        {
            // Marked content left open runs to the end, as viewers hide it.
            regions.Add((outer, content.Count - 1, false));
        }

        if (regions.Count == 0)
        {
            return 0;
        }

        int[]? renderModes = null;
        for (int r = regions.Count - 1; r >= 0; r--)
        {
            (int start, int end, bool closed) = regions[r];
            if (start == end || Clean(content, start + 1, closed ? end - 1 : end))
            {
                content.RemoveRange(start, end - start + 1);
                continue;
            }

            renderModes ??= RenderModes(content, resources);
            Hide(content, start, end, closed, renderModes, op => Properties(op, resources) is PdfDictionary oc && visibility.Affected(oc));
        }

        return regions.Count;
    }

    /// <summary>
    /// Whether dropping the operations from <paramref name="first"/> to <paramref name="last"/> leaves what follows as it
    /// was: outside <c>q</c> and <c>Q</c> they only build and paint paths, draw, or mark content.
    /// </summary>
    private static bool Clean(PdfContent content, int first, int last)
    {
        int depth = 0;
        for (int i = first; i <= last; i++)
        {
            string op = content[i].Operator;
            switch (op)
            {
                case "q":
                    depth++;
                    continue;
                case "Q":
                    if (--depth < 0)
                    {
                        return false;
                    }

                    continue;
            }

            if (depth == 0 && !(PathConstruction.Contains(op) || PathPainting.Contains(op) || Marking.Contains(op) || op is "Do" or "sh" or "BI" or "n"))
            {
                return false;
            }
        }

        return depth == 0;
    }

    /// <summary>
    /// Keeps the region's state changes and takes out its painting: its own <c>BDC</c> and <c>EMC</c> go, painted paths end
    /// with <c>n</c>, images, shadings and XObjects go, and text is shown in rendering mode 3.
    /// </summary>
    private static void Hide(PdfContent content, int start, int end, bool closed, int[] renderModes, Func<PdfOperation, bool> ours)
    {
        // Marked content of the group nested inside goes too, so nothing of the group is left to remove another time.
        HashSet<int> markers = [];
        Stack<(int Index, bool Ours)> open = new();
        int inner = closed ? end - 1 : end;
        for (int i = start + 1; i <= inner; i++)
        {
            switch (content[i].Operator)
            {
                case "BDC" or "BMC":
                    open.Push((i, content[i].Operator == "BDC" && content[i].GetName(0)?.Value == "OC" && ours(content[i])));
                    break;
                case "EMC" when open.Count > 0:
                    (int begin, bool nested) = open.Pop();
                    if (nested)
                    {
                        markers.Add(begin);
                        markers.Add(i);
                    }

                    break;
            }
        }

        foreach ((int begin, bool nested) in open)
        {
            if (nested)
            {
                markers.Add(begin);
            }
        }

        if (closed)
        {
            content.RemoveAt(end);
        }

        int last = closed ? end - 1 : end;
        for (int i = last; i > start; i--)
        {
            PdfOperation op = content[i];
            if (markers.Contains(i) || op.Operator is "Do" or "sh" or "BI")
            {
                content.RemoveAt(i);
            }
            else if (PathPainting.Contains(op.Operator))
            {
                content[i] = new PdfOperation("n");
            }
            else if (TextShowing.Contains(op.Operator) && renderModes[i] != 3)
            {
                content.Insert(i + 1, new PdfOperation("Tr", new PdfInteger(renderModes[i])));
                content.Insert(i, new PdfOperation("Tr", new PdfInteger(3)));
            }
        }

        content.RemoveAt(start);
    }

    /// <summary>The text rendering mode in effect at each operation, from a walk of the content alone.</summary>
    private static int[] RenderModes(PdfContent content, PdfDictionary? resources)
    {
        ModeRecorder recorder = new(content.Count);
        recorder.Walk(content, resources, PdfMatrix.Identity);
        return recorder.Modes;
    }

    private static PdfDictionary? Properties(PdfOperation op, PdfDictionary? resources)
    {
        return op.Operands.Count > 1 ? op.Operands[1] switch
        {
            PdfDictionary inline => inline,
            PdfName name => resources?.Get<PdfDictionary>("Properties")?.Get<PdfDictionary>(name),
            _ => null,
        } : null;
    }

    /// <summary>Records the text rendering mode before each top-level operation, without entering forms or glyphs.</summary>
    private sealed class ModeRecorder(int count) : PdfContentWalker
    {
        private int index;

        public int[] Modes { get; } = new int[count];

        protected override void OnOperation(PdfOperation operation)
        {
            if (Depth == 0 && index < Modes.Length)
            {
                Modes[index++] = State.TextRenderMode;
            }
        }

        protected override bool OnBeginForm(PdfStream form, PdfName name)
        {
            return false;
        }

        protected override bool OnBeginType3Glyph(PdfGlyph glyph, PdfStream procedure)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether optional content is visible (8.11.2): groups as the default configuration sets them, one group forced on or
    /// off, and membership dictionaries by their <c>/VE</c> expression or <c>/OCGs</c> and <c>/P</c> policy.
    /// </summary>
    private sealed class Visibility
    {
        private readonly PdfDictionary group;
        private readonly PdfDictionary? configuration;

        public Visibility(PdfDocument document, PdfDictionary group)
        {
            this.group = group;
            configuration = document.Catalog.Get<PdfDictionary>("OCProperties")?.Get<PdfDictionary>("D");
        }

        /// <summary>Whether the content is the group's: shown with the group on, hidden with it off.</summary>
        public bool Affected(PdfDictionary oc)
        {
            return Visible(oc, forcedOn: true, 0) && !Visible(oc, forcedOn: false, 0);
        }

        private bool Visible(PdfDictionary oc, bool forcedOn, int depth)
        {
            if (depth > 32)
            {
                return true;
            }

            if (oc.GetName("Type")?.Value != "OCMD")
            {
                return State(oc, forcedOn);
            }

            if (oc.Get<PdfArray>("VE") is PdfArray expression)
            {
                return Expression(expression, forcedOn, depth + 1);
            }

            List<PdfDictionary> groups = oc.Get("OCGs") switch
            {
                PdfDictionary single => [single],
                PdfArray array => [.. Enumerable.Range(0, array.Count).Select(array.Get<PdfDictionary>).OfType<PdfDictionary>()],
                _ => [],
            };
            if (groups.Count == 0)
            {
                return true;
            }

            return oc.GetName("P")?.Value switch
            {
                "AllOn" => groups.All(g => State(g, forcedOn)),
                "AnyOff" => groups.Any(g => !State(g, forcedOn)),
                "AllOff" => groups.All(g => !State(g, forcedOn)),
                _ => groups.Any(g => State(g, forcedOn)),
            };
        }

        private bool Expression(PdfArray expression, bool forcedOn, int depth)
        {
            if (depth > 32 || expression.Count == 0)
            {
                return true;
            }

            IEnumerable<bool> operands = Enumerable.Range(1, expression.Count - 1).Select(i => expression.Get(i) switch
            {
                PdfArray nested => Expression(nested, forcedOn, depth + 1),
                PdfDictionary g => State(g, forcedOn),
                _ => true,
            });
            return expression.GetName(0)?.Value switch
            {
                "And" => operands.All(v => v),
                "Or" => operands.Any(v => v),
                "Not" => !operands.FirstOrDefault(true),
                _ => true,
            };
        }

        private bool State(PdfDictionary g, bool forcedOn)
        {
            if (ReferenceEquals(g, group))
            {
                return forcedOn;
            }

            if (Contains(configuration?.Get<PdfArray>("OFF"), g))
            {
                return false;
            }

            return Contains(configuration?.Get<PdfArray>("ON"), g) || configuration?.GetName("BaseState")?.Value != "OFF";
        }

        private static bool Contains(PdfArray? array, PdfDictionary g)
        {
            if (array is null)
            {
                return false;
            }

            for (int i = 0; i < array.Count; i++)
            {
                if (ReferenceEquals(array.Get(i), g))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
