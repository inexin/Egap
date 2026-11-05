#!/usr/bin/env python3
"""Runs the function oracle cases through LibreOffice Calc, headless, as a second opinion beside Excel.

Reads the same cases file as tools/excel-function-oracle.ps1 (tests/Egap.Spreadsheets.Tests/Oracle/functions.txt), writes
them into a workbook of its own making (inputs in columns A to J, each formula in column L at row 20 * i + 1, no cached
values), has LibreOffice open and recalculate it and save it again, and reads back what it calculated. The output is a
tab-separated file in the Excel script's form: a header naming the LibreOffice version, then index, kind, value, formula.

With --compare it also lists the cases where LibreOffice and Excel's file disagree, which says where Excel's choices are
its own rather than the function's definition.

Usage:
  python tools/libreoffice-function-oracle.py <cases.txt> <out.tsv> [--excel functions.excel.tsv] [--soffice path]
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile
import zipfile
from xml.sax.saxutils import escape
import xml.etree.ElementTree as ET

MAIN = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'

# Functions Excel stores with the _xlfn. prefix, because they came after Excel 2007.
NEWER = ['AGGREGATE', 'ACOT', 'ACOTH', 'ARABIC', 'BASE', 'CEILING.MATH', 'CEILING.PRECISE', 'COMBINA', 'CONCAT', 'COT',
         'COTH', 'COVARIANCE.P', 'COVARIANCE.S', 'CSC', 'CSCH', 'DAYS', 'DECIMAL', 'FLOOR.MATH', 'FLOOR.PRECISE',
         'FORECAST.LINEAR', 'FORMULATEXT', 'IFNA', 'IFS', 'ISFORMULA', 'ISO.CEILING', 'ISOWEEKNUM', 'MAXIFS', 'MINIFS',
         'MODE.SNGL', 'NETWORKDAYS.INTL', 'NUMBERVALUE', 'PERCENTILE.EXC', 'PERCENTILE.INC', 'QUARTILE.EXC',
         'QUARTILE.INC', 'RANK.AVG', 'RANK.EQ', 'SEC', 'SECH', 'SHEET', 'SHEETS', 'STDEV.P', 'STDEV.S', 'SWITCH',
         'TEXTJOIN', 'UNICHAR', 'UNICODE', 'VAR.P', 'VAR.S', 'WORKDAY.INTL', 'XOR']
NEWER_PATTERN = re.compile(r'(?<![\w.])(' + '|'.join(re.escape(n) for n in sorted(NEWER, key=len, reverse=True)) + r')\(',
                           re.IGNORECASE)


def read_cases(path):
    inputs, formulas = [], []
    with open(path, encoding='utf-8') as f:
        for line in f:
            text = line.strip()
            if not text or text.startswith('#'):
                continue
            if text.startswith('@'):
                cell, value = text[1:].split(' ', 1)
                inputs.append((cell, value))
            else:
                formulas.append(text)
    return inputs, formulas


def stored(formula):
    """The formula as a file stores it: newer functions with _xlfn., outside string literals."""
    parts = formula.split('"')
    for i in range(0, len(parts), 2):
        parts[i] = NEWER_PATTERN.sub(lambda m: '_xlfn.' + m.group(1).upper() + '(', parts[i])
    return '"'.join(parts)


def row_of(cell):
    return int(re.match(r'[A-Z]+(\d+)', cell).group(1))


def write_workbook(path, inputs, formulas):
    rows = {}
    for cell, value in inputs:
        if value.startswith('"'):
            xml = f'<c r="{cell}" t="inlineStr"><is><t xml:space="preserve">{escape(value[1:-1])}</t></is></c>'
        elif value in ('TRUE', 'FALSE'):
            xml = f'<c r="{cell}" t="b"><v>{1 if value == "TRUE" else 0}</v></c>'
        elif value.startswith('#'):
            xml = f'<c r="{cell}" t="e"><v>{escape(value)}</v></c>'
        else:
            xml = f'<c r="{cell}"><v>{value}</v></c>'
        rows.setdefault(row_of(cell), []).append((cell, xml))
    for i, formula in enumerate(formulas):
        cell = f'L{20 * i + 1}'
        rows.setdefault(row_of(cell), []).append((cell, f'<c r="{cell}"><f>{escape(stored(formula))}</f></c>'))

    def column_key(cell):
        letters = re.match(r'[A-Z]+', cell).group(0)
        return (len(letters), letters)

    sheet_rows = ''.join(
        f'<row r="{r}">' + ''.join(xml for _, xml in sorted(cells, key=lambda c: column_key(c[0]))) + '</row>'
        for r, cells in sorted(rows.items()))
    files = {
        '[Content_Types].xml':
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
            '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
            '<Default Extension="xml" ContentType="application/xml"/>'
            '<Override PartName="/xl/workbook.xml" '
            'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
            '<Override PartName="/xl/worksheets/sheet1.xml" '
            'ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>'
            '</Types>',
        '_rels/.rels':
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" '
            'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" '
            'Target="xl/workbook.xml"/></Relationships>',
        'xl/workbook.xml':
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
            'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
            '<sheets><sheet name="Sheet1" sheetId="1" r:id="rId1"/></sheets>'
            '<calcPr fullCalcOnLoad="1"/></workbook>',
        'xl/_rels/workbook.xml.rels':
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
            '<Relationship Id="rId1" '
            'Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" '
            'Target="worksheets/sheet1.xml"/></Relationships>',
        'xl/worksheets/sheet1.xml':
            '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>'
            '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
            f'<sheetData>{sheet_rows}</sheetData></worksheet>',
    }
    with zipfile.ZipFile(path, 'w', zipfile.ZIP_DEFLATED) as z:
        for name, content in files.items():
            z.writestr(name, content)


def read_results(path, count):
    with zipfile.ZipFile(path) as z:
        shared = []
        if 'xl/sharedStrings.xml' in z.namelist():
            for si in ET.fromstring(z.read('xl/sharedStrings.xml')).iter(MAIN + 'si'):
                shared.append(''.join(t.text or '' for t in si.iter(MAIN + 't')))
        sheet = ET.fromstring(z.read('xl/worksheets/sheet1.xml'))
    cells = {c.get('r'): c for c in sheet.iter(MAIN + 'c')}
    results = []
    for i in range(count):
        c = cells.get(f'L{20 * i + 1}')
        v = c.find(MAIN + 'v') if c is not None else None
        if c is None or v is None or v.text is None:
            results.append(('blank', ''))
            continue
        kind = c.get('t', 'n')
        if kind == 'b':
            results.append(('boolean', 'TRUE' if v.text == '1' else 'FALSE'))
        elif kind == 'e':
            results.append(('error', v.text))
        elif kind == 's':
            results.append(('text', shared[int(v.text)]))
        elif kind == 'str':
            results.append(('text', v.text))
        else:
            results.append(('number', repr(float(v.text))))
    return results


def soffice_version(soffice):
    # On Windows soffice.exe prints nothing; soffice.com beside it is the console form.
    console = os.path.splitext(soffice)[0] + '.com'
    if os.path.exists(console):
        soffice = console
    try:
        out = subprocess.run([soffice, '--version'], capture_output=True, text=True, timeout=120).stdout.strip()
        return out or 'unknown version'
    except (OSError, subprocess.SubprocessError):
        return 'unknown version'


def escape_text(text):
    return text.replace('\\', '\\\\').replace('\t', '\\t').replace('\r', '\\r').replace('\n', '\\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('cases')
    parser.add_argument('output')
    parser.add_argument('--excel', help="Excel's results, to list where the two disagree")
    parser.add_argument('--soffice', default=shutil.which('soffice') or r'C:\Program Files\LibreOffice\program\soffice.exe')
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    inputs, formulas = read_cases(args.cases)
    with tempfile.TemporaryDirectory() as work:
        source = os.path.join(work, 'cases.xlsx')
        outdir = os.path.join(work, 'out')
        os.makedirs(outdir)
        write_workbook(source, inputs, formulas)
        # A profile of its own, so a LibreOffice the user has open is left alone.
        profile = 'file:///' + os.path.join(work, 'profile').replace('\\', '/')
        subprocess.run([args.soffice, f'-env:UserInstallation={profile}', '--headless', '--calc', '--convert-to',
                        'xlsx:Calc MS Excel 2007 XML', '--outdir', outdir, source],
                       check=True, capture_output=True, timeout=600)
        results = read_results(os.path.join(outdir, 'cases.xlsx'), len(formulas))

    version = soffice_version(args.soffice)
    with open(args.output, 'w', encoding='utf-8', newline='\n') as f:
        f.write(f'# {version}, written by tools/libreoffice-function-oracle.py\n')
        for i, (formula, (kind, value)) in enumerate(zip(formulas, results)):
            f.write(f'{i}\t{kind}\t{escape_text(value)}\t{formula}\n')

    if args.excel:
        excel = {}
        with open(args.excel, encoding='utf-8') as f:
            for line in f:
                if line.startswith('#'):
                    continue
                fields = line.rstrip('\n').split('\t')
                excel[fields[3]] = (fields[1], fields[2])
        differ = 0
        for formula, (kind, value) in zip(formulas, results):
            expected = excel.get(formula)
            if expected is None or expected[0] == 'invalid':
                continue
            same = expected[0] == kind and (
                expected[1] == escape_text(value) if kind != 'number'
                else abs(float(expected[1]) - float(value)) <= 1e-14 * max(abs(float(expected[1])), abs(float(value))))
            if not same:
                differ += 1
                print(f'{formula}: Excel {expected[0]} {expected[1]}, LibreOffice {kind} {escape_text(value)}')
        print(f'{differ} of {len(formulas)} cases differ from Excel')


if __name__ == '__main__':
    sys.exit(main())
