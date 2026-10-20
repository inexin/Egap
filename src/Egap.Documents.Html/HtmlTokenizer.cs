using System.Text;

using Egami.Css;

namespace Egap.Documents.Html;

/// <summary>The kinds of token the tokenizer emits (WHATWG 13.2.5).</summary>
internal enum HtmlTokenKind
{
    Doctype,
    StartTag,
    EndTag,
    Comment,
    ProcessingInstruction,
    Character,
    EndOfFile,
}

/// <summary>One token: a tag with its attributes, a comment, characters, a doctype or the end of the file.</summary>
internal sealed class HtmlToken
{
    public HtmlTokenKind Kind { get; set; }

    /// <summary>A tag's or doctype's name (null for a doctype without one), a comment's or characters' data.</summary>
    public string? Name { get; set; }

    public string Data { get; set; } = "";

    public List<CssAttribute> Attributes { get; } = [];

    public bool SelfClosing { get; set; }

    public bool SelfClosingAcknowledged { get; set; }

    public string? PublicId { get; set; }

    public string? SystemId { get; set; }

    public bool ForceQuirks { get; set; }

    public string? GetAttribute(string name)
    {
        foreach (CssAttribute attribute in Attributes)
        {
            if (attribute.LocalName == name)
            {
                return attribute.Value;
            }
        }

        return null;
    }
}

/// <summary>The tokenizer's states (WHATWG 13.2.5.1 to 13.2.5.80).</summary>
internal enum HtmlTokenizerState
{
    Data,
    RcData,
    RawText,
    ScriptData,
    PlainText,
    TagOpen,
    EndTagOpen,
    TagName,
    RcDataLessThan,
    RcDataEndTagOpen,
    RcDataEndTagName,
    RawTextLessThan,
    RawTextEndTagOpen,
    RawTextEndTagName,
    ScriptDataLessThan,
    ScriptDataEndTagOpen,
    ScriptDataEndTagName,
    ScriptDataEscapeStart,
    ScriptDataEscapeStartDash,
    ScriptDataEscaped,
    ScriptDataEscapedDash,
    ScriptDataEscapedDashDash,
    ScriptDataEscapedLessThan,
    ScriptDataEscapedEndTagOpen,
    ScriptDataEscapedEndTagName,
    ScriptDataDoubleEscapeStart,
    ScriptDataDoubleEscaped,
    ScriptDataDoubleEscapedDash,
    ScriptDataDoubleEscapedDashDash,
    ScriptDataDoubleEscapedLessThan,
    ScriptDataDoubleEscapeEnd,
    BeforeAttributeName,
    AttributeName,
    AfterAttributeName,
    BeforeAttributeValue,
    AttributeValueDoubleQuoted,
    AttributeValueSingleQuoted,
    AttributeValueUnquoted,
    AfterAttributeValueQuoted,
    SelfClosingStartTag,
    BogusComment,
    MarkupDeclarationOpen,
    CommentStart,
    CommentStartDash,
    Comment,
    CommentLessThan,
    CommentLessThanBang,
    CommentLessThanBangDash,
    CommentLessThanBangDashDash,
    CommentEndDash,
    CommentEnd,
    CommentEndBang,
    Doctype,
    BeforeDoctypeName,
    DoctypeName,
    AfterDoctypeName,
    AfterDoctypePublicKeyword,
    BeforeDoctypePublicIdentifier,
    DoctypePublicIdentifierDoubleQuoted,
    DoctypePublicIdentifierSingleQuoted,
    AfterDoctypePublicIdentifier,
    BetweenDoctypePublicAndSystemIdentifiers,
    AfterDoctypeSystemKeyword,
    BeforeDoctypeSystemIdentifier,
    DoctypeSystemIdentifierDoubleQuoted,
    DoctypeSystemIdentifierSingleQuoted,
    AfterDoctypeSystemIdentifier,
    BogusDoctype,
    CDataSection,
    CDataSectionBracket,
    CDataSectionEnd,
    ProcessingInstructionOpen,
    ProcessingInstructionTarget,
    AfterProcessingInstructionTarget,
    ProcessingInstructionData,
    ProcessingInstructionQuestionable,
    CharacterReference,
    NamedCharacterReference,
    AmbiguousAmpersand,
    NumericCharacterReference,
    HexadecimalCharacterReferenceStart,
    DecimalCharacterReferenceStart,
    HexadecimalCharacterReference,
    DecimalCharacterReference,
    NumericCharacterReferenceEnd,
}

/// <summary>
/// The WHATWG HTML tokenizer (13.2.5), state for state: the tree builder pulls one token at a time and switches the state for
/// RCDATA, RAWTEXT, script data and PLAINTEXT elements. Parse errors are recorded by their spec names and never stop it.
/// </summary>
internal sealed class HtmlTokenizer
{
    private const int Eof = -1;

    private readonly string input;
    private readonly Queue<HtmlToken> pending = new();
    private readonly StringBuilder characters = new();
    private readonly StringBuilder buffer = new();
    private readonly StringBuilder attributeName = new();
    private readonly StringBuilder attributeValue = new();
    private readonly StringBuilder temporary = new();
    private int position;
    private int checkedUpTo;
    private HtmlToken? current;
    private bool attributeOpen;
    private HtmlTokenizerState returnState;
    private long characterCode;
    private bool done;

    public HtmlTokenizer(string input)
    {
        // Input preprocessing (13.2.3.5): CR LF and lone CR become LF.
        this.input = input.Contains('\r', StringComparison.Ordinal) ? input.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n') : input;
    }

    /// <summary>The state the next character is read in; the tree builder sets it for raw text elements.</summary>
    public HtmlTokenizerState State { get; set; }

    /// <summary>The name of the last start tag emitted, for "appropriate end tag" checks.</summary>
    public string? LastStartTag { get; set; }

    /// <summary>Whether CDATA sections are allowed: the adjusted current node is in a foreign namespace.</summary>
    public Func<bool>? AllowCData { get; set; }

    /// <summary>The parse errors met, by their spec codes.</summary>
    public List<string> Errors { get; } = [];

    /// <summary>The next token; after the end of the file, end-of-file tokens forever.</summary>
    public HtmlToken Next()
    {
        while (pending.Count == 0)
        {
            if (done)
            {
                return new HtmlToken { Kind = HtmlTokenKind.EndOfFile };
            }

            Step();
        }

        return pending.Dequeue();
    }

    private int Peek(int offset = 0)
    {
        int at = position + offset;
        return at < input.Length ? input[at] : Eof;
    }

    private int Consume()
    {
        if (position >= input.Length)
        {
            position++;
            return Eof;
        }

        char c = input[position++];
        if (position <= checkedUpTo)
        {
            // A reconsumed character was checked the first time.
            return c;
        }

        checkedUpTo = position;
        if (char.IsSurrogate(c))
        {
            bool paired = char.IsHighSurrogate(c)
                ? position < input.Length && char.IsLowSurrogate(input[position])
                : position >= 2 && char.IsHighSurrogate(input[position - 2]);
            if (!paired)
            {
                Error("surrogate-in-input-stream");
            }
            else if (char.IsLowSurrogate(c) && char.ConvertToUtf32(input[position - 2], c) is int code && (code & 0xFFFE) == 0xFFFE)
            {
                Error("noncharacter-in-input-stream");
            }
        }
        else if (c is (< ' ' and not '\t' and not '\n' and not '\f' and not '\0') or (>= '\u007F' and <= '\u009F'))
        {
            Error("control-character-in-input-stream");
        }
        else if (c is (>= '﷐' and <= '﷯') or '￾' or '￿')
        {
            Error("noncharacter-in-input-stream");
        }

        return c;
    }

    private void Reconsume()
    {
        position--;
    }

    private void Error(string code)
    {
        Errors.Add(code);
    }

    private void EmitCharacter(int c)
    {
        characters.Append((char)c);
    }

    private void EmitCharacters(string text)
    {
        characters.Append(text);
    }

    private void FlushCharacters()
    {
        if (characters.Length > 0)
        {
            pending.Enqueue(new HtmlToken { Kind = HtmlTokenKind.Character, Data = characters.ToString() });
            characters.Clear();
        }
    }

    private void Emit(HtmlToken token)
    {
        FlushCharacters();
        if (token.Kind is HtmlTokenKind.StartTag or HtmlTokenKind.EndTag)
        {
            FinishAttribute();
            if (token.Kind == HtmlTokenKind.StartTag)
            {
                LastStartTag = token.Name;
            }
            else
            {
                if (token.Attributes.Count > 0)
                {
                    Error("end-tag-with-attributes");
                }

                if (token.SelfClosing)
                {
                    Error("end-tag-with-trailing-solidus");
                }
            }
        }

        pending.Enqueue(token);
    }

    private void EmitCurrent()
    {
        HtmlToken token = current!;
        if (token.Kind is HtmlTokenKind.StartTag or HtmlTokenKind.EndTag)
        {
            token.Name = buffer.ToString();
            FinishAttribute();
        }

        current = null;
        Emit(token);
    }

    private void EmitEof()
    {
        FlushCharacters();
        pending.Enqueue(new HtmlToken { Kind = HtmlTokenKind.EndOfFile });
        done = true;
    }

    private void StartTag(HtmlTokenKind kind)
    {
        current = new HtmlToken { Kind = kind };
        buffer.Clear();
        attributeOpen = false;
    }

    private void StartAttribute()
    {
        FinishAttribute();
        attributeName.Clear();
        attributeValue.Clear();
        attributeOpen = true;
    }

    private void FinishAttribute()
    {
        if (!attributeOpen || current is null)
        {
            return;
        }

        attributeOpen = false;
        string name = attributeName.ToString();
        foreach (CssAttribute existing in current.Attributes)
        {
            if (existing.LocalName == name)
            {
                Error("duplicate-attribute");
                return;
            }
        }

        current.Attributes.Add(new CssAttribute(null, name, attributeValue.ToString()));
    }

    private bool IsAppropriateEndTag()
    {
        return LastStartTag is not null && buffer.Equals(LastStartTag.AsSpan());
    }

    private bool ConsumedAsPartOfAttribute => returnState is HtmlTokenizerState.AttributeValueDoubleQuoted or HtmlTokenizerState.AttributeValueSingleQuoted or HtmlTokenizerState.AttributeValueUnquoted;

    private void FlushCodePoints()
    {
        if (ConsumedAsPartOfAttribute)
        {
            attributeValue.Append(temporary);
        }
        else
        {
            characters.Append(temporary);
        }
    }

    private static bool IsAsciiAlpha(int c)
    {
        return c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    }

    private static bool IsAsciiAlphanumeric(int c)
    {
        return IsAsciiAlpha(c) || c is >= '0' and <= '9';
    }

    private static bool IsWhitespace(int c)
    {
        return c is '\t' or '\n' or '\f' or ' ';
    }

    private static char Lower(int c)
    {
        return c is >= 'A' and <= 'Z' ? (char)(c + 32) : (char)c;
    }

    private bool MatchAhead(string word, bool ignoreCase)
    {
        if (position + word.Length > input.Length)
        {
            return false;
        }

        return string.Compare(input, position, word, 0, word.Length, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) == 0;
    }

    private void Step()
    {
        switch (State)
        {
            case HtmlTokenizerState.Data:
                {
                    int c = Consume();
                    switch (c)
                    {
                        case '&':
                            returnState = HtmlTokenizerState.Data;
                            State = HtmlTokenizerState.CharacterReference;
                            break;
                        case '<':
                            State = HtmlTokenizerState.TagOpen;
                            break;
                        case '\0':
                            Error("unexpected-null-character");
                            EmitCharacter(c);
                            break;
                        case Eof:
                            EmitEof();
                            break;
                        default:
                            EmitCharacter(c);
                            break;
                    }

                    break;
                }

            case HtmlTokenizerState.RcData:
                {
                    int c = Consume();
                    switch (c)
                    {
                        case '&':
                            returnState = HtmlTokenizerState.RcData;
                            State = HtmlTokenizerState.CharacterReference;
                            break;
                        case '<':
                            State = HtmlTokenizerState.RcDataLessThan;
                            break;
                        case '\0':
                            Error("unexpected-null-character");
                            EmitCharacter('�');
                            break;
                        case Eof:
                            EmitEof();
                            break;
                        default:
                            EmitCharacter(c);
                            break;
                    }

                    break;
                }

            case HtmlTokenizerState.RawText:
            case HtmlTokenizerState.ScriptData:
                {
                    int c = Consume();
                    switch (c)
                    {
                        case '<':
                            State = State == HtmlTokenizerState.RawText ? HtmlTokenizerState.RawTextLessThan : HtmlTokenizerState.ScriptDataLessThan;
                            break;
                        case '\0':
                            Error("unexpected-null-character");
                            EmitCharacter('�');
                            break;
                        case Eof:
                            EmitEof();
                            break;
                        default:
                            EmitCharacter(c);
                            break;
                    }

                    break;
                }

            case HtmlTokenizerState.PlainText:
                {
                    int c = Consume();
                    switch (c)
                    {
                        case '\0':
                            Error("unexpected-null-character");
                            EmitCharacter('�');
                            break;
                        case Eof:
                            EmitEof();
                            break;
                        default:
                            EmitCharacter(c);
                            break;
                    }

                    break;
                }

            case HtmlTokenizerState.TagOpen:
                {
                    int c = Consume();
                    if (c == '!')
                    {
                        State = HtmlTokenizerState.MarkupDeclarationOpen;
                    }
                    else if (c == '/')
                    {
                        State = HtmlTokenizerState.EndTagOpen;
                    }
                    else if (IsAsciiAlpha(c))
                    {
                        StartTag(HtmlTokenKind.StartTag);
                        Reconsume();
                        State = HtmlTokenizerState.TagName;
                    }
                    else if (c == '?')
                    {
                        temporary.Clear();
                        State = HtmlTokenizerState.ProcessingInstructionOpen;
                    }
                    else if (c == Eof)
                    {
                        Error("eof-before-tag-name");
                        EmitCharacter('<');
                        EmitEof();
                    }
                    else
                    {
                        Error("invalid-first-character-of-tag-name");
                        EmitCharacter('<');
                        Reconsume();
                        State = HtmlTokenizerState.Data;
                    }

                    break;
                }

            case HtmlTokenizerState.EndTagOpen:
                {
                    int c = Consume();
                    if (IsAsciiAlpha(c))
                    {
                        StartTag(HtmlTokenKind.EndTag);
                        Reconsume();
                        State = HtmlTokenizerState.TagName;
                    }
                    else if (c == '>')
                    {
                        Error("missing-end-tag-name");
                        State = HtmlTokenizerState.Data;
                    }
                    else if (c == Eof)
                    {
                        Error("eof-before-tag-name");
                        EmitCharacters("</");
                        EmitEof();
                    }
                    else
                    {
                        Error("invalid-first-character-of-tag-name");
                        current = new HtmlToken { Kind = HtmlTokenKind.Comment };
                        buffer.Clear();
                        Reconsume();
                        State = HtmlTokenizerState.BogusComment;
                    }

                    break;
                }

            case HtmlTokenizerState.TagName:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        State = HtmlTokenizerState.BeforeAttributeName;
                    }
                    else if (c == '/')
                    {
                        State = HtmlTokenizerState.SelfClosingStartTag;
                    }
                    else if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitCurrent();
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        buffer.Append('�');
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-tag");
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append(Lower(c));
                    }

                    break;
                }

            case HtmlTokenizerState.RcDataLessThan:
            case HtmlTokenizerState.RawTextLessThan:
                {
                    int c = Consume();
                    if (c == '/')
                    {
                        temporary.Clear();
                        State = State == HtmlTokenizerState.RcDataLessThan ? HtmlTokenizerState.RcDataEndTagOpen : HtmlTokenizerState.RawTextEndTagOpen;
                    }
                    else
                    {
                        EmitCharacter('<');
                        Reconsume();
                        State = State == HtmlTokenizerState.RcDataLessThan ? HtmlTokenizerState.RcData : HtmlTokenizerState.RawText;
                    }

                    break;
                }

            case HtmlTokenizerState.RcDataEndTagOpen:
            case HtmlTokenizerState.RawTextEndTagOpen:
            case HtmlTokenizerState.ScriptDataEndTagOpen:
            case HtmlTokenizerState.ScriptDataEscapedEndTagOpen:
                {
                    int c = Consume();
                    HtmlTokenizerState text = TextStateOf(State);
                    if (IsAsciiAlpha(c))
                    {
                        StartTag(HtmlTokenKind.EndTag);
                        Reconsume();
                        State = State switch
                        {
                            HtmlTokenizerState.RcDataEndTagOpen => HtmlTokenizerState.RcDataEndTagName,
                            HtmlTokenizerState.RawTextEndTagOpen => HtmlTokenizerState.RawTextEndTagName,
                            HtmlTokenizerState.ScriptDataEndTagOpen => HtmlTokenizerState.ScriptDataEndTagName,
                            _ => HtmlTokenizerState.ScriptDataEscapedEndTagName,
                        };
                    }
                    else
                    {
                        EmitCharacters("</");
                        Reconsume();
                        State = text;
                    }

                    break;
                }

            case HtmlTokenizerState.RcDataEndTagName:
            case HtmlTokenizerState.RawTextEndTagName:
            case HtmlTokenizerState.ScriptDataEndTagName:
            case HtmlTokenizerState.ScriptDataEscapedEndTagName:
                {
                    int c = Consume();
                    if (IsWhitespace(c) && IsAppropriateEndTag())
                    {
                        State = HtmlTokenizerState.BeforeAttributeName;
                    }
                    else if (c == '/' && IsAppropriateEndTag())
                    {
                        State = HtmlTokenizerState.SelfClosingStartTag;
                    }
                    else if (c == '>' && IsAppropriateEndTag())
                    {
                        State = HtmlTokenizerState.Data;
                        EmitCurrent();
                    }
                    else if (IsAsciiAlpha(c))
                    {
                        buffer.Append(Lower(c));
                        temporary.Append((char)c);
                    }
                    else
                    {
                        EmitCharacters("</");
                        characters.Append(temporary);
                        current = null;
                        Reconsume();
                        State = TextStateOf(State);
                    }

                    break;
                }

            case HtmlTokenizerState.ScriptDataLessThan:
                {
                    int c = Consume();
                    if (c == '/')
                    {
                        temporary.Clear();
                        State = HtmlTokenizerState.ScriptDataEndTagOpen;
                    }
                    else if (c == '!')
                    {
                        State = HtmlTokenizerState.ScriptDataEscapeStart;
                        EmitCharacters("<!");
                    }
                    else
                    {
                        EmitCharacter('<');
                        Reconsume();
                        State = HtmlTokenizerState.ScriptData;
                    }

                    break;
                }

            case HtmlTokenizerState.ScriptDataEscapeStart:
            case HtmlTokenizerState.ScriptDataEscapeStartDash:
                {
                    int c = Consume();
                    if (c == '-')
                    {
                        State = State == HtmlTokenizerState.ScriptDataEscapeStart ? HtmlTokenizerState.ScriptDataEscapeStartDash : HtmlTokenizerState.ScriptDataEscapedDashDash;
                        EmitCharacter('-');
                    }
                    else
                    {
                        Reconsume();
                        State = HtmlTokenizerState.ScriptData;
                    }

                    break;
                }

            case HtmlTokenizerState.ScriptDataEscaped:
            case HtmlTokenizerState.ScriptDataEscapedDash:
            case HtmlTokenizerState.ScriptDataEscapedDashDash:
                {
                    int c = Consume();
                    HtmlTokenizerState state = State;
                    if (c == '-')
                    {
                        State = state == HtmlTokenizerState.ScriptDataEscaped ? HtmlTokenizerState.ScriptDataEscapedDash : HtmlTokenizerState.ScriptDataEscapedDashDash;
                        EmitCharacter('-');
                    }
                    else if (c == '<')
                    {
                        State = HtmlTokenizerState.ScriptDataEscapedLessThan;
                    }
                    else if (c == '>' && state == HtmlTokenizerState.ScriptDataEscapedDashDash)
                    {
                        State = HtmlTokenizerState.ScriptData;
                        EmitCharacter('>');
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        State = HtmlTokenizerState.ScriptDataEscaped;
                        EmitCharacter('�');
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-script-html-comment-like-text");
                        EmitEof();
                    }
                    else
                    {
                        State = HtmlTokenizerState.ScriptDataEscaped;
                        EmitCharacter(c);
                    }

                    break;
                }

            case HtmlTokenizerState.ScriptDataEscapedLessThan:
                {
                    int c = Consume();
                    if (c == '/')
                    {
                        temporary.Clear();
                        State = HtmlTokenizerState.ScriptDataEscapedEndTagOpen;
                    }
                    else if (IsAsciiAlpha(c))
                    {
                        temporary.Clear();
                        EmitCharacter('<');
                        Reconsume();
                        State = HtmlTokenizerState.ScriptDataDoubleEscapeStart;
                    }
                    else
                    {
                        EmitCharacter('<');
                        Reconsume();
                        State = HtmlTokenizerState.ScriptDataEscaped;
                    }

                    break;
                }

            case HtmlTokenizerState.ScriptDataDoubleEscapeStart:
            case HtmlTokenizerState.ScriptDataDoubleEscapeEnd:
                {
                    int c = Consume();
                    bool start = State == HtmlTokenizerState.ScriptDataDoubleEscapeStart;
                    if (IsWhitespace(c) || c is '/' or '>')
                    {
                        bool script = temporary.Equals("script");
                        State = start == script ? HtmlTokenizerState.ScriptDataDoubleEscaped : HtmlTokenizerState.ScriptDataEscaped;
                        EmitCharacter(c);
                    }
                    else if (IsAsciiAlpha(c))
                    {
                        temporary.Append(Lower(c));
                        EmitCharacter(c);
                    }
                    else
                    {
                        Reconsume();
                        State = start ? HtmlTokenizerState.ScriptDataEscaped : HtmlTokenizerState.ScriptDataDoubleEscaped;
                    }

                    break;
                }

            case HtmlTokenizerState.ScriptDataDoubleEscaped:
            case HtmlTokenizerState.ScriptDataDoubleEscapedDash:
            case HtmlTokenizerState.ScriptDataDoubleEscapedDashDash:
                {
                    int c = Consume();
                    HtmlTokenizerState state = State;
                    if (c == '-')
                    {
                        State = state == HtmlTokenizerState.ScriptDataDoubleEscaped ? HtmlTokenizerState.ScriptDataDoubleEscapedDash : HtmlTokenizerState.ScriptDataDoubleEscapedDashDash;
                        EmitCharacter('-');
                    }
                    else if (c == '<')
                    {
                        State = HtmlTokenizerState.ScriptDataDoubleEscapedLessThan;
                        EmitCharacter('<');
                    }
                    else if (c == '>' && state == HtmlTokenizerState.ScriptDataDoubleEscapedDashDash)
                    {
                        State = HtmlTokenizerState.ScriptData;
                        EmitCharacter('>');
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        State = HtmlTokenizerState.ScriptDataDoubleEscaped;
                        EmitCharacter('�');
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-script-html-comment-like-text");
                        EmitEof();
                    }
                    else
                    {
                        State = HtmlTokenizerState.ScriptDataDoubleEscaped;
                        EmitCharacter(c);
                    }

                    break;
                }

            case HtmlTokenizerState.ScriptDataDoubleEscapedLessThan:
                {
                    int c = Consume();
                    if (c == '/')
                    {
                        temporary.Clear();
                        State = HtmlTokenizerState.ScriptDataDoubleEscapeEnd;
                        EmitCharacter('/');
                    }
                    else
                    {
                        Reconsume();
                        State = HtmlTokenizerState.ScriptDataDoubleEscaped;
                    }

                    break;
                }

            case HtmlTokenizerState.BeforeAttributeName:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        break;
                    }

                    if (c is '/' or '>' or Eof)
                    {
                        Reconsume();
                        State = HtmlTokenizerState.AfterAttributeName;
                    }
                    else if (c == '=')
                    {
                        Error("unexpected-equals-sign-before-attribute-name");
                        StartAttribute();
                        attributeName.Append('=');
                        State = HtmlTokenizerState.AttributeName;
                    }
                    else
                    {
                        StartAttribute();
                        Reconsume();
                        State = HtmlTokenizerState.AttributeName;
                    }

                    break;
                }

            case HtmlTokenizerState.AttributeName:
                {
                    int c = Consume();
                    if (IsWhitespace(c) || c is '/' or '>' or Eof)
                    {
                        Reconsume();
                        State = HtmlTokenizerState.AfterAttributeName;
                    }
                    else if (c == '=')
                    {
                        State = HtmlTokenizerState.BeforeAttributeValue;
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        attributeName.Append('�');
                    }
                    else
                    {
                        if (c is '"' or '\'' or '<')
                        {
                            Error("unexpected-character-in-attribute-name");
                        }

                        attributeName.Append(Lower(c));
                    }

                    break;
                }

            case HtmlTokenizerState.AfterAttributeName:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        break;
                    }

                    if (c == '/')
                    {
                        State = HtmlTokenizerState.SelfClosingStartTag;
                    }
                    else if (c == '=')
                    {
                        State = HtmlTokenizerState.BeforeAttributeValue;
                    }
                    else if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitCurrent();
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-tag");
                        EmitEof();
                    }
                    else
                    {
                        StartAttribute();
                        Reconsume();
                        State = HtmlTokenizerState.AttributeName;
                    }

                    break;
                }

            case HtmlTokenizerState.BeforeAttributeValue:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        break;
                    }

                    if (c == '"')
                    {
                        State = HtmlTokenizerState.AttributeValueDoubleQuoted;
                    }
                    else if (c == '\'')
                    {
                        State = HtmlTokenizerState.AttributeValueSingleQuoted;
                    }
                    else if (c == '>')
                    {
                        Error("missing-attribute-value");
                        State = HtmlTokenizerState.Data;
                        EmitCurrent();
                    }
                    else
                    {
                        Reconsume();
                        State = HtmlTokenizerState.AttributeValueUnquoted;
                    }

                    break;
                }

            case HtmlTokenizerState.AttributeValueDoubleQuoted:
            case HtmlTokenizerState.AttributeValueSingleQuoted:
                {
                    int c = Consume();
                    char quote = State == HtmlTokenizerState.AttributeValueDoubleQuoted ? '"' : '\'';
                    if (c == quote)
                    {
                        State = HtmlTokenizerState.AfterAttributeValueQuoted;
                    }
                    else if (c == '&')
                    {
                        returnState = State;
                        State = HtmlTokenizerState.CharacterReference;
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        attributeValue.Append('�');
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-tag");
                        EmitEof();
                    }
                    else
                    {
                        attributeValue.Append((char)c);
                    }

                    break;
                }

            case HtmlTokenizerState.AttributeValueUnquoted:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        State = HtmlTokenizerState.BeforeAttributeName;
                    }
                    else if (c == '&')
                    {
                        returnState = HtmlTokenizerState.AttributeValueUnquoted;
                        State = HtmlTokenizerState.CharacterReference;
                    }
                    else if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitCurrent();
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        attributeValue.Append('�');
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-tag");
                        EmitEof();
                    }
                    else
                    {
                        if (c is '"' or '\'' or '<' or '=' or '`')
                        {
                            Error("unexpected-character-in-unquoted-attribute-value");
                        }

                        attributeValue.Append((char)c);
                    }

                    break;
                }

            case HtmlTokenizerState.AfterAttributeValueQuoted:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        State = HtmlTokenizerState.BeforeAttributeName;
                    }
                    else if (c == '/')
                    {
                        State = HtmlTokenizerState.SelfClosingStartTag;
                    }
                    else if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitCurrent();
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-tag");
                        EmitEof();
                    }
                    else
                    {
                        Error("missing-whitespace-between-attributes");
                        Reconsume();
                        State = HtmlTokenizerState.BeforeAttributeName;
                    }

                    break;
                }

            case HtmlTokenizerState.SelfClosingStartTag:
                {
                    int c = Consume();
                    if (c == '>')
                    {
                        current!.SelfClosing = true;
                        State = HtmlTokenizerState.Data;
                        EmitCurrent();
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-tag");
                        EmitEof();
                    }
                    else
                    {
                        Error("unexpected-solidus-in-tag");
                        Reconsume();
                        State = HtmlTokenizerState.BeforeAttributeName;
                    }

                    break;
                }

            case HtmlTokenizerState.BogusComment:
                {
                    int c = Consume();
                    if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitComment();
                    }
                    else if (c == Eof)
                    {
                        EmitComment();
                        EmitEof();
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        buffer.Append('�');
                    }
                    else
                    {
                        buffer.Append((char)c);
                    }

                    break;
                }

            case HtmlTokenizerState.MarkupDeclarationOpen:
                {
                    if (MatchAhead("--", false))
                    {
                        position += 2;
                        current = new HtmlToken { Kind = HtmlTokenKind.Comment };
                        buffer.Clear();
                        State = HtmlTokenizerState.CommentStart;
                    }
                    else if (MatchAhead("DOCTYPE", true))
                    {
                        position += 7;
                        State = HtmlTokenizerState.Doctype;
                    }
                    else if (MatchAhead("[CDATA[", false))
                    {
                        position += 7;
                        if (AllowCData?.Invoke() == true)
                        {
                            State = HtmlTokenizerState.CDataSection;
                        }
                        else
                        {
                            Error("cdata-in-html-content");
                            current = new HtmlToken { Kind = HtmlTokenKind.Comment };
                            buffer.Clear().Append("[CDATA[");
                            State = HtmlTokenizerState.BogusComment;
                        }
                    }
                    else
                    {
                        Error("incorrectly-opened-comment");
                        current = new HtmlToken { Kind = HtmlTokenKind.Comment };
                        buffer.Clear();
                        State = HtmlTokenizerState.BogusComment;
                    }

                    break;
                }

            case HtmlTokenizerState.CommentStart:
                {
                    int c = Consume();
                    if (c == '-')
                    {
                        State = HtmlTokenizerState.CommentStartDash;
                    }
                    else if (c == '>')
                    {
                        Error("abrupt-closing-of-empty-comment");
                        State = HtmlTokenizerState.Data;
                        EmitComment();
                    }
                    else
                    {
                        Reconsume();
                        State = HtmlTokenizerState.Comment;
                    }

                    break;
                }

            case HtmlTokenizerState.CommentStartDash:
                {
                    int c = Consume();
                    if (c == '-')
                    {
                        State = HtmlTokenizerState.CommentEnd;
                    }
                    else if (c == '>')
                    {
                        Error("abrupt-closing-of-empty-comment");
                        State = HtmlTokenizerState.Data;
                        EmitComment();
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-comment");
                        EmitComment();
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append('-');
                        Reconsume();
                        State = HtmlTokenizerState.Comment;
                    }

                    break;
                }

            case HtmlTokenizerState.Comment:
                {
                    int c = Consume();
                    if (c == '<')
                    {
                        buffer.Append('<');
                        State = HtmlTokenizerState.CommentLessThan;
                    }
                    else if (c == '-')
                    {
                        State = HtmlTokenizerState.CommentEndDash;
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        buffer.Append('�');
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-comment");
                        EmitComment();
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append((char)c);
                    }

                    break;
                }

            case HtmlTokenizerState.CommentLessThan:
                {
                    int c = Consume();
                    if (c == '!')
                    {
                        buffer.Append('!');
                        State = HtmlTokenizerState.CommentLessThanBang;
                    }
                    else if (c == '<')
                    {
                        buffer.Append('<');
                    }
                    else
                    {
                        Reconsume();
                        State = HtmlTokenizerState.Comment;
                    }

                    break;
                }

            case HtmlTokenizerState.CommentLessThanBang:
                {
                    int c = Consume();
                    Reconsume();
                    State = c == '-' ? HtmlTokenizerState.CommentLessThanBangDash : HtmlTokenizerState.Comment;
                    if (c == '-')
                    {
                        position++;
                    }

                    break;
                }

            case HtmlTokenizerState.CommentLessThanBangDash:
                {
                    int c = Consume();
                    if (c == '-')
                    {
                        State = HtmlTokenizerState.CommentLessThanBangDashDash;
                    }
                    else
                    {
                        Reconsume();
                        State = HtmlTokenizerState.CommentEndDash;
                    }

                    break;
                }

            case HtmlTokenizerState.CommentLessThanBangDashDash:
                {
                    int c = Consume();
                    if (c is not ('>' or Eof))
                    {
                        Error("nested-comment");
                    }

                    Reconsume();
                    State = HtmlTokenizerState.CommentEnd;
                    break;
                }

            case HtmlTokenizerState.CommentEndDash:
                {
                    int c = Consume();
                    if (c == '-')
                    {
                        State = HtmlTokenizerState.CommentEnd;
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-comment");
                        EmitComment();
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append('-');
                        Reconsume();
                        State = HtmlTokenizerState.Comment;
                    }

                    break;
                }

            case HtmlTokenizerState.CommentEnd:
                {
                    int c = Consume();
                    if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitComment();
                    }
                    else if (c == '!')
                    {
                        State = HtmlTokenizerState.CommentEndBang;
                    }
                    else if (c == '-')
                    {
                        buffer.Append('-');
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-comment");
                        EmitComment();
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append("--");
                        Reconsume();
                        State = HtmlTokenizerState.Comment;
                    }

                    break;
                }

            case HtmlTokenizerState.CommentEndBang:
                {
                    int c = Consume();
                    if (c == '-')
                    {
                        buffer.Append("--!");
                        State = HtmlTokenizerState.CommentEndDash;
                    }
                    else if (c == '>')
                    {
                        Error("incorrectly-closed-comment");
                        State = HtmlTokenizerState.Data;
                        EmitComment();
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-comment");
                        EmitComment();
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append("--!");
                        Reconsume();
                        State = HtmlTokenizerState.Comment;
                    }

                    break;
                }

            case HtmlTokenizerState.Doctype:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        State = HtmlTokenizerState.BeforeDoctypeName;
                    }
                    else if (c == '>')
                    {
                        Reconsume();
                        State = HtmlTokenizerState.BeforeDoctypeName;
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-doctype");
                        Emit(new HtmlToken { Kind = HtmlTokenKind.Doctype, ForceQuirks = true });
                        EmitEof();
                    }
                    else
                    {
                        Error("missing-whitespace-before-doctype-name");
                        Reconsume();
                        State = HtmlTokenizerState.BeforeDoctypeName;
                    }

                    break;
                }

            case HtmlTokenizerState.BeforeDoctypeName:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        break;
                    }

                    current = new HtmlToken { Kind = HtmlTokenKind.Doctype };
                    buffer.Clear();
                    if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        buffer.Append('�');
                        State = HtmlTokenizerState.DoctypeName;
                    }
                    else if (c == '>')
                    {
                        Error("missing-doctype-name");
                        current.ForceQuirks = true;
                        State = HtmlTokenizerState.Data;
                        EmitDoctype(named: false);
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-doctype");
                        current.ForceQuirks = true;
                        EmitDoctype(named: false);
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append(Lower(c));
                        State = HtmlTokenizerState.DoctypeName;
                    }

                    break;
                }

            case HtmlTokenizerState.DoctypeName:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        current!.Name = buffer.ToString();
                        State = HtmlTokenizerState.AfterDoctypeName;
                    }
                    else if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitDoctype(named: true);
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        buffer.Append('�');
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-doctype");
                        current!.ForceQuirks = true;
                        EmitDoctype(named: true);
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append(Lower(c));
                    }

                    break;
                }

            case HtmlTokenizerState.AfterDoctypeName:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        break;
                    }

                    if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitDoctype(named: null);
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-doctype");
                        current!.ForceQuirks = true;
                        EmitDoctype(named: null);
                        EmitEof();
                    }
                    else
                    {
                        Reconsume();
                        if (MatchAhead("PUBLIC", true))
                        {
                            position += 6;
                            State = HtmlTokenizerState.AfterDoctypePublicKeyword;
                        }
                        else if (MatchAhead("SYSTEM", true))
                        {
                            position += 6;
                            State = HtmlTokenizerState.AfterDoctypeSystemKeyword;
                        }
                        else
                        {
                            Error("invalid-character-sequence-after-doctype-name");
                            current!.ForceQuirks = true;
                            State = HtmlTokenizerState.BogusDoctype;
                        }
                    }

                    break;
                }

            case HtmlTokenizerState.AfterDoctypePublicKeyword:
            case HtmlTokenizerState.AfterDoctypeSystemKeyword:
                {
                    int c = Consume();
                    bool isPublic = State == HtmlTokenizerState.AfterDoctypePublicKeyword;
                    if (IsWhitespace(c))
                    {
                        State = isPublic ? HtmlTokenizerState.BeforeDoctypePublicIdentifier : HtmlTokenizerState.BeforeDoctypeSystemIdentifier;
                    }
                    else if (c is '"' or '\'')
                    {
                        Error(isPublic ? "missing-whitespace-after-doctype-public-keyword" : "missing-whitespace-after-doctype-system-keyword");
                        OpenIdentifier(isPublic, c);
                    }
                    else
                    {
                        DoctypeIdentifierMissing(c, isPublic);
                    }

                    break;
                }

            case HtmlTokenizerState.BeforeDoctypePublicIdentifier:
            case HtmlTokenizerState.BeforeDoctypeSystemIdentifier:
                {
                    int c = Consume();
                    bool isPublic = State == HtmlTokenizerState.BeforeDoctypePublicIdentifier;
                    if (IsWhitespace(c))
                    {
                        break;
                    }

                    if (c is '"' or '\'')
                    {
                        OpenIdentifier(isPublic, c);
                    }
                    else
                    {
                        DoctypeIdentifierMissing(c, isPublic);
                    }

                    break;
                }

            case HtmlTokenizerState.DoctypePublicIdentifierDoubleQuoted:
            case HtmlTokenizerState.DoctypePublicIdentifierSingleQuoted:
            case HtmlTokenizerState.DoctypeSystemIdentifierDoubleQuoted:
            case HtmlTokenizerState.DoctypeSystemIdentifierSingleQuoted:
                {
                    int c = Consume();
                    bool isPublic = State is HtmlTokenizerState.DoctypePublicIdentifierDoubleQuoted or HtmlTokenizerState.DoctypePublicIdentifierSingleQuoted;
                    char quote = State is HtmlTokenizerState.DoctypePublicIdentifierDoubleQuoted or HtmlTokenizerState.DoctypeSystemIdentifierDoubleQuoted ? '"' : '\'';
                    if (c == quote)
                    {
                        SetIdentifier(isPublic);
                        State = isPublic ? HtmlTokenizerState.AfterDoctypePublicIdentifier : HtmlTokenizerState.AfterDoctypeSystemIdentifier;
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                        buffer.Append('�');
                    }
                    else if (c == '>')
                    {
                        Error(isPublic ? "abrupt-doctype-public-identifier" : "abrupt-doctype-system-identifier");
                        SetIdentifier(isPublic);
                        current!.ForceQuirks = true;
                        State = HtmlTokenizerState.Data;
                        EmitDoctype(named: null);
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-doctype");
                        SetIdentifier(isPublic);
                        current!.ForceQuirks = true;
                        EmitDoctype(named: null);
                        EmitEof();
                    }
                    else
                    {
                        buffer.Append((char)c);
                    }

                    break;
                }

            case HtmlTokenizerState.AfterDoctypePublicIdentifier:
            case HtmlTokenizerState.BetweenDoctypePublicAndSystemIdentifiers:
                {
                    int c = Consume();
                    bool after = State == HtmlTokenizerState.AfterDoctypePublicIdentifier;
                    if (IsWhitespace(c))
                    {
                        State = HtmlTokenizerState.BetweenDoctypePublicAndSystemIdentifiers;
                    }
                    else if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitDoctype(named: null);
                    }
                    else if (c is '"' or '\'')
                    {
                        if (after)
                        {
                            Error("missing-whitespace-between-doctype-public-and-system-identifiers");
                        }

                        OpenIdentifier(false, c);
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-doctype");
                        current!.ForceQuirks = true;
                        EmitDoctype(named: null);
                        EmitEof();
                    }
                    else
                    {
                        Error("missing-quote-before-doctype-system-identifier");
                        current!.ForceQuirks = true;
                        Reconsume();
                        State = HtmlTokenizerState.BogusDoctype;
                    }

                    break;
                }

            case HtmlTokenizerState.AfterDoctypeSystemIdentifier:
                {
                    int c = Consume();
                    if (IsWhitespace(c))
                    {
                        break;
                    }

                    if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitDoctype(named: null);
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-doctype");
                        current!.ForceQuirks = true;
                        EmitDoctype(named: null);
                        EmitEof();
                    }
                    else
                    {
                        Error("unexpected-character-after-doctype-system-identifier");
                        Reconsume();
                        State = HtmlTokenizerState.BogusDoctype;
                    }

                    break;
                }

            case HtmlTokenizerState.BogusDoctype:
                {
                    int c = Consume();
                    if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitDoctype(named: null);
                    }
                    else if (c == '\0')
                    {
                        Error("unexpected-null-character");
                    }
                    else if (c == Eof)
                    {
                        EmitDoctype(named: null);
                        EmitEof();
                    }

                    break;
                }

            case HtmlTokenizerState.CDataSection:
                {
                    int c = Consume();
                    if (c == ']')
                    {
                        State = HtmlTokenizerState.CDataSectionBracket;
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-cdata");
                        EmitEof();
                    }
                    else
                    {
                        EmitCharacter(c);
                    }

                    break;
                }

            case HtmlTokenizerState.CDataSectionBracket:
                {
                    int c = Consume();
                    if (c == ']')
                    {
                        State = HtmlTokenizerState.CDataSectionEnd;
                    }
                    else
                    {
                        EmitCharacter(']');
                        Reconsume();
                        State = HtmlTokenizerState.CDataSection;
                    }

                    break;
                }

            case HtmlTokenizerState.CDataSectionEnd:
                {
                    int c = Consume();
                    if (c == ']')
                    {
                        EmitCharacter(']');
                    }
                    else if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                    }
                    else
                    {
                        EmitCharacters("]]");
                        Reconsume();
                        State = HtmlTokenizerState.CDataSection;
                    }

                    break;
                }

            case HtmlTokenizerState.ProcessingInstructionOpen:
                {
                    int c = Consume();
                    if (IsAsciiAlpha(c) || c == '_')
                    {
                        Reconsume();
                        State = HtmlTokenizerState.ProcessingInstructionTarget;
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-processing-instruction");
                        EmitEof();
                    }
                    else
                    {
                        Error("invalid-first-character-of-processing-instruction-target");
                        TemporaryToComment();
                        Reconsume();
                    }

                    break;
                }

            case HtmlTokenizerState.ProcessingInstructionTarget:
                {
                    int c = Consume();
                    if (IsWhitespace(c) || c is '?' or '>')
                    {
                        string target = temporary.ToString();
                        if (target.Equals("xml", StringComparison.OrdinalIgnoreCase) || target.Equals("xml-stylesheet", StringComparison.OrdinalIgnoreCase))
                        {
                            Error("disallowed-processing-instruction-target");
                            TemporaryToComment();
                        }
                        else
                        {
                            current = new HtmlToken { Kind = HtmlTokenKind.ProcessingInstruction, Name = target };
                            buffer.Clear();
                            State = HtmlTokenizerState.AfterProcessingInstructionTarget;
                        }

                        Reconsume();
                    }
                    else if (IsAsciiAlphanumeric(c) || c is '-' or '_')
                    {
                        temporary.Append((char)c);
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-processing-instruction");
                        EmitEof();
                    }
                    else
                    {
                        Error("invalid-processing-instruction-target");
                        TemporaryToComment();
                        Reconsume();
                    }

                    break;
                }

            case HtmlTokenizerState.AfterProcessingInstructionTarget:
                {
                    int c = Consume();
                    if (!IsWhitespace(c))
                    {
                        Reconsume();
                        State = HtmlTokenizerState.ProcessingInstructionData;
                    }

                    break;
                }

            case HtmlTokenizerState.ProcessingInstructionData:
            case HtmlTokenizerState.ProcessingInstructionQuestionable:
                {
                    int c = Consume();
                    bool questionable = State == HtmlTokenizerState.ProcessingInstructionQuestionable;
                    if (c == '?' && !questionable)
                    {
                        State = HtmlTokenizerState.ProcessingInstructionQuestionable;
                    }
                    else if (c == '>')
                    {
                        State = HtmlTokenizerState.Data;
                        EmitComment();
                    }
                    else if (c == Eof)
                    {
                        Error("eof-in-processing-instruction");
                        EmitEof();
                    }
                    else if (questionable)
                    {
                        buffer.Append('?');
                        Reconsume();
                        State = HtmlTokenizerState.ProcessingInstructionData;
                    }
                    else
                    {
                        buffer.Append((char)c);
                    }

                    break;
                }

            case HtmlTokenizerState.CharacterReference:
                {
                    temporary.Clear().Append('&');
                    int c = Consume();
                    if (IsAsciiAlphanumeric(c))
                    {
                        Reconsume();
                        State = HtmlTokenizerState.NamedCharacterReference;
                    }
                    else if (c == '#')
                    {
                        temporary.Append('#');
                        State = HtmlTokenizerState.NumericCharacterReference;
                    }
                    else
                    {
                        FlushCodePoints();
                        Reconsume();
                        State = returnState;
                    }

                    break;
                }

            case HtmlTokenizerState.NamedCharacterReference:
                NamedReference();
                break;

            case HtmlTokenizerState.AmbiguousAmpersand:
                {
                    int c = Consume();
                    if (IsAsciiAlphanumeric(c))
                    {
                        if (ConsumedAsPartOfAttribute)
                        {
                            attributeValue.Append((char)c);
                        }
                        else
                        {
                            EmitCharacter(c);
                        }
                    }
                    else
                    {
                        if (c == ';')
                        {
                            Error("unknown-named-character-reference");
                        }

                        Reconsume();
                        State = returnState;
                    }

                    break;
                }

            case HtmlTokenizerState.NumericCharacterReference:
                {
                    characterCode = 0;
                    int c = Consume();
                    if (c is 'x' or 'X')
                    {
                        temporary.Append((char)c);
                        State = HtmlTokenizerState.HexadecimalCharacterReferenceStart;
                    }
                    else
                    {
                        Reconsume();
                        State = HtmlTokenizerState.DecimalCharacterReferenceStart;
                    }

                    break;
                }

            case HtmlTokenizerState.HexadecimalCharacterReferenceStart:
            case HtmlTokenizerState.DecimalCharacterReferenceStart:
                {
                    int c = Consume();
                    bool hex = State == HtmlTokenizerState.HexadecimalCharacterReferenceStart;
                    if (hex ? Uri.IsHexDigit((char)Math.Max(0, c)) && c != Eof : c is >= '0' and <= '9')
                    {
                        Reconsume();
                        State = hex ? HtmlTokenizerState.HexadecimalCharacterReference : HtmlTokenizerState.DecimalCharacterReference;
                    }
                    else
                    {
                        Error("absence-of-digits-in-numeric-character-reference");
                        FlushCodePoints();
                        Reconsume();
                        State = returnState;
                    }

                    break;
                }

            case HtmlTokenizerState.HexadecimalCharacterReference:
            case HtmlTokenizerState.DecimalCharacterReference:
                {
                    int c = Consume();
                    bool hex = State == HtmlTokenizerState.HexadecimalCharacterReference;
                    int digit = c switch
                    {
                        >= '0' and <= '9' => c - '0',
                        >= 'a' and <= 'f' when hex => c - 'a' + 10,
                        >= 'A' and <= 'F' when hex => c - 'A' + 10,
                        _ => -1,
                    };
                    if (digit >= 0)
                    {
                        characterCode = Math.Min((characterCode * (hex ? 16 : 10)) + digit, 0x7FFFFFFF);
                    }
                    else if (c == ';')
                    {
                        State = HtmlTokenizerState.NumericCharacterReferenceEnd;
                    }
                    else
                    {
                        Error("missing-semicolon-after-character-reference");
                        Reconsume();
                        State = HtmlTokenizerState.NumericCharacterReferenceEnd;
                    }

                    break;
                }

            case HtmlTokenizerState.NumericCharacterReferenceEnd:
                {
                    long code = characterCode;
                    if (code == 0)
                    {
                        Error("null-character-reference");
                    }
                    else if (code > 0x10FFFF)
                    {
                        Error("character-reference-outside-unicode-range");
                    }
                    else if (code is >= 0xD800 and <= 0xDFFF)
                    {
                        Error("surrogate-character-reference");
                    }
                    else if (code is (>= 0xFDD0 and <= 0xFDEF) || (code & 0xFFFE) == 0xFFFE)
                    {
                        Error("noncharacter-character-reference");
                    }
                    else if (code == 0x0D || (code < 0x20 && code is not (0x09 or 0x0A or 0x0C)) || code is >= 0x7F and <= 0x9F)
                    {
                        Error("control-character-reference");
                    }

                    temporary.Clear().Append(HtmlEntities.FromCodePoint(code, windows1252: true));
                    FlushCodePoints();
                    State = returnState;
                    break;
                }
        }
    }

    private static HtmlTokenizerState TextStateOf(HtmlTokenizerState state)
    {
        return state switch
        {
            HtmlTokenizerState.RcDataEndTagOpen or HtmlTokenizerState.RcDataEndTagName => HtmlTokenizerState.RcData,
            HtmlTokenizerState.RawTextEndTagOpen or HtmlTokenizerState.RawTextEndTagName => HtmlTokenizerState.RawText,
            HtmlTokenizerState.ScriptDataEndTagOpen or HtmlTokenizerState.ScriptDataEndTagName => HtmlTokenizerState.ScriptData,
            _ => HtmlTokenizerState.ScriptDataEscaped,
        };
    }

    /// <summary>A processing instruction with a target it may not have becomes a bogus comment: "?" and the target so far.</summary>
    private void TemporaryToComment()
    {
        current = new HtmlToken { Kind = HtmlTokenKind.Comment };
        buffer.Clear().Append('?').Append(temporary);
        State = HtmlTokenizerState.BogusComment;
    }

    private void EmitComment()
    {
        HtmlToken token = current!;
        token.Data = buffer.ToString();
        current = null;
        Emit(token);
    }

    /// <summary>Emits the doctype; <paramref name="named"/> true takes the name from the buffer, false leaves it null.</summary>
    private void EmitDoctype(bool? named)
    {
        HtmlToken token = current ?? new HtmlToken { Kind = HtmlTokenKind.Doctype };
        if (named == true)
        {
            token.Name = buffer.ToString();
        }

        current = null;
        Emit(token);
    }

    private void OpenIdentifier(bool isPublic, int quote)
    {
        buffer.Clear();
        State = (isPublic, quote) switch
        {
            (true, '"') => HtmlTokenizerState.DoctypePublicIdentifierDoubleQuoted,
            (true, _) => HtmlTokenizerState.DoctypePublicIdentifierSingleQuoted,
            (false, '"') => HtmlTokenizerState.DoctypeSystemIdentifierDoubleQuoted,
            _ => HtmlTokenizerState.DoctypeSystemIdentifierSingleQuoted,
        };
    }

    private void SetIdentifier(bool isPublic)
    {
        if (isPublic)
        {
            current!.PublicId = buffer.ToString();
        }
        else
        {
            current!.SystemId = buffer.ToString();
        }
    }

    private void DoctypeIdentifierMissing(int c, bool isPublic)
    {
        if (c == '>')
        {
            Error(isPublic ? "missing-doctype-public-identifier" : "missing-doctype-system-identifier");
            current!.ForceQuirks = true;
            State = HtmlTokenizerState.Data;
            EmitDoctype(named: null);
        }
        else if (c == Eof)
        {
            Error("eof-in-doctype");
            current!.ForceQuirks = true;
            EmitDoctype(named: null);
            EmitEof();
        }
        else
        {
            Error(isPublic ? "missing-quote-before-doctype-public-identifier" : "missing-quote-before-doctype-system-identifier");
            current!.ForceQuirks = true;
            Reconsume();
            State = HtmlTokenizerState.BogusDoctype;
        }
    }

    /// <summary>The named character reference state: the longest name in the table that the input starts with.</summary>
    private void NamedReference()
    {
        int longest = 0;
        string? value = null;
        int available = Math.Min(HtmlEntities.MaxNameLength, input.Length - position);
        ReadOnlySpan<char> ahead = input.AsSpan(position, available);
        for (int length = 1; length <= available; length++)
        {
            if (!IsAsciiAlphanumeric(ahead[length - 1]) && ahead[length - 1] != ';')
            {
                break;
            }

            if (HtmlEntities.Lookup(ahead[..length]) is { } found)
            {
                longest = length;
                value = found;
            }

            if (ahead[length - 1] == ';')
            {
                break;
            }
        }

        if (value is null)
        {
            FlushCodePoints();
            State = HtmlTokenizerState.AmbiguousAmpersand;
            return;
        }

        bool semicolon = ahead[longest - 1] == ';';
        int next = position + longest < input.Length ? input[position + longest] : Eof;
        if (ConsumedAsPartOfAttribute && !semicolon && (next == '=' || IsAsciiAlphanumeric(next)))
        {
            // Historical: in an attribute, "&amp=" or "&ampx" stays as written.
            temporary.Append(input, position, longest);
            position += longest;
            FlushCodePoints();
            State = returnState;
            return;
        }

        position += longest;
        if (!semicolon)
        {
            Error("missing-semicolon-after-character-reference");
        }

        temporary.Clear().Append(value);
        FlushCodePoints();
        State = returnState;
    }
}
