using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>
/// The WHATWG tree construction stage (13.2.6), insertion mode for insertion mode, with the adoption agency, foster
/// parenting, templates, foreign content and the 2025 select parsing; scripts never run, and scripting is off unless asked
/// for, so noscript content is parsed as markup.
/// </summary>
internal sealed partial class HtmlTreeBuilder
{
    private enum Mode
    {
        Initial,
        BeforeHtml,
        BeforeHead,
        InHead,
        InHeadNoscript,
        AfterHead,
        InBody,
        Text,
        InTable,
        InTableText,
        InCaption,
        InColumnGroup,
        InTableBody,
        InRow,
        InCell,
        InTemplate,
        AfterBody,
        InFrameset,
        AfterFrameset,
        AfterAfterBody,
        AfterAfterFrameset,
    }

    private readonly HtmlTokenizer tokenizer;
    private readonly HtmlDocument document = new();
    private readonly List<HtmlElement> open = [];
    private readonly List<HtmlElement?> active = [];
    private readonly Dictionary<HtmlElement, HtmlToken> tokenOf = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<Mode> templateModes = new();
    private readonly System.Text.StringBuilder pendingTableText = new();
    private readonly bool scripting;
    private readonly HtmlElement? context;
    private Mode mode = Mode.Initial;
    private Mode original;
    private HtmlElement? head;
    private HtmlElement? form;
    private bool framesetOk = true;
    private bool fosterParenting;
    private bool skipNextNewline;
    private bool pendingTableTextHasNonSpace;
    private bool stopped;

    private HtmlTreeBuilder(HtmlTokenizer tokenizer, bool scripting, HtmlElement? context)
    {
        this.tokenizer = tokenizer;
        this.scripting = scripting;
        this.context = context;
        tokenizer.AllowCData = () => AdjustedCurrentNode is { } node && node.NamespaceUri != HtmlElement.HtmlNamespace;
    }

    /// <summary>The encoding the first meta charset in the head declares, if any.</summary>
    public string? DeclaredEncoding { get; private set; }

    /// <summary>
    /// Parses HTML bytes: the encoding a byte order mark or the prescan finds (or <paramref name="encoding"/>, when given),
    /// and once more in the encoding a later meta charset declares if that differs (13.2.3.4, change the encoding).
    /// </summary>
    public static HtmlDocument Parse(ReadOnlySpan<byte> bytes, out string used, string? encoding = null, bool scripting = false)
    {
        (string sniffed, int bom) = HtmlEncoding.Sniff(bytes);
        bool certain = bom > 0 || (encoding is not null && HtmlEncoding.FromLabel(encoding) is not null);
        used = encoding is not null && HtmlEncoding.FromLabel(encoding) is { } given ? given : sniffed;
        HtmlTreeBuilder builder = Build(HtmlEncoding.DotNetEncoding(used).GetString(bytes[(used == sniffed ? bom : 0)..]), scripting);
        if (!certain && builder.DeclaredEncoding is { } declared)
        {
            declared = declared switch
            {
                "UTF-16BE" or "UTF-16LE" => "UTF-8",
                "x-user-defined" => "windows-1252",
                _ => declared,
            };
            if (declared != used)
            {
                used = declared;
                builder = Build(HtmlEncoding.DotNetEncoding(used).GetString(bytes), scripting);
            }
        }

        return builder.document;
    }

    private static HtmlTreeBuilder Build(string html, bool scripting)
    {
        HtmlTreeBuilder builder = new(new HtmlTokenizer(html), scripting, null);
        builder.Run();
        return builder;
    }

    /// <summary>The number of tree construction parse errors (tokenizer errors are the tokenizer's).</summary>
    public int ErrorCount { get; private set; }

    /// <summary>Parses a whole document.</summary>
    public static HtmlDocument Parse(string html, bool scripting = false, List<string>? errors = null)
    {
        HtmlTokenizer tokenizer = new(html);
        HtmlTreeBuilder builder = new(tokenizer, scripting, null);
        builder.Run();
        errors?.AddRange(tokenizer.Errors);
        return builder.document;
    }

    /// <summary>
    /// Parses a fragment as the children of <paramref name="contextName"/> in <paramref name="contextNamespace"/> would be
    /// parsed (13.4), returning them in a fragment.
    /// </summary>
    public static HtmlFragment ParseFragment(string html, string contextName, string contextNamespace = HtmlElement.HtmlNamespace, bool scripting = false, HtmlQuirksMode quirks = HtmlQuirksMode.NoQuirks)
    {
        HtmlTokenizer tokenizer = new(html);
        HtmlElement context = new(contextName, contextNamespace);
        HtmlTreeBuilder builder = new(tokenizer, scripting, context);
        builder.document.QuirksMode = quirks;
        if (contextNamespace == HtmlElement.HtmlNamespace)
        {
            tokenizer.State = contextName switch
            {
                "title" or "textarea" => HtmlTokenizerState.RcData,
                "style" or "xmp" or "iframe" or "noembed" or "noframes" => HtmlTokenizerState.RawText,
                "script" => HtmlTokenizerState.ScriptData,
                "noscript" when scripting => HtmlTokenizerState.RawText,
                "plaintext" => HtmlTokenizerState.PlainText,
                _ => HtmlTokenizerState.Data,
            };
        }

        HtmlElement root = new("html", HtmlElement.HtmlNamespace);
        builder.document.Append(root);
        builder.open.Add(root);
        if (context.IsHtml("template"))
        {
            builder.templateModes.Push(Mode.InTemplate);
        }

        builder.tokenOf[context] = new HtmlToken { Kind = HtmlTokenKind.StartTag, Name = contextName };
        builder.ResetInsertionMode();
        builder.Run();
        HtmlFragment fragment = new();
        root.MoveChildrenTo(fragment);
        return fragment;
    }

    private void Run()
    {
        while (!stopped)
        {
            HtmlToken token = tokenizer.Next();
            if (token.Kind == HtmlTokenKind.Character)
            {
                ProcessCharacters(token.Data);
            }
            else
            {
                if (skipNextNewline)
                {
                    skipNextNewline = false;
                }

                Dispatch(token);
            }

            if (token.Kind == HtmlTokenKind.EndOfFile)
            {
                break;
            }
        }

        open.Clear();
        FillSelectedContent(document);
    }

    /// <summary>
    /// A customizable select's selectedcontent shows a copy of the selected option's content (the option with the selected
    /// attribute, the last such, else the first option), as the DOM's option steps leave it once parsing ends.
    /// </summary>
    private static void FillSelectedContent(HtmlNode root)
    {
        foreach (HtmlElement select in root.Descendants().OfType<HtmlElement>().Where(e => e.IsHtml("select") && e.GetAttribute("multiple", null) is null).ToList())
        {
            List<HtmlElement> inside = [.. select.Descendants().OfType<HtmlElement>()];
            if (inside.FirstOrDefault(e => e.IsHtml("selectedcontent")) is not { } shown)
            {
                continue;
            }

            List<HtmlElement> options = [.. inside.Where(e => e.IsHtml("option"))];
            HtmlElement? chosen = options.LastOrDefault(o => o.GetAttribute("selected", null) is not null) ?? options.FirstOrDefault();
            foreach (HtmlNode old in shown.Children.ToList())
            {
                HtmlNode.Detach(old);
            }

            if (chosen is not null)
            {
                foreach (HtmlNode child in chosen.Children)
                {
                    shown.Append(Clone(child));
                }
            }
        }

        static HtmlNode Clone(HtmlNode node)
        {
            HtmlNode copy = node switch
            {
                HtmlElement element => new HtmlElement(element.LocalName, element.NamespaceUri, [.. element.AttributeList]),
                HtmlText text => new HtmlText(text.Data),
                HtmlComment comment => new HtmlComment(comment.Data),
                HtmlProcessingInstruction instruction => new HtmlProcessingInstruction(instruction.Target, instruction.Data),
                _ => new HtmlFragment(),
            };
            foreach (HtmlNode child in node.Children)
            {
                copy.Append(Clone(child));
            }

            return copy;
        }
    }

    private void Error()
    {
        ErrorCount++;
    }

    // ---- Characters ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A run of characters, dispatched in segments of one class each (whitespace, NULL, other), since the rules differ only by
    /// class; the mode can change between segments.
    /// </summary>
    private void ProcessCharacters(string data)
    {
        int i = 0;
        if (skipNextNewline)
        {
            skipNextNewline = false;
            if (data.Length > 0 && data[0] == '\n')
            {
                i = 1;
            }
        }

        while (i < data.Length)
        {
            char first = data[i];
            int kind = CharClass(first);
            int j = i + 1;
            while (j < data.Length && CharClass(data[j]) == kind)
            {
                j++;
            }

            Dispatch(new HtmlToken { Kind = HtmlTokenKind.Character, Data = data[i..j] });
            i = j;
        }
    }

    private static int CharClass(char c)
    {
        return c switch
        {
            '\t' or '\n' or '\f' or '\r' or ' ' => 0,
            '\0' => 1,
            _ => 2,
        };
    }

    private static bool IsSpace(HtmlToken token)
    {
        return token.Kind == HtmlTokenKind.Character && CharClass(token.Data[0]) == 0;
    }

    private static bool IsNull(HtmlToken token)
    {
        return token.Kind == HtmlTokenKind.Character && token.Data[0] == '\0';
    }

    // ---- Stack and nodes ----------------------------------------------------------------------------------------------

    private HtmlElement CurrentNode => open[^1];

    private HtmlElement? AdjustedCurrentNode => context is not null && open.Count == 1 ? context : open.Count > 0 ? open[^1] : null;

    private static bool IsHtml(HtmlElement element, string name)
    {
        return element.NamespaceUri == HtmlElement.HtmlNamespace && element.LocalName == name;
    }

    private static bool IsHtml(HtmlElement element, ReadOnlySpan<string> names)
    {
        return element.NamespaceUri == HtmlElement.HtmlNamespace && names.Contains(element.LocalName);
    }

    private static readonly HashSet<string> SpecialHtml =
    [
        "address", "applet", "area", "article", "aside", "base", "basefont", "bgsound", "blockquote", "body", "br", "button",
        "caption", "center", "col", "colgroup", "dd", "details", "dir", "div", "dl", "dt", "embed", "fieldset", "figcaption",
        "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head", "header", "hgroup", "hr",
        "html", "iframe", "img", "input", "keygen", "li", "link", "listing", "main", "marquee", "menu", "meta", "nav",
        "noembed", "noframes", "noscript", "object", "ol", "p", "param", "plaintext", "pre", "script", "search", "section",
        "select", "source", "style", "summary", "table", "tbody", "td", "template", "textarea", "tfoot", "th", "thead",
        "title", "tr", "track", "ul", "wbr", "xmp",
    ];

    private static bool IsSpecial(HtmlElement element)
    {
        return element.NamespaceUri switch
        {
            HtmlElement.HtmlNamespace => SpecialHtml.Contains(element.LocalName),
            HtmlElement.MathMLNamespace => element.LocalName is "mi" or "mo" or "mn" or "ms" or "mtext" or "annotation-xml",
            HtmlElement.SvgNamespace => element.LocalName is "foreignObject" or "desc" or "title",
            _ => false,
        };
    }

    private static bool IsMathMLTextIntegrationPoint(HtmlElement element)
    {
        return element.NamespaceUri == HtmlElement.MathMLNamespace && element.LocalName is "mi" or "mo" or "mn" or "ms" or "mtext";
    }

    private static bool IsHtmlIntegrationPoint(HtmlElement element)
    {
        if (element.NamespaceUri == HtmlElement.MathMLNamespace && element.LocalName == "annotation-xml")
        {
            string? encoding = element.GetAttribute("encoding", null);
            return encoding is not null && (encoding.Equals("text/html", StringComparison.OrdinalIgnoreCase) || encoding.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));
        }

        return element.NamespaceUri == HtmlElement.SvgNamespace && element.LocalName is "foreignObject" or "desc" or "title";
    }

    /// <summary>The default scope's boundaries: HTML applet to template, MathML text points and SVG integration points.</summary>
    private static bool IsScopeBoundary(HtmlElement element)
    {
        return element.NamespaceUri switch
        {
            HtmlElement.HtmlNamespace => element.LocalName is "applet" or "caption" or "html" or "table" or "td" or "th" or "marquee" or "object" or "select" or "template",
            HtmlElement.MathMLNamespace => element.LocalName is "mi" or "mo" or "mn" or "ms" or "mtext" or "annotation-xml",
            HtmlElement.SvgNamespace => element.LocalName is "foreignObject" or "desc" or "title",
            _ => false,
        };
    }

    private enum Scope
    {
        Default,
        ListItem,
        Button,
        Table,
    }

    private bool InScope(Func<HtmlElement, bool> target, Scope scope)
    {
        for (int i = open.Count - 1; i >= 0; i--)
        {
            HtmlElement node = open[i];
            if (target(node))
            {
                return true;
            }

            bool boundary = scope switch
            {
                Scope.Table => IsHtml(node, ["html", "table", "template"]),
                Scope.ListItem => IsScopeBoundary(node) || IsHtml(node, ["ol", "ul"]),
                Scope.Button => IsScopeBoundary(node) || IsHtml(node, "button"),
                _ => IsScopeBoundary(node),
            };
            if (boundary)
            {
                return false;
            }
        }

        return false;
    }

    private bool InScope(string name, Scope scope = Scope.Default)
    {
        return InScope(e => IsHtml(e, name), scope);
    }

    private bool InScope(HtmlElement element)
    {
        return InScope(e => ReferenceEquals(e, element), Scope.Default);
    }

    private void PopUntil(string name)
    {
        while (open.Count > 0)
        {
            HtmlElement popped = Pop();
            if (IsHtml(popped, name))
            {
                return;
            }
        }
    }

    private void PopUntil(ReadOnlySpan<string> names)
    {
        while (open.Count > 0)
        {
            HtmlElement popped = Pop();
            if (IsHtml(popped, names))
            {
                return;
            }
        }
    }

    private HtmlElement Pop()
    {
        HtmlElement popped = open[^1];
        open.RemoveAt(open.Count - 1);
        return popped;
    }

    private bool OnStack(string name)
    {
        return open.Exists(e => IsHtml(e, name));
    }

    private static readonly string[] ImpliedEnd = ["dd", "dt", "li", "optgroup", "option", "p", "rb", "rp", "rt", "rtc"];

    private static readonly string[] ImpliedEndThorough =
        ["caption", "colgroup", "dd", "dt", "li", "optgroup", "option", "p", "rb", "rp", "rt", "rtc", "tbody", "td", "tfoot", "th", "thead", "tr"];

    private void GenerateImpliedEndTags(string? except = null)
    {
        while (open.Count > 0 && IsHtml(CurrentNode, ImpliedEnd) && (except is null || !IsHtml(CurrentNode, except)))
        {
            Pop();
        }
    }

    private void GenerateImpliedEndTagsThoroughly()
    {
        while (open.Count > 0 && IsHtml(CurrentNode, ImpliedEndThorough))
        {
            Pop();
        }
    }

    // ---- Insertion -------------------------------------------------------------------------------------------------------

    /// <summary>The appropriate place for inserting a node (13.2.6.1), with foster parenting and template contents.</summary>
    private (HtmlNode Parent, HtmlNode? Before) InsertionPlace(HtmlElement? overrideTarget = null)
    {
        HtmlElement target = overrideTarget ?? CurrentNode;
        HtmlNode parent = target;
        HtmlNode? before = null;
        if (fosterParenting && IsHtml(target, ["table", "tbody", "tfoot", "thead", "tr"]))
        {
            int lastTemplate = open.FindLastIndex(e => IsHtml(e, "template"));
            int lastTable = open.FindLastIndex(e => IsHtml(e, "table"));
            if (lastTemplate >= 0 && (lastTable < 0 || lastTemplate > lastTable))
            {
                parent = open[lastTemplate];
            }
            else if (lastTable < 0)
            {
                parent = open[0];
            }
            else if (open[lastTable].Parent is { } tableParent)
            {
                parent = tableParent;
                before = open[lastTable];
            }
            else
            {
                parent = open[lastTable - 1];
            }
        }

        if (parent is HtmlElement { Content: { } contents } template && template.IsHtml("template"))
        {
            return (contents, null);
        }

        return (parent, before);
    }

    private HtmlElement CreateElement(HtmlToken token, string ns)
    {
        HtmlElement element = new(token.Name!, ns, [.. token.Attributes]);
        if (element.IsHtml("template"))
        {
            element.Content = new HtmlFragment();
        }

        tokenOf[element] = token;
        return element;
    }

    private HtmlElement InsertForeignElement(HtmlToken token, string ns)
    {
        (HtmlNode parent, HtmlNode? before) = InsertionPlace();
        HtmlElement element = CreateElement(token, ns);
        if (parent is not HtmlDocument || document.DocumentElement is null)
        {
            parent.InsertBefore(element, before);
        }

        open.Add(element);
        return element;
    }

    private HtmlElement InsertHtmlElement(HtmlToken token)
    {
        return InsertForeignElement(token, HtmlElement.HtmlNamespace);
    }

    private HtmlElement InsertHtmlElement(string name)
    {
        return InsertHtmlElement(new HtmlToken { Kind = HtmlTokenKind.StartTag, Name = name });
    }

    private void InsertCharacters(string data)
    {
        (HtmlNode parent, HtmlNode? before) = InsertionPlace();
        if (parent is HtmlDocument)
        {
            return;
        }

        parent.InsertText(data, before);
    }

    private void InsertComment(HtmlToken token, HtmlNode? parent = null)
    {
        HtmlNode node = token.Kind == HtmlTokenKind.ProcessingInstruction ? new HtmlProcessingInstruction(token.Name!, token.Data) : new HtmlComment(token.Data);
        if (parent is not null)
        {
            parent.Append(node);
            return;
        }

        (HtmlNode target, HtmlNode? before) = InsertionPlace();
        target.InsertBefore(node, before);
    }

    private static void AcknowledgeSelfClosing(HtmlToken token)
    {
        token.SelfClosingAcknowledged = true;
    }

    private void InsertVoid(HtmlToken token)
    {
        InsertHtmlElement(token);
        Pop();
        AcknowledgeSelfClosing(token);
    }

    private void GenericText(HtmlToken token, HtmlTokenizerState state)
    {
        InsertHtmlElement(token);
        tokenizer.State = state;
        original = mode;
        mode = Mode.Text;
    }

    // ---- Active formatting elements ----------------------------------------------------------------------------------

    private void PushActive(HtmlElement element)
    {
        HtmlToken token = tokenOf[element];
        int count = 0;
        int earliest = -1;
        for (int i = active.Count - 1; i >= 0 && active[i] is { } entry; i--)
        {
            if (entry.LocalName == element.LocalName && entry.NamespaceUri == element.NamespaceUri && SameAttributes(tokenOf[entry], token))
            {
                count++;
                earliest = i;
            }
        }

        if (count >= 3)
        {
            active.RemoveAt(earliest);
        }

        active.Add(element);
    }

    private static bool SameAttributes(HtmlToken a, HtmlToken b)
    {
        if (a.Attributes.Count != b.Attributes.Count)
        {
            return false;
        }

        foreach (CssAttribute attribute in a.Attributes)
        {
            if (b.GetAttribute(attribute.LocalName) != attribute.Value)
            {
                return false;
            }
        }

        return true;
    }

    private void InsertMarker()
    {
        active.Add(null);
    }

    private void ClearActiveToMarker()
    {
        while (active.Count > 0)
        {
            HtmlElement? entry = active[^1];
            active.RemoveAt(active.Count - 1);
            if (entry is null)
            {
                return;
            }
        }
    }

    private void ReconstructActive()
    {
        if (active.Count == 0 || active[^1] is null || open.Contains(active[^1]!))
        {
            return;
        }

        int index = active.Count - 1;
        while (index > 0)
        {
            HtmlElement? previous = active[index - 1];
            if (previous is null || open.Contains(previous))
            {
                break;
            }

            index--;
        }

        for (; index < active.Count; index++)
        {
            HtmlToken token = tokenOf[active[index]!];
            HtmlElement created = InsertHtmlElement(token);
            active[index] = created;
        }
    }

    // ---- Insertion mode reset ----------------------------------------------------------------------------------------

    private void ResetInsertionMode()
    {
        for (int i = open.Count - 1; i >= 0; i--)
        {
            bool last = i == 0;
            HtmlElement node = last && context is not null ? context : open[i];
            if (node.NamespaceUri == HtmlElement.HtmlNamespace)
            {
                switch (node.LocalName)
                {
                    case "td" or "th" when !last:
                        mode = Mode.InCell;
                        return;
                    case "tr":
                        mode = Mode.InRow;
                        return;
                    case "tbody" or "thead" or "tfoot":
                        mode = Mode.InTableBody;
                        return;
                    case "caption":
                        mode = Mode.InCaption;
                        return;
                    case "colgroup":
                        mode = Mode.InColumnGroup;
                        return;
                    case "table":
                        mode = Mode.InTable;
                        return;
                    case "template":
                        mode = templateModes.Peek();
                        return;
                    case "head" when !last:
                        mode = Mode.InHead;
                        return;
                    case "body":
                        mode = Mode.InBody;
                        return;
                    case "frameset":
                        mode = Mode.InFrameset;
                        return;
                    case "html":
                        mode = head is null ? Mode.BeforeHead : Mode.AfterHead;
                        return;
                }
            }

            if (last)
            {
                mode = Mode.InBody;
                return;
            }
        }
    }
}
