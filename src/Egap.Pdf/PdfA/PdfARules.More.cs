namespace Egap.Pdf.PdfA;

/// <summary>The rules over signatures, every object and every content stream.</summary>
internal sealed partial class PdfARules
{
    private const int MaxDepth = 8;

    /// <summary>
    /// Signatures: conversion writes the file whole, which breaks every signature, and PDF/A asks a signature to cover the
    /// whole file (ISO 19005-2 6.4.3). Their values are removed with the report saying so, leaving empty signature fields.
    /// </summary>
    private void Signatures()
    {
        if (document.WriterConformance == conformance || !document.Form.Exists)
        {
            return;
        }

        List<PdfDictionary> signed = [.. document.Form.GetAllFields().Select(f => f.Dictionary)
            .Where(d => (d.GetName("FT")?.Value ?? d.Get<PdfDictionary>("Parent")?.GetName("FT")?.Value) == "Sig" && d.Get("V") is not null)];
        if (signed.Count > 0)
        {
            Issue("6.4.3", $"the file has {signed.Count} signatures, which writing it again would break; they are removed and their fields left empty", () =>
            {
                foreach (PdfDictionary field in signed)
                {
                    field.Remove("V");
                    field.Remove("Lock");
                    field.Remove("SV");
                }

                document.Catalog.Get<PdfDictionary>("Perms")?.Remove("DocMDP");
            }, lossy: true);
        }
    }

    /// <summary>Whether a dictionary is an additional-actions dictionary: no other type, and every entry an action.</summary>
    private static bool IsAdditionalActions(PdfDictionary triggers)
    {
        return triggers.GetName("Type") is null && triggers.Entries.All(e => e.Value.Resolve() is PdfDictionary action && action.GetName("Type")?.Value is null or "Action" && action.ContainsKey("S"));
    }

    /// <summary>
    /// Rules over every object: actions and additional actions wherever they sit (form fields too), file specifications
    /// that name an embedded file both ways (/F and /UF), filters spelled as inline image abbreviations, page sizes.
    /// </summary>
    private void Objects()
    {
        List<(PdfDictionary Owner, string Key)> actions = [];
        List<PdfDictionary> additional = [];
        List<PdfDictionary> specs = [];
        List<PdfStream> abbreviated = [];
        HashSet<PdfObject> seen = new(ReferenceEqualityComparer.Instance);
        foreach (PdfReference reference in document.Objects)
        {
            Visit(document.Objects[reference], 0);
        }

        void Visit(PdfObject? value, int depth)
        {
            if (value is null || depth > MaxDepth || !seen.Add(value))
            {
                return;
            }

            PdfDictionary? dictionary = value switch
            {
                PdfStream stream => stream.Dictionary,
                PdfDictionary d => d,
                _ => null,
            };
            if (value is PdfStream s && s.Filters.Any(f => f.Value is "A85" or "AHx" or "LZW" or "Fl" or "RL" or "CCF" or "DCT"))
            {
                abbreviated.Add(s);
            }

            if (dictionary is not null)
            {
                // Pages, annotations and outline items are judged where they are read; this finds the rest.
                bool covered = dictionary.GetName("Type")?.Value is "Annot" or "Page" or "Catalog" || dictionary.ContainsKey("Title");
                // A resource named AA or A (a font, say) is not an action: only a dictionary of actions is.
                if (!covered && dictionary.Get("AA")?.Resolve() is PdfDictionary triggers && IsAdditionalActions(triggers))
                {
                    additional.Add(dictionary);
                }

                if (!covered && dictionary.Get("A")?.Resolve() is PdfDictionary action && action.ContainsKey("S") && action.GetName("Type")?.Value is null or "Action" && Forbidden(action) is not null)
                {
                    actions.Add((dictionary, "A"));
                }

                if (dictionary.Get<PdfDictionary>("EF") is not null && (dictionary.Get("F") is null) != (dictionary.Get("UF") is null))
                {
                    specs.Add(dictionary);
                }

                foreach ((PdfName _, PdfObject item) in dictionary.Entries)
                {
                    if (item is not PdfReference)
                    {
                        Visit(item, depth + 1);
                    }
                }
            }
            else if (value is PdfArray array)
            {
                foreach (PdfObject item in array)
                {
                    if (item is not PdfReference)
                    {
                        Visit(item, depth + 1);
                    }
                }
            }
        }

        if (additional.Count > 0)
        {
            Issue("6.6.2", $"{additional.Count} form fields or other objects have additional actions; they are removed", () => additional.ForEach(d => d.Remove("AA")), lossy: true);
        }

        if (actions.Count > 0)
        {
            Issue("6.6.1", $"{actions.Count} form fields or other objects run forbidden actions; the actions are removed", () => actions.ForEach(a => a.Owner.Remove(a.Key)), lossy: true);
        }

        if (specs.Count > 0)
        {
            Issue("6.8", $"{specs.Count} embedded file specifications give the file name one way only; both /F and /UF are written", () =>
            {
                foreach (PdfDictionary spec in specs)
                {
                    PdfObject name = spec.Get("UF") ?? spec.Get("F")!;
                    spec["F"] = spec.Get("F") ?? name;
                    spec["UF"] = spec.Get("UF") ?? name;
                }
            });
        }

        if (abbreviated.Count > 0)
        {
            Issue("6.1.7.2", $"{abbreviated.Count} streams name their filters by the inline image abbreviations; the full names are written", () =>
            {
                foreach (PdfStream stream in abbreviated)
                {
                    Unabbreviate(stream);
                }
            });
        }

        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfDictionary page = document.Pages[i].Dictionary;
            foreach (string box in (string[])["MediaBox", "CropBox", "BleedBox", "TrimBox", "ArtBox"])
            {
                if (Inherited(page, box) is PdfArray { Count: 4 } array && array.GetNumber(0) is double x0 && array.GetNumber(1) is double y0
                    && array.GetNumber(2) is double x1 && array.GetNumber(3) is double y1)
                {
                    double unit = page.GetNumber("UserUnit") ?? 1;
                    double width = Math.Abs(x1 - x0) * unit, height = Math.Abs(y1 - y0) * unit;
                    if (width < 3 || height < 3 || width > 14400 || height > 14400)
                    {
                        Issue("6.1.13", $"the {box} is {width:0.#} by {height:0.#} points, outside the 3 to 14400 PDF/A allows", page: i + 1);
                        break;
                    }
                }
            }
        }
    }

    private static PdfArray? Inherited(PdfDictionary page, string key)
    {
        PdfDictionary? node = page;
        for (int depth = 0; node is not null && depth < 64; depth++, node = node.Get<PdfDictionary>("Parent"))
        {
            if (node.Get<PdfArray>(key) is PdfArray found)
            {
                return found;
            }

            if (key is not ("MediaBox" or "CropBox"))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Writes a stream's abbreviated filter names in full; one without an image filter is simply compressed again.</summary>
    private static void Unabbreviate(PdfStream stream)
    {
        static string Full(string name) => name switch
        {
            "A85" => "ASCII85Decode",
            "AHx" => "ASCIIHexDecode",
            "LZW" => "LZWDecode",
            "Fl" => "FlateDecode",
            "RL" => "RunLengthDecode",
            "CCF" => "CCITTFaxDecode",
            "DCT" => "DCTDecode",
            _ => name,
        };

        switch (stream.Dictionary.Get("Filter")?.Resolve())
        {
            case PdfName name:
                stream.Dictionary["Filter"] = PdfName.Get(Full(name.Value));
                break;
            case PdfArray array:
                stream.Dictionary["Filter"] = new PdfArray(array.Select(f => f.Resolve() is PdfName n ? (PdfObject)PdfName.Get(Full(n.Value)) : f));
                break;
        }
    }

    /// <summary>
    /// Content streams (ISO 19005-2 6.2.2): operators ISO 32000-1 does not define, and references to XObjects, graphics
    /// states and shadings the resources lack, are removed, which viewers skip anyway; text in a font the resources lack is
    /// an unmet rule.
    /// </summary>
    private void ContentStreams(List<PdfStream> forms)
    {
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            if (page.Dictionary.Get("Contents") is not null && TryParse(page.GetContent) is PdfContent content)
            {
                Content(content, page.Resources, $"page {i + 1}", () => page.SetContent(content), i + 1);
            }
        }

        foreach (PdfStream form in forms)
        {
            if (TryParse(() => PdfContent.Parse(form.GetDecodedBytes())) is PdfContent content)
            {
                Content(content, form.Dictionary.Get<PdfDictionary>("Resources"), "a form XObject", () => form.SetDecodedBytes(content.ToBytes()), null);
            }
        }
    }

    private void Content(PdfContent content, PdfDictionary? resources, string where, Action write, int? page)
    {
        List<PdfOperation> unknown = [], dangling = [];
        HashSet<string> fonts = [];
        foreach (PdfOperation operation in content)
        {
            if (!operation.IsKnown && operation is not PdfInlineImage)
            {
                unknown.Add(operation);
                continue;
            }

            (string? category, int index) = operation.Operator switch
            {
                "Do" => ("XObject", 0),
                "gs" => ("ExtGState", 0),
                "sh" => ("Shading", 0),
                "Tf" => ("Font", 0),
                "cs" or "CS" when operation.GetName(0)?.Value is not ("DeviceGray" or "DeviceRGB" or "DeviceCMYK" or "Pattern") => ("ColorSpace", 0),
                "scn" or "SCN" when operation.Operands.Count > 0 && operation.Operands[^1] is PdfName => ("Pattern", operation.Operands.Count - 1),
                "BDC" or "DP" when operation.Operands.Count > 1 && operation.Operands[1] is PdfName => ("Properties", 1),
                _ => ((string?)null, 0),
            };
            if (category is not null && operation.GetName(index) is PdfName name && resources?.Get<PdfDictionary>(category)?.Get(name)?.Resolve() is null)
            {
                if (category is "Font" or "ColorSpace" or "Pattern" or "Properties")
                {
                    fonts.Add(name.Value);
                }
                else
                {
                    dangling.Add(operation);
                }
            }
        }

        if (fonts.Count > 0)
        {
            Issue("6.2.2", $"{where} uses fonts, colour spaces, patterns or properties its resources lack ({string.Join(", ", fonts)})", page: page);
        }

        if (unknown.Count > 0 || dangling.Count > 0)
        {
            string what = unknown.Count > 0 ? $"operators ISO 32000-1 does not define ({string.Join(", ", unknown.Select(o => o.Operator).Distinct().Take(5))})" : "";
            string missing = dangling.Count > 0 ? $"references to resources it lacks ({string.Join(", ", dangling.Select(o => o.GetName(0)?.Value).Distinct().Take(5))})" : "";
            Issue("6.2.2", $"{where} has {string.Join(" and ", new[] { what, missing }.Where(t => t.Length > 0))}; they are removed", () =>
            {
                foreach (PdfOperation operation in unknown.Concat(dangling))
                {
                    content.Remove(operation);
                }

                write();
            }, page);
        }
    }
}
