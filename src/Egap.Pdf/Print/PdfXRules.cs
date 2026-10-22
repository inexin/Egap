using System.Globalization;

using Egami.Colors;

using Egap.Pdf.PdfA;

namespace Egap.Pdf.Print;

/// <summary>
/// The PDF/X rules Egap enforces (Phase 8 design Q9, Build Plan tasks 15 to 19), a second standard on Phase 5's rule
/// engine: run in checking mode, which changes nothing and says which issues conversion could fix, or fixing mode, which
/// puts each fixable issue right as it is found. The rules come from ISO 15930-4, -6, -7, -8 and -9 as Egap reads them:
/// output intent, trim and bleed boxes, identification and metadata, trapping, embedded fonts, colour spaces against the
/// output intent, transparency for PDF/X-1a and X-3, encryption, external content, transfer functions and halftones,
/// annotations in the print area, actions and optional content. No open validator exists to judge them: Acrobat Preflight
/// or PackzView is the full judge.
/// </summary>
internal sealed class PdfXRules
{
    private readonly PdfDocument document;
    private readonly PdfXConformance conformance;
    private readonly PdfXConvertOptions options;
    private readonly bool fixing;
    private readonly List<PdfAIssue> issues = [];

    private PdfXRules(PdfDocument document, PdfXConformance conformance, PdfXConvertOptions options, bool fixing)
    {
        this.document = document;
        this.conformance = conformance;
        this.options = options;
        this.fixing = fixing;
    }

    /// <summary>Runs every rule; in fixing mode puts right what it can.</summary>
    public static List<PdfAIssue> Run(PdfDocument document, PdfXConformance conformance, PdfXConvertOptions options, bool fixing, CancellationToken cancellationToken)
    {
        PdfXRules rules = new(document, conformance, options, fixing);
        rules.RunAll(cancellationToken);
        return rules.issues;
    }

    /// <summary>The version string the file identifies itself with: <c>PDF/X-1a:2003</c>, <c>PDF/X-4</c> and so on.</summary>
    public static string VersionName(PdfXConformance conformance) => conformance switch
    {
        PdfXConformance.PdfX1a => "PDF/X-1a:2003",
        PdfXConformance.PdfX3 => "PDF/X-3:2003",
        PdfXConformance.PdfX4 => "PDF/X-4",
        PdfXConformance.PdfX4p => "PDF/X-4p",
        PdfXConformance.PdfX5g => "PDF/X-5g",
        PdfXConformance.PdfX5pg => "PDF/X-5pg",
        PdfXConformance.PdfX5n => "PDF/X-5n",
        PdfXConformance.PdfX6 => "PDF/X-6",
        PdfXConformance.PdfX6p => "PDF/X-6p",
        PdfXConformance.PdfX6n => "PDF/X-6n",
        _ => "plain PDF",
    };

    /// <summary>The PDF version a conformance is written as: 1.4 for X-1a and X-3, 2.0 for X-6, 1.6 otherwise.</summary>
    public static string Version(PdfXConformance conformance) => conformance switch
    {
        PdfXConformance.PdfX1a or PdfXConformance.PdfX3 => "1.4",
        PdfXConformance.PdfX6 or PdfXConformance.PdfX6p or PdfXConformance.PdfX6n => "2.0",
        _ => "1.6",
    };

    /// <summary>The standard's part, for report headings: <c>ISO 15930-7</c>.</summary>
    public static string Standard(PdfXConformance conformance) => conformance switch
    {
        PdfXConformance.PdfX1a => "ISO 15930-4",
        PdfXConformance.PdfX3 => "ISO 15930-6",
        PdfXConformance.PdfX4 or PdfXConformance.PdfX4p => "ISO 15930-7",
        PdfXConformance.PdfX5g or PdfXConformance.PdfX5pg or PdfXConformance.PdfX5n => "ISO 15930-8",
        _ => "ISO 15930-9",
    };

    private bool OldStyle => conformance is PdfXConformance.PdfX1a or PdfXConformance.PdfX3;

    private bool ProfileByReference => conformance is PdfXConformance.PdfX4p or PdfXConformance.PdfX5pg or PdfXConformance.PdfX6p;

    private bool NColor => conformance is PdfXConformance.PdfX5n or PdfXConformance.PdfX6n;

    private bool ExternalAllowed => conformance is PdfXConformance.PdfX5g or PdfXConformance.PdfX5pg;

    private bool PdfTwo => conformance is PdfXConformance.PdfX6 or PdfXConformance.PdfX6p or PdfXConformance.PdfX6n;

    private void Issue(string rule, string description, Action? fix = null, int? page = null)
    {
        bool fixable = fix is not null;
        if (fixable && fixing)
        {
            fix!();
        }

        issues.Add(new PdfAIssue(rule, description, page, fixable, fixable && fixing));
    }

    private void RunAll(CancellationToken cancellationToken)
    {
        FileStructure();
        OutputIntent();
        Metadata();
        Boxes();
        cancellationToken.ThrowIfCancellationRequested();
        Fonts();
        cancellationToken.ThrowIfCancellationRequested();
        Content(cancellationToken);
        GraphicsStates();
        Annotations();
        Actions();
        OptionalContent();
    }

    /// <summary>Encryption and the PDF version.</summary>
    private void FileStructure()
    {
        bool settled = document.WriterPdfX == conformance;
        if (document.IsEncrypted && !settled)
        {
            Issue("Encryption", "the file is encrypted; it is written without encryption", () => { });
        }

        string target = Version(conformance);
        bool fits = PdfTwo ? document.Version == "2.0" : string.CompareOrdinal(document.Version, target) <= 0;
        if (!fits && !settled)
        {
            Issue("Version", $"the file is PDF {document.Version}; it is written as PDF {target}", () => document.Catalog.Remove("Version"));
        }
    }

    /// <summary>One GTS_PDFX output intent naming a condition, with its profile embedded (or referenced for the p versions).</summary>
    private void OutputIntent()
    {
        PdfDictionary[] intents = [.. (document.Catalog.Get<PdfArray>("OutputIntents") ?? []).Select(i => i.Resolve()).OfType<PdfDictionary>()
            .Where(i => i.GetName("S")?.Value == "GTS_PDFX")];
        PdfOutputCondition? wanted = options.OutputCondition;
        Action? replace = WriteIntent(wanted ?? Existing(intents.FirstOrDefault()) ?? PdfOutputCondition.Fogra39);
        if (intents.Length == 0)
        {
            Issue("OutputIntent", replace is null ? "the file has no PDF/X output intent, and the output condition has no profile to embed"
                : "the file has no PDF/X output intent; one is added", replace);
            return;
        }

        if (intents.Length > 1)
        {
            Issue("OutputIntent", "the file has more than one PDF/X output intent; one is kept", replace);
            return;
        }

        PdfDictionary intent = intents[0];
        if (wanted is not null && intent.Get<PdfString>("OutputConditionIdentifier")?.ToText() != wanted.Identifier)
        {
            Issue("OutputIntent", $"the output intent is not {wanted.Identifier}; it is replaced", replace);
            return;
        }

        if (intent.Get<PdfString>("OutputConditionIdentifier") is null)
        {
            Issue("OutputIntent", "the output intent names no output condition identifier", replace);
        }

        if (ProfileByReference)
        {
            if (intent.Get<PdfDictionary>("DestOutputProfileRef") is null)
            {
                Issue("OutputIntent", $"{VersionName(conformance)} references its output profile (/DestOutputProfileRef), and the intent does not");
            }

            return;
        }

        if (intent.Get<PdfStream>("DestOutputProfile") is not PdfStream profile)
        {
            Issue("OutputIntent", "the output intent embeds no profile", replace);
            return;
        }

        byte[] data;
        try
        {
            data = profile.GetDecodedBytes();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            Issue("OutputIntent", $"the output intent's profile cannot be read: {e.Message}", replace);
            return;
        }

        string space = ColorScan.ProfileSpace(data);
        string deviceClass = data.Length >= 16 ? System.Text.Encoding.ASCII.GetString(data, 12, 4) : "";
        bool spaceFits = NColor ? space.EndsWith("CLR", StringComparison.Ordinal) && space != "3CLR" && space != "4CLR"
            : conformance == PdfXConformance.PdfX1a ? space is "CMYK" or "GRAY" : space is "CMYK" or "GRAY" or "RGB";
        if (!spaceFits)
        {
            Issue("OutputIntent", $"the output profile is {space}, which {VersionName(conformance)} does not print to", NColor ? null : replace);
        }

        if (deviceClass != "prtr")
        {
            Issue("OutputIntent", $"the output profile is a '{deviceClass.Trim()}' profile, not an output ('prtr') profile", replace);
        }
    }

    /// <summary>The condition an existing intent names, when Egap knows it and can embed its profile.</summary>
    private static PdfOutputCondition? Existing(PdfDictionary? intent)
    {
        string? identifier = intent?.Get<PdfString>("OutputConditionIdentifier")?.ToText();
        if (identifier is null || PdfOutputCondition.Find(identifier) is not PdfOutputCondition known)
        {
            return null;
        }

        if (known.Profile is null && intent!.Get<PdfStream>("DestOutputProfile") is PdfStream stream)
        {
            try
            {
                return known.WithProfile(ColorProfile.FromIcc(stream.GetDecodedBytes()));
            }
            catch (Exception e) when (e is Egami.EgamiException or ArgumentException or InvalidDataException or PdfException)
            {
                return null;
            }
        }

        return known.Profile is null ? null : known;
    }

    /// <summary>The fix that writes the one PDF/X output intent, keeping a PDF/A one; null when the condition has no profile.</summary>
    private Action? WriteIntent(PdfOutputCondition condition)
    {
        if (condition.Profile is not ColorProfile profile)
        {
            return null;
        }

        return () =>
        {
            byte[] icc = profile.ToIcc();
            PdfStream stream = new(new PdfDictionary
            {
                ["N"] = new PdfInteger(profile.ChannelCount),
            });
            stream.SetDecodedBytes(icc);
            stream.Dictionary["Filter"] = PdfName.Get("FlateDecode");
            PdfDictionary intent = new()
            {
                ["Type"] = PdfName.Get("OutputIntent"),
                ["S"] = PdfName.Get("GTS_PDFX"),
                ["OutputConditionIdentifier"] = PdfString.FromText(condition.Identifier),
                ["OutputCondition"] = PdfString.FromText(condition.Description),
                ["Info"] = PdfString.FromText(condition.Description),
                ["RegistryName"] = PdfString.FromText(PdfOutputCondition.Registry),
                ["DestOutputProfile"] = document.Objects.Add(stream),
            };
            PdfArray kept = [.. (document.Catalog.Get<PdfArray>("OutputIntents") ?? []).Where(i => i.Resolve() is PdfDictionary d && d.GetName("S")?.Value != "GTS_PDFX")
                .Select(i => i is PdfReference ? i : ((PdfDictionary)i.Resolve()).Clone())];
            kept.Add(document.Objects.Add(intent));
            document.Catalog["OutputIntents"] = kept;
            document.ResetRenderCache();
        };
    }

    /// <summary>Identification, title, dates, trapping and, from PDF/X-4, the XMP packet.</summary>
    private void Metadata()
    {
        PdfMetadata metadata = document.Metadata;
        string version = VersionName(conformance);
        if (OldStyle)
        {
            string? claimed = metadata.Info?.GetText(PdfName.Get("GTS_PDFXVersion"));
            if (claimed != version)
            {
                Issue("Identification", $"the document information does not say {version} (/GTS_PDFXVersion); it is written", () => Info()["GTS_PDFXVersion"] = PdfString.FromText(version));
            }
        }
        else
        {
            if (!metadata.HasXmp)
            {
                Issue("Metadata", $"{version} needs an XMP metadata packet; one is written from the document information", metadata.CreateXmp);
            }

            if (metadata.GetPdfXId() != version)
            {
                Issue("Identification", $"the XMP metadata does not say {version} (pdfxid:GTS_PDFXVersion); it is written", () => metadata.SetPdfXId(version));
            }
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            Issue("Metadata", "the document has no title; \"Untitled\" is written", () => metadata.Title = "Untitled");
        }

        if (metadata.CreationDate is null)
        {
            Issue("Metadata", "the document has no creation date; the time of conversion is written", () => metadata.CreationDate = document.Clock.GetLocalNow());
        }

        if (metadata.ModificationDate is null)
        {
            Issue("Metadata", "the document has no modification date; the time of conversion is written", () => metadata.ModificationDate = document.Clock.GetLocalNow());
        }

        string? trapped = metadata.Info?.GetName("Trapped")?.Value;
        if (trapped is not ("True" or "False"))
        {
            string value = options.Trapped ? "True" : "False";
            Issue("Trapped", $"the document does not say whether it is trapped (/Trapped {trapped ?? "missing"}); /Trapped /{value} is written", () =>
            {
                Info()["Trapped"] = PdfName.Get(value);
                if (!OldStyle)
                {
                    metadata.EditXmp(packet => packet.SetText(Egap.Pdf.Metadata.XmpPacket.Pdf + "Trapped", value));
                }
            });
        }
    }

    private PdfDictionary Info()
    {
        if (document.Trailer.Get<PdfDictionary>("Info") is PdfDictionary info)
        {
            return info;
        }

        PdfDictionary created = [];
        document.Trailer["Info"] = document.Objects.Add(created);
        return created;
    }

    /// <summary>Each page has a trim box (or art box, not both), and the bleed box holds it inside the media box.</summary>
    private void Boxes()
    {
        double bleed = options.Bleed * 72 / 25.4;
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            PdfRectangle media = page.MediaBox.Normalize();
            bool trim = page.Dictionary.ContainsKey("TrimBox"), art = page.Dictionary.ContainsKey("ArtBox");
            if (!trim && !art)
            {
                Issue("TrimBox", "the page has no trim box; its crop box becomes the trim box", () => page.Dictionary["TrimBox"] = page.CropBox.Normalize().Intersect(media).ToArray(), i + 1);
            }
            else if (trim && art)
            {
                Issue("TrimBox", "the page has both a trim box and an art box; the art box is removed", () => page.Dictionary.Remove("ArtBox"), i + 1);
            }

            PdfRectangle trimBox = (trim || !art ? (PdfRectangle.FromArray(page.Dictionary.Get<PdfArray>("TrimBox")) ?? page.CropBox) : page.ArtBox).Normalize();
            if (trimBox.Left < media.Left - 0.01 || trimBox.Bottom < media.Bottom - 0.01 || trimBox.Right > media.Right + 0.01 || trimBox.Top > media.Top + 0.01)
            {
                Issue("TrimBox", "the trim box reaches past the media box; it is cut to it", () => page.Dictionary["TrimBox"] = trimBox.Intersect(media).ToArray(), i + 1);
                trimBox = trimBox.Intersect(media);
            }

            PdfRectangle? bleedBox = PdfRectangle.FromArray(page.Dictionary.Get<PdfArray>("BleedBox"))?.Normalize();
            PdfRectangle wantedBleed = new PdfRectangle(trimBox.Left - bleed, trimBox.Bottom - bleed, trimBox.Right + bleed, trimBox.Top + bleed).Intersect(media);
            if (bleedBox is PdfRectangle b && (b.Left > trimBox.Left + 0.01 || b.Bottom > trimBox.Bottom + 0.01 || b.Right < trimBox.Right - 0.01 || b.Top < trimBox.Top - 0.01))
            {
                Issue("BleedBox", "the bleed box does not hold the trim box; it is set from the trim box and the bleed", () => page.Dictionary["BleedBox"] = wantedBleed.ToArray(), i + 1);
            }
            else if (bleedBox is PdfRectangle outside && (outside.Left < media.Left - 0.01 || outside.Bottom < media.Bottom - 0.01 || outside.Right > media.Right + 0.01 || outside.Top > media.Top + 0.01))
            {
                Issue("BleedBox", "the bleed box reaches past the media box; it is cut to it", () => page.Dictionary["BleedBox"] = outside.Intersect(media).ToArray(), i + 1);
            }
            else if (bleedBox is null && bleed > 0 && fixing)
            {
                // Not a rule: the bleed the caller asked for, set where the page has none.
                page.Dictionary["BleedBox"] = wantedBleed.ToArray();
            }
        }
    }

    /// <summary>Every font that shows text is embedded, from this machine or a bundled substitute when allowed.</summary>
    private void Fonts()
    {
        (List<PdfDictionary> resources, List<PdfStream> forms) = ResourceWalker.Walk(document);
        Dictionary<PdfDictionary, HashSet<int>> used = TextUse.Scan(document, forms);
        PdfAConvertOptions fonts = new() { UseSystemFonts = options.UseSystemFonts, AllowFontSubstitution = options.AllowFontSubstitution };
        foreach (PdfDictionary font in ResourceWalker.Fonts(resources))
        {
            string name = font.GetName("BaseFont")?.Value ?? "(unnamed)";
            if (font.GetName("Subtype")?.Value == "Type3" || StandardFontEmbedder.IsEmbedded(font) || used.GetValueOrDefault(font) is not { Count: > 0 } codes)
            {
                continue;
            }

            if (StandardFontEmbedder.PlanFor(font, fonts) is not StandardFontEmbedder.Plan plan)
            {
                Issue("Fonts", $"the font {name} is not embedded and no program for it was found" + (options.AllowFontSubstitution ? "" : " (AllowFontSubstitution would use a bundled substitute)"));
            }
            else if (codes.Except(StandardFontEmbedder.Codes(font, plan).Keys).Any())
            {
                Issue("Fonts", $"the font {name} is not embedded and {plan.Face.FullName}, its stand-in, lacks characters it shows");
            }
            else
            {
                Issue("Fonts", $"the font {name} is not embedded; it is embedded", () => StandardFontEmbedder.TryEmbed(document, font, fonts));
            }
        }
    }

    /// <summary>
    /// What the pages paint, from the preflight inventory: colour spaces against the output intent (converted through 8.3
    /// when they break it), transparency for PDF/X-1a and X-3, and external or PostScript content.
    /// </summary>
    private void Content(CancellationToken cancellationToken)
    {
        List<PdfPreflightItem> items = new Inventory(document, 0).Run(cancellationToken);
        string intentSpace = IntentSpace();
        PdfColorSpaceKinds allowed = conformance switch
        {
            PdfXConformance.PdfX1a => PdfColorSpaceKinds.DeviceCmyk | PdfColorSpaceKinds.DeviceGray | PdfColorSpaceKinds.Spot,
            _ => PdfColorSpaceKinds.All & ~(intentSpace == "RGB" ? PdfColorSpaceKinds.DeviceCmyk : PdfColorSpaceKinds.DeviceRgb),
        };
        if (intentSpace == "GRAY")
        {
            allowed &= ~(PdfColorSpaceKinds.DeviceRgb | PdfColorSpaceKinds.DeviceCmyk);
        }

        PdfPreflightItem[] wrong = [.. items.Where(i => i.Kind is PdfPreflightItemKind.ColorSpace or PdfPreflightItemKind.Image && (i.ColorSpace & ~allowed) != 0)];
        if (wrong.Length > 0)
        {
            string spaces = string.Join(", ", wrong.Select(w => w.ColorSpaceName).Distinct(StringComparer.Ordinal).Take(6));
            // An n-colorant intent takes content already in its inks: conversion into one is not part of Egap.
            Issue("ColorSpace", $"content paints in {spaces}, which {VersionName(conformance)} with a {intentSpace} output intent does not allow"
                + (NColor ? "" : "; it is converted into the output intent's profile"), NColor ? null : ConvertColors, wrong.Select(w => w.Page).Distinct().Count() == 1 ? wrong[0].Page : null);
        }

        if (OldStyle)
        {
            foreach (IGrouping<int?, PdfPreflightItem> page in items.Where(i => i.Kind == PdfPreflightItemKind.Transparency).GroupBy(i => i.Page))
            {
                bool groupsOnly = page.All(i => i.Description.Contains("transparency group", StringComparison.Ordinal));
                Issue("Transparency", $"{VersionName(conformance)} does not allow transparency: {string.Join(", ", page.Select(i => i.Description))}"
                    + (groupsOnly ? "; the groups are removed" : "; flattening it is not part of conversion"), groupsOnly ? () => RemoveGroups(page.Key) : null, page.Key);
            }
        }

        foreach ((PdfStream form, int page) in Forms())
        {
            if (form.Dictionary.GetName("Subtype")?.Value == "PS")
            {
                Issue("PostScript", "a PostScript XObject draws on the page; it is removed", () => form.Dictionary["Subtype"] = PdfName.Get("Form"), page);
            }

            if (form.Dictionary.ContainsKey("Ref") && !ExternalAllowed)
            {
                Issue("ExternalContent", $"a reference XObject points at external content, which {VersionName(conformance)} does not allow; its own content stays",
                    () => form.Dictionary.Remove("Ref"), page);
            }

            if (form.Dictionary.ContainsKey("OPI"))
            {
                Issue("OPI", "an XObject carries OPI comments; they are removed", () => form.Dictionary.Remove("OPI"), page);
            }

            if (form.Dictionary.ContainsKey("Alternates"))
            {
                Issue("Images", "an image has alternate images; they are removed", () => form.Dictionary.Remove("Alternates"), page);
            }

            if (OldStyle && form.Dictionary.GetName("Filter")?.Value == "JPXDecode")
            {
                Issue("Images", $"a JPEG 2000 image is newer than {VersionName(conformance)}'s PDF 1.4", null, page);
            }
        }
    }

    /// <summary>The output intent's colour space: <c>CMYK</c>, <c>RGB</c>, <c>GRAY</c>, or the one conversion will add.</summary>
    private string IntentSpace()
    {
        foreach (PdfDictionary intent in (document.Catalog.Get<PdfArray>("OutputIntents") ?? []).Select(i => i.Resolve()).OfType<PdfDictionary>())
        {
            if (intent.GetName("S")?.Value == "GTS_PDFX" && intent.Get<PdfStream>("DestOutputProfile") is PdfStream stream)
            {
                try
                {
                    return ColorScan.ProfileSpace(stream.GetDecodedBytes());
                }
                catch (PdfException e) when (e is not PdfLimitException)
                {
                    break;
                }
            }
        }

        return (options.OutputCondition?.Profile?.Model ?? ColorModel.Cmyk) switch
        {
            ColorModel.Gray => "GRAY",
            ColorModel.Rgb => "RGB",
            _ => "CMYK",
        };
    }

    /// <summary>Colours converted into the output intent's profile through Phase 8's conversion; for X-1a every colour-managed space to device.</summary>
    private void ConvertColors()
    {
        ColorProfile target = PdfPage.PressProfile(document);
        if (IntentProfile() is ColorProfile intent)
        {
            target = intent;
        }

        PdfColorConversionOptions conversion = options.ColorConversion with { Target = target };
        PdfColorConverter.Convert(document, conversion, forceDevice: conformance == PdfXConformance.PdfX1a);
    }

    private ColorProfile? IntentProfile()
    {
        foreach (PdfDictionary intent in (document.Catalog.Get<PdfArray>("OutputIntents") ?? []).Select(i => i.Resolve()).OfType<PdfDictionary>())
        {
            if (intent.GetName("S")?.Value == "GTS_PDFX" && intent.Get<PdfStream>("DestOutputProfile") is PdfStream stream)
            {
                try
                {
                    return ColorProfile.FromIcc(stream.GetDecodedBytes());
                }
                catch (Exception e) when (e is Egami.EgamiException or ArgumentException or InvalidDataException or PdfException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    private void RemoveGroups(int? page)
    {
        if (page is not int number)
        {
            return;
        }

        PdfPage pdfPage = document.Pages[number - 1];
        if (pdfPage.Dictionary.Get<PdfDictionary>("Group")?.GetName("S")?.Value == "Transparency")
        {
            pdfPage.Dictionary.Remove("Group");
        }

        foreach ((PdfStream form, int formPage) in Forms())
        {
            if (formPage == number && form.Dictionary.Get<PdfDictionary>("Group")?.GetName("S")?.Value == "Transparency")
            {
                form.Dictionary.Remove("Group");
            }
        }
    }

    /// <summary>Every XObject the pages' resources reach, with the page that first does.</summary>
    private IEnumerable<(PdfStream XObject, int Page)> Forms()
    {
        HashSet<PdfObject> seen = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < document.Pages.Count; i++)
        {
            Stack<(PdfDictionary? Resources, int Depth)> stack = new([(document.Pages[i].Resources, 0)]);
            while (stack.TryPop(out (PdfDictionary? Resources, int Depth) next))
            {
                if (next.Resources is null || next.Depth > 16 || !seen.Add(next.Resources))
                {
                    continue;
                }

                foreach (PdfObject value in ResourceWalker.Values(next.Resources.Get<PdfDictionary>("XObject")))
                {
                    if (value is PdfStream xobject && seen.Add(xobject))
                    {
                        yield return (xobject, i + 1);
                        stack.Push((xobject.Dictionary.Get<PdfDictionary>("Resources"), next.Depth + 1));
                    }
                }
            }
        }
    }

    /// <summary>Transfer functions, halftones with them, and black generation are left to the printer.</summary>
    private void GraphicsStates()
    {
        (List<PdfDictionary> resources, _) = ResourceWalker.Walk(document);
        foreach (PdfDictionary state in resources.SelectMany(r => ResourceWalker.Values(r.Get<PdfDictionary>("ExtGState"))).OfType<PdfDictionary>().Distinct(ReferenceEqualityComparer.Instance).Cast<PdfDictionary>())
        {
            foreach (string key in new[] { "TR", "TR2" })
            {
                if (state.Get(key)?.Resolve() is PdfObject tr && tr is not PdfName { Value: "Identity" or "Default" })
                {
                    Issue("TransferFunction", $"a graphics state sets a transfer function (/{key}), which PDF/X leaves to the printer; it is removed", () => state.Remove(key));
                }
            }

            if (state.ContainsKey("HTP"))
            {
                Issue("Halftone", "a graphics state sets a halftone phase (/HTP); it is removed", () => state.Remove("HTP"));
            }

            if (state.Get("HT")?.Resolve() is PdfDictionary halftone && halftone.ContainsKey("TransferFunction"))
            {
                Issue("Halftone", "a halftone has a transfer function; it is removed", () => halftone.Remove("TransferFunction"));
            }
        }
    }

    /// <summary>Annotations that print inside the bleed box are flattened into the page, as only printer marks and trap networks may be annotations there.</summary>
    private void Annotations()
    {
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            PdfRectangle bleed = page.BleedBox.Normalize();
            List<PdfAnnotation> inside = [.. page.Annotations.Where(a => a.Subtype is not ("PrinterMark" or "TrapNet" or "Popup" or "Link") && (a.Flags & PdfAnnotationFlags.Print) != 0
                && (a.Flags & PdfAnnotationFlags.Hidden) == 0 && !a.Rectangle.Normalize().Intersect(bleed).IsEmpty)];
            if (inside.Count > 0)
            {
                Issue("Annotations", string.Create(CultureInfo.InvariantCulture, $"{inside.Count} annotations print inside the bleed box; their appearances are flattened into the page"),
                    () => page.Annotations.Flatten(PdfFlattenFilter.All), i + 1);
            }
        }
    }

    /// <summary>JavaScript and actions that run on opening or events have no place in a print file.</summary>
    private void Actions()
    {
        PdfDictionary catalog = document.Catalog;
        if (catalog.Get<PdfDictionary>("Names") is PdfDictionary names && names.ContainsKey("JavaScript"))
        {
            Issue("Actions", "the document has JavaScript; it is removed", () => names.Remove("JavaScript"));
        }

        if (catalog.ContainsKey("AA"))
        {
            Issue("Actions", "the document has additional actions; they are removed", () => catalog.Remove("AA"));
        }

        if (catalog.Get("OpenAction")?.Resolve() is PdfDictionary open && open.GetName("S")?.Value is "JavaScript" or "Launch")
        {
            Issue("Actions", "the document runs an action when it opens; it is removed", () => catalog.Remove("OpenAction"));
        }
    }

    /// <summary>Every optional content configuration has a name, and none sets the automatic states (/AS) PDF/X leaves out.</summary>
    private void OptionalContent()
    {
        if (OldStyle || document.Catalog.Get<PdfDictionary>("OCProperties") is not PdfDictionary properties)
        {
            return;
        }

        List<PdfDictionary> configurations = [];
        if (properties.Get<PdfDictionary>("D") is PdfDictionary defaults)
        {
            configurations.Add(defaults);
        }

        configurations.AddRange((properties.Get<PdfArray>("Configs") ?? []).Select(c => c.Resolve()).OfType<PdfDictionary>());
        foreach (PdfDictionary configuration in configurations)
        {
            if (configuration.Get<PdfString>("Name") is null)
            {
                Issue("OptionalContent", "an optional content configuration has no name; one is written", () => configuration["Name"] = PdfString.FromText("Default"));
            }

            if (configuration.ContainsKey("AS"))
            {
                Issue("OptionalContent", "an optional content configuration sets automatic states (/AS); they are removed", () => configuration.Remove("AS"));
            }
        }
    }
}
