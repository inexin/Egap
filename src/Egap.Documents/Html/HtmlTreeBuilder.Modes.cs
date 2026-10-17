using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>The insertion modes (13.2.6.4) and foreign content (13.2.6.5).</summary>
internal sealed partial class HtmlTreeBuilder
{
    private void Dispatch(HtmlToken token)
    {
        HtmlElement? node = AdjustedCurrentNode;
        bool html = node is null || node.NamespaceUri == HtmlElement.HtmlNamespace
            || (IsMathMLTextIntegrationPoint(node) && ((token.Kind == HtmlTokenKind.StartTag && token.Name is not ("mglyph" or "malignmark")) || token.Kind == HtmlTokenKind.Character))
            || (node.NamespaceUri == HtmlElement.MathMLNamespace && node.LocalName == "annotation-xml" && token.Kind == HtmlTokenKind.StartTag && token.Name == "svg")
            || (IsHtmlIntegrationPoint(node) && token.Kind is HtmlTokenKind.StartTag or HtmlTokenKind.Character)
            || token.Kind == HtmlTokenKind.EndOfFile;
        if (html)
        {
            Process(token, mode);
        }
        else
        {
            ForeignContent(token);
        }
    }

    private void Process(HtmlToken token, Mode rules)
    {
        switch (rules)
        {
            case Mode.Initial:
                Initial(token);
                break;
            case Mode.BeforeHtml:
                BeforeHtml(token);
                break;
            case Mode.BeforeHead:
                BeforeHead(token);
                break;
            case Mode.InHead:
                InHead(token);
                break;
            case Mode.InHeadNoscript:
                InHeadNoscript(token);
                break;
            case Mode.AfterHead:
                AfterHead(token);
                break;
            case Mode.InBody:
                InBody(token);
                break;
            case Mode.Text:
                Text(token);
                break;
            case Mode.InTable:
                InTable(token);
                break;
            case Mode.InTableText:
                InTableText(token);
                break;
            case Mode.InCaption:
                InCaption(token);
                break;
            case Mode.InColumnGroup:
                InColumnGroup(token);
                break;
            case Mode.InTableBody:
                InTableBody(token);
                break;
            case Mode.InRow:
                InRow(token);
                break;
            case Mode.InCell:
                InCell(token);
                break;
            case Mode.InTemplate:
                InTemplate(token);
                break;
            case Mode.AfterBody:
                AfterBody(token);
                break;
            case Mode.InFrameset:
                InFrameset(token);
                break;
            case Mode.AfterFrameset:
                AfterFrameset(token);
                break;
            case Mode.AfterAfterBody:
                AfterAfterBody(token);
                break;
            case Mode.AfterAfterFrameset:
                AfterAfterFrameset(token);
                break;
        }
    }

    private void Reprocess(HtmlToken token)
    {
        Dispatch(token);
    }

    private static bool IsStart(HtmlToken token, string name)
    {
        return token.Kind == HtmlTokenKind.StartTag && token.Name == name;
    }

    private static bool IsStart(HtmlToken token, ReadOnlySpan<string> names)
    {
        return token.Kind == HtmlTokenKind.StartTag && names.Contains(token.Name!);
    }

    private static bool IsEnd(HtmlToken token, string name)
    {
        return token.Kind == HtmlTokenKind.EndTag && token.Name == name;
    }

    private static bool IsEnd(HtmlToken token, ReadOnlySpan<string> names)
    {
        return token.Kind == HtmlTokenKind.EndTag && names.Contains(token.Name!);
    }

    private static bool IsCommentLike(HtmlToken token)
    {
        return token.Kind is HtmlTokenKind.Comment or HtmlTokenKind.ProcessingInstruction;
    }

    // ---- 13.2.6.4.1 initial -------------------------------------------------------------------------------------------

    private void Initial(HtmlToken token)
    {
        if (IsSpace(token))
        {
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token, document);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            if (token.Name != "html" || token.PublicId is not null || (token.SystemId is not null && token.SystemId != "about:legacy-compat"))
            {
                Error();
            }

            document.Append(new HtmlDocumentType(token.Name ?? "", token.PublicId ?? "", token.SystemId ?? ""));
            document.QuirksMode = QuirksOf(token);
            mode = Mode.BeforeHtml;
            return;
        }

        Error();
        document.QuirksMode = HtmlQuirksMode.Quirks;
        mode = Mode.BeforeHtml;
        Reprocess(token);
    }

    private static readonly string[] QuirkyPublicPrefixes =
    [
        "+//Silmaril//dtd html Pro v0r11 19970101//", "-//AS//DTD HTML 3.0 asWedit + extensions//",
        "-//AdvaSoft Ltd//DTD HTML 3.0 asWedit + extensions//", "-//IETF//DTD HTML 2.0 Level 1//", "-//IETF//DTD HTML 2.0 Level 2//",
        "-//IETF//DTD HTML 2.0 Strict Level 1//", "-//IETF//DTD HTML 2.0 Strict Level 2//", "-//IETF//DTD HTML 2.0 Strict//",
        "-//IETF//DTD HTML 2.0//", "-//IETF//DTD HTML 2.1E//", "-//IETF//DTD HTML 3.0//", "-//IETF//DTD HTML 3.2 Final//",
        "-//IETF//DTD HTML 3.2//", "-//IETF//DTD HTML 3//", "-//IETF//DTD HTML Level 0//", "-//IETF//DTD HTML Level 1//",
        "-//IETF//DTD HTML Level 2//", "-//IETF//DTD HTML Level 3//", "-//IETF//DTD HTML Strict Level 0//",
        "-//IETF//DTD HTML Strict Level 1//", "-//IETF//DTD HTML Strict Level 2//", "-//IETF//DTD HTML Strict Level 3//",
        "-//IETF//DTD HTML Strict//", "-//IETF//DTD HTML//", "-//Metrius//DTD Metrius Presentational//",
        "-//Microsoft//DTD Internet Explorer 2.0 HTML Strict//", "-//Microsoft//DTD Internet Explorer 2.0 HTML//",
        "-//Microsoft//DTD Internet Explorer 2.0 Tables//", "-//Microsoft//DTD Internet Explorer 3.0 HTML Strict//",
        "-//Microsoft//DTD Internet Explorer 3.0 HTML//", "-//Microsoft//DTD Internet Explorer 3.0 Tables//",
        "-//Netscape Comm. Corp.//DTD HTML//", "-//Netscape Comm. Corp.//DTD Strict HTML//", "-//O'Reilly and Associates//DTD HTML 2.0//",
        "-//O'Reilly and Associates//DTD HTML Extended 1.0//", "-//O'Reilly and Associates//DTD HTML Extended Relaxed 1.0//",
        "-//SQ//DTD HTML 2.0 HoTMetaL + extensions//",
        "-//SoftQuad Software//DTD HoTMetaL PRO 6.0::19990601::extensions to HTML 4.0//",
        "-//SoftQuad//DTD HoTMetaL PRO 4.0::19971010::extensions to HTML 4.0//", "-//Spyglass//DTD HTML 2.0 Extended//",
        "-//Sun Microsystems Corp.//DTD HotJava HTML//", "-//Sun Microsystems Corp.//DTD HotJava Strict HTML//",
        "-//W3C//DTD HTML 3 1995-03-24//", "-//W3C//DTD HTML 3.2 Draft//", "-//W3C//DTD HTML 3.2 Final//", "-//W3C//DTD HTML 3.2//",
        "-//W3C//DTD HTML 3.2S Draft//", "-//W3C//DTD HTML 4.0 Frameset//", "-//W3C//DTD HTML 4.0 Transitional//",
        "-//W3C//DTD HTML Experimental 19960712//", "-//W3C//DTD HTML Experimental 970421//", "-//W3C//DTD W3 HTML//",
        "-//W3O//DTD W3 HTML 3.0//", "-//WebTechs//DTD Mozilla HTML 2.0//", "-//WebTechs//DTD Mozilla HTML//",
    ];

    private static HtmlQuirksMode QuirksOf(HtmlToken token)
    {
        string? publicId = token.PublicId;
        string? systemId = token.SystemId;
        bool Starts(string prefix) => publicId is not null && publicId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        bool Is(string? value, string expected) => value is not null && value.Equals(expected, StringComparison.OrdinalIgnoreCase);
        if (token.ForceQuirks || token.Name != "html" || Is(publicId, "-//W3O//DTD W3 HTML Strict 3.0//EN//")
            || Is(publicId, "-/W3C/DTD HTML 4.0 Transitional/EN") || Is(publicId, "HTML")
            || Is(systemId, "http://www.ibm.com/data/dtd/v11/ibmxhtml1-transitional.dtd")
            || QuirkyPublicPrefixes.Any(Starts)
            || (systemId is null && (Starts("-//W3C//DTD HTML 4.01 Frameset//") || Starts("-//W3C//DTD HTML 4.01 Transitional//"))))
        {
            return HtmlQuirksMode.Quirks;
        }

        if (Starts("-//W3C//DTD XHTML 1.0 Frameset//") || Starts("-//W3C//DTD XHTML 1.0 Transitional//")
            || (systemId is not null && (Starts("-//W3C//DTD HTML 4.01 Frameset//") || Starts("-//W3C//DTD HTML 4.01 Transitional//"))))
        {
            return HtmlQuirksMode.LimitedQuirks;
        }

        return HtmlQuirksMode.NoQuirks;
    }

    // ---- 13.2.6.4.2 before html ---------------------------------------------------------------------------------------

    private void BeforeHtml(HtmlToken token)
    {
        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token, document);
            return;
        }

        if (IsSpace(token))
        {
            return;
        }

        if (IsStart(token, "html"))
        {
            HtmlElement html = CreateElement(token, HtmlElement.HtmlNamespace);
            document.Append(html);
            open.Add(html);
            mode = Mode.BeforeHead;
            return;
        }

        if (token.Kind == HtmlTokenKind.EndTag && token.Name is not ("head" or "body" or "html" or "br"))
        {
            Error();
            return;
        }

        HtmlElement root = new("html", HtmlElement.HtmlNamespace);
        tokenOf[root] = new HtmlToken { Kind = HtmlTokenKind.StartTag, Name = "html" };
        document.Append(root);
        open.Add(root);
        mode = Mode.BeforeHead;
        Reprocess(token);
    }

    // ---- 13.2.6.4.3 before head ---------------------------------------------------------------------------------------

    private void BeforeHead(HtmlToken token)
    {
        if (IsSpace(token))
        {
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (IsStart(token, "head"))
        {
            head = InsertHtmlElement(token);
            mode = Mode.InHead;
            return;
        }

        if (token.Kind == HtmlTokenKind.EndTag && token.Name is not ("head" or "body" or "html" or "br"))
        {
            Error();
            return;
        }

        head = InsertHtmlElement("head");
        mode = Mode.InHead;
        Reprocess(token);
    }

    // ---- 13.2.6.4.4 in head ---------------------------------------------------------------------------------------------

    private void InHead(HtmlToken token)
    {
        if (IsSpace(token))
        {
            InsertCharacters(token.Data);
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (IsStart(token, ["base", "basefont", "bgsound", "link", "meta"]))
        {
            InsertVoid(token);
            if (token.Name == "meta" && DeclaredEncoding is null)
            {
                // A charset met while the encoding is still a guess changes it (13.2.3.4); the caller reparses.
                if (token.GetAttribute("charset") is { } charset)
                {
                    DeclaredEncoding = HtmlEncoding.FromLabel(charset);
                }
                else if (token.GetAttribute("http-equiv") is { } equiv && equiv.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)
                    && token.GetAttribute("content") is { } content && HtmlEncoding.MetaCharset(content) is { } label)
                {
                    DeclaredEncoding = HtmlEncoding.FromLabel(label);
                }
            }

            return;
        }

        if (IsStart(token, "title"))
        {
            GenericText(token, HtmlTokenizerState.RcData);
            return;
        }

        if ((IsStart(token, "noscript") && scripting) || IsStart(token, ["noframes", "style"]))
        {
            GenericText(token, HtmlTokenizerState.RawText);
            return;
        }

        if (IsStart(token, "noscript"))
        {
            InsertHtmlElement(token);
            mode = Mode.InHeadNoscript;
            return;
        }

        if (IsStart(token, "script"))
        {
            GenericText(token, HtmlTokenizerState.ScriptData);
            return;
        }

        if (IsEnd(token, "head"))
        {
            Pop();
            mode = Mode.AfterHead;
            return;
        }

        if (IsStart(token, "template"))
        {
            InsertHtmlElement(token);
            InsertMarker();
            framesetOk = false;
            mode = Mode.InTemplate;
            templateModes.Push(Mode.InTemplate);
            return;
        }

        if (IsEnd(token, "template"))
        {
            if (!OnStack("template"))
            {
                Error();
                return;
            }

            GenerateImpliedEndTagsThoroughly();
            if (!IsHtml(CurrentNode, "template"))
            {
                Error();
            }

            PopUntil("template");
            ClearActiveToMarker();
            templateModes.Pop();
            ResetInsertionMode();
            return;
        }

        if (IsStart(token, "head") || (token.Kind == HtmlTokenKind.EndTag && token.Name is not ("body" or "html" or "br")))
        {
            Error();
            return;
        }

        Pop();
        mode = Mode.AfterHead;
        Reprocess(token);
    }

    // ---- 13.2.6.4.5 in head noscript ----------------------------------------------------------------------------------

    private void InHeadNoscript(HtmlToken token)
    {
        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (IsEnd(token, "noscript"))
        {
            Pop();
            mode = Mode.InHead;
            return;
        }

        if (IsSpace(token) || IsCommentLike(token) || IsStart(token, ["basefont", "bgsound", "link", "meta", "noframes", "style"]))
        {
            InHead(token);
            return;
        }

        if (IsStart(token, ["head", "noscript"]) || (token.Kind == HtmlTokenKind.EndTag && token.Name != "br"))
        {
            Error();
            return;
        }

        Error();
        Pop();
        mode = Mode.InHead;
        Reprocess(token);
    }

    // ---- 13.2.6.4.6 after head ----------------------------------------------------------------------------------------

    private void AfterHead(HtmlToken token)
    {
        if (IsSpace(token))
        {
            InsertCharacters(token.Data);
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (IsStart(token, "body"))
        {
            InsertHtmlElement(token);
            framesetOk = false;
            mode = Mode.InBody;
            return;
        }

        if (IsStart(token, "frameset"))
        {
            InsertHtmlElement(token);
            mode = Mode.InFrameset;
            return;
        }

        if (IsStart(token, ["base", "basefont", "bgsound", "link", "meta", "noframes", "script", "style", "template", "title"]))
        {
            Error();
            open.Add(head!);
            InHead(token);
            open.Remove(head!);
            return;
        }

        if (IsEnd(token, "template"))
        {
            InHead(token);
            return;
        }

        if (IsStart(token, "head") || (token.Kind == HtmlTokenKind.EndTag && token.Name is not ("body" or "html" or "br")))
        {
            Error();
            return;
        }

        InsertHtmlElement("body");
        framesetOk = true;
        mode = Mode.InBody;
        Reprocess(token);
    }

    // ---- 13.2.6.4.8 text ----------------------------------------------------------------------------------------------

    private void Text(HtmlToken token)
    {
        if (token.Kind == HtmlTokenKind.Character)
        {
            InsertCharacters(token.Data);
            return;
        }

        if (token.Kind == HtmlTokenKind.EndOfFile)
        {
            Error();
            Pop();
            mode = original;
            Reprocess(token);
            return;
        }

        Pop();
        mode = original;
    }

    // ---- 13.2.6.4.9 in table ------------------------------------------------------------------------------------------

    private void InTable(HtmlToken token)
    {
        if (token.Kind == HtmlTokenKind.Character && IsHtml(CurrentNode, ["table", "tbody", "template", "tfoot", "thead", "tr"]))
        {
            pendingTableText.Clear();
            pendingTableTextHasNonSpace = false;
            original = mode;
            mode = Mode.InTableText;
            Reprocess(token);
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsStart(token, "caption"))
        {
            ClearToTableContext();
            InsertMarker();
            InsertHtmlElement(token);
            mode = Mode.InCaption;
            return;
        }

        if (IsStart(token, "colgroup"))
        {
            ClearToTableContext();
            InsertHtmlElement(token);
            mode = Mode.InColumnGroup;
            return;
        }

        if (IsStart(token, "col"))
        {
            ClearToTableContext();
            InsertHtmlElement("colgroup");
            mode = Mode.InColumnGroup;
            Reprocess(token);
            return;
        }

        if (IsStart(token, ["tbody", "tfoot", "thead"]))
        {
            ClearToTableContext();
            InsertHtmlElement(token);
            mode = Mode.InTableBody;
            return;
        }

        if (IsStart(token, ["td", "th", "tr"]))
        {
            ClearToTableContext();
            InsertHtmlElement("tbody");
            mode = Mode.InTableBody;
            Reprocess(token);
            return;
        }

        if (IsStart(token, "table"))
        {
            Error();
            if (!InScope("table", Scope.Table))
            {
                return;
            }

            PopUntil("table");
            ResetInsertionMode();
            Reprocess(token);
            return;
        }

        if (IsEnd(token, "table"))
        {
            if (!InScope("table", Scope.Table))
            {
                Error();
                return;
            }

            PopUntil("table");
            ResetInsertionMode();
            return;
        }

        if (IsEnd(token, ["body", "caption", "col", "colgroup", "html", "tbody", "td", "tfoot", "th", "thead", "tr"]))
        {
            Error();
            return;
        }

        if (IsStart(token, ["style", "script", "template"]) || IsEnd(token, "template"))
        {
            InHead(token);
            return;
        }

        if (IsStart(token, "input") && token.GetAttribute("type") is { } type && type.Equals("hidden", StringComparison.OrdinalIgnoreCase))
        {
            Error();
            InsertVoid(token);
            return;
        }

        if (IsStart(token, "form"))
        {
            Error();
            if (form is not null && !ParsingTemplateContents)
            {
                return;
            }

            HtmlElement created = InsertHtmlElement(token);
            if (!ParsingTemplateContents)
            {
                form = created;
            }

            Pop();
            return;
        }

        if (token.Kind == HtmlTokenKind.EndOfFile)
        {
            InBody(token);
            return;
        }

        Error();
        fosterParenting = true;
        InBody(token);
        fosterParenting = false;
    }

    private void ClearToTableContext()
    {
        while (!IsHtml(CurrentNode, ["table", "template", "html"]))
        {
            Pop();
        }
    }

    // ---- 13.2.6.4.10 in table text --------------------------------------------------------------------------------------

    private void InTableText(HtmlToken token)
    {
        if (IsNull(token))
        {
            Error();
            return;
        }

        if (token.Kind == HtmlTokenKind.Character)
        {
            pendingTableText.Append(token.Data);
            pendingTableTextHasNonSpace |= !IsSpace(token);
            return;
        }

        string text = pendingTableText.ToString();
        pendingTableText.Clear();
        if (text.Length > 0)
        {
            if (pendingTableTextHasNonSpace)
            {
                Error();
                fosterParenting = true;
                ProcessCharactersInBody(text);
                fosterParenting = false;
            }
            else
            {
                InsertCharacters(text);
            }
        }

        mode = original;
        Reprocess(token);
    }

    /// <summary>Pending table text that is not all whitespace, through the in-body rules with foster parenting.</summary>
    private void ProcessCharactersInBody(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int kind = CharClass(text[i]);
            int j = i + 1;
            while (j < text.Length && CharClass(text[j]) == kind)
            {
                j++;
            }

            InBody(new HtmlToken { Kind = HtmlTokenKind.Character, Data = text[i..j] });
            i = j;
        }
    }

    // ---- 13.2.6.4.11 in caption ---------------------------------------------------------------------------------------

    private void InCaption(HtmlToken token)
    {
        bool closes = IsStart(token, ["caption", "col", "colgroup", "tbody", "td", "tfoot", "th", "thead", "tr"]) || IsEnd(token, "table");
        if (IsEnd(token, "caption") || closes)
        {
            if (!InScope("caption", Scope.Table))
            {
                Error();
                return;
            }

            GenerateImpliedEndTags();
            if (!IsHtml(CurrentNode, "caption"))
            {
                Error();
            }

            PopUntil("caption");
            ClearActiveToMarker();
            mode = Mode.InTable;
            if (closes)
            {
                Reprocess(token);
            }

            return;
        }

        if (IsEnd(token, ["body", "col", "colgroup", "html", "tbody", "td", "tfoot", "th", "thead", "tr"]))
        {
            Error();
            return;
        }

        InBody(token);
    }

    // ---- 13.2.6.4.12 in column group ----------------------------------------------------------------------------------

    private void InColumnGroup(HtmlToken token)
    {
        if (IsSpace(token))
        {
            InsertCharacters(token.Data);
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (IsStart(token, "col"))
        {
            InsertVoid(token);
            return;
        }

        if (IsEnd(token, "colgroup"))
        {
            if (!IsHtml(CurrentNode, "colgroup"))
            {
                Error();
                return;
            }

            Pop();
            mode = Mode.InTable;
            return;
        }

        if (IsEnd(token, "col"))
        {
            Error();
            return;
        }

        if (IsStart(token, "template") || IsEnd(token, "template"))
        {
            InHead(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.EndOfFile)
        {
            InBody(token);
            return;
        }

        if (!IsHtml(CurrentNode, "colgroup"))
        {
            Error();
            return;
        }

        Pop();
        mode = Mode.InTable;
        Reprocess(token);
    }

    // ---- 13.2.6.4.13 in table body ------------------------------------------------------------------------------------

    private void InTableBody(HtmlToken token)
    {
        if (IsStart(token, "tr"))
        {
            ClearToTableBodyContext();
            InsertHtmlElement(token);
            mode = Mode.InRow;
            return;
        }

        if (IsStart(token, ["th", "td"]))
        {
            Error();
            ClearToTableBodyContext();
            InsertHtmlElement("tr");
            mode = Mode.InRow;
            Reprocess(token);
            return;
        }

        if (IsEnd(token, ["tbody", "tfoot", "thead"]))
        {
            if (!InScope(token.Name!, Scope.Table))
            {
                Error();
                return;
            }

            ClearToTableBodyContext();
            Pop();
            mode = Mode.InTable;
            return;
        }

        if (IsStart(token, ["caption", "col", "colgroup", "tbody", "tfoot", "thead"]) || IsEnd(token, "table"))
        {
            if (!InScope(e => IsHtml(e, ["tbody", "thead", "tfoot"]), Scope.Table))
            {
                Error();
                return;
            }

            ClearToTableBodyContext();
            Pop();
            mode = Mode.InTable;
            Reprocess(token);
            return;
        }

        if (IsEnd(token, ["body", "caption", "col", "colgroup", "html", "td", "th", "tr"]))
        {
            Error();
            return;
        }

        InTable(token);
    }

    private void ClearToTableBodyContext()
    {
        while (!IsHtml(CurrentNode, ["tbody", "tfoot", "thead", "template", "html"]))
        {
            Pop();
        }
    }

    // ---- 13.2.6.4.14 in row -------------------------------------------------------------------------------------------

    private void InRow(HtmlToken token)
    {
        if (IsStart(token, ["th", "td"]))
        {
            ClearToTableRowContext();
            InsertHtmlElement(token);
            mode = Mode.InCell;
            InsertMarker();
            return;
        }

        if (IsEnd(token, "tr"))
        {
            if (!InScope("tr", Scope.Table))
            {
                Error();
                return;
            }

            ClearToTableRowContext();
            Pop();
            mode = Mode.InTableBody;
            return;
        }

        if (IsStart(token, ["caption", "col", "colgroup", "tbody", "tfoot", "thead", "tr"]) || IsEnd(token, "table"))
        {
            if (!InScope("tr", Scope.Table))
            {
                Error();
                return;
            }

            ClearToTableRowContext();
            Pop();
            mode = Mode.InTableBody;
            Reprocess(token);
            return;
        }

        if (IsEnd(token, ["tbody", "tfoot", "thead"]))
        {
            if (!InScope(token.Name!, Scope.Table))
            {
                Error();
                return;
            }

            if (!InScope("tr", Scope.Table))
            {
                return;
            }

            ClearToTableRowContext();
            Pop();
            mode = Mode.InTableBody;
            Reprocess(token);
            return;
        }

        if (IsEnd(token, ["body", "caption", "col", "colgroup", "html", "td", "th"]))
        {
            Error();
            return;
        }

        InTable(token);
    }

    private void ClearToTableRowContext()
    {
        while (!IsHtml(CurrentNode, ["tr", "template", "html"]))
        {
            Pop();
        }
    }

    // ---- 13.2.6.4.15 in cell ------------------------------------------------------------------------------------------

    private void InCell(HtmlToken token)
    {
        if (IsEnd(token, ["td", "th"]))
        {
            if (!InScope(token.Name!, Scope.Table))
            {
                Error();
                return;
            }

            GenerateImpliedEndTags();
            if (!IsHtml(CurrentNode, token.Name!))
            {
                Error();
            }

            PopUntil(token.Name!);
            ClearActiveToMarker();
            mode = Mode.InRow;
            return;
        }

        if (IsStart(token, ["caption", "col", "colgroup", "tbody", "td", "tfoot", "th", "thead", "tr"]))
        {
            if (!InScope(e => IsHtml(e, ["td", "th"]), Scope.Table))
            {
                Error();
                return;
            }

            CloseCell();
            Reprocess(token);
            return;
        }

        if (IsEnd(token, ["body", "caption", "col", "colgroup", "html"]))
        {
            Error();
            return;
        }

        if (IsEnd(token, ["table", "tbody", "tfoot", "thead", "tr"]))
        {
            if (!InScope(token.Name!, Scope.Table))
            {
                Error();
                return;
            }

            CloseCell();
            Reprocess(token);
            return;
        }

        InBody(token);
    }

    private void CloseCell()
    {
        GenerateImpliedEndTags();
        if (!IsHtml(CurrentNode, ["td", "th"]))
        {
            Error();
        }

        PopUntil(["td", "th"]);
        ClearActiveToMarker();
        mode = Mode.InRow;
    }

    // ---- 13.2.6.4.16 in template --------------------------------------------------------------------------------------

    private void InTemplate(HtmlToken token)
    {
        if (token.Kind is HtmlTokenKind.Character or HtmlTokenKind.Comment or HtmlTokenKind.ProcessingInstruction or HtmlTokenKind.Doctype)
        {
            InBody(token);
            return;
        }

        if (IsStart(token, ["base", "basefont", "bgsound", "link", "meta", "noframes", "script", "style", "template", "title"]) || IsEnd(token, "template"))
        {
            InHead(token);
            return;
        }

        Mode? next = token.Kind != HtmlTokenKind.StartTag ? null : token.Name switch
        {
            "caption" or "colgroup" or "tbody" or "tfoot" or "thead" => Mode.InTable,
            "col" => Mode.InColumnGroup,
            "tr" => Mode.InTableBody,
            "td" or "th" => Mode.InRow,
            _ => Mode.InBody,
        };
        if (next is { } switched)
        {
            templateModes.Pop();
            templateModes.Push(switched);
            mode = switched;
            Reprocess(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.EndTag)
        {
            Error();
            return;
        }

        // End of file.
        if (!OnStack("template"))
        {
            stopped = true;
            return;
        }

        Error();
        PopUntil("template");
        ClearActiveToMarker();
        templateModes.Pop();
        ResetInsertionMode();
        Reprocess(token);
    }

    // ---- 13.2.6.4.17 to 21: after body, frameset ----------------------------------------------------------------------

    private void AfterBody(HtmlToken token)
    {
        if (IsSpace(token) || IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token, open[0]);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsEnd(token, "html"))
        {
            if (context is not null)
            {
                Error();
                return;
            }

            mode = Mode.AfterAfterBody;
            return;
        }

        if (token.Kind == HtmlTokenKind.EndOfFile)
        {
            stopped = true;
            return;
        }

        Error();
        mode = Mode.InBody;
        Reprocess(token);
    }

    private void InFrameset(HtmlToken token)
    {
        if (IsSpace(token))
        {
            InsertCharacters(token.Data);
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (IsStart(token, "frameset"))
        {
            InsertHtmlElement(token);
            return;
        }

        if (IsEnd(token, "frameset"))
        {
            if (open.Count == 1)
            {
                Error();
                return;
            }

            Pop();
            if (context is null && !IsHtml(CurrentNode, "frameset"))
            {
                mode = Mode.AfterFrameset;
            }

            return;
        }

        if (IsStart(token, "frame"))
        {
            InsertVoid(token);
            return;
        }

        if (IsStart(token, "noframes"))
        {
            InHead(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.EndOfFile)
        {
            if (open.Count != 1)
            {
                Error();
            }

            stopped = true;
            return;
        }

        if (token.Kind == HtmlTokenKind.Character)
        {
            // Characters other than whitespace are ignored one by one; the whitespace among them is kept.
            Error();
            return;
        }

        Error();
    }

    private void AfterFrameset(HtmlToken token)
    {
        if (IsSpace(token))
        {
            InsertCharacters(token.Data);
            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        if (IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (IsEnd(token, "html"))
        {
            mode = Mode.AfterAfterFrameset;
            return;
        }

        if (IsStart(token, "noframes"))
        {
            InHead(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.EndOfFile)
        {
            stopped = true;
            return;
        }

        Error();
    }

    private void AfterAfterBody(HtmlToken token)
    {
        if (IsCommentLike(token))
        {
            InsertComment(token, document);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype || IsSpace(token) || IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.EndOfFile)
        {
            stopped = true;
            return;
        }

        Error();
        mode = Mode.InBody;
        Reprocess(token);
    }

    private void AfterAfterFrameset(HtmlToken token)
    {
        if (IsCommentLike(token))
        {
            InsertComment(token, document);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype || IsSpace(token) || IsStart(token, "html"))
        {
            InBody(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.EndOfFile)
        {
            stopped = true;
            return;
        }

        if (IsStart(token, "noframes"))
        {
            InHead(token);
            return;
        }

        Error();
    }

    // ---- 13.2.6.5 foreign content -------------------------------------------------------------------------------------

    private static readonly HashSet<string> BreakoutTags =
    [
        "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt", "em", "embed", "h1", "h2", "h3", "h4",
        "h5", "h6", "head", "hr", "i", "img", "li", "listing", "menu", "meta", "nobr", "ol", "p", "pre", "ruby", "s", "small",
        "span", "strong", "strike", "sub", "sup", "table", "tt", "u", "ul", "var",
    ];

    private void ForeignContent(HtmlToken token)
    {
        if (IsNull(token))
        {
            Error();
            InsertCharacters(new string('�', token.Data.Length));
            return;
        }

        if (token.Kind == HtmlTokenKind.Character)
        {
            InsertCharacters(token.Data);
            if (!IsSpace(token))
            {
                framesetOk = false;
            }

            return;
        }

        if (IsCommentLike(token))
        {
            InsertComment(token);
            return;
        }

        if (token.Kind == HtmlTokenKind.Doctype)
        {
            Error();
            return;
        }

        bool breakout = (token.Kind == HtmlTokenKind.StartTag && BreakoutTags.Contains(token.Name!))
            || (IsStart(token, "font") && (token.GetAttribute("color") is not null || token.GetAttribute("face") is not null || token.GetAttribute("size") is not null))
            || IsEnd(token, ["br", "p"]);
        if (breakout)
        {
            Error();
            while (!IsMathMLTextIntegrationPoint(CurrentNode) && !IsHtmlIntegrationPoint(CurrentNode) && CurrentNode.NamespaceUri != HtmlElement.HtmlNamespace)
            {
                Pop();
            }

            Process(token, mode);
            return;
        }

        if (token.Kind == HtmlTokenKind.StartTag)
        {
            HtmlElement adjusted = AdjustedCurrentNode!;
            if (adjusted.NamespaceUri == HtmlElement.MathMLNamespace)
            {
                AdjustMathMLAttributes(token);
            }

            if (adjusted.NamespaceUri == HtmlElement.SvgNamespace)
            {
                token.Name = SvgTagName(token.Name!);
                AdjustSvgAttributes(token);
            }

            AdjustForeignAttributes(token);
            InsertForeignElement(token, adjusted.NamespaceUri);
            if (token.SelfClosing)
            {
                AcknowledgeSelfClosing(token);
                Pop();
            }

            return;
        }

        if (token.Kind == HtmlTokenKind.EndTag)
        {
            if (token.Name == "script" && CurrentNode.NamespaceUri == HtmlElement.SvgNamespace && CurrentNode.LocalName == "script")
            {
                Pop();
                return;
            }

            int index = open.Count - 1;
            HtmlElement node = open[index];
            if (!node.LocalName.Equals(token.Name, StringComparison.OrdinalIgnoreCase))
            {
                Error();
            }

            while (true)
            {
                if (index == 0)
                {
                    return;
                }

                if (node.LocalName.Equals(token.Name, StringComparison.OrdinalIgnoreCase))
                {
                    while (open.Count > index)
                    {
                        Pop();
                    }

                    return;
                }

                index--;
                node = open[index];
                if (node.NamespaceUri == HtmlElement.HtmlNamespace)
                {
                    Process(token, mode);
                    return;
                }
            }
        }
    }

    private static void AdjustMathMLAttributes(HtmlToken token)
    {
        for (int i = 0; i < token.Attributes.Count; i++)
        {
            if (token.Attributes[i].LocalName == "definitionurl")
            {
                token.Attributes[i] = token.Attributes[i] with { LocalName = "definitionURL" };
            }
        }
    }

    private static readonly Dictionary<string, string> SvgAttributes = Pairs(
        "attributename attributeName attributetype attributeType basefrequency baseFrequency baseprofile baseProfile calcmode calcMode " +
        "clippathunits clipPathUnits diffuseconstant diffuseConstant edgemode edgeMode filterunits filterUnits glyphref glyphRef " +
        "gradienttransform gradientTransform gradientunits gradientUnits kernelmatrix kernelMatrix kernelunitlength kernelUnitLength " +
        "keypoints keyPoints keysplines keySplines keytimes keyTimes lengthadjust lengthAdjust limitingconeangle limitingConeAngle " +
        "markerheight markerHeight markerunits markerUnits markerwidth markerWidth maskcontentunits maskContentUnits maskunits maskUnits " +
        "numoctaves numOctaves pathlength pathLength patterncontentunits patternContentUnits patterntransform patternTransform " +
        "patternunits patternUnits pointsatx pointsAtX pointsaty pointsAtY pointsatz pointsAtZ preservealpha preserveAlpha " +
        "preserveaspectratio preserveAspectRatio primitiveunits primitiveUnits refx refX refy refY repeatcount repeatCount " +
        "repeatdur repeatDur requiredextensions requiredExtensions requiredfeatures requiredFeatures specularconstant specularConstant " +
        "specularexponent specularExponent spreadmethod spreadMethod startoffset startOffset stddeviation stdDeviation " +
        "stitchtiles stitchTiles surfacescale surfaceScale systemlanguage systemLanguage tablevalues tableValues targetx targetX " +
        "targety targetY textlength textLength viewbox viewBox viewtarget viewTarget xchannelselector xChannelSelector " +
        "ychannelselector yChannelSelector zoomandpan zoomAndPan");

    private static readonly Dictionary<string, string> SvgTags = Pairs(
        "altglyph altGlyph altglyphdef altGlyphDef altglyphitem altGlyphItem animatecolor animateColor animatemotion animateMotion " +
        "animatetransform animateTransform clippath clipPath feblend feBlend fecolormatrix feColorMatrix fecomponenttransfer " +
        "feComponentTransfer fecomposite feComposite feconvolvematrix feConvolveMatrix fediffuselighting feDiffuseLighting " +
        "fedisplacementmap feDisplacementMap fedistantlight feDistantLight fedropshadow feDropShadow feflood feFlood fefunca feFuncA " +
        "fefuncb feFuncB fefuncg feFuncG fefuncr feFuncR fegaussianblur feGaussianBlur feimage feImage femerge feMerge " +
        "femergenode feMergeNode femorphology feMorphology feoffset feOffset fepointlight fePointLight fespecularlighting " +
        "feSpecularLighting fespotlight feSpotLight fetile feTile feturbulence feTurbulence foreignobject foreignObject " +
        "glyphref glyphRef lineargradient linearGradient radialgradient radialGradient textpath textPath");

    private static Dictionary<string, string> Pairs(string list)
    {
        string[] words = list.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        for (int i = 0; i + 1 < words.Length; i += 2)
        {
            map[words[i]] = words[i + 1];
        }

        return map;
    }

    private static string SvgTagName(string name)
    {
        return SvgTags.TryGetValue(name, out string? fixedName) ? fixedName : name;
    }

    private static void AdjustSvgAttributes(HtmlToken token)
    {
        for (int i = 0; i < token.Attributes.Count; i++)
        {
            if (SvgAttributes.TryGetValue(token.Attributes[i].LocalName, out string? fixedName))
            {
                token.Attributes[i] = token.Attributes[i] with { LocalName = fixedName };
            }
        }
    }

    /// <summary>xlink:, xml: and xmlns attributes into their namespaces; the prefix stays in the name the tests print.</summary>
    private static void AdjustForeignAttributes(HtmlToken token)
    {
        for (int i = 0; i < token.Attributes.Count; i++)
        {
            CssAttribute attribute = token.Attributes[i];
            string? ns = attribute.LocalName switch
            {
                "xlink:actuate" or "xlink:arcrole" or "xlink:href" or "xlink:role" or "xlink:show" or "xlink:title" or "xlink:type" => HtmlElement.XLinkNamespace,
                "xml:lang" or "xml:space" => ICssElement.XmlNamespace,
                "xmlns" or "xmlns:xlink" => HtmlElement.XmlnsNamespace,
                _ => null,
            };
            if (ns is not null)
            {
                string local = attribute.LocalName.Contains(':', StringComparison.Ordinal) ? attribute.LocalName[(attribute.LocalName.IndexOf(':', StringComparison.Ordinal) + 1)..] : attribute.LocalName;
                token.Attributes[i] = new CssAttribute(ns, local, attribute.Value);
            }
        }
    }
}
