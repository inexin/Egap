"""Writes tests/Egap.Spreadsheets.Tests/Oracle/links.xlsx: formulas that read a linked workbook's kept values.

The linked book is not on disk, so Excel calculates from the values kept in the file, with links not updated. Run
tools/excel-links-oracle.ps1 next, which records what Excel calculates in links.excel.tsv. Everything here is written by hand
from ECMA-376; nothing comes from a corpus. A formula's saved value is what a cell keeps when Excel can't calculate it.
"""
import pathlib
import zipfile
from xml.sax.saxutils import escape

MAIN = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
REL = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
PKG = 'http://schemas.openxmlformats.org/package/2006/relationships'
ML = 'application/vnd.openxmlformats-officedocument.spreadsheetml.'

# (formula, saved value or None)
FORMULAS = [
    ('[1]Prices!A1', None),
    ('[1]Prices!$A$2*2', None),
    ('[1]Prices!B1', None),
    ('[1]Prices!B2', None),
    ('[1]Prices!B3', None),
    ('[1]Prices!C9', None),
    ('ISBLANK([1]Prices!C9)', None),
    ('[1]!Rate', None),
    ('[1]!Table', None),
    ('SUM([1]!Table)', None),
    ('[1]!Gone', None),
    ('[1]!NoFormula', 5),
    ('1+[1]!NoFormula', 6),
    ('SUM([1]!NoFormula)', 5),
    ('IF(TRUE,[1]!NoFormula)', 5),
    ('[1]Nowhere!A1', 11),
    ('1+[1]Nowhere!A1', 13),
    ('[1]Other!A1', 12),
    ('1+[1]Other!A1', 14),
    ('SUM([1]Prices!A1:A3)', None),
    ('COUNT([1]Prices!A1:C9)', None),
    ('COUNTA([1]Prices!A1:C3)', None),
    ('AVERAGE([1]Prices!A1:A3)', None),
    ('MAX([1]Prices!A1:A3)', None),
    ('SUMPRODUCT([1]Prices!A1:A3)', None),
    ('SUMPRODUCT(A1:A3,[1]Prices!A1:A3)', None),
    ('ROWS([1]Prices!A1:A3)', None),
    ('COLUMNS([1]Prices!A1:C3)', None),
    ('INDEX([1]Prices!A1:B3,3,1)', None),
    ('VLOOKUP(7,[1]Prices!A1:B3,1,FALSE)', None),
    ('HLOOKUP(2.5,[1]Prices!A1:A3,2,FALSE)', None),
    ('MATCH(7,[1]Prices!A1:A3,0)', None),
    ('LOOKUP(7,[1]Prices!A1:A3)', None),
    ('INDIRECT("[1]Prices!A2")', 21),
    ('OFFSET([1]Prices!A1,1,0)', 22),
    ('SUM(OFFSET([1]Prices!A1,0,0,2,1))', 23),
    ('SUMIF([1]Prices!A1:A3,">3")', None),
    ('SUMIF([1]Prices!A1:A3,">3",[1]Prices!A1:A3)', None),
    ('SUMIF(A1:A3,">1",[1]Prices!A1:A3)', None),
    ('SUMIF(A1:A3,">1",[1]Prices!A1)', None),
    ('SUMIFS([1]Prices!A1:A3,A1:A3,">1")', None),
    ('SUMIFS(A1:A3,[1]Prices!A1:A3,">3")', None),
    ('COUNTIF([1]Prices!A1:A3,">3")', None),
    ('COUNTIFS([1]Prices!A1:A3,">3")', None),
    ('COUNTIFS(A1:A3,">1",[1]Prices!A1:A3,">3")', None),
    ('AVERAGEIF([1]Prices!A1:A3,">3")', None),
    ('AVERAGEIF(A1:A3,">1",[1]Prices!A1:A3)', None),
    ('AVERAGEIFS([1]Prices!A1:A3,A1:A3,">1")', None),
    ('AVERAGEIFS(A1:A3,[1]Prices!A1:A3,">3")', None),
    ('COUNTBLANK([1]Prices!A1:C3)', None),
    ('SUMIF(A1:A3,[1]Prices!A1)', None),
    ('SUM(A1:A3,[1]Prices!A1)', None),
    ('[1]!Undeclared', 31),
    ('1+[1]!Undeclared', 32),
    ('[2]!Undeclared', 33),
    ('1+[2]!Undeclared', 34),
    ('SUM([2]!Undeclared)', 35),
    ('IF(TRUE,[2]!Undeclared)', 36),
    ('[2]!Undeclared', None),
    ('[2]!Undeclared', '"kept"'),
    ('[2]!Undeclared(1)', 37),
    ('SUMIF(A1:A3,">1",[2]Data!A1:A3)', 38),
    ('SUMIF(A1:A3,A2&"",[2]Data!A1:A3)', 39),
    ('SUMIF(A1:A3,2,[2]Data!A1:A3)/2', 40),
    ('COUNTIF([2]Data!A1:A3,">1")', 41),
    ('SUMIF(A1:A3,">99",[1]Prices!A1)', 71),
    ('SUMIF(A1:A3,">99",[1]Prices!A1:A3)', 72),
    ('SUMIF(A1:A3,#REF!,[1]Prices!A1)', 73),
    ('SUMIF(A1:A3,A1&#REF!,[1]Prices!A1)', 74),
    ('SUMIF(E1:E3,#REF!,[1]Prices!A1)', 75),
    ('SUMIF(E1:E3,A1&#REF!&#REF!,[1]Prices!A1)', 76),
    ('SUMIF(E1:E3,A1&#REF!&#REF!,F1:F3)', 77),
    ('SUMIF(E1:E3,#REF!,F1:F3)', 78),
    ('SUMIF(E1:E3,A1&#REF!&#REF!,[1]Prices!A1)/2', 79),
    ('SUMIF([1]Prices!A1:A3,">99")', 80),
    ('SUMIF([1]Prices!A1:A3,#REF!)', 81),
    ('COUNTIF([1]Prices!A1:A3,">99")', 82),
    ('SUMIFS([1]Prices!A1:A3,A1:A3,">99")', 83),
    ('AVERAGEIF(A1:A3,">99",[1]Prices!A1)', 84),
    ('SUMIF(Away,">3",[1]Prices!A1)', 91),
    ('SUMIF(Away,">3",[1]Prices!A1:A3)', 92),
    ('SUMIF(Away,">3")', 93),
    ('SUMIF(Away,">3",Away)', 94),
    ('SUMIF(Away,">99",[1]Prices!A1)', 95),
    ('SUMIF(Away,A1&#REF!&#REF!,[1]Prices!A1)', 96),
    ('SUMIF(Away,">3",F1:F3)', 97),
    ('SUMIF(A1:A3,">1",Away)', 98),
    ('COUNTIF(Away,">3")', 99),
    ('COUNTIFS(Away,">3",Away,">8")', 100),
    ('SUMIFS(Away,Away,">3")', 101),
    ('SUMIFS(F1:F3,Away,">3")', 102),
    ('AVERAGEIF(Away,">3")', 103),
    ('AVERAGEIFS(Away,Away,">3")', 104),
    ('OFFSET(Away,1,0)', 105),
    ('SUM(OFFSET(Away,0,0,2,1))', 106),
    ('SUM(Away)', 107),
    ('INDEX(Away,2)', 108),
    ('SUMIF((Away),">3",[1]Prices!A1)', 109),
    ('SUMIF(Local,">1",[1]Prices!A1)', 42),
    ('SUMIF(Local,">1",[1]Prices!A1:A3)', 43),
    ('SUMIF(Local,A2&"",[2]Data!A1)/2', 44),
    ('[3]!Number', 51),
    ('1+[3]!Number', 52),
    ('SUM([3]!Number)', 53),
    ('[3]!Text', 54),
    ('[3]!Yes', 55),
    ('[3]!Failed', 56),
    ('[3]!Empty', 57),
    ("[3]!'A b'", 59),
    ('[3]!Grid', 60),
    ('SUM([3]!Grid)', 61),
    ('INDEX([3]!Grid,2,1)', 62),
    ('ROWS([3]!Grid)', 63),
    ('[3]!Undeclared', 64),
    ('1+[3]!Undeclared', 65),
    ('ISNUMBER([3]!Number)', 66),
    ('SUMIF([3]!Grid,">1")', 67),
]

LINK = f'''<externalLink xmlns="{MAIN}" xmlns:r="{REL}"><externalBook r:id="rId1">
<sheetNames><sheetName val="Prices"/><sheetName val="Other"/></sheetNames>
<definedNames><definedName name="Gone" refersTo="#REF!"/><definedName name="NoFormula"/><definedName name="Rate" refersTo="Prices!$A$1"/><definedName name="Table" refersTo="Prices!$A$1:$A$3"/></definedNames>
<sheetDataSet>
<sheetData sheetId="0">
<row r="1"><cell r="A1"><v>2.5</v></cell><cell r="B1" t="str"><v>first</v></cell></row>
<row r="2"><cell r="A2"><v>7</v></cell><cell r="B2" t="b"><v>1</v></cell></row>
<row r="3"><cell r="A3"><v>9</v></cell><cell r="B3" t="e"><v>#N/A</v></cell></row>
</sheetData>
<sheetData sheetId="1" refreshError="1"/>
</sheetDataSet></externalBook></externalLink>'''


def sheet():
    rows = ['<row r="1"><c r="A1"><v>1</v></c><c r="E1" t="e"><v>#REF!</v></c><c r="F1"><v>100</v></c></row>',
            '<row r="2"><c r="A2"><v>2</v></c><c r="E2"><v>5</v></c><c r="F2"><v>200</v></c></row>',
            '<row r="3"><c r="A3"><v>3</v></c><c r="E3" t="e"><v>#REF!</v></c><c r="F3"><v>400</v></c></row>']
    for i, (formula, saved) in enumerate(FORMULAS):
        r = i + 5
        text = isinstance(saved, str)
        value = f'<v>{saved.strip(chr(34)) if text else saved}</v>' if saved is not None else ''
        kind = ' t="str"' if text else ''
        rows.append(f'<row r="{r}"><c r="D{r}"{kind}><f>{escape(formula)}</f>{value}</c></row>')
    return f'<worksheet xmlns="{MAIN}"><sheetData>{"".join(rows)}</sheetData></worksheet>'


LINK3 = f'''<externalLink xmlns="{MAIN}"><ddeLink ddeService="EgapNoSuchServer" ddeTopic="NoTopic"><ddeItems>
<ddeItem name="Number"><values><value><val>5.5</val></value></values></ddeItem>
<ddeItem name="Text"><values><value t="str"><val>kept</val></value></values></ddeItem>
<ddeItem name="Yes"><values><value t="b"><val>1</val></value></values></ddeItem>
<ddeItem name="Failed"><values><value t="e"><val>#N/A</val></value></values></ddeItem>
<ddeItem name="Empty"/>
<ddeItem name="A b"><values><value><val>6.5</val></value></values></ddeItem>
<ddeItem name="Grid"><values rows="2" cols="2"><value><val>1</val></value><value><val>2</val></value><value><val>3</val></value><value><val>4</val></value></values></ddeItem>
</ddeItems></ddeLink></externalLink>'''

LINK2 = f'''<externalLink xmlns="{MAIN}" xmlns:r="{REL}"><externalBook r:id="rId1">
<sheetNames><sheetName val="Data"/></sheetNames>
<sheetDataSet><sheetData sheetId="0"><row r="1"><cell r="A1"><v>10</v></cell></row><row r="2"><cell r="A2"><v>20</v></cell></row><row r="3"><cell r="A3"><v>30</v></cell></row></sheetData></sheetDataSet>
</externalBook></externalLink>'''

PARTS = {
    '[Content_Types].xml': f'<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="{ML}sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="{ML}worksheet+xml"/><Override PartName="/xl/externalLinks/externalLink1.xml" ContentType="{ML}externalLink+xml"/><Override PartName="/xl/externalLinks/externalLink2.xml" ContentType="{ML}externalLink+xml"/><Override PartName="/xl/externalLinks/externalLink3.xml" ContentType="{ML}externalLink+xml"/></Types>',
    '_rels/.rels': f'<Relationships xmlns="{PKG}"><Relationship Id="rId1" Type="{REL}/officeDocument" Target="xl/workbook.xml"/></Relationships>',
    'xl/_rels/workbook.xml.rels': f'<Relationships xmlns="{PKG}"><Relationship Id="rId1" Type="{REL}/worksheet" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="{REL}/externalLink" Target="externalLinks/externalLink1.xml"/><Relationship Id="rId3" Type="{REL}/externalLink" Target="externalLinks/externalLink2.xml"/><Relationship Id="rId4" Type="{REL}/externalLink" Target="externalLinks/externalLink3.xml"/></Relationships>',
    'xl/workbook.xml': f'<workbook xmlns="{MAIN}" xmlns:r="{REL}"><sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets><externalReferences><externalReference r:id="rId2"/><externalReference r:id="rId3"/><externalReference r:id="rId4"/></externalReferences><definedNames><definedName name="Away">[1]Prices!$A$1:$A$3</definedName><definedName name="Local">Sheet1!$A$1:$A$3</definedName></definedNames></workbook>',
    'xl/worksheets/sheet1.xml': sheet(),
    'xl/externalLinks/externalLink1.xml': LINK,
    'xl/externalLinks/externalLink2.xml': LINK2,
    'xl/externalLinks/externalLink3.xml': LINK3,
    'xl/externalLinks/_rels/externalLink2.xml.rels': f'<Relationships xmlns="{PKG}"><Relationship Id="rId1" Type="{REL}/externalLinkPath" Target="egap-no-such-book-2.xls" TargetMode="External"/></Relationships>',
    'xl/externalLinks/_rels/externalLink1.xml.rels': f'<Relationships xmlns="{PKG}"><Relationship Id="rId1" Type="{REL}/externalLinkPath" Target="egap-no-such-book.xlsx" TargetMode="External"/></Relationships>',
}

path = pathlib.Path(__file__).parent.parent / 'tests/Egap.Spreadsheets.Tests/Oracle/links.xlsx'
with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as package:
    for name, text in PARTS.items():
        package.writestr(zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)), text)
print(path, len(FORMULAS), 'formulas')
