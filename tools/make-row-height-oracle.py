"""Writes one workbook per normal font, with rows of known heights, for tools/excel-row-height-oracle.ps1.

Excel prints a row by the normal font: a row's pixels count in the font's printed row height over its row height on screen,
so a 15 point row is taller on paper in one font than in another. The workbooks are hand-written from ECMA-376, and each
sets its normal font and nothing else that could change it (no theme, no font scheme). Rows 1 to 3 set no height and
print the font's default height; rows 4 on set the heights in HEIGHTS.

Run it as:
  python tools/make-row-height-oracle.py <folder>
"""
import pathlib
import sys
import zipfile

MAIN = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
REL = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships'
PKG = 'http://schemas.openxmlformats.org/package/2006/relationships'
ML = 'application/vnd.openxmlformats-officedocument.spreadsheetml.'

COMMON = [8, 9, 10, 11, 12, 14]
MORE = [7, 16, 18, 20]
FONTS = {
    'Arial': (2, COMMON + MORE),
    'Calibri': (2, COMMON + MORE),
    'Times New Roman': (1, COMMON + MORE),
    'Aptos Narrow': (2, COMMON),
    'Cambria': (1, COMMON),
    'Courier New': (3, COMMON),
    'Courier': (3, [10, 12]),
    'Verdana': (2, COMMON),
    'Tahoma': (2, COMMON),
    'Segoe UI': (2, COMMON),
    'Arial Narrow': (2, [7, 8, 9, 10, 11, 12]),
    'Book Antiqua': (1, [10, 11, 12]),
    'Garamond': (1, [10, 11, 12]),
    'Century Gothic': (2, [10, 11, 12]),
    'Comic Sans MS': (2, [10, 11, 12]),
    'MS Sans Serif': (2, [8, 10, 12]),
    'Geneva': (2, [9, 10, 12]),
    'Helvetica': (2, [9, 10, 12]),
}
HEIGHTS = [30, 33.2, 45, 60, 90]


def part(font, family, size):
    cells = '<c r="A{0}" t="inlineStr"><is><t>x</t></is></c>'
    rows = ''.join(f'<row r="{r}">{cells.format(r)}</row>' for r in range(1, 4))
    for i, height in enumerate(HEIGHTS):
        r = i + 4
        rows += f'<row r="{r}" ht="{height}" customHeight="1">{cells.format(r)}</row>'
    sheet = (f'<worksheet xmlns="{MAIN}"><sheetFormatPr defaultRowHeight="15"/><sheetData>{rows}</sheetData>'
             '<pageMargins left="0.5" right="0.5" top="0.5" bottom="0.5" header="0" footer="0"/></worksheet>')
    styles = (f'<styleSheet xmlns="{MAIN}"><fonts count="1"><font><sz val="{size}"/><name val="{font}"/><family val="{family}"/></font></fonts>'
              '<fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>'
              '<borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="1"><xf/></cellXfs></styleSheet>')
    return {
        '[Content_Types].xml': ('<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                                '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                                '<Default Extension="xml" ContentType="application/xml"/>'
                                f'<Override PartName="/xl/workbook.xml" ContentType="{ML}sheet.main+xml"/>'
                                f'<Override PartName="/xl/worksheets/sheet1.xml" ContentType="{ML}worksheet+xml"/>'
                                f'<Override PartName="/xl/styles.xml" ContentType="{ML}styles+xml"/></Types>'),
        '_rels/.rels': f'<Relationships xmlns="{PKG}"><Relationship Id="rId1" Type="{REL}/officeDocument" Target="xl/workbook.xml"/></Relationships>',
        'xl/_rels/workbook.xml.rels': (f'<Relationships xmlns="{PKG}"><Relationship Id="rId1" Type="{REL}/worksheet" Target="worksheets/sheet1.xml"/>'
                                       f'<Relationship Id="rId2" Type="{REL}/styles" Target="styles.xml"/></Relationships>'),
        'xl/workbook.xml': f'<workbook xmlns="{MAIN}" xmlns:r="{REL}"><sheets><sheet name="S" sheetId="1" r:id="rId1"/></sheets></workbook>',
        'xl/worksheets/sheet1.xml': sheet,
        'xl/styles.xml': styles,
    }


def main():
    folder = pathlib.Path(sys.argv[1])
    folder.mkdir(parents=True, exist_ok=True)
    for font, (family, sizes) in FONTS.items():
        for size in sizes:
            with zipfile.ZipFile(folder / f'{font}-{size}.xlsx', 'w', zipfile.ZIP_DEFLATED) as package:
                for name, text in part(font, family, size).items():
                    package.writestr(name, text)


if __name__ == '__main__':
    main()
