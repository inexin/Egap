"""Writes tests/files/fields/docvariable.docx, the DOCVARIABLE field fixture, from scratch.

The settings part holds the document variables (w:docVars); each field's stored result is the value Word's documented
rules give, so the tests compare Egap with these until tools/update-field-fixtures.ps1 has saved Word's own results next
to it (docvariable.word.docx, and docvariable.word.doc and docvariable.word.rtf with the variables as Word writes them).

    python tools/make-docvariable-fixture.py
"""

import os
import zipfile
from xml.sax.saxutils import escape, quoteattr

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "tests", "files", "fields")

W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
MISSING = "Error! No document variable supplied."

VARIABLES = [("Customer", "Acme AS"), ("Amount", "12.5"), ("Owner", "Ærlig Åse"), ("Spaced", "  two  spaces  ")]

# Each line: a label, the field code and the result Word's rules give.
LINES = [
    ("Value", " DOCVARIABLE Customer ", "Acme AS"),
    ("Other case", " DOCVARIABLE customer ", "Acme AS"),
    ("Quoted", ' DOCVARIABLE "Customer" ', "Acme AS"),
    ("Upper", r" DOCVARIABLE Customer \* Upper ", "ACME AS"),
    ("Lower", r" DOCVARIABLE Customer \* Lower ", "acme as"),
    ("Merge format", r" DOCVARIABLE Customer \* MERGEFORMAT ", "Acme AS"),
    ("Picture", r' DOCVARIABLE Amount \# "0.00" ', "12.50"),
    ("Letters", " DOCVARIABLE Owner ", "Ærlig Åse"),
    ("Spaces", " DOCVARIABLE Spaced ", "  two  spaces  "),
    ("Missing", " DOCVARIABLE Missing ", MISSING),
    ("Missing upper", r" DOCVARIABLE Missing \* Upper ", MISSING),
    ("No name", " DOCVARIABLE ", "Error! Document Variable not defined."),
]

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


def run(text):
    return f'<w:r><w:t xml:space="preserve">{escape(text)}</w:t></w:r>'


def field(code, result):
    return ('<w:r><w:fldChar w:fldCharType="begin"/></w:r>'
            f'<w:r><w:instrText xml:space="preserve">{escape(code)}</w:instrText></w:r>'
            '<w:r><w:fldChar w:fldCharType="separate"/></w:r>' + run(result) + '<w:r><w:fldChar w:fldCharType="end"/></w:r>')


def main():
    body = "".join(f"<w:p>{run(label + ': ')}{field(code, result)}</w:p>" for label, code, result in LINES)
    document = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<w:document xmlns:w="{W}"><w:body>{body}'
                '<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1134" w:right="1134" w:bottom="1134" w:left="1134" '
                'w:header="567" w:footer="567" w:gutter="0"/></w:sectPr></w:body></w:document>')
    variables = "".join(f"<w:docVar w:name={quoteattr(name)} w:val={quoteattr(value)}/>" for name, value in VARIABLES)
    settings = (f'<?xml version="1.0" encoding="UTF-8" standalone="yes"?>\n<w:settings xmlns:w="{W}">'
                f'<w:docVars>{variables}</w:docVars><w:decimalSymbol w:val="."/><w:listSeparator w:val=","/></w:settings>')
    path = os.path.join(ROOT, "docvariable.docx")
    os.makedirs(ROOT, exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as package:
        for entry, data in (("[Content_Types].xml", CONTENT_TYPES), ("_rels/.rels", RELS), ("word/_rels/document.xml.rels", DOCUMENT_RELS),
                            ("word/document.xml", document), ("word/settings.xml", settings)):
            info = zipfile.ZipInfo(entry, date_time=(2026, 11, 28, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(info, data)
    print(path)


if __name__ == "__main__":
    main()
