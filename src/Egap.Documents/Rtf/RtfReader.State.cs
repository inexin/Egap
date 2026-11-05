using System.Text;

using Egap.Documents.Word;

namespace Egap.Documents.Rtf;

/// <summary>The state RTF scopes to groups, and the small records it holds.</summary>
internal sealed partial class RtfReader
{
    /// <summary>What the border control words that follow (<c>\brdrs</c>, <c>\brdrw</c>, ...) describe.</summary>
    private enum BorderTarget
    {
        None,
        ParagraphTop,
        ParagraphBottom,
        ParagraphLeft,
        ParagraphRight,
        ParagraphBetween,
        ParagraphBar,
        ParagraphBox,
        Character,
        TableTop,
        TableBottom,
        TableLeft,
        TableRight,
        TableInsideHorizontal,
        TableInsideVertical,
        CellTop,
        CellBottom,
        CellLeft,
        CellRight,
        CellDiagonalDown,
        CellDiagonalUp,
        Page,
    }

    /// <summary>A tracked insertion or deletion as RTF marks it: the author's index in the revision table and a DTTM date.</summary>
    private sealed record RevisionMark(int Author, int Date);

    /// <summary>A tracked formatting change: who, when, and the formatting before it.</summary>
    private sealed record FormatChange<T>(int Author, DateTimeOffset? Date, T? Old)
        where T : FormatBase;

    /// <summary>An old-style paragraph number (<c>\pn</c>), Word 6's numbering that later Word writes only for old readers.</summary>
    private sealed record PnState
    {
        public int Level { get; init; } = 1;

        public bool Bullet { get; init; }

        public bool Ignored { get; init; }

        public NumberStyle Style { get; init; }

        public int Start { get; init; } = 1;

        public string Before { get; init; } = "";

        public string After { get; init; } = "";

        public int Font { get; init; } = -1;

        public double? Indent { get; init; }
    }

    /// <summary>The state one group sees: its character and paragraph formatting and the reader's place in it.</summary>
    private sealed class State
    {
        private RunFormat character = new();
        private ParagraphFormat paragraph = new();
        private bool characterShared;
        private bool paragraphShared;

        /// <summary>Where the group's opening brace is in the file, to keep the group raw.</summary>
        public int GroupStart { get; set; } = -1;

        /// <summary>The group started with <c>\*</c>: a destination a reader may skip.</summary>
        public bool Ignorable { get; set; }

        public int CharacterStyle { get; set; } = -1;

        public int ParagraphStyle { get; set; }

        public int UnicodeSkip { get; set; } = 1;

        public int Font { get; set; } = -1;

        public WordFontSlot Slot { get; set; }

        public bool InTable { get; set; }

        public int Itap { get; set; }

        public int ListOverride { get; set; }

        public int ListLevel { get; set; }

        public PnState? Pn { get; set; }

        public RevisionMark? Inserted { get; set; }

        public RevisionMark? Deleted { get; set; }

        public FormatChange<RunFormat>? CharChange { get; set; }

        public FormatChange<ParagraphFormat>? ParaChange { get; set; }

        public BorderTarget Border { get; set; }

        public TabAlignment TabAlignment { get; set; }

        public TabLeader TabLeader { get; set; }

        public int LineSpacing { get; set; }

        public bool LineMultiple { get; set; }

        /// <summary>The character formatting, shared with the enclosing group until this one changes it.</summary>
        public RunFormat Char => character;

        /// <summary>The paragraph formatting, shared with the enclosing group until this one changes it.</summary>
        public ParagraphFormat Para => paragraph;

        /// <summary>The character formatting to change: a copy of the shared one on the first change.</summary>
        public RunFormat MutableChar()
        {
            if (characterShared)
            {
                RunFormat copy = new();
                copy.CopyRaw(character);
                character = copy;
                characterShared = false;
            }

            return character;
        }

        /// <summary>The paragraph formatting to change: a copy of the shared one on the first change.</summary>
        public ParagraphFormat MutablePara()
        {
            if (paragraphShared)
            {
                ParagraphFormat copy = new();
                copy.CopyRaw(paragraph);
                paragraph = copy;
                paragraphShared = false;
            }

            return paragraph;
        }

        /// <summary>\plain: the default character formatting.</summary>
        public void ResetCharacter()
        {
            character = new RunFormat();
            characterShared = false;
            CharacterStyle = -1;
            Font = -1;
            Slot = WordFontSlot.ComplexScript;
            CharChange = null;
        }

        /// <summary>\pard: the default paragraph formatting, in the Normal style and outside tables and lists.</summary>
        public void ResetParagraph()
        {
            paragraph = new ParagraphFormat();
            paragraphShared = false;
            ParagraphStyle = 0;
            InTable = false;
            Itap = 0;
            ListOverride = 0;
            ListLevel = 0;
            Pn = null;
            ParaChange = null;
            TabAlignment = TabAlignment.Left;
            TabLeader = TabLeader.None;
            LineSpacing = 0;
            LineMultiple = false;
            if (Border is >= BorderTarget.ParagraphTop and <= BorderTarget.ParagraphBox)
            {
                Border = BorderTarget.None;
            }
        }

        public void CopyFrom(State other)
        {
            character = other.character;
            paragraph = other.paragraph;
            characterShared = true;
            paragraphShared = true;
            other.characterShared = true;
            other.paragraphShared = true;
            GroupStart = -1;
            Ignorable = false;
            CharacterStyle = other.CharacterStyle;
            ParagraphStyle = other.ParagraphStyle;
            UnicodeSkip = other.UnicodeSkip;
            Font = other.Font;
            Slot = other.Slot;
            InTable = other.InTable;
            Itap = other.Itap;
            ListOverride = other.ListOverride;
            ListLevel = other.ListLevel;
            Pn = other.Pn;
            Inserted = other.Inserted;
            Deleted = other.Deleted;
            CharChange = other.CharChange;
            ParaChange = other.ParaChange;
            Border = other.Border;
            TabAlignment = other.TabAlignment;
            TabLeader = other.TabLeader;
            LineSpacing = other.LineSpacing;
            LineMultiple = other.LineMultiple;
        }
    }

    /// <summary>Gathers a destination's plain text: a font, style or bookmark name, a property's value.</summary>
    private sealed class TextCollector(RtfReader reader)
    {
        private readonly StringBuilder value = new();
        private readonly List<byte> pending = [];

        /// <summary>The encoding of the bytes, when not the current font's: a font name in the font's own character set.</summary>
        public Encoding? Encoding { get; set; }

        /// <summary>Takes a text token; false for a token that is not text.</summary>
        public bool Add(RtfTokenKind kind)
        {
            RtfTokenizer tokens = reader.tokens;
            if (reader.skip > 0 && kind is RtfTokenKind.Text or RtfTokenKind.Hex or RtfTokenKind.ControlSymbol or RtfTokenKind.ControlWord)
            {
                if (kind == RtfTokenKind.Text && tokens.Text.Length > reader.skip)
                {
                    int count = reader.skip;
                    reader.skip = 0;
                    pending.AddRange(tokens.Text[count..]);
                    return true;
                }

                reader.skip = Math.Max(0, kind == RtfTokenKind.Text ? reader.skip - tokens.Text.Length : reader.skip - 1);
                return true;
            }

            switch (kind)
            {
                case RtfTokenKind.Text:
                    pending.AddRange(tokens.Text);
                    return true;
                case RtfTokenKind.Hex:
                    pending.Add(tokens.Byte);
                    return true;
                case RtfTokenKind.ControlSymbol when tokens.Symbol is '\\' or '{' or '}' or '~' or '_' or '-':
                    Append(tokens.Symbol switch { '~' => ' ', '_' => '‑', '-' => '­', char c => c });
                    return true;
                case RtfTokenKind.ControlWord when tokens.Word == "u" && tokens.HasParameter:
                    Append((char)(ushort)(short)tokens.Parameter);
                    reader.skip = reader.state.UnicodeSkip;
                    return true;
                case RtfTokenKind.ControlWord when tokens.Word == "uc" && tokens.HasParameter:
                    // The header tables read names through here alone, so \uc must count here too.
                    reader.state.UnicodeSkip = Math.Clamp(tokens.Parameter, 0, 16);
                    return true;
                case RtfTokenKind.ControlWord when SpecialCharacter(tokens.Word) is char special:
                    Append(special);
                    return true;
                default:
                    return false;
            }
        }

        public void Append(char c)
        {
            Flush();
            value.Append(c);
        }

        /// <summary>The text so far, without what follows the first semicolon when <paramref name="toSemicolon"/>.</summary>
        public string Text(bool toSemicolon = false, bool trim = true)
        {
            Flush();
            string result = value.ToString();
            if (toSemicolon && result.IndexOf(';') is int at && at >= 0)
            {
                result = result[..at];
            }

            result = WithoutLoneSurrogates(result);
            return trim ? result.Trim() : result;
        }

        public void Clear()
        {
            pending.Clear();
            value.Clear();
        }

        private void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }

            value.Append((Encoding ?? reader.EncodingOf(reader.state.Font)).GetString([.. pending]));
            pending.Clear();
        }
    }

    /// <summary>The character a control word such as <c>\emdash</c> stands for, or null.</summary>
    private static char? SpecialCharacter(string word) => word switch
    {
        "emdash" => '—',
        "endash" => '–',
        "emspace" => ' ',
        "enspace" => ' ',
        "qmspace" => ' ',
        "bullet" => '•',
        "lquote" => '‘',
        "rquote" => '’',
        "ldblquote" => '“',
        "rdblquote" => '”',
        "zwj" => '‍',
        "zwnj" => '‌',
        "zwbo" => '​',
        "zwnbo" => '﻿',
        "ltrmark" => '‎',
        "rtlmark" => '‏',
        _ => null,
    };
}
