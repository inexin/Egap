namespace Egap.Pdf.Signatures;

/// <summary>
/// Finds or makes the signature field a signature goes into (ISO 32000-2, 12.7.5.5): an existing unsigned signature field
/// of that name, or a new invisible one, its own widget on the first page, added to the form; the form's
/// <c>/SigFlags</c> get SignaturesExist and AppendOnly (12.7.3).
/// </summary>
internal static class SignatureFields
{
    /// <summary>Puts the signature dictionary into the field <paramref name="fieldName"/>, made when missing, and returns its dictionary.</summary>
    /// <exception cref="ArgumentException">A field of that name exists but is not a signature field, or is signed already.</exception>
    public static PdfDictionary Attach(PdfDocument document, string fieldName, PdfReference signature)
    {
        PdfDictionary form = Form(document);
        PdfDictionary field;
        if (document.Form.TryGetField(fieldName, out PdfField existing))
        {
            if (existing is not PdfSignatureField signatureField)
            {
                throw new ArgumentException($"The field '{fieldName}' is not a signature field.", nameof(fieldName));
            }

            if (signatureField.IsSigned)
            {
                throw new ArgumentException($"The field '{fieldName}' is signed already; sign into a new field.", nameof(fieldName));
            }

            field = signatureField.Dictionary;
        }
        else
        {
            if (fieldName.Contains('.', StringComparison.Ordinal))
            {
                throw new ArgumentException("A new signature field's name cannot contain a period, which separates the names of nested fields.", nameof(fieldName));
            }

            if (document.Pages.Count == 0)
            {
                throw new InvalidOperationException("A document without pages cannot hold a signature field.");
            }

            PdfPage page = document.Pages[0];
            field = new PdfDictionary
            {
                ["FT"] = PdfName.Get("Sig"),
                ["T"] = PdfString.FromText(fieldName),
                ["Type"] = PdfName.Get("Annot"),
                ["Subtype"] = PdfName.Get("Widget"),
                ["Rect"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0) },

                // Print and Locked (12.5.3): an invisible signature still prints nothing, and no one moves it.
                ["F"] = new PdfInteger(132),
            };
            if (page.Reference is { } pageReference)
            {
                field["P"] = pageReference;
            }

            PdfReference reference = document.Objects.Add(field);
            field = (PdfDictionary)document.Objects[reference];
            if (page.Dictionary.Get<PdfArray>("Annots") is PdfArray annotations)
            {
                annotations.Add(reference);
            }
            else
            {
                page.Dictionary["Annots"] = new PdfArray { reference };
            }

            if (form.Get<PdfArray>("Fields") is PdfArray fields)
            {
                fields.Add(reference);
            }
            else
            {
                form["Fields"] = new PdfArray { reference };
            }
        }

        field["V"] = signature;
        form["SigFlags"] = new PdfInteger((form.GetInt("SigFlags") ?? 0) | 3);
        return field;
    }

    /// <summary>The catalog's interactive form, made as an indirect dictionary when missing.</summary>
    private static PdfDictionary Form(PdfDocument document)
    {
        if (document.Catalog.Get<PdfDictionary>("AcroForm") is PdfDictionary form)
        {
            return form;
        }

        document.Catalog["AcroForm"] = document.Objects.Add(new PdfDictionary { ["Fields"] = new PdfArray() });
        return document.Catalog.Get<PdfDictionary>("AcroForm")!;
    }
}
