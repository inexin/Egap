using Egap.Pdf.Annotations;

namespace Egap.Pdf.PdfA;

/// <summary>
/// PDF/A for documents Egap creates (Phase 5 task 14, design Q11): the identification written at creation, and the writer
/// rules applied at each save. What a save can put right it does (embedding the standard 14 fonts, re-encoding LZW streams,
/// printing annotations, describing attachments); what it cannot throws a <see cref="PdfException"/> naming the rule.
/// </summary>
internal static class PdfAWriter
{
    /// <summary>Actions PDF/A forbids (ISO 19005-2 6.6.1); PDF/A-1 also forbids hiding fields and changing layers.</summary>
    private static readonly HashSet<string> ForbiddenActions = ["Launch", "Sound", "Movie", "ResetForm", "ImportData", "JavaScript", "Hide", "SetOCGState", "Rendition", "Trans", "GoTo3DView", "RichMediaExecute"];

    private static readonly HashSet<string> ForbiddenActionsPart2 = ["Launch", "Sound", "Movie", "ResetForm", "ImportData", "JavaScript", "Rendition", "Trans", "GoTo3DView", "RichMediaExecute"];

    /// <summary>The part number (1 to 4), or 0 for none.</summary>
    public static int Part(PdfAConformance conformance) => conformance switch
    {
        PdfAConformance.PdfA1b => 1,
        PdfAConformance.PdfA2b or PdfAConformance.PdfA2u => 2,
        PdfAConformance.PdfA3b or PdfAConformance.PdfA3u => 3,
        PdfAConformance.PdfA4 or PdfAConformance.PdfA4f => 4,
        _ => 0,
    };

    /// <summary>The <c>pdfaid:conformance</c> letter: B or U, F for PDF/A-4f, none for PDF/A-4.</summary>
    public static string? Level(PdfAConformance conformance) => conformance switch
    {
        PdfAConformance.PdfA1b or PdfAConformance.PdfA2b or PdfAConformance.PdfA3b => "B",
        PdfAConformance.PdfA2u or PdfAConformance.PdfA3u => "U",
        PdfAConformance.PdfA4f => "F",
        _ => null,
    };

    /// <summary>The PDF version a part is written as: 1.4 for PDF/A-1, 2.0 for PDF/A-4, 1.7 otherwise.</summary>
    public static string Version(PdfAConformance conformance) => Part(conformance) switch
    {
        1 => "1.4",
        4 => "2.0",
        _ => "1.7",
    };

    /// <summary>The conformance a document's XMP claims, or <see cref="PdfAConformance.None"/> for none Egap writes.</summary>
    public static PdfAConformance Read(PdfDocument document)
    {
        (string? part, string? level, _) = document.Metadata.GetPdfAId();
        return (part?.Trim(), level?.Trim().ToUpperInvariant()) switch
        {
            ("1", "B") => PdfAConformance.PdfA1b,
            ("2", "B") => PdfAConformance.PdfA2b,
            ("2", "U") => PdfAConformance.PdfA2u,
            ("3", "B") => PdfAConformance.PdfA3b,
            ("3", "U") => PdfAConformance.PdfA3u,
            ("4", null or "") => PdfAConformance.PdfA4,
            ("4", "F") => PdfAConformance.PdfA4f,
            _ => PdfAConformance.None,
        };
    }

    /// <summary>Gives a new document the output intent and XMP identification of a conformance.</summary>
    public static void Identify(PdfDocument document, PdfAConformance conformance)
    {
        AddOutputIntent(document);
        document.Metadata.SetPdfAId(Part(conformance), Level(conformance), Part(conformance) == 4 ? "2020" : null);
    }

    /// <summary>The sRGB output intent (14.11.5), unless the catalog has a PDF/A one already.</summary>
    public static void AddOutputIntent(PdfDocument document)
    {
        PdfArray intents = document.Catalog.Get<PdfArray>("OutputIntents") ?? [];
        if (intents.Any(i => i.Resolve() is PdfDictionary d && d.GetName("S")?.Value == "GTS_PDFA1"))
        {
            return;
        }

        PdfStream profile = new(new PdfDictionary { ["N"] = new PdfInteger(3) });
        profile.SetDecodedBytes(SrgbProfile.Data);
        intents.Add(document.Objects.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("OutputIntent"),
            ["S"] = PdfName.Get("GTS_PDFA1"),
            ["OutputConditionIdentifier"] = PdfString.FromText(SrgbProfile.Condition),
            ["Info"] = PdfString.FromText(SrgbProfile.Condition),
            ["RegistryName"] = PdfString.FromText("http://www.color.org"),
            ["DestOutputProfile"] = document.Objects.Add(profile),
        }));
        document.Catalog["OutputIntents"] = intents;
    }

    /// <summary>The save options a conformance needs: no encryption, and for PDF/A-1 no object or cross-reference streams.</summary>
    public static PdfSaveOptions Options(PdfAConformance conformance, PdfSaveOptions options)
    {
        if (options.Encryption.Kind == PdfEncryptionKind.Aes256)
        {
            throw Rule(conformance, "6.1.3", "a PDF/A file cannot be encrypted");
        }

        return Part(conformance) == 1 ? options with { Compression = PdfCompression.None } : options;
    }

    /// <summary>Applies the writer rules before a save, after drawing's fonts are written.</summary>
    public static void Apply(PdfDocument document, PdfAConformance conformance)
    {
        int part = Part(conformance);
        if (Read(document) != conformance)
        {
            // Identification replaced since creation (SetXmp), so it is written again.
            Identify(document, conformance);
        }

        AddOutputIntent(document);
        (List<PdfDictionary> resources, List<PdfStream> forms) = ResourceWalker.Walk(document);
        Fonts(document, conformance, resources);
        if (part == 1)
        {
            NoTransparency(conformance, resources, forms, document);
        }

        Annotations(document, conformance);
        Attachments(document, conformance);
        ReencodeLzw(document);
        if (document.Catalog.Get<PdfDictionary>("Names")?.ContainsKey("JavaScript") == true || document.Catalog.ContainsKey("AA"))
        {
            throw Rule(conformance, "6.6.2", "the document has JavaScript or additional actions");
        }

        if (document.Catalog.Get("OpenAction")?.Resolve() is PdfDictionary open)
        {
            CheckAction(conformance, open);
        }

        if (part == 1 && document.Catalog.ContainsKey("OCProperties"))
        {
            throw Rule(conformance, "6.1.13", "PDF/A-1 does not allow optional content (layers)");
        }

        if (part == 4)
        {
            // ISO 19005-4 6.1.3: no Info dictionary, or only its ModDate when the catalog has PieceInfo.
            if (document.Trailer.Get<PdfDictionary>("Info") is PdfDictionary info)
            {
                if (document.Catalog.ContainsKey("PieceInfo") && info.Get("ModDate") is PdfObject modified)
                {
                    document.Trailer["Info"] = document.Objects.Add(new PdfDictionary { ["ModDate"] = modified });
                }
                else
                {
                    document.Trailer.Remove("Info");
                }
            }
        }
    }

    /// <summary>Every font embedded: the standard 14 by <see cref="StandardFontEmbedder"/>; any other font left out is an error.</summary>
    private static void Fonts(PdfDocument document, PdfAConformance conformance, List<PdfDictionary> resources)
    {
        foreach (PdfDictionary font in ResourceWalker.Fonts(resources))
        {
            if (font.GetName("Subtype")?.Value == "Type3" || StandardFontEmbedder.IsEmbedded(font) || StandardFontEmbedder.TryEmbed(document, font))
            {
                continue;
            }

            throw Rule(conformance, "6.2.11.4", $"the font {font.GetName("BaseFont")?.Value ?? "(unnamed)"} is not embedded and is not one of the standard 14 Egap can embed");
        }
    }

    /// <summary>PDF/A-1 has no transparency (ISO 19005-1 6.4): soft masks, constant alpha below 1, blend modes or transparency groups.</summary>
    private static void NoTransparency(PdfAConformance conformance, List<PdfDictionary> resources, List<PdfStream> forms, PdfDocument document)
    {
        foreach (PdfDictionary resource in resources)
        {
            foreach (PdfObject value in ResourceWalker.Values(resource.Get<PdfDictionary>("ExtGState")))
            {
                if (value is not PdfDictionary state)
                {
                    continue;
                }

                bool mask = state.Get("SMask") is PdfObject smask && smask.Resolve() is not PdfName { Value: "None" };
                bool alpha = (state.GetNumber("CA") ?? 1) < 1 || (state.GetNumber("ca") ?? 1) < 1;
                bool blend = state.Get("BM")?.Resolve() switch
                {
                    PdfName name => name.Value is not ("Normal" or "Compatible"),
                    PdfArray array => array.Any(b => b.Resolve() is PdfName { Value: not ("Normal" or "Compatible") }),
                    _ => false,
                };
                if (mask || alpha || blend)
                {
                    throw Rule(conformance, "6.4", "PDF/A-1 does not allow transparency (soft masks, opacity below 1 or blend modes); use PDF/A-2b or later");
                }
            }

            foreach (PdfObject value in ResourceWalker.Values(resource.Get<PdfDictionary>("XObject")))
            {
                if (value is PdfStream { Dictionary: var image } && image.GetName("Subtype")?.Value == "Image")
                {
                    if (image.ContainsKey("SMask") || (image.GetInt("SMaskInData") ?? 0) != 0)
                    {
                        throw Rule(conformance, "6.4", "PDF/A-1 does not allow images with soft masks (alpha); use PDF/A-2b or later");
                    }

                    if (image.Get("Filter")?.Resolve() is PdfName { Value: "JPXDecode" } || image.Get<PdfArray>("Filter")?.Any(f => f.Resolve() is PdfName { Value: "JPXDecode" }) == true)
                    {
                        throw Rule(conformance, "6.1.10", "PDF/A-1 does not allow JPEG 2000 images; use PDF/A-2b or later");
                    }
                }
            }
        }

        static bool TransparencyGroup(PdfDictionary dictionary) => dictionary.Get<PdfDictionary>("Group")?.GetName("S")?.Value == "Transparency";
        if (forms.Any(f => TransparencyGroup(f.Dictionary)) || Enumerable.Range(0, document.Pages.Count).Any(i => TransparencyGroup(document.Pages[i].Dictionary)))
        {
            throw Rule(conformance, "6.4", "PDF/A-1 does not allow transparency groups; use PDF/A-2b or later");
        }
    }

    /// <summary>
    /// Annotations (ISO 19005-2 6.3): printed and never hidden, with only a normal appearance, drawn where Egap can draw one
    /// and empty otherwise; no forbidden actions; the form needs no appearances generated by the viewer.
    /// </summary>
    private static void Annotations(PdfDocument document, PdfAConformance conformance)
    {
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            if (page.Dictionary.ContainsKey("AA"))
            {
                throw Rule(conformance, "6.6.2", $"page {i + 1} has additional actions");
            }

            foreach (PdfAnnotation annotation in page.Annotations)
            {
                PdfDictionary dictionary = annotation.Dictionary;
                string? subtype = dictionary.GetName("Subtype")?.Value;
                if (subtype is "Movie" or "Sound" or "Screen" or "3D" or "RichMedia" || (Part(conformance) == 1 && subtype is "FileAttachment"))
                {
                    throw Rule(conformance, "6.3.1", $"page {i + 1} has a {subtype} annotation, which PDF/A does not allow");
                }

                if (subtype != "Popup")
                {
                    PdfAnnotationFlags flags = annotation.Flags;
                    flags = (flags | PdfAnnotationFlags.Print) & ~(PdfAnnotationFlags.Hidden | PdfAnnotationFlags.Invisible | PdfAnnotationFlags.NoView | PdfAnnotationFlags.ToggleNoView);
                    if (flags != annotation.Flags)
                    {
                        dictionary["F"] = new PdfInteger((int)flags);
                    }
                }

                if (dictionary.ContainsKey("AA"))
                {
                    throw Rule(conformance, "6.6.2", $"an annotation on page {i + 1} has additional actions");
                }

                if (dictionary.Get("A")?.Resolve() is PdfDictionary action)
                {
                    CheckAction(conformance, action);
                }

                PdfRectangle box = annotation.Rectangle;
                bool needsAppearance = subtype is not ("Popup" or "Link") && box.Width > 0 && box.Height > 0;
                if (needsAppearance && dictionary.Get<PdfDictionary>("AP")?.Get("N") is null && !AppearanceBuilder.Update(annotation, document))
                {
                    PdfStream empty = new(new PdfDictionary
                    {
                        ["Type"] = PdfName.Get("XObject"),
                        ["Subtype"] = PdfName.Get("Form"),
                        ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfReal(box.Width), new PdfReal(box.Height) },
                    });
                    empty.SetDecodedBytes([]);
                    dictionary["AP"] = new PdfDictionary { ["N"] = document.Objects.Add(empty) };
                }

                if (dictionary.Get<PdfDictionary>("AP") is PdfDictionary appearance)
                {
                    appearance.Remove("D");
                    appearance.Remove("R");
                }
            }
        }

        if (document.Catalog.Get<PdfDictionary>("AcroForm") is PdfDictionary form)
        {
            form.Remove("NeedAppearances");
            if (form.ContainsKey("XFA"))
            {
                throw Rule(conformance, "6.4.2", "the form has an XFA part");
            }
        }
    }

    private static void CheckAction(PdfAConformance conformance, PdfDictionary action)
    {
        HashSet<string> forbidden = Part(conformance) == 1 ? ForbiddenActions : ForbiddenActionsPart2;
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Queue<PdfDictionary> queue = new([action]);
        while (queue.TryDequeue(out PdfDictionary? next) && seen.Count < 1000)
        {
            if (!seen.Add(next))
            {
                continue;
            }

            if (next.GetName("S")?.Value is string kind && (forbidden.Contains(kind) || (Part(conformance) == 1 && kind is "Named" && next.GetName("N")?.Value is not ("NextPage" or "PrevPage" or "FirstPage" or "LastPage"))))
            {
                throw Rule(conformance, "6.6.1", $"the document uses a {kind} action, which PDF/A does not allow");
            }

            switch (next.Get("Next")?.Resolve())
            {
                case PdfDictionary single:
                    queue.Enqueue(single);
                    break;
                case PdfArray many:
                    foreach (PdfObject item in many)
                    {
                        if (item.Resolve() is PdfDictionary d)
                        {
                            queue.Enqueue(d);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Attachments: none in PDF/A-1; PDF/A files only in PDF/A-2 and PDF/A-4; in PDF/A-3 and 4f any file, each given a
    /// relationship, a MIME type and a place in the catalog's associated files (ISO 19005-3 6.8).
    /// </summary>
    private static void Attachments(PdfDocument document, PdfAConformance conformance)
    {
        List<PdfAttachment> attachments = [.. document.Attachments];
        int part = Part(conformance);
        if (conformance == PdfAConformance.PdfA4f && attachments.Count == 0)
        {
            throw Rule(conformance, "6.9", "a PDF/A-4f file must carry at least one attachment; use PDF/A-4 without one");
        }

        if (attachments.Count == 0)
        {
            return;
        }

        if (part == 1)
        {
            throw Rule(conformance, "6.1.11", "PDF/A-1 does not allow attachments; use PDF/A-3b");
        }

        bool anyKind = part == 3 || conformance == PdfAConformance.PdfA4f;
        PdfArray associated = document.Catalog.Get<PdfArray>("AF") ?? [];
        foreach (PdfAttachment attachment in attachments)
        {
            if (!anyKind && !IsPdfA(attachment))
            {
                throw Rule(conformance, "6.8", $"the attachment {attachment.Name} is not a PDF/A file; use PDF/A-3b, 3u or 4f");
            }

            if (part >= 3)
            {
                PdfDictionary spec = attachment.FileSpecification;
                if (!spec.ContainsKey("AFRelationship"))
                {
                    spec["AFRelationship"] = PdfName.Get("Unspecified");
                }

                if (attachment.Stream is PdfStream stream && !stream.Dictionary.ContainsKey("Subtype"))
                {
                    stream.Dictionary["Subtype"] = PdfName.Get("application/octet-stream");
                }

                if (attachment.Reference is PdfReference reference && !associated.Any(a => a is PdfReference r && r.ObjectNumber == reference.ObjectNumber))
                {
                    associated.Add(reference);
                }
            }
        }

        if (part >= 3)
        {
            document.Catalog["AF"] = associated;
        }
    }

    private static bool IsPdfA(PdfAttachment attachment)
    {
        try
        {
            if (attachment.GetBytes() is not byte[] bytes)
            {
                return false;
            }

            using PdfDocument embedded = PdfDocument.Open(bytes);
            return Read(embedded) != PdfAConformance.None;
        }
        catch (PdfException)
        {
            return false;
        }
    }

    /// <summary>LZW is not allowed (ISO 19005-2 6.1.7.2): such streams are decoded and written again with Flate.</summary>
    private static void ReencodeLzw(PdfDocument document)
    {
        foreach (PdfReference reference in document.Objects)
        {
            if (document.Objects[reference] is PdfStream stream && stream.UndecodedFilter is null && stream.Filters.Any(f => f.Value is "LZWDecode" or "LZW"))
            {
                byte[] decoded = stream.GetDecodedBytes();
                stream.Dictionary.Remove("DecodeParms");
                stream.SetDecodedBytes(decoded);
            }
        }
    }

    private static PdfException Rule(PdfAConformance conformance, string clause, string problem)
    {
        int part = Part(conformance);
        return new PdfException($"Cannot save as {Name(conformance)}: {problem} (ISO 19005-{part} {clause}).");
    }

    /// <summary>The conformance as people write it, such as PDF/A-2b.</summary>
    public static string Name(PdfAConformance conformance)
    {
        return conformance == PdfAConformance.None ? "plain PDF" : "PDF/A-" + Part(conformance) + (Level(conformance)?.ToLowerInvariant() ?? "");
    }
}
