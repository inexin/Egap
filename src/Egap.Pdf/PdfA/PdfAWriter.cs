namespace Egap.Pdf.PdfA;

/// <summary>
/// PDF/A writing (Phase 5 tasks 14 and 15, design Q11, Q12): parts and versions, the identification and output intent, and
/// the rules applied at each save of a document created or converted as PDF/A. What a save can put right without losing
/// content it does (embedding fonts, re-encoding LZW streams, printing annotations, describing attachments); anything else
/// throws a <see cref="PdfException"/> naming the rule.
/// </summary>
internal static class PdfAWriter
{
    /// <summary>The part number (1 to 4), or 0 for none.</summary>
    public static int Part(PdfAConformance conformance) => conformance switch
    {
        PdfAConformance.PdfA1b => 1,
        PdfAConformance.PdfA2b or PdfAConformance.PdfA2u or PdfAConformance.PdfA2a => 2,
        PdfAConformance.PdfA3b or PdfAConformance.PdfA3u or PdfAConformance.PdfA3a => 3,
        PdfAConformance.PdfA4 or PdfAConformance.PdfA4f => 4,
        _ => 0,
    };

    /// <summary>The <c>pdfaid:conformance</c> letter: B or U, F for PDF/A-4f, none for PDF/A-4.</summary>
    public static string? Level(PdfAConformance conformance) => conformance switch
    {
        PdfAConformance.PdfA1b or PdfAConformance.PdfA2b or PdfAConformance.PdfA3b => "B",
        PdfAConformance.PdfA2u or PdfAConformance.PdfA3u => "U",
        PdfAConformance.PdfA2a or PdfAConformance.PdfA3a => "A",
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
            ("2", "A") => PdfAConformance.PdfA2a,
            ("3", "A") => PdfAConformance.PdfA3a,
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

    /// <summary>
    /// A PDF/A output intent (14.11.5) with the given ICC profile, sRGB without one, unless the catalog has a PDF/A one already.
    /// </summary>
    public static void AddOutputIntent(PdfDocument document, ReadOnlyMemory<byte>? profileBytes = null, string? condition = null)
    {
        PdfArray intents = document.Catalog.Get<PdfArray>("OutputIntents") ?? [];
        if (intents.Any(i => i.Resolve() is PdfDictionary d && d.GetName("S")?.Value == "GTS_PDFA1"))
        {
            return;
        }

        ReadOnlySpan<byte> data = profileBytes is { } given ? given.Span : SrgbProfile.Data;
        int components = ColorScan.ProfileSpace(data) switch
        {
            "CMYK" => 4,
            "GRAY" => 1,
            _ => 3,
        };
        condition ??= profileBytes is null ? SrgbProfile.Condition : "Custom";
        PdfStream profile = new(new PdfDictionary { ["N"] = new PdfInteger(components) });
        profile.SetDecodedBytes(data);
        intents.Add(document.Objects.Add(new PdfDictionary
        {
            ["Type"] = PdfName.Get("OutputIntent"),
            ["S"] = PdfName.Get("GTS_PDFA1"),
            ["OutputConditionIdentifier"] = PdfString.FromText(condition),
            ["Info"] = PdfString.FromText(condition),
            ["RegistryName"] = PdfString.FromText("http://www.color.org"),
            ["DestOutputProfile"] = document.Objects.Add(profile),
        }));
        document.Catalog["OutputIntents"] = intents;
    }

    /// <summary>
    /// The save options a document under PDF/A rules needs: written whole (a converted file's header and structure change),
    /// without encryption, and for PDF/A-1 without object or cross-reference streams.
    /// </summary>
    public static PdfSaveOptions Options(PdfDocument document, PdfAConformance conformance, PdfSaveOptions options)
    {
        if (options.Encryption.Kind == PdfEncryptionKind.Aes256)
        {
            throw new PdfException($"Cannot save as {Name(conformance)}: a PDF/A file cannot be encrypted (ISO 19005-{Part(conformance)} 6.1.3).");
        }

        options = options with { Mode = PdfSaveMode.Rewrite, Encryption = document.IsEncrypted ? PdfEncryption.None : options.Encryption };
        return Part(conformance) == 1 ? options with { Compression = PdfCompression.None } : options;
    }

    /// <summary>
    /// Applies the rules before a save, after drawing's fonts are written: what can be put right without losing content is,
    /// and anything else throws, naming the rule.
    /// </summary>
    public static void Apply(PdfDocument document, PdfAConformance conformance)
    {
        List<PdfAIssue> issues = PdfARules.Run(document, conformance, PdfAConvertOptions.Default, fixing: true, allowLossy: false);
        if (issues.FirstOrDefault(i => !i.Fixed) is PdfAIssue unmet)
        {
            throw new PdfException($"Cannot save as {Name(conformance)}: {unmet.Description}{(unmet.Page is int page ? $" (page {page})" : "")} (ISO 19005-{Part(conformance)} {unmet.Clause}).");
        }
    }

    /// <summary>Applies the PDF/UA basics before a save of a document created claiming PDF/UA; anything unmet throws.</summary>
    public static void ApplyAccessibility(PdfDocument document)
    {
        if (PdfUARules.Run(document, fixing: true).FirstOrDefault(i => !i.Fixed) is PdfAIssue unmet)
        {
            throw new PdfException($"Cannot save as PDF/UA-1: {unmet.Description}{(unmet.Page is int page ? $" (page {page})" : "")} (ISO 14289-1 {unmet.Clause}).");
        }
    }

    /// <summary>The conformance as people write it, such as PDF/A-2b.</summary>
    public static string Name(PdfAConformance conformance)
    {
        return conformance == PdfAConformance.None ? "plain PDF" : "PDF/A-" + Part(conformance) + (Level(conformance)?.ToLowerInvariant() ?? "");
    }
}
