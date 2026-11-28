"""Writes the formula field fixtures in tests/files/fields from scratch.

Each field's stored result is the value Word's documented rules give, so the tests compare Egap with these until
tools/update-field-fixtures.ps1 has saved Word's own results next to them as <name>.word.docx.

    python tools/make-field-fixtures.py
"""

import os
import zipfile
from xml.sax.saxutils import escape

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "files", "fields")

W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"

CONTENT_TYPES = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
<Default Extension="xml" ContentType="application/xml"/>
<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
<Override PartName="/word/settings.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml"/>
</Types>"""

RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>"""

DOCUMENT_RELS = """<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings" Target="settings.xml"/>
</Relationships>"""


def settings(decimal, separator):
    return (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<w:settings xmlns:w="{W}">'
            f'<w:decimalSymbol w:val="{decimal}"/><w:listSeparator w:val="{separator}"/></w:settings>')


def run(text):
    return f'<w:r><w:t xml:space="preserve">{escape(text)}</w:t></w:r>'


def field(code, result):
    """A complex field; code is a string or a list of strings and nested (code, result) fields."""
    parts = code if isinstance(code, list) else [code]
    xml = '<w:r><w:fldChar w:fldCharType="begin"/></w:r>'
    for part in parts:
        if isinstance(part, tuple):
            xml += field(*part)
        else:
            xml += f'<w:r><w:instrText xml:space="preserve">{escape(part)}</w:instrText></w:r>'
    xml += '<w:r><w:fldChar w:fldCharType="separate"/></w:r>'
    xml += run(result)
    xml += '<w:r><w:fldChar w:fldCharType="end"/></w:r>'
    return xml


def content(item):
    """A paragraph's inner XML: text, a field (code, result), a bookmark ("[", name) or ("]", name), or a list of these."""
    if item is None:
        return ""
    if isinstance(item, str):
        return run(item)
    if isinstance(item, list):
        return "".join(content(i) for i in item)
    if item[0] == "[":
        return f'<w:bookmarkStart w:id="{bookmark_id(item[1])}" w:name="{item[1]}"/>'
    if item[0] == "]":
        return f'<w:bookmarkEnd w:id="{bookmark_id(item[1])}"/>'
    return field(*item)


BOOKMARKS = {}


def bookmark_id(name):
    return BOOKMARKS.setdefault(name, len(BOOKMARKS) + 1)


def paragraph(item):
    return f"<w:p>{content(item)}</w:p>"


def table(rows):
    """rows: lists of cells; a cell is content, or a dict with "content", "span" and "merge" ("restart" or "continue")."""
    columns = max(sum(c.get("span", 1) if isinstance(c, dict) else 1 for c in row) for row in rows)
    xml = ('<w:tbl><w:tblPr><w:tblW w:w="0" w:type="auto"/><w:tblBorders>'
           + "".join(f'<w:{side} w:val="single" w:sz="4" w:space="0" w:color="auto"/>' for side in ("top", "left", "bottom", "right", "insideH", "insideV"))
           + '</w:tblBorders></w:tblPr><w:tblGrid>' + '<w:gridCol w:w="1800"/>' * columns + '</w:tblGrid>')
    for row in rows:
        xml += "<w:tr>"
        for cell in row:
            spec = cell if isinstance(cell, dict) else {"content": cell}
            span = spec.get("span", 1)
            properties = f'<w:tcW w:w="{1800 * span}" w:type="dxa"/>'
            if span > 1:
                properties += f'<w:gridSpan w:val="{span}"/>'
            if "merge" in spec:
                properties += '<w:vMerge w:val="restart"/>' if spec["merge"] == "restart" else "<w:vMerge/>"
            xml += f"<w:tc><w:tcPr>{properties}</w:tcPr>{paragraph(spec.get('content'))}</w:tc>"
        xml += "</w:tr>"
    return xml + "</w:tbl>" + paragraph(None)


def write(name, blocks, decimal=".", separator=","):
    body = "".join(blocks)
    document = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<w:document xmlns:w="{W}"><w:body>{body}'
                '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1134" '
                'w:header="567" w:footer="567" w:gutter="0"/></w:sectPr></w:body></w:document>')
    path = os.path.join(ROOT, name)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as package:
        for entry, data in (("[Content_Types].xml", CONTENT_TYPES), ("_rels/.rels", RELS), ("word/_rels/document.xml.rels", DOCUMENT_RELS),
                            ("word/document.xml", document), ("word/settings.xml", settings(decimal, separator))):
            info = zipfile.ZipInfo(entry, date_time=(2026, 11, 28, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(info, data)
    print(path)


def line(label, code, result):
    return paragraph([label + ": ", (code, result)])


def formulas():
    BOOKMARKS.clear()
    plain = [
        ("= 2+3", "5"), ("=2+3*4", "14"), ("= (2+3)*4", "20"), ("= 10/4", "2.5"), ("= 1/3", "0.33"), ("= 2^10", "1024"),
        ("= -2^2", "4"), ("= 2^3^2", "512"), ("= 50%", "0.5"), ("= 200*10%", "20"), ("= 7-10", "-3"), ("= 3 > 2", "1"),
        ("= 3 < 2", "0"), ("= 2 = 2", "1"), ("= 2 <> 2", "0"), ("= 2 <= 2", "1"), ("= 3 >= 4", "0"), ("= 1 + 2 > 2", "1"),
        ("= ABS(-4.5)", "4.5"), ("= AND(1, 0)", "0"), ("= OR(1, 0)", "1"), ("= NOT(0)", "1"), ("= AVERAGE(1, 2, 3, 4)", "2.5"),
        ("= COUNT(1, 2, 3)", "3"), ("= DEFINED(Price)", "1"), ("= DEFINED(Missing)", "0"), ("= FALSE", "0"), ("= TRUE", "1"),
        ("= IF(2 > 1, 10, 20)", "10"), ("= IF(1, 1, 1/0)", "!Zero Divide"), ("= INT(-7.8)", "-7"), ("= INT(7.8)", "7"),
        ("= MAX(3, 9, 4)", "9"), ("= MIN(3, 9, 4)", "3"), ("= MOD(17, 5)", "2"), ("= MOD(-7, 3)", "-1"),
        ("= PRODUCT(2, 3, 4)", "24"), ("= ROUND(2.345, 2)", "2.35"), ("= ROUND(1234.5, -2)", "1200"), ("= SIGN(-3)", "-1"),
        ("= SUM(1, 2, 3)", "6"),
        ("= Price * 2", "25"), ("= Price * Qty", "37.5"),
        (["= ", ("= 2+3", "5"), " * 2"], "10"), (["= ", (" REF Price ", "12.50"), " + 1"], "13.5"),
        ('= 1234.5 \\# "#,##0.00"', "1,234.50"), ('= 5 \\# "0.00"', "5.00"), ('= 9+6 \\# "$###"', "$ 15"),
        ("= 111053 + 111439 \\# x##", "492"), ("= 1/8 \\# 0.00x", "0.125"), ("= 3/4 \\# .x", ".8"), ("= 10-90 \\# -##", "-80"),
        ("= 90-10 \\# -##", " 80"), ("= 100-90 \\# +##", "+10"), ('= -1245.65 \\# "$#,##0.00;($#,##0.00)"', "($1,245.65)"),
        ("= 0 \\# \"0.00;(0.00);'zero'\"", "zero"), ('= 33 \\# "##%"', "33%"), ("= 347.44 \\# \"$##0.00 'is sales tax'\"", "$347.44 is sales tax"),
        ('= -5 \\# "0.00"', "-5.00"),
        ("= 2 +", "!Unexpected End of Formula"), ("= (1+2", "!Unexpected End of Formula"), ("= 5 $ 3", "!Syntax Error, $"),
        ("= 1/0", "!Zero Divide"), ("= Nothing + 1", "!Undefined Bookmark, NOTHING"), ("= A1 + 1", "1"),
        ([" IF ", ("= 2*60", "120"), ' > 100 "big" "small" '], "big"), (' IF Price > 10 "dear" "cheap" ', "dear"),
        (' IF Price < 10 "dear" "cheap" ', "cheap"),
    ]
    blocks = [paragraph("Formula fields, each followed by the result Word's rules give."),
              paragraph(["Price: ", ("[", "Price"), "12.50", ("]", "Price"), ", quantity: ", ("[", "Qty"), "3", ("]", "Qty")])]
    blocks += [line(str(i + 1), code, result) for i, (code, result) in enumerate(plain)]

    # Positional references over text, empty and currency cells; a bookmark over cells.
    blocks.append(paragraph("Order:"))
    blocks.append(table([
        ["Item", "Qty", "Price"],
        ["Bolts", "4", [("[", "Prices"), "$1.50"]],
        ["Nuts", "10", "$0.25"],
        ["Washers", None, ["1,200.00", ("]", "Prices")]],
        ["Total", ("= SUM(ABOVE)", "14"), ('= SUM(ABOVE) \\# "$#,##0.00"', "$1,200.00")],
    ]))
    blocks.append(line("Prices", "= SUM(Prices)", "!Syntax Error, )"))
    blocks.append(line("Dearest", "= MAX(Prices)", "!Syntax Error, )"))

    # A1 and RnCn references, ranges, whole rows and a bookmarked table.
    blocks.append(paragraph("Numbers:"))
    blocks.append(table([
        [[("[", "Numbers"), "1"], "2", "3", ("= SUM(LEFT)", "6")],
        ["4", "5", "6", ("= SUM(LEFT)", "15")],
        [("= SUM(A1:A2)", "5"), ("= B1*B2", "10"), ("= SUM(R1C3:R2C3)", "!Syntax Error, :"), ("= SUM(A1:C2)", "21")],
        [("= AVERAGE(1:1)", "3"), ("= COUNT(A1,B2,C1)", "3"), ("= R2C1 + C1", "7"), [("= PRODUCT(B1:C1)", "6"), ("]", "Numbers")]],
    ]))
    blocks.append(line("Column B", "= SUM(Numbers B1:B2)", "7"))

    # How a cell's text reads as a number: each probe, then the SUM(LEFT) Egap gives it.
    probes = [("$ 1.50", "1.5"), ("€2.25", "2.25"), ("€ 2.25", "2.25"), ("5 €", "5"), ("$1.50", "1.5"), ("12 kg", "12"), ("(5)", "(5)"),
              ("50%", "0.5"), ("-3", "-3"), ("+4", "0"), ("1,234", "1,234"), ("1,23", "24"), ("5.", "5"), (".5", "0.5"),
              ("Item 7", "7"), ("1 000", "1"), ("1,234.5", "1,234.5")]
    blocks.append(paragraph("Cell text as numbers:"))
    blocks.append(table([[text, ("= SUM(LEFT)", result)] for text, result in probes]))
    blocks.append(paragraph(["Label: ", ("[", "Label"), "abc", ("]", "Label")]))
    blocks.append(line("Text bookmark", "= Label + 1", "1"))

    # RIGHT and BELOW.
    blocks.append(table([
        [("= SUM(RIGHT)", "15"), "7", "8"],
        [("= SUM(BELOW)", "5"), "x", None],
        ["2", "text", None],
        ["3", None, None],
    ]))

    # Merged cells: a row's cells count in order.
    blocks.append(table([
        [{"content": "10", "span": 2}, "5"],
        ["1", "2", ("= SUM(A1:C1)", "15")],
        [{"content": "100", "merge": "restart"}, "3", ("= B1", "5")],
        [{"content": None, "merge": "continue"}, "4", ("= SUM(A3:A4)", "100")],
        [("= SUM(ABOVE)", "111"), None, None],
    ]))
    write("formulas.docx", blocks)


def formulas_comma():
    BOOKMARKS.clear()
    blocks = [paragraph("Formula fields with a decimal comma and semicolons between arguments.")]
    blocks += [line(str(i + 1), code, result) for i, (code, result) in enumerate([
        ("= 1,5 + 2", "3,5"), ("= SUM(1;2;3,5)", "6,5"), ('= 1234,5 \\# "#.##0,00"', "1.234,50"), ("= 10/4", "2,5"),
    ])]
    blocks.append(table([
        ["Sum"],
        ["1.200,50"],
        ["€ 2,25"],
        [("= SUM(ABOVE)", "1.202,75")],
    ]))
    write("formulas-comma.docx", blocks, decimal=",", separator=";")


if __name__ == "__main__":
    os.makedirs(ROOT, exist_ok=True)
    formulas()
    formulas_comma()
