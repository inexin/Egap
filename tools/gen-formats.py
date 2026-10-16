"""Generates Egap.Documents' format classes from one table of properties. Run from the repo root: python tools/gen-formats.py src/Egap.Documents"""
import sys, pathlib

out = pathlib.Path(sys.argv[1]) / "Formatting"

# (name, type, kind, doc, flags). kind: bool, enum, len (>=0 points), slen (signed points), int, double, ref, struct
RUN = [
    ("Bold", "bool", "bool", "Bold text (Latin and East Asian scripts).", "toggle"),
    ("Italic", "bool", "bool", "Italic text (Latin and East Asian scripts).", "toggle"),
    ("BoldComplexScript", "bool", "bool", "Bold for complex scripts such as Arabic and Hebrew.", "toggle"),
    ("ItalicComplexScript", "bool", "bool", "Italic for complex scripts.", "toggle"),
    ("Underline", "UnderlineStyle", "enum", "The underline.", ""),
    ("UnderlineColor", "DocumentColor", "struct", "The underline's colour; automatic follows the text colour.", ""),
    ("Strikethrough", "bool", "bool", "A line through the text.", "toggle"),
    ("DoubleStrikethrough", "bool", "bool", "Two lines through the text.", "toggle"),
    ("AllCaps", "bool", "bool", "Shown in capitals, stored as typed.", "toggle"),
    ("SmallCaps", "bool", "bool", "Lower-case letters shown as small capitals.", "toggle"),
    ("Hidden", "bool", "bool", "Hidden text: kept, not shown or laid out.", "toggle"),
    ("Emboss", "bool", "bool", "Embossed.", "toggle"),
    ("Imprint", "bool", "bool", "Engraved.", "toggle"),
    ("Outline", "bool", "bool", "Drawn as an outline.", "toggle"),
    ("Shadow", "bool", "bool", "With a shadow.", "toggle"),
    ("Size", "double", "len", "The font size in points (Latin and East Asian scripts).", ""),
    ("SizeComplexScript", "double", "len", "The font size in points for complex scripts.", ""),
    ("Color", "DocumentColor", "struct", "The text colour.", ""),
    ("Highlight", "HighlightColor", "enum", "The highlight behind the text.", ""),
    ("Shading", "Shading", "ref", "The shading behind the text.", ""),
    ("Border", "Border", "ref", "A border around the text.", ""),
    ("VerticalPosition", "VerticalTextPosition", "enum", "Superscript or subscript.", ""),
    ("Position", "double", "slen", "How far the text is raised (positive) or lowered (negative), in points.", ""),
    ("Spacing", "double", "slen", "Extra space after each character in points; negative condenses.", ""),
    ("Scale", "double", "len", "Horizontal scaling in per cent, 100 for none.", ""),
    ("Kerning", "double", "len", "The font size in points from which pairs are kerned; 0 for none.", ""),
    ("FontAscii", "string", "ref", "The font for ASCII characters.", ""),
    ("FontHighAnsi", "string", "ref", "The font for other Latin characters.", ""),
    ("FontEastAsia", "string", "ref", "The font for East Asian characters.", ""),
    ("FontComplexScript", "string", "ref", "The font for complex scripts.", ""),
    ("ThemeFontAscii", "ThemeFont", "enum", "The theme font for ASCII characters; wins over FontAscii set at the same level.", ""),
    ("ThemeFontHighAnsi", "ThemeFont", "enum", "The theme font for other Latin characters.", ""),
    ("ThemeFontEastAsia", "ThemeFont", "enum", "The theme font for East Asian characters.", ""),
    ("ThemeFontComplexScript", "ThemeFont", "enum", "The theme font for complex scripts.", ""),
    ("Language", "string", "ref", "The language of Latin text, as a BCP 47 tag such as en-US.", ""),
    ("LanguageEastAsia", "string", "ref", "The language of East Asian text.", ""),
    ("LanguageBidi", "string", "ref", "The language of complex script text.", ""),
    ("RightToLeft", "bool", "bool", "Right-to-left text.", ""),
    ("ComplexScript", "bool", "bool", "Treat the text as complex script whatever its characters.", ""),
    ("NoProof", "bool", "bool", "Leave out of spelling and grammar checks.", ""),
    ("SpecialHidden", "bool", "bool", "Word's specVanish: hidden even when hidden text is shown (used by style separators).", ""),
]

PARA = [
    ("Alignment", "ParagraphAlignment", "enum", "How lines line up between the indents.", ""),
    ("LeftIndent", "double", "slen", "The indent from the start margin in points.", ""),
    ("RightIndent", "double", "slen", "The indent from the end margin in points.", ""),
    ("FirstLineIndent", "double", "slen", "The first line's extra indent in points; negative for a hanging indent.", ""),
    ("SpaceBefore", "double", "len", "Space before the paragraph in points.", ""),
    ("SpaceAfter", "double", "len", "Space after the paragraph in points.", ""),
    ("SpaceBeforeAuto", "bool", "bool", "Space before chosen by the application, as HTML's margins (Word's beforeAutospacing).", ""),
    ("SpaceAfterAuto", "bool", "bool", "Space after chosen by the application.", ""),
    ("LineSpacing", "double", "len", "The line spacing: a multiple of single, or points, as LineSpacingRule says.", ""),
    ("LineSpacingRule", "LineSpacingRule", "enum", "How LineSpacing is read.", ""),
    ("KeepWithNext", "bool", "bool", "On the same page as the next paragraph.", ""),
    ("KeepTogether", "bool", "bool", "All lines on one page.", ""),
    ("PageBreakBefore", "bool", "bool", "Starts on a new page.", ""),
    ("WidowControl", "bool", "bool", "No single first or last line on its own page.", ""),
    ("OutlineLevel", "int", "outline", "The outline level, 0 to 8, or 9 for body text.", ""),
    ("TabStops", "IReadOnlyList<TabStop>", "tabs", "Tab stops added here; a Clear stop removes one a style gives.", ""),
    ("BorderTop", "Border", "ref", "The border above.", ""),
    ("BorderBottom", "Border", "ref", "The border below.", ""),
    ("BorderLeft", "Border", "ref", "The border at the start.", ""),
    ("BorderRight", "Border", "ref", "The border at the end.", ""),
    ("BorderBetween", "Border", "ref", "The border between paragraphs with the same borders.", ""),
    ("BorderBar", "Border", "ref", "A bar beside the paragraph, outside its borders.", ""),
    ("Shading", "Shading", "ref", "The shading behind the paragraph.", ""),
    ("ContextualSpacing", "bool", "bool", "No space between paragraphs of the same style.", ""),
    ("Bidi", "bool", "bool", "A right-to-left paragraph.", ""),
    ("SuppressLineNumbers", "bool", "bool", "Left out of line numbering.", ""),
    ("SuppressAutoHyphens", "bool", "bool", "Not hyphenated automatically.", ""),
    ("SnapToGrid", "bool", "bool", "Lines follow the document grid.", ""),
    ("MirrorIndents", "bool", "bool", "Left and right indents swap on even pages.", ""),
]

TABLE = [
    ("Width", "TableWidth", "struct", "The preferred width.", ""),
    ("Alignment", "TableAlignment", "enum", "Where the table sits between the margins.", ""),
    ("Indent", "double", "slen", "The indent from the start margin in points, for a left-aligned table.", ""),
    ("Layout", "TableLayoutKind", "enum", "Fixed or autofit column widths.", ""),
    ("CellSpacing", "double", "len", "Space between cells in points.", ""),
    ("CellMarginTop", "double", "len", "The default top margin inside cells, in points.", ""),
    ("CellMarginBottom", "double", "len", "The default bottom margin inside cells, in points.", ""),
    ("CellMarginLeft", "double", "len", "The default start margin inside cells, in points.", ""),
    ("CellMarginRight", "double", "len", "The default end margin inside cells, in points.", ""),
    ("BorderTop", "Border", "ref", "The border along the top.", ""),
    ("BorderBottom", "Border", "ref", "The border along the bottom.", ""),
    ("BorderLeft", "Border", "ref", "The border at the start.", ""),
    ("BorderRight", "Border", "ref", "The border at the end.", ""),
    ("BorderInsideHorizontal", "Border", "ref", "The borders between rows.", ""),
    ("BorderInsideVertical", "Border", "ref", "The borders between columns.", ""),
    ("Shading", "Shading", "ref", "The shading behind every cell.", ""),
    ("Bidi", "bool", "bool", "A right-to-left table: the first column on the right.", ""),
    ("RowBandSize", "int", "posint", "How many rows each band of a table style's banded rows covers.", ""),
    ("ColumnBandSize", "int", "posint", "How many columns each band covers.", ""),
    ("LookFirstRow", "bool", "bool", "The table style's first row formatting applies.", ""),
    ("LookLastRow", "bool", "bool", "The table style's last row formatting applies.", ""),
    ("LookFirstColumn", "bool", "bool", "The table style's first column formatting applies.", ""),
    ("LookLastColumn", "bool", "bool", "The table style's last column formatting applies.", ""),
    ("LookNoRowBands", "bool", "bool", "The table style's banded rows do not apply.", ""),
    ("LookNoColumnBands", "bool", "bool", "The table style's banded columns do not apply.", ""),
]

ROW = [
    ("Height", "double", "len", "The height in points, read as HeightRule says.", ""),
    ("HeightRule", "RowHeightRule", "enum", "How Height is read.", ""),
    ("IsHeader", "bool", "bool", "Repeated at the top of each page the table continues on.", ""),
    ("CantSplit", "bool", "bool", "Kept on one page.", ""),
    ("Alignment", "TableAlignment", "enum", "Where the row sits, overriding the table's alignment.", ""),
    ("CellSpacing", "double", "len", "Space between the row's cells in points.", ""),
    ("Hidden", "bool", "bool", "Hidden row.", ""),
]

CELL = [
    ("Width", "TableWidth", "struct", "The preferred width.", ""),
    ("GridSpan", "int", "posint", "How many grid columns the cell spans (horizontal merge).", ""),
    ("VerticalMerge", "VerticalMerge", "enum", "The cell's part in a vertical merge.", ""),
    ("VerticalAlignment", "VerticalAlignment", "enum", "Where content sits vertically.", ""),
    ("MarginTop", "double", "len", "The top margin inside the cell in points.", ""),
    ("MarginBottom", "double", "len", "The bottom margin inside the cell in points.", ""),
    ("MarginLeft", "double", "len", "The start margin inside the cell in points.", ""),
    ("MarginRight", "double", "len", "The end margin inside the cell in points.", ""),
    ("BorderTop", "Border", "ref", "The border along the top.", ""),
    ("BorderBottom", "Border", "ref", "The border along the bottom.", ""),
    ("BorderLeft", "Border", "ref", "The border at the start.", ""),
    ("BorderRight", "Border", "ref", "The border at the end.", ""),
    ("BorderDiagonalDown", "Border", "ref", "A diagonal from the top start corner to the bottom end corner.", ""),
    ("BorderDiagonalUp", "Border", "ref", "A diagonal from the bottom start corner to the top end corner.", ""),
    ("Shading", "Shading", "ref", "The shading behind the cell.", ""),
    ("TextDirection", "TextDirection", "enum", "Which way text runs.", ""),
    ("NoWrap", "bool", "bool", "Text is not wrapped.", ""),
    ("FitText", "bool", "bool", "Text is squeezed to fit the width.", ""),
    ("HideMark", "bool", "bool", "The cell's end mark takes no height when the cell is empty.", ""),
]

PAGE = [
    ("PageWidth", "double", "len", "The page width in points.", ""),
    ("PageHeight", "double", "len", "The page height in points.", ""),
    ("Orientation", "PageOrientation", "enum", "Portrait or landscape; the width and height already reflect it.", ""),
    ("MarginTop", "double", "slen", "The top margin in points; negative keeps text from moving below a header.", ""),
    ("MarginBottom", "double", "slen", "The bottom margin in points.", ""),
    ("MarginLeft", "double", "len", "The left margin in points.", ""),
    ("MarginRight", "double", "len", "The right margin in points.", ""),
    ("HeaderDistance", "double", "len", "From the top edge to the header, in points.", ""),
    ("FooterDistance", "double", "len", "From the bottom edge to the footer, in points.", ""),
    ("Gutter", "double", "len", "Extra margin for binding, in points.", ""),
    ("SectionStart", "SectionStart", "enum", "Where the section starts.", ""),
    ("DifferentFirstPage", "bool", "bool", "The first page has its own header and footer.", ""),
    ("VerticalAlignment", "VerticalAlignment", "enum", "Where the text sits on the page vertically.", ""),
    ("PageNumberStart", "int", "int", "The first page number, when the section restarts numbering.", ""),
    ("PageNumberStyle", "NumberStyle", "enum", "How page numbers are written.", ""),
    ("ColumnCount", "int", "posint", "The number of text columns.", ""),
    ("ColumnSpacing", "double", "len", "The space between equal columns in points.", ""),
    ("EqualColumnWidths", "bool", "bool", "Whether columns are equal; otherwise Columns gives each one.", ""),
    ("Columns", "IReadOnlyList<TextColumn>", "columns", "Each column's width and the space after it, for unequal columns.", ""),
    ("ColumnSeparator", "bool", "bool", "A line between columns.", ""),
    ("TextDirection", "TextDirection", "enum", "Which way text runs.", ""),
    ("Bidi", "bool", "bool", "A right-to-left section: columns from the right.", ""),
]

FORMATS = [
    ("RunFormat", RUN, "Character formatting: font, size, colour, bold and the rest (Word's run properties)."),
    ("ParagraphFormat", PARA, "Paragraph formatting: alignment, indents, spacing, keep rules, tabs, borders and shading (Word's paragraph properties)."),
    ("TableFormat", TABLE, "Table formatting: width, alignment, layout, default cell margins, borders, shading and style options."),
    ("RowFormat", ROW, "Table row formatting: height, header repeat and page splitting."),
    ("CellFormat", CELL, "Table cell formatting: width, merges, vertical alignment, margins, borders and shading."),
    ("PageSetup", PAGE, "A section's page setup: paper size, orientation, margins, columns, page numbering and where it starts."),
]


def prop_code(i, name, typ, kind, doc):
    nullable = f"{typ}?"
    lines = [f"    /// <summary>{doc}</summary>", f"    public {nullable} {name}", "    {"]
    if kind in ("ref", "tabs", "columns"):
        lines.append(f"        get => GetRef<{typ}>({i});")
    else:
        lines.append(f"        get => Get<{typ}>({i});")
    if kind == "enum":
        setter = f"SetEnum({i}, value);"
    elif kind == "len":
        setter = f"SetLength({i}, value);"
    elif kind == "slen":
        setter = f"SetLength({i}, value, allowNegative: true);"
    elif kind == "outline":
        setter = f"SetRaw({i}, value is null or (>= 0 and <= 9) ? value : throw new ArgumentOutOfRangeException(nameof(value)));"
    elif kind == "posint":
        setter = f"SetRaw({i}, value is null or > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value)));"
    elif kind == "tabs":
        setter = f"SetRaw({i}, value is null ? null : (TabStop[])[.. value]);"
    elif kind == "columns":
        setter = f"SetRaw({i}, value is null ? null : (TextColumn[])[.. value]);"
    else:
        setter = f"SetRaw({i}, value);"
    lines.append(f"        set => {setter}")
    lines.append("    }")
    return "\n".join(lines)


for cls, props, summary in FORMATS:
    body = []
    body.append("// Generated from one table of properties (tools/gen-formats.py); edit the table, not this file.")
    body.append("namespace Egap.Documents;")
    body.append("")
    body.append(f"/// <summary>{summary} Every property is nullable: null means not set here.</summary>")
    body.append(f"public sealed partial class {cls} : FormatBase")
    body.append("{")
    body.append(f"    internal const int Count = {len(props)};")
    body.append("")
    toggles = [i for i, p in enumerate(props) if "toggle" in p[4]]
    if cls == "RunFormat":
        body.append("    /// <summary>The toggle properties, whose values from styles combine as Word's do (ECMA-376 17.7.3).</summary>")
        body.append("    internal static readonly int[] Toggles = [" + ", ".join(str(i) for i in toggles) + "];")
        body.append("")
    for i, (name, typ, kind, doc, flags) in enumerate(props):
        body.append(f"    internal const int {name}Id = {i};")
    body.append("")
    body.append("    internal override int PropertyCount => Count;")
    body.append("")
    for i, (name, typ, kind, doc, flags) in enumerate(props):
        body.append(prop_code(i, name, typ, kind, doc))
        body.append("")
    body.append("    internal override string PropertyName(int id)")
    body.append("    {")
    body.append("        return id switch")
    body.append("        {")
    for i, p in enumerate(props):
        body.append(f"            {i} => nameof({p[0]}),")
    body.append("            _ => throw new ArgumentOutOfRangeException(nameof(id)),")
    body.append("        };")
    body.append("    }")
    body.append("}")
    (out / f"{cls}.cs").write_text("\n".join(body) + "\n", encoding="utf-8", newline="\n")
    print(cls, len(props))
