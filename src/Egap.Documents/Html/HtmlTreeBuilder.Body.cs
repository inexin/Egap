using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>The "in body" insertion mode (13.2.6.4.7) and the adoption agency algorithm.</summary>
internal sealed partial class HtmlTreeBuilder
{
    private bool ParsingTemplateContents => OnStack("template") || (context is not null && context.IsHtml("template"));

    private static readonly string[] NotErrorAtEnd =
        ["dd", "dt", "li", "optgroup", "option", "p", "rb", "rp", "rt", "rtc", "tbody", "td", "tfoot", "th", "thead", "tr", "body", "html"];

    private static readonly string[] BlockStarts =
    [
        "address", "article", "aside", "blockquote", "center", "details", "dialog", "dir", "div", "dl", "fieldset", "figcaption",
        "figure", "footer", "header", "hgroup", "main", "menu", "nav", "ol", "p", "search", "section", "summary", "ul",
    ];

    private static readonly string[] BlockEnds =
    [
        "address", "article", "aside", "blockquote", "button", "center", "details", "dialog", "dir", "div", "dl", "fieldset",
        "figcaption", "figure", "footer", "header", "hgroup", "listing", "main", "menu", "nav", "ol", "pre", "search", "section",
        "select", "summary", "ul",
    ];

    private static readonly string[] Headings = ["h1", "h2", "h3", "h4", "h5", "h6"];

    private static readonly string[] Formatting = ["b", "big", "code", "em", "font", "i", "s", "small", "strike", "strong", "tt", "u"];

    private void InBody(HtmlToken token)
    {
        switch (token.Kind)
        {
            case HtmlTokenKind.Character:
                if (IsNull(token))
                {
                    Error();
                    return;
                }

                ReconstructActive();
                InsertCharacters(token.Data);
                if (!IsSpace(token))
                {
                    framesetOk = false;
                }

                return;
            case HtmlTokenKind.Comment or HtmlTokenKind.ProcessingInstruction:
                InsertComment(token);
                return;
            case HtmlTokenKind.Doctype:
                Error();
                return;
            case HtmlTokenKind.EndOfFile:
                if (templateModes.Count > 0)
                {
                    InTemplate(token);
                    return;
                }

                if (open.Exists(e => !IsHtml(e, NotErrorAtEnd)))
                {
                    Error();
                }

                stopped = true;
                return;
            case HtmlTokenKind.StartTag:
                InBodyStart(token);
                return;
            default:
                InBodyEnd(token);
                return;
        }
    }

    private void InBodyStart(HtmlToken token)
    {
        string name = token.Name!;
        switch (name)
        {
            case "html":
                Error();
                if (!OnStack("template"))
                {
                    MergeAttributes(open[0], token);
                }

                return;
            case "base" or "basefont" or "bgsound" or "link" or "meta" or "noframes" or "script" or "style" or "template" or "title":
                InHead(token);
                return;
            case "body":
                Error();
                if (open.Count == 1 || !IsHtml(open[1], "body") || OnStack("template"))
                {
                    return;
                }

                framesetOk = false;
                MergeAttributes(open[1], token);
                return;
            case "frameset":
                Error();
                if (open.Count == 1 || !IsHtml(open[1], "body") || !framesetOk)
                {
                    return;
                }

                HtmlNode.Detach(open[1]);
                while (open.Count > 1)
                {
                    Pop();
                }

                InsertHtmlElement(token);
                mode = Mode.InFrameset;
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                ClosePInButtonScope();
                if (IsHtml(CurrentNode, Headings))
                {
                    Error();
                    Pop();
                }

                InsertHtmlElement(token);
                return;
            case "pre" or "listing":
                ClosePInButtonScope();
                InsertHtmlElement(token);
                skipNextNewline = true;
                framesetOk = false;
                return;
            case "form":
                if (form is not null && !ParsingTemplateContents)
                {
                    Error();
                    return;
                }

                ClosePInButtonScope();
                HtmlElement created = InsertHtmlElement(token);
                if (!ParsingTemplateContents)
                {
                    form = created;
                }

                return;
            case "li" or "dd" or "dt":
                ListItemStart(token);
                return;
            case "plaintext":
                ClosePInButtonScope();
                InsertHtmlElement(token);
                tokenizer.State = HtmlTokenizerState.PlainText;
                return;
            case "button":
                if (InScope("button"))
                {
                    Error();
                    GenerateImpliedEndTags();
                    PopUntil("button");
                }

                ReconstructActive();
                InsertHtmlElement(token);
                framesetOk = false;
                return;
            case "a":
                {
                    HtmlElement? existing = LastActive("a");
                    if (existing is not null)
                    {
                        Error();
                        AdoptionAgency("a");
                        active.Remove(existing);
                        open.Remove(existing);
                    }

                    ReconstructActive();
                    PushActive(InsertHtmlElement(token));
                    return;
                }

            case "b" or "big" or "code" or "em" or "font" or "i" or "s" or "small" or "strike" or "strong" or "tt" or "u":
                ReconstructActive();
                PushActive(InsertHtmlElement(token));
                return;
            case "nobr":
                ReconstructActive();
                if (InScope("nobr"))
                {
                    Error();
                    AdoptionAgency("nobr");
                    ReconstructActive();
                }

                PushActive(InsertHtmlElement(token));
                return;
            case "applet" or "marquee" or "object":
                ReconstructActive();
                InsertHtmlElement(token);
                InsertMarker();
                framesetOk = false;
                return;
            case "table":
                if (document.QuirksMode != HtmlQuirksMode.Quirks)
                {
                    ClosePInButtonScope();
                }

                InsertHtmlElement(token);
                framesetOk = false;
                mode = Mode.InTable;
                return;
            case "area" or "br" or "embed" or "img" or "keygen" or "wbr":
                ReconstructActive();
                InsertVoid(token);
                framesetOk = false;
                return;
            case "input":
                if (context is not null && context.IsHtml("select"))
                {
                    Error();
                    return;
                }

                if (InScope("select"))
                {
                    Error();
                    PopUntil("select");
                }

                ReconstructActive();
                InsertVoid(token);
                if (token.GetAttribute("type") is not { } type || !type.Equals("hidden", StringComparison.OrdinalIgnoreCase))
                {
                    framesetOk = false;
                }

                return;
            case "param" or "source" or "track":
                InsertVoid(token);
                return;
            case "hr":
                ClosePInButtonScope();
                if (InScope("select"))
                {
                    GenerateImpliedEndTags();
                    if (InScope("option") || InScope("optgroup"))
                    {
                        Error();
                    }
                }

                InsertVoid(token);
                framesetOk = false;
                return;
            case "image":
                Error();
                token.Name = "img";
                Reprocess(token);
                return;
            case "textarea":
                InsertHtmlElement(token);
                skipNextNewline = true;
                tokenizer.State = HtmlTokenizerState.RcData;
                original = mode;
                framesetOk = false;
                mode = Mode.Text;
                return;
            case "xmp":
                ClosePInButtonScope();
                ReconstructActive();
                framesetOk = false;
                GenericText(token, HtmlTokenizerState.RawText);
                return;
            case "iframe":
                framesetOk = false;
                GenericText(token, HtmlTokenizerState.RawText);
                return;
            case "noembed":
                GenericText(token, HtmlTokenizerState.RawText);
                return;
            case "noscript" when scripting:
                GenericText(token, HtmlTokenizerState.RawText);
                return;
            case "select":
                if (context is not null && context.IsHtml("select"))
                {
                    Error();
                    return;
                }

                if (InScope("select"))
                {
                    Error();
                    PopUntil("select");
                    return;
                }

                ReconstructActive();
                InsertHtmlElement(token);
                framesetOk = false;
                return;
            case "option":
                if (InScope("select"))
                {
                    GenerateImpliedEndTags("optgroup");
                    if (InScope("option"))
                    {
                        Error();
                    }
                }
                else if (IsHtml(CurrentNode, "option"))
                {
                    Pop();
                }

                ReconstructActive();
                InsertHtmlElement(token);
                return;
            case "optgroup":
                if (InScope("select"))
                {
                    GenerateImpliedEndTags();
                    if (InScope("option") || InScope("optgroup"))
                    {
                        Error();
                    }
                }
                else if (IsHtml(CurrentNode, "option"))
                {
                    Pop();
                }

                ReconstructActive();
                InsertHtmlElement(token);
                return;
            case "rb" or "rtc":
                if (InScope("ruby"))
                {
                    GenerateImpliedEndTags();
                    if (!IsHtml(CurrentNode, "ruby"))
                    {
                        Error();
                    }
                }

                InsertHtmlElement(token);
                return;
            case "rp" or "rt":
                if (InScope("ruby"))
                {
                    GenerateImpliedEndTags("rtc");
                    if (!IsHtml(CurrentNode, ["rtc", "ruby"]))
                    {
                        Error();
                    }
                }

                InsertHtmlElement(token);
                return;
            case "math":
                ReconstructActive();
                AdjustMathMLAttributes(token);
                AdjustForeignAttributes(token);
                InsertForeignElement(token, HtmlElement.MathMLNamespace);
                if (token.SelfClosing)
                {
                    Pop();
                    AcknowledgeSelfClosing(token);
                }

                return;
            case "svg":
                ReconstructActive();
                AdjustSvgAttributes(token);
                AdjustForeignAttributes(token);
                InsertForeignElement(token, HtmlElement.SvgNamespace);
                if (token.SelfClosing)
                {
                    Pop();
                    AcknowledgeSelfClosing(token);
                }

                return;
            case "caption" or "col" or "colgroup" or "frame" or "head" or "tbody" or "td" or "tfoot" or "th" or "thead" or "tr":
                Error();
                return;
        }

        if (BlockStarts.Contains(name))
        {
            ClosePInButtonScope();
            InsertHtmlElement(token);
            return;
        }

        ReconstructActive();
        InsertHtmlElement(token);
    }

    private void InBodyEnd(HtmlToken token)
    {
        string name = token.Name!;
        switch (name)
        {
            case "template":
                InHead(token);
                return;
            case "body" or "html":
                if (!InScope("body"))
                {
                    Error();
                    return;
                }

                if (open.Exists(e => !IsHtml(e, NotErrorAtEnd)))
                {
                    Error();
                }

                mode = Mode.AfterBody;
                if (name == "html")
                {
                    Reprocess(token);
                }

                return;
            case "form":
                if (!ParsingTemplateContents)
                {
                    HtmlElement? node = form;
                    form = null;
                    if (node is null || !InScope(node))
                    {
                        Error();
                        return;
                    }

                    GenerateImpliedEndTags();
                    if (!ReferenceEquals(CurrentNode, node))
                    {
                        Error();
                    }

                    open.Remove(node);
                }
                else
                {
                    if (!InScope("form"))
                    {
                        Error();
                        return;
                    }

                    GenerateImpliedEndTags();
                    if (!IsHtml(CurrentNode, "form"))
                    {
                        Error();
                    }

                    PopUntil("form");
                }

                return;
            case "p":
                if (!InScope("p", Scope.Button))
                {
                    Error();
                    InsertHtmlElement("p");
                }

                CloseP();
                return;
            case "li":
                if (!InScope("li", Scope.ListItem))
                {
                    Error();
                    return;
                }

                GenerateImpliedEndTags("li");
                if (!IsHtml(CurrentNode, "li"))
                {
                    Error();
                }

                PopUntil("li");
                return;
            case "dd" or "dt":
                if (!InScope(name))
                {
                    Error();
                    return;
                }

                GenerateImpliedEndTags(name);
                if (!IsHtml(CurrentNode, name))
                {
                    Error();
                }

                PopUntil(name);
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                if (!InScope(e => IsHtml(e, Headings), Scope.Default))
                {
                    Error();
                    return;
                }

                GenerateImpliedEndTags();
                if (!IsHtml(CurrentNode, name))
                {
                    Error();
                }

                PopUntil(Headings);
                return;
            case "a" or "b" or "big" or "code" or "em" or "font" or "i" or "nobr" or "s" or "small" or "strike" or "strong" or "tt" or "u":
                AdoptionAgency(name);
                return;
            case "applet" or "marquee" or "object":
                if (!InScope(name))
                {
                    Error();
                    return;
                }

                GenerateImpliedEndTags();
                if (!IsHtml(CurrentNode, name))
                {
                    Error();
                }

                PopUntil(name);
                ClearActiveToMarker();
                return;
            case "br":
                Error();
                token.Kind = HtmlTokenKind.StartTag;
                token.Attributes.Clear();
                InBodyStart(token);
                return;
        }

        if (BlockEnds.Contains(name))
        {
            if (!InScope(name))
            {
                Error();
                return;
            }

            GenerateImpliedEndTags();
            if (!IsHtml(CurrentNode, name))
            {
                Error();
            }

            PopUntil(name);
            return;
        }

        AnyOtherEndTag(name);
    }

    private void AnyOtherEndTag(string name)
    {
        for (int i = open.Count - 1; i >= 0; i--)
        {
            HtmlElement node = open[i];
            if (IsHtml(node, name))
            {
                GenerateImpliedEndTags(name);
                if (!ReferenceEquals(node, CurrentNode))
                {
                    Error();
                }

                while (open.Count > i)
                {
                    Pop();
                }

                return;
            }

            if (IsSpecial(node))
            {
                Error();
                return;
            }
        }
    }

    private static void MergeAttributes(HtmlElement element, HtmlToken token)
    {
        foreach (CssAttribute attribute in token.Attributes)
        {
            if (element.GetAttribute(attribute.LocalName, null) is null)
            {
                element.AttributeList.Add(attribute);
            }
        }
    }

    private void ClosePInButtonScope()
    {
        if (InScope("p", Scope.Button))
        {
            CloseP();
        }
    }

    private void CloseP()
    {
        GenerateImpliedEndTags("p");
        if (!IsHtml(CurrentNode, "p"))
        {
            Error();
        }

        PopUntil("p");
    }

    private void ListItemStart(HtmlToken token)
    {
        framesetOk = false;
        for (int i = open.Count - 1; i >= 0; i--)
        {
            HtmlElement node = open[i];
            string? match = token.Name == "li" ? (IsHtml(node, "li") ? "li" : null) : (IsHtml(node, "dd") ? "dd" : IsHtml(node, "dt") ? "dt" : null);
            if (match is not null)
            {
                GenerateImpliedEndTags(match);
                if (!IsHtml(CurrentNode, match))
                {
                    Error();
                }

                PopUntil(match);
                break;
            }

            if (IsSpecial(node) && !IsHtml(node, ["address", "div", "p"]))
            {
                break;
            }
        }

        ClosePInButtonScope();
        InsertHtmlElement(token);
    }

    /// <summary>The last element named <paramref name="name"/> in the active formatting list after the last marker.</summary>
    private HtmlElement? LastActive(string name)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            if (active[i] is not { } entry)
            {
                return null;
            }

            if (entry.LocalName == name)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>The adoption agency algorithm (13.2.6.4.7) for misnested formatting elements.</summary>
    private void AdoptionAgency(string subject)
    {
        if (IsHtml(CurrentNode, subject) && !active.Contains(CurrentNode))
        {
            Pop();
            return;
        }

        for (int outer = 0; outer < 8; outer++)
        {
            HtmlElement? formatting = LastActive(subject);
            if (formatting is null)
            {
                AnyOtherEndTag(subject);
                return;
            }

            int formattingIndex = open.IndexOf(formatting);
            if (formattingIndex < 0)
            {
                Error();
                active.Remove(formatting);
                return;
            }

            if (!InScope(formatting))
            {
                Error();
                return;
            }

            if (!ReferenceEquals(formatting, CurrentNode))
            {
                Error();
            }

            HtmlElement? furthest = null;
            for (int i = formattingIndex + 1; i < open.Count; i++)
            {
                if (IsSpecial(open[i]))
                {
                    furthest = open[i];
                    break;
                }
            }

            if (furthest is null)
            {
                while (open.Count > formattingIndex)
                {
                    Pop();
                }

                active.Remove(formatting);
                return;
            }

            HtmlElement commonAncestor = open[formattingIndex - 1];
            int bookmark = active.IndexOf(formatting);
            HtmlElement node = furthest;
            HtmlElement lastNode = furthest;
            int nodeIndex = open.IndexOf(furthest);
            for (int inner = 1; ; inner++)
            {
                nodeIndex--;
                node = open[nodeIndex];
                if (ReferenceEquals(node, formatting))
                {
                    break;
                }

                int activeIndex = active.IndexOf(node);
                if (inner > 3 && activeIndex >= 0)
                {
                    active.RemoveAt(activeIndex);
                    if (activeIndex < bookmark)
                    {
                        bookmark--;
                    }

                    activeIndex = -1;
                }

                if (activeIndex < 0)
                {
                    open.RemoveAt(nodeIndex);
                    continue;
                }

                HtmlElement replacement = CreateElement(tokenOf[node], HtmlElement.HtmlNamespace);
                active[activeIndex] = replacement;
                open[nodeIndex] = replacement;
                node = replacement;
                if (ReferenceEquals(lastNode, furthest))
                {
                    bookmark = activeIndex + 1;
                }

                node.Append(lastNode);
                lastNode = node;
            }

            (HtmlNode parent, HtmlNode? before) = InsertionPlace(commonAncestor);
            parent.InsertBefore(lastNode, before);

            HtmlElement newElement = CreateElement(tokenOf[formatting], HtmlElement.HtmlNamespace);
            furthest.MoveChildrenTo(newElement);
            furthest.Append(newElement);

            int formattingActive = active.IndexOf(formatting);
            active.RemoveAt(formattingActive);
            if (formattingActive < bookmark)
            {
                bookmark--;
            }

            active.Insert(Math.Min(bookmark, active.Count), newElement);
            open.Remove(formatting);
            open.Insert(open.IndexOf(furthest) + 1, newElement);
        }
    }
}
