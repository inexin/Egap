"""Makes an XLS workbook encrypted with the Office 97/2000 compatible RC4 encryption, which Excel no longer writes, with
LibreOffice Calc.

Run it with LibreOffice's own Python, which has the uno module, as one plain command:

  "C:/Program Files/LibreOffice/program/python.exe" tools/libreoffice-encrypted-oracle.py <folder>

It starts LibreOffice headless on a named pipe, makes the same small workbook as tools/excel-encrypted-oracle.ps1 (two
sheets, text, numbers, a date, a formula, 400 filler rows), stores it as rc4.xls with the password "Egap pässword 1",
asks LibreOffice to quit and writes the LibreOffice version to rc4.txt. Open rc4.xls in Excel afterwards with the Excel
script's Open mode to check that Excel reads it.
"""

import os
import subprocess
import sys
import time
import uuid

import uno
from com.sun.star.beans import PropertyValue

PASSWORD = 'Egap p\u00e4ssword 1'


def prop(name, value):
    p = PropertyValue()
    p.Name = name
    p.Value = value
    return p


def main():
    folder = os.path.abspath(sys.argv[1])
    os.makedirs(folder, exist_ok=True)
    soffice = os.path.join(os.path.dirname(sys.executable), 'soffice.exe')
    pipe = 'egap' + uuid.uuid4().hex
    profile = uno.systemPathToFileUrl(os.path.join(folder, '.lo-profile'))
    office = subprocess.Popen([soffice, '--headless', '--invisible', '--norestore', '-env:UserInstallation=' + profile,
                               f'--accept=pipe,name={pipe};urp;'])
    local = uno.getComponentContext()
    resolver = local.ServiceManager.createInstanceWithContext('com.sun.star.bridge.UnoUrlResolver', local)
    context = None
    for _ in range(60):
        try:
            context = resolver.resolve(f'uno:pipe,name={pipe};urp;StarOffice.ComponentContext')
            break
        except Exception:
            time.sleep(1)
    if context is None:
        raise SystemExit('LibreOffice did not start')
    desktop = context.ServiceManager.createInstanceWithContext('com.sun.star.frame.Desktop', context)
    try:
        doc = desktop.loadComponentFromURL('private:factory/scalc', '_blank', 0, (prop('Hidden', True),))
        sheets = doc.Sheets
        data = sheets.getByIndex(0)
        sheets.insertNewByName('Second', 1)
        data.setName('Data')
        second = sheets.getByName('Second')
        cell = data.getCellByPosition
        cell(0, 0).setString('Name')
        cell(1, 0).setString('Amount')
        cell(2, 0).setString('Date')
        cell(0, 1).setString('Alpha')
        cell(1, 1).setValue(12.5)
        cell(2, 1).setValue(45000)
        formats = doc.NumberFormats
        locale = uno.createUnoStruct('com.sun.star.lang.Locale')
        key = formats.queryKey('YYYY-MM-DD', locale, False)
        if key == -1:
            key = formats.addNew('YYYY-MM-DD', locale)
        cell(2, 1).NumberFormat = key
        cell(0, 2).setString('Beta \u00e6\u00f8\u00e5')
        cell(1, 2).setValue(-3)
        cell(0, 3).setString('Total')
        cell(1, 3).setFormula('=SUM(B2:B3)')
        second.getCellByPosition(0, 0).setString('On the second sheet')
        for r in range(2, 401):
            second.getCellByPosition(0, r - 1).setString(f'Row {r} of filler text')
            second.getCellByPosition(1, r - 1).setValue(r * 1.5)
        target = uno.systemPathToFileUrl(os.path.join(folder, 'rc4.xls'))
        doc.storeToURL(target, (prop('FilterName', 'MS Excel 97'), prop('Password', PASSWORD), prop('Overwrite', True)))
        doc.close(True)
        version = context.ServiceManager.createInstanceWithContext('com.sun.star.configuration.ConfigurationProvider', context) \
            .createInstanceWithArguments('com.sun.star.configuration.ConfigurationAccess',
                                         (prop('nodepath', '/org.openoffice.Setup/Product'),)).getByName('ooSetupVersionAboutBox')
        with open(os.path.join(folder, 'rc4.txt'), 'w', encoding='utf-8') as f:
            f.write(f'# LibreOffice {version}; rc4.xls, password: Egap p\\u00e4ssword 1\n')
    finally:
        try:
            desktop.terminate()
        except Exception:
            pass
        office.wait(timeout=60)


if __name__ == '__main__':
    main()
