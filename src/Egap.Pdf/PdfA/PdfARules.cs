using Egami.Fonts;

using Egap.Pdf.Annotations;

namespace Egap.Pdf.PdfA;

/// <summary>
/// The PDF/A rules Egap enforces (Phase 5 design Q12, Q13), run in one of two modes over the same list: checking, which
/// changes nothing and says which issues conversion could fix, or fixing, which puts each fixable issue right as it is
/// found. The converter checks first and fixes only when nothing unfixable was found, so a failed conversion leaves the
/// document as it was; the pre-check is the checking run; the writer of new PDF/A documents is the fixing run, with fixes
/// that lose content (scripts, attachments, annotations removed) refused.
/// </summary>
internal sealed partial class PdfARules
{
    /// <summary>Actions PDF/A forbids (ISO 19005-1 6.6.1, ISO 19005-2 6.5.1), the same list for every part.</summary>
    private static readonly HashSet<string> ForbiddenActions = ["Launch", "Sound", "Movie", "ResetForm", "ImportData", "JavaScript", "Hide", "SetOCGState", "Rendition", "Trans", "GoTo3DView", "RichMediaExecute"];

    private static readonly HashSet<string> StandardFilters = ["ASCIIHexDecode", "ASCII85Decode", "LZWDecode", "FlateDecode", "RunLengthDecode", "CCITTFaxDecode", "JBIG2Decode", "DCTDecode", "JPXDecode", "Crypt",
        "AHx", "A85", "LZW", "Fl", "RL", "CCF", "DCT"];

    private static readonly HashSet<string> ForbiddenAnnotations = ["Movie", "Sound", "Screen", "3D", "RichMedia", "TrapNet", "PrinterMark"];

    private readonly PdfDocument document;
    private readonly PdfAConformance conformance;
    private readonly PdfAConvertOptions options;
    private readonly bool fixing;
    private readonly bool allowLossy;
    private readonly int part;
    private readonly List<PdfAIssue> issues = [];

    private PdfARules(PdfDocument document, PdfAConformance conformance, PdfAConvertOptions options, bool fixing, bool allowLossy)
    {
        this.document = document;
        this.conformance = conformance;
        this.options = options;
        this.fixing = fixing;
        this.allowLossy = allowLossy;
        part = PdfAWriter.Part(conformance);
    }

    /// <summary>Runs every rule; in fixing mode puts right what it can.</summary>
    public static List<PdfAIssue> Run(PdfDocument document, PdfAConformance conformance, PdfAConvertOptions options, bool fixing, bool allowLossy)
    {
        PdfARules rules = new(document, conformance, options, fixing, allowLossy);
        rules.RunAll();
        return rules.issues;
    }

    private void RunAll()
    {
        FileStructure();
        Metadata();
        Actions();
        Annotations();
        Forms();
        Attachments();
        OptionalContent();
        Signatures();
        Tagged();
        Objects();
        (List<PdfDictionary> resources, List<PdfStream> forms) = ResourceWalker.Walk(document);
        ContentStreams(forms);
        Limits();
        DeviceN(resources);
        if (part > 1)
        {
            Jpeg2000();
        }

        Fonts(resources, TextUse.Scan(document, forms));
        Graphics(resources, forms);
        Colors(resources, forms);
        Streams();
    }

    /// <summary>
    /// Records an issue: unfixable without <paramref name="fix"/>; with one, fixable, and in fixing mode put right now. A
    /// <paramref name="lossy"/> fix removes content and is refused where lossy fixes are not allowed.
    /// </summary>
    private void Issue(string clause, string description, Action? fix = null, int? page = null, bool lossy = false)
    {
        Noted(clause, description, fix is null ? null : () =>
        {
            fix();
            return null;
        }, page, lossy);
    }

    /// <summary>Records an issue whose fix says what it did, which then describes the fixed issue.</summary>
    private void Noted(string clause, string description, Func<string?>? fix, int? page = null, bool lossy = false)
    {
        bool fixable = fix is not null && (!lossy || allowLossy);
        if (fixable && fixing && fix!() is string note)
        {
            description = note;
        }

        issues.Add(new PdfAIssue(clause, description, page, fixable, fixable && fixing));
    }

    /// <summary>Encryption, version, catalog entries PDF/A does not allow.</summary>
    private void FileStructure()
    {
        // Once the document is under these rules, its saves write it unencrypted, as the part's version, without object
        // streams for PDF/A-1; those issues are settled.
        bool settled = document.WriterConformance == conformance;
        if (document.IsEncrypted && !settled)
        {
            // The save that follows a conversion writes the file without encryption.
            Issue("6.1.3", "the file is encrypted; it is written without encryption", () => { });
        }

        string target = PdfAWriter.Version(conformance);
        string version = document.Version;
        bool versionFits = part switch
        {
            1 => string.CompareOrdinal(version, "1.4") <= 0,
            4 => version == "2.0",
            _ => string.CompareOrdinal(version, "1.7") <= 0,
        };
        if (!versionFits && !settled)
        {
            Issue("6.1.2", $"the file is PDF {version}; it is written as PDF {target}", () => document.Catalog.Remove("Version"));
        }
        else if (document.Catalog.GetName("Version") is PdfName declared && declared.Value != target)
        {
            Issue("6.1.2", "the catalog declares another version; the declaration is removed", () => document.Catalog.Remove("Version"));
        }

        if (part == 1 && !settled && document.Xref.Entries.Any(e => e.Value.Kind == Parsing.XrefEntryKind.InObjectStream))
        {
            Issue("6.1.4", "PDF/A-1 does not allow object streams; objects are written on their own", () => { });
        }

        if (document.Catalog.ContainsKey("NeedsRendering"))
        {
            Issue("6.11", "the catalog asks for XFA rendering (/NeedsRendering); the request is removed", () => document.Catalog.Remove("NeedsRendering"));
        }

        if (document.Catalog.Get<PdfDictionary>("Perms") is PdfDictionary perms && perms.Entries.Any(e => e.Key.Value is not ("DocMDP" or "UR3")))
        {
            Issue("6.1.12", "the permissions dictionary has entries other than DocMDP and UR3; they are removed", () =>
            {
                foreach (PdfName key in perms.Entries.Select(e => e.Key).Where(k => k.Value is not ("DocMDP" or "UR3")).ToList())
                {
                    perms.Remove(key);
                }
            });
        }
    }

    /// <summary>The output intent, the XMP packet, its identification and its agreement with the Info dictionary.</summary>
    private void Metadata()
    {
        PdfArray? intents = document.Catalog.Get<PdfArray>("OutputIntents");
        List<PdfDictionary> pdfa = [.. (intents ?? []).Select(i => i.Resolve()).OfType<PdfDictionary>().Where(d => d.GetName("S")?.Value == "GTS_PDFA1")];
        if (pdfa.Count == 0)
        {
            string added = options.OutputIntentProfile is { } given ? $"the given {ColorScan.ProfileSpace(given.Span)} profile" : "sRGB";
            Issue("6.2.3", $"the file has no PDF/A output intent; one with {added} is added", () => PdfAWriter.AddOutputIntent(document, options.OutputIntentProfile, options.OutputCondition));
        }
        else
        {
            List<PdfStream?> profiles = [.. pdfa.Select(d => d.Get<PdfStream>("DestOutputProfile"))];
            if (profiles.Any(p => p is null) || profiles.Select(p => p!.Dictionary).Distinct(ReferenceEqualityComparer.Instance).Count() > 1)
            {
                Issue("6.2.3", "the PDF/A output intents lack a profile or name different ones; they are replaced by one sRGB intent", () =>
                {
                    foreach (PdfDictionary intent in pdfa)
                    {
                        intent["S"] = PdfName.Get("GTS_PDFX");
                    }

                    PdfAWriter.AddOutputIntent(document, options.OutputIntentProfile, options.OutputCondition);
                });
            }
        }

        PdfMetadata metadata = document.Metadata;
        if (document.Catalog.Get("Metadata") is not null && !metadata.HasXmp)
        {
            Issue("6.6.2", "the XMP metadata does not parse; it is written again from the document information", () =>
            {
                document.Catalog.Remove("Metadata");
                metadata.CreateXmp();
            });
        }

        // PDF/A-4 drops the Info dictionary below, so its values are carried into the XMP first, as for the other parts.
        foreach (string key in metadata.InfoXmpMismatches())
        {
            Issue("6.6.3", $"the document information's {key} has no equal in the XMP metadata; the XMP is set from it", metadata.SyncXmpFromInfo);
        }

        if (part == 4 && document.Trailer.Get<PdfDictionary>("Info") is PdfDictionary info
            && (!document.Catalog.ContainsKey("PieceInfo") || info.Entries.Any(e => e.Key.Value != "ModDate")))
        {
            // ISO 19005-4 6.1.3: no Info dictionary, or only its ModDate when the catalog has PieceInfo.
            Issue("6.1.3", "PDF/A-4 allows no document information dictionary beyond ModDate; its values stay in the XMP metadata", () =>
            {
                if (document.Catalog.ContainsKey("PieceInfo") && info.Get("ModDate") is PdfObject modified)
                {
                    document.Trailer["Info"] = document.Objects.Add(new PdfDictionary { ["ModDate"] = modified });
                }
                else
                {
                    document.Trailer.Remove("Info");
                }
            });
        }

        if (PdfAWriter.Read(document) != conformance || (part == 4 && metadata.GetPdfAId().Revision is null))
        {
            Issue("6.6.4", $"the XMP metadata does not identify the file as {PdfAWriter.Name(conformance)}; the identification is written",
                () => metadata.SetPdfAId(part, PdfAWriter.Level(conformance), part == 4 ? "2020" : null));
        }

        List<string> xmp = metadata.XmpPdfAProblems();
        if (xmp.Count > 0)
        {
            string shown = string.Join("; ", xmp.Take(5)) + (xmp.Count > 5 ? $"; and {xmp.Count - 5} more" : "");
            Issue("6.6.2.3", $"the XMP metadata breaks PDF/A's rules ({shown}); undeclared properties are removed and the rest put in form", metadata.FixXmpForPdfA);
        }

        // Metadata of pages, images and forms follows the same rules (ISO 19005-2 6.6.2.1).
        PdfStream? own = document.Catalog.Get<PdfStream>("Metadata");
        foreach (PdfReference reference in document.Objects)
        {
            if (document.Objects[reference] is PdfStream other && !ReferenceEquals(other, own) && other.Dictionary.GetName("Type")?.Value == "Metadata")
            {
                byte[]? bytes = TryDecode(other);
                global::Egap.Pdf.Metadata.XmpPacket? packet = bytes is null ? null : global::Egap.Pdf.Metadata.XmpPacket.Parse(bytes);
                List<string> problems = packet is null ? ["it does not parse"] : global::Egap.Pdf.Metadata.XmpPdfA.Problems(packet);
                if (problems.Count > 0 || other.Filters.Count > 0)
                {
                    Issue("6.6.2.3", $"object metadata breaks PDF/A's rules ({(problems.Count > 0 ? string.Join("; ", problems.Take(3)) : "it is compressed")}); it is put right", () =>
                    {
                        if (packet is null)
                        {
                            other.Dictionary.Remove("DecodeParms");
                            other.SetDecodedBytes(global::Egap.Pdf.Metadata.XmpPacket.Create().ToBytes(), compress: false);
                            return;
                        }

                        global::Egap.Pdf.Metadata.XmpPdfA.Fix(packet);
                        other.Dictionary.Remove("DecodeParms");
                        other.SetDecodedBytes(packet.ToBytes(), compress: false);
                    });
                }
            }
        }

        if (document.Catalog.Get<PdfStream>("Metadata") is PdfStream stream && stream.Filters.Count > 0)
        {
            Issue("6.6.2.1", "the XMP metadata stream is compressed; it is written uncompressed", () =>
            {
                byte[] bytes = stream.GetDecodedBytes();
                stream.Dictionary.Remove("DecodeParms");
                stream.SetDecodedBytes(bytes, compress: false);
            });
        }
    }

    /// <summary>JavaScript, additional actions and forbidden actions anywhere: catalog, outline, pages, annotations, fields.</summary>
    private void Actions()
    {
        PdfDictionary catalog = document.Catalog;
        if (catalog.Get<PdfDictionary>("Names") is PdfDictionary names && names.ContainsKey("JavaScript"))
        {
            Issue("6.6.1", "the document has JavaScript; it is removed", () => names.Remove("JavaScript"), lossy: true);
        }

        AdditionalActions(catalog, "the document", null);
        if (catalog.Get("OpenAction")?.Resolve() is PdfDictionary open && Forbidden(open) is string kind)
        {
            Issue("6.6.1", $"the document opens with a {kind} action; it is removed", () => catalog.Remove("OpenAction"), lossy: true);
        }

        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Stack<PdfDictionary?> outline = new([catalog.Get<PdfDictionary>("Outlines")?.Get<PdfDictionary>("First")]);
        int removed = 0;
        List<PdfDictionary> bad = [];
        while (outline.TryPop(out PdfDictionary? item) && seen.Count < 1_000_000)
        {
            if (item is null || !seen.Add(item))
            {
                continue;
            }

            if (item.Get("A")?.Resolve() is PdfDictionary action && Forbidden(action) is not null)
            {
                bad.Add(item);
                removed++;
            }

            outline.Push(item.Get<PdfDictionary>("Next"));
            outline.Push(item.Get<PdfDictionary>("First"));
        }

        if (removed > 0)
        {
            Issue("6.6.1", $"{removed} bookmarks run forbidden actions; the actions are removed", () => bad.ForEach(b => b.Remove("A")), lossy: true);
        }

        for (int i = 0; i < document.Pages.Count; i++)
        {
            AdditionalActions(document.Pages[i].Dictionary, "the page", i + 1);
        }
    }

    private void AdditionalActions(PdfDictionary dictionary, string what, int? page)
    {
        if (dictionary.ContainsKey("AA"))
        {
            Issue("6.6.2", $"{what} has additional actions; they are removed", () => dictionary.Remove("AA"), page, lossy: true);
        }
    }

    /// <summary>The first forbidden action kind in an action and its <c>/Next</c> chain, or null.</summary>
    private static string? Forbidden(PdfDictionary action)
    {
        HashSet<string> forbidden = ForbiddenActions;
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Queue<PdfDictionary> queue = new([action]);
        while (queue.TryDequeue(out PdfDictionary? next) && seen.Count < 1000)
        {
            if (!seen.Add(next))
            {
                continue;
            }

            string? kind = next.GetName("S")?.Value;
            if (kind is null || forbidden.Contains(kind) || (kind == "Named" && next.GetName("N")?.Value is not ("NextPage" or "PrevPage" or "FirstPage" or "LastPage")))
            {
                return kind ?? "unnamed";
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

        return null;
    }

    /// <summary>
    /// Annotations (ISO 19005-2 6.3): no forbidden kinds, printed and never hidden, a normal appearance (drawn where Egap
    /// can draw one, empty otherwise) and nothing else in the appearance dictionary, no forbidden actions.
    /// </summary>
    private void Annotations()
    {
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            int number = i + 1;
            foreach (PdfAnnotation annotation in page.Annotations.ToList())
            {
                PdfDictionary dictionary = annotation.Dictionary;
                string subtype = dictionary.GetName("Subtype")?.Value ?? "(no subtype)";
                if (ForbiddenAnnotations.Contains(subtype) || (part == 1 && subtype == "FileAttachment"))
                {
                    Issue("6.3.1", $"a {subtype} annotation is not allowed; it is removed", () => page.Annotations.Remove(annotation), number, lossy: true);
                    continue;
                }

                // A popup may leave its flags out; when it gives them, they follow the same rule.
                if (subtype != "Popup" || dictionary.ContainsKey("F"))
                {
                    PdfAnnotationFlags flags = annotation.Flags;
                    PdfAnnotationFlags wanted = (flags | PdfAnnotationFlags.Print) & ~(PdfAnnotationFlags.Hidden | PdfAnnotationFlags.Invisible | PdfAnnotationFlags.NoView | PdfAnnotationFlags.ToggleNoView);
                    if (flags != wanted)
                    {
                        Issue("6.3.2", $"a {subtype} annotation is hidden or not printed; it is set to print and show", () => dictionary["F"] = new PdfInteger((int)wanted), number);
                    }
                }

                AdditionalActions(dictionary, $"a {subtype} annotation", number);
                if (dictionary.Get("A")?.Resolve() is PdfDictionary action && Forbidden(action) is string kind)
                {
                    Issue("6.6.1", $"a {subtype} annotation runs a {kind} action; the action is removed", () => dictionary.Remove("A"), number, lossy: true);
                }

                PdfRectangle box = annotation.Rectangle;
                bool needsAppearance = subtype is not ("Popup" or "Link") && box.Width > 0 && box.Height > 0;
                if (needsAppearance && dictionary.Get<PdfDictionary>("AP")?.Get("N") is null)
                {
                    Issue("6.3.3", $"a {subtype} annotation has no appearance; one is drawn", () => Appearance(annotation, box), number);
                }

                // A button's normal appearance is a dictionary of states (ISO 19005-2 6.3.3), the state chosen by /AS.
                string? field = dictionary.GetName("FT")?.Value ?? dictionary.Get<PdfDictionary>("Parent")?.GetName("FT")?.Value;
                if (subtype == "Widget" && field == "Btn" && dictionary.Get<PdfDictionary>("AP") is PdfDictionary buttons && buttons.Get("N")?.Resolve() is PdfStream)
                {
                    Issue("6.3.3", "a button's appearance is not a dictionary of states; it becomes one", () =>
                    {
                        // The one appearance is the button's on state; a check box or radio button also gets an empty off state.
                        long flags = dictionary.GetInt("Ff") ?? dictionary.Get<PdfDictionary>("Parent")?.GetInt("Ff") ?? 0;
                        bool push = (flags & (1 << 16)) != 0;
                        PdfName on = dictionary.GetName("AS") is { Value: not "Off" } shown ? shown : PdfName.Get("On");
                        PdfDictionary states = new() { [on] = buttons["N"]! };
                        if (!push)
                        {
                            PdfStream off = new(new PdfDictionary
                            {
                                ["Type"] = PdfName.Get("XObject"),
                                ["Subtype"] = PdfName.Get("Form"),
                                ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfReal(box.Width), new PdfReal(box.Height) },
                            });
                            off.SetDecodedBytes([]);
                            states["Off"] = document.Objects.Add(off);
                        }

                        buttons["N"] = states;
                        dictionary["AS"] = dictionary.GetName("AS") is PdfName current && states.ContainsKey(current.Value) ? current : on;
                    }, number);
                }

                if (dictionary.Get<PdfDictionary>("AP") is PdfDictionary appearance && (appearance.ContainsKey("D") || appearance.ContainsKey("R")))
                {
                    Issue("6.3.3", $"a {subtype} annotation has down or rollover appearances; they are removed", () =>
                    {
                        appearance.Remove("D");
                        appearance.Remove("R");
                    }, number);
                }
            }
        }
    }

    private void Appearance(PdfAnnotation annotation, PdfRectangle box)
    {
        if (AppearanceBuilder.Update(annotation, document))
        {
            return;
        }

        PdfStream empty = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfArray { new PdfInteger(0), new PdfInteger(0), new PdfReal(box.Width), new PdfReal(box.Height) },
        });
        empty.SetDecodedBytes([]);
        annotation.Dictionary["AP"] = new PdfDictionary { ["N"] = document.Objects.Add(empty) };
    }

    /// <summary>The interactive form: appearances drawn rather than left to viewers, no XFA.</summary>
    private void Forms()
    {
        if (document.Catalog.Get<PdfDictionary>("AcroForm") is not PdfDictionary form)
        {
            return;
        }

        if (form.GetBool("NeedAppearances") == true)
        {
            Issue("6.4.1", "the form asks viewers to draw field appearances; Egap draws them", () =>
            {
                form.Remove("NeedAppearances");
                foreach (PdfField field in document.Form.GetTerminalFields())
                {
                    field.UpdateAppearances();
                }

                form.Remove("NeedAppearances");
            });
        }

        if (form.ContainsKey("XFA"))
        {
            if (form.Get<PdfArray>("Fields") is { Count: > 0 })
            {
                Issue("6.4.2", "the form has an XFA part beside its fields; the XFA part is removed", () => form.Remove("XFA"), lossy: true);
            }
            else
            {
                Issue("6.4.2", "the form is XFA only, which PDF/A does not allow");
            }
        }
    }

    /// <summary>
    /// Attachments: none in PDF/A-1; PDF/A files only in PDF/A-2 and PDF/A-4; in PDF/A-3 and 4f any file, each with a
    /// relationship, a MIME type and a place in the catalog's associated files (ISO 19005-3 6.8).
    /// </summary>
    private void Attachments()
    {
        List<PdfAttachment> attachments = [.. document.Attachments];
        if (conformance == PdfAConformance.PdfA4f && attachments.Count == 0)
        {
            Issue("6.9", "a PDF/A-4f file must carry at least one attachment; use PDF/A-4 without one");
        }

        if (attachments.Count == 0)
        {
            return;
        }

        if (part == 1)
        {
            Issue("6.1.11", "PDF/A-1 does not allow attachments; use PDF/A-3b");
            return;
        }

        bool anyKind = part == 3 || conformance == PdfAConformance.PdfA4f;
        PdfArray associated = document.Catalog.Get<PdfArray>("AF") ?? [];
        foreach (PdfAttachment attachment in attachments)
        {
            if (!anyKind && !IsPdfA(attachment))
            {
                Issue("6.8", $"the attachment {attachment.Name} is not a PDF/A file; use PDF/A-3b, 3u or 4f");
                continue;
            }

            if (part < 3)
            {
                continue;
            }

            PdfDictionary spec = attachment.FileSpecification;
            if (!spec.ContainsKey("AFRelationship"))
            {
                Issue("6.8", $"the attachment {attachment.Name} has no relationship; it is given Unspecified", () => spec["AFRelationship"] = PdfName.Get("Unspecified"));
            }

            if (attachment.Stream is PdfStream stream && !stream.Dictionary.ContainsKey("Subtype"))
            {
                Issue("6.8", $"the attachment {attachment.Name} has no MIME type; it is given application/octet-stream", () => stream.Dictionary["Subtype"] = PdfName.Get("application/octet-stream"));
            }

            if (attachment.Reference is PdfReference reference && !associated.Any(a => a is PdfReference r && r.ObjectNumber == reference.ObjectNumber))
            {
                Issue("6.8", $"the attachment {attachment.Name} is not among the document's associated files; it is added", () =>
                {
                    associated.Add(reference);
                    document.Catalog["AF"] = associated;
                });
            }
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
            return PdfAWriter.Read(embedded) != PdfAConformance.None;
        }
        catch (PdfException)
        {
            return false;
        }
    }

    /// <summary>Layers: none in PDF/A-1; otherwise every configuration named and without automatic states.</summary>
    private void OptionalContent()
    {
        if (document.Catalog.Get<PdfDictionary>("OCProperties") is not PdfDictionary properties)
        {
            return;
        }

        if (part == 1)
        {
            Issue("6.1.13", "PDF/A-1 does not allow optional content (layers); use PDF/A-2b or later");
            return;
        }

        List<PdfDictionary> configurations = [.. new[] { properties.Get<PdfDictionary>("D") }.Concat((properties.Get<PdfArray>("Configs") ?? []).Select(c => c.Resolve() as PdfDictionary)).OfType<PdfDictionary>()];
        int index = 0;
        foreach (PdfDictionary configuration in configurations)
        {
            int n = ++index;
            if (configuration.Get("Name") is null)
            {
                Issue("6.9", "a layer configuration has no name; it is named", () => configuration["Name"] = PdfString.FromText(n == 1 ? "Default" : "Configuration " + n));
            }

            if (configuration.ContainsKey("AS"))
            {
                Issue("6.9", "a layer configuration sets layers automatically (/AS); the entry is removed", () => configuration.Remove("AS"));
            }

            // A configuration's /Order, when present, lists every layer (ISO 19005-2 6.9).
            if (configuration.Get<PdfArray>("Order") is PdfArray order && properties.Get<PdfArray>("OCGs") is PdfArray groups)
            {
                HashSet<int> listed = [.. Flatten(order).OfType<PdfReference>().Select(r => r.ObjectNumber)];
                List<PdfReference> missing = [.. groups.OfType<PdfReference>().Where(g => !listed.Contains(g.ObjectNumber))];
                if (missing.Count > 0)
                {
                    Issue("6.9", $"a layer configuration's order leaves out {missing.Count} layers; they are added", () => missing.ForEach(order.Add));
                }
            }
        }
    }

    private static IEnumerable<PdfObject> Flatten(PdfArray array, int depth = 0)
    {
        foreach (PdfObject item in array)
        {
            if (item.Resolve() is PdfArray nested && depth < 32)
            {
                foreach (PdfObject inner in Flatten(nested, depth + 1))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return item;
            }
        }
    }

    /// <summary>Every font embedded: the standard 14, system fonts and, when allowed, substitutes; others are unmet.</summary>
    private void Fonts(List<PdfDictionary> resources, Dictionary<PdfDictionary, HashSet<int>> used)
    {
        // A program several fonts share takes one set of advances, so it is only rewritten for a font that has it alone.
        List<PdfDictionary> fonts = ResourceWalker.Fonts(resources);
        Dictionary<PdfStream, int> programUsers = new(ReferenceEqualityComparer.Instance);
        foreach (PdfDictionary font in fonts)
        {
            if (EmbeddedWidths.Program(font) is PdfStream program)
            {
                programUsers[program] = programUsers.GetValueOrDefault(program) + 1;
            }
        }

        foreach (PdfDictionary font in fonts)
        {
            string subtype = font.GetName("Subtype")?.Value ?? "";
            string name = font.GetName("BaseFont")?.Value ?? "(unnamed)";
            HashSet<int> codes = used.GetValueOrDefault(font) ?? [];
            if (conformance is PdfAConformance.PdfA2u or PdfAConformance.PdfA3u or PdfAConformance.PdfA2a or PdfAConformance.PdfA3a)
            {
                Unicode(font, name, codes);
            }

            if (font.GetName("Type")?.Value != "Font")
            {
                Issue("6.2.11.2", $"the font {name} does not say it is a font (/Type /Font); it is written", () => font["Type"] = PdfName.Get("Font"));
            }

            if (subtype == "Type3")
            {
                PdfFont type3 = PdfFont.FromDictionary(font);
                PdfDictionary? procedures = font.Get<PdfDictionary>("CharProcs");
                int lacking = codes.Count(c => type3.GetGlyphName(c) is not string glyph || procedures?.Get(glyph) is null);
                if (lacking > 0)
                {
                    Issue("6.2.11.4.1", $"the Type 3 font {name} shows {lacking} codes it has no glyph procedure for");
                }

                continue;
            }

            if (!StandardFontEmbedder.IsEmbedded(font))
            {
                if (StandardFontEmbedder.PlanFor(font, options) is not StandardFontEmbedder.Plan plan)
                {
                    Issue("6.2.11.4", $"the font {name} is not embedded and no program for it was found" + (options.AllowFontSubstitution ? "" : " (AllowFontSubstitution would use a bundled substitute)"));
                }
                else if (codes.Except(StandardFontEmbedder.Codes(font, plan).Keys).Count() is int lacking and > 0)
                {
                    Issue("6.2.11.4.1", $"the font {name} is not embedded and {plan.Face.FullName}, its stand-in, lacks {lacking} of the characters it shows");
                }
                else
                {
                    Noted("6.2.11.4", $"the font {name} is not embedded; it is embedded", () => StandardFontEmbedder.TryEmbed(document, font, options) is string done ? $"the font {name} was not embedded; {done}" : null);
                }

                continue;
            }

            if (PdfFont.FromDictionary(font).GetTypeface() is null)
            {
                Issue("6.2.11.4", $"the font {name}'s embedded program cannot be read");
                continue;
            }

            if (subtype == "TrueType" && font.Get("Encoding") is not null && (PdfFont.FromDictionary(font).Flags & PdfFontFlags.Symbolic) != 0
                && (PdfFont.FromDictionary(font).Flags & PdfFontFlags.Nonsymbolic) == 0)
            {
                Issue("6.2.11.6", $"the symbolic TrueType font {name} has an encoding, which PDF/A does not allow");
            }

            if (EmbeddedWidths.Missing(font, codes) is int missing and > 0)
            {
                Issue("6.2.11.4.1", $"the font {name} shows {missing} codes its embedded program has no glyph for");
            }

            if (EmbeddedWidths.Mismatches(font, codes) is not { } mismatches)
            {
                Issue("6.2.11.5", $"the font {name} gives one glyph different widths, which its program cannot agree with");
            }
            else if (mismatches.Count > 0 && EmbeddedWidths.Program(font) is PdfStream shared && programUsers.GetValueOrDefault(shared) > 1)
            {
                Issue("6.2.11.5", $"the font {name}'s program disagrees with its widths for {mismatches.Count} glyphs, and other fonts share the program");
            }
            else if (mismatches.Count > 0 && EmbeddedWidths.CanFix(font) && !EmbeddedWidths.Achievable(font, mismatches))
            {
                Issue("6.2.11.5", $"the font {name}'s program disagrees with its widths for {mismatches.Count} glyphs, and its units per em are too coarse to hold them");
            }
            else if (mismatches.Count > 0 && !EmbeddedWidths.CanFix(font))
            {
                Issue("6.2.11.5", $"the font {name}'s program disagrees with its widths for {mismatches.Count} glyphs, and Egap rewrites only well-formed TrueType programs");
            }
            else if (mismatches.Count > 0)
            {
                Issue("6.2.11.5", $"the font {name}'s program disagrees with its widths for {mismatches.Count} glyphs; the program's advances are set to the widths", () => EmbeddedWidths.Fix(font, mismatches));
            }

            // A Type 2 CIDFont names its CID to glyph map (ISO 32000-1 Table 117); the default it leaves out is Identity.
            if (subtype == "Type0" && font.Get<PdfArray>("DescendantFonts")?.Get<PdfDictionary>(0) is PdfDictionary cid
                && cid.GetName("Subtype")?.Value == "CIDFontType2" && cid.Get("CIDToGIDMap") is null)
            {
                Issue("6.2.11.3.2", $"the font {name} does not name its CID to glyph map; Identity, its default, is written", () => cid["CIDToGIDMap"] = PdfName.Get("Identity"));
            }

            if (part > 1 && FontDescriptor(font) is PdfDictionary descriptor)
            {
                if (descriptor.ContainsKey("CIDSet"))
                {
                    Issue("6.2.11.4.2", $"the font {name} lists its glyphs (/CIDSet), which must then be complete; the list is removed", () => descriptor.Remove("CIDSet"));
                }

                if (descriptor.ContainsKey("CharSet"))
                {
                    Issue("6.2.11.4.2", $"the font {name} lists its glyph names (/CharSet), which must then be complete; the list is removed", () => descriptor.Remove("CharSet"));
                }
            }

            // PDF/A-1 asks a subset CIDFont to list the CIDs its program holds (ISO 19005-1 6.3.5); files made for other levels
            // leave the list out.
            if (part == 1 && subtype == "Type0" && PdfFont.FromDictionary(font).IsSubset && FontDescriptor(font) is PdfDictionary listed
                && listed.Get("CIDSet") is null)
            {
                if (CidSet(font, codes) is byte[] bits)
                {
                    Issue("6.3.5", $"the subset font {name} does not list its CIDs (/CIDSet); the list is written", () =>
                    {
                        PdfStream set = new();
                        set.SetDecodedBytes(bits);
                        listed["CIDSet"] = document.Objects.Add(set);
                    });
                }
                else
                {
                    Issue("6.3.5", $"the subset font {name} does not list its CIDs (/CIDSet), and its program cannot be read to write the list");
                }
            }
        }
    }

    /// <summary>
    /// Level u: every shown code maps to Unicode (ISO 19005-2 6.2.11.7), through ToUnicode or what readers derive (glyph
    /// names, the program's cmap). A font whose codes all resolve but whose ToUnicode is missing or incomplete gets one
    /// written; codes nothing resolves are unmet.
    /// </summary>
    private void Unicode(PdfDictionary font, string name, HashSet<int> codes)
    {
        PdfFont parsed = PdfFont.FromDictionary(font);
        Dictionary<int, string?> texts = codes.ToDictionary(c => c, c => parsed.ToUnicode(c) is { Length: > 0 } t && t is not ("\0" or "\uFEFF" or "\uFFFE") ? t : null);
        int unresolved = texts.Count(t => t.Value is null);
        if (unresolved > 0)
        {
            Issue("6.2.11.7", $"the font {name} shows {unresolved} codes that map to no Unicode text, which level u needs");
            return;
        }

        // Only a ToUnicode stream says for certain; anything else is the reader's guess, so a stream is written.
        bool complete = font.Get<PdfStream>("ToUnicode") is not null && texts.Keys.All(parsed.HasToUnicode);
        bool identity = parsed.IsComposite && font.GetName("Encoding")?.Value is "Identity-H" or "Identity-V";
        if (complete || texts.Count == 0)
        {
            return;
        }

        if (parsed.IsComposite && !identity)
        {
            Issue("6.2.11.7", $"the composite font {name} has no complete ToUnicode map, and Egap writes one only for Identity encodings");
            return;
        }

        Issue("6.2.11.7", $"the font {name} has no complete ToUnicode map; one is written", () =>
        {
            Dictionary<int, string> map = texts.ToDictionary(t => t.Key, t => t.Value!);
            if (!parsed.IsComposite)
            {
                // Every code with a text, used or not, so later edits keep their text too.
                for (int code = 0; code < 256; code++)
                {
                    if (!map.ContainsKey(code) && parsed.ToUnicode(code) is { Length: > 0 } text)
                    {
                        map[code] = text;
                    }
                }
            }

            font["ToUnicode"] = StandardFontEmbedder.ToUnicode(document, map, parsed.IsComposite ? 2 : 1);
            PdfFont.Forget(font);
        });
    }

    /// <summary>
    /// A CIDSet for a Type0 font: one bit per CID whose glyph its program holds, the high bit first. A glyph is held when it
    /// has an outline, is shown, or is .notdef. Null when the program or its CID to glyph map cannot be read.
    /// </summary>
    private static byte[]? CidSet(PdfDictionary font, HashSet<int> codes)
    {
        PdfFont parsed = PdfFont.FromDictionary(font);
        PdfDictionary? cid = font.Get<PdfArray>("DescendantFonts")?.Get<PdfDictionary>(0);
        if (parsed.GetTypeface() is not Typeface face || cid is null)
        {
            return null;
        }

        HashSet<ushort> held = [0];
        for (int glyph = 1; glyph < face.GlyphCount; glyph++)
        {
            if (!face.GetGlyphExtents((ushort)glyph).IsEmpty)
            {
                held.Add((ushort)glyph);
            }
        }

        foreach (int code in codes)
        {
            if (parsed.GetGlyphId(code) is ushort glyph && glyph < face.GlyphCount)
            {
                held.Add(glyph);
            }
        }

        // A TrueType CIDFont maps CIDs to glyphs by Identity or a stream of two-byte glyph ids (ISO 32000-1 Table 117); a CFF
        // one by its charset when it is CID-keyed, else CID and glyph id are the same.
        List<int> cids = [];
        if (cid.GetName("Subtype")?.Value == "CIDFontType2" && cid.Get("CIDToGIDMap")?.Resolve() is PdfStream map)
        {
            byte[] glyphs = map.GetDecodedBytes();
            for (int c = 0; c + 1 < glyphs.Length; c += 2)
            {
                if ((c == 0 || glyphs[c] != 0 || glyphs[c + 1] != 0) && held.Contains((ushort)((glyphs[c] << 8) | glyphs[c + 1])))
                {
                    cids.Add(c / 2);
                }
            }
        }
        else if (cid.GetName("Subtype")?.Value == "CIDFontType2" && cid.Get("CIDToGIDMap")?.Resolve() is PdfObject other && other is not PdfName { Value: "Identity" })
        {
            return null;
        }
        else
        {
            cids.AddRange(held.Select(g => face.GetCid(g) is int c and >= 0 ? c : g));
        }

        byte[] bits = new byte[(cids.Max() / 8) + 1];
        foreach (int c in cids)
        {
            bits[c / 8] |= (byte)(0x80 >> (c % 8));
        }

        return bits;
    }

    private static PdfDictionary? FontDescriptor(PdfDictionary font)
    {
        return font.Get<PdfDictionary>("FontDescriptor") ?? font.Get<PdfArray>("DescendantFonts")?.Get<PdfDictionary>(0)?.Get<PdfDictionary>("FontDescriptor");
    }

    /// <summary>
    /// Graphics states and images: transparency only from PDF/A-2 on; no transfer functions or halftone overrides; images
    /// not interpolated and without alternates or OPI; no PostScript; no JPEG 2000 in PDF/A-1.
    /// </summary>
    private void Graphics(List<PdfDictionary> resources, List<PdfStream> forms)
    {
        foreach (PdfDictionary resource in resources)
        {
            foreach (PdfObject value in ResourceWalker.Values(resource.Get<PdfDictionary>("ExtGState")))
            {
                if (value is not PdfDictionary state)
                {
                    continue;
                }

                if (part == 1)
                {
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
                        Issue("6.4", "PDF/A-1 does not allow transparency (soft masks, opacity below 1 or blend modes); use PDF/A-2b or later");
                    }
                }

                if (state.ContainsKey("TR") || (state.Get("TR2")?.Resolve() is PdfObject tr2 && tr2 is not PdfName { Value: "Default" }))
                {
                    Issue("6.2.5", "a graphics state sets a transfer function; it is removed", () =>
                    {
                        state.Remove("TR");
                        state.Remove("TR2");
                    });
                }

                if (state.GetInt("OPM") == 1)
                {
                    // ISO 19005-2 6.2.4.2 forbids overprint mode 1 with ICC-based CMYK and overprinting; mode 0 always passes.
                    Issue("6.2.4.2", "a graphics state sets overprint mode 1; it is set to 0", () => state["OPM"] = new PdfInteger(0));
                }

                if (state.ContainsKey("HTP") || state.ContainsKey("HTO"))
                {
                    Issue("6.2.5", "a graphics state sets halftone origins; they are removed", () =>
                    {
                        state.Remove("HTP");
                        state.Remove("HTO");
                    });
                }
            }

            foreach (PdfObject value in ResourceWalker.Values(resource.Get<PdfDictionary>("XObject")))
            {
                if (value is not PdfStream { Dictionary: var x })
                {
                    continue;
                }

                string? subtype = x.GetName("Subtype")?.Value;
                if (subtype == "PS" || x.GetName("Subtype2")?.Value == "PS")
                {
                    Issue("6.2.9", "the file has a PostScript XObject, which PDF/A does not allow");
                }

                if (x.ContainsKey("OPI"))
                {
                    Issue("6.2.9", "an XObject carries OPI data; it is removed", () => x.Remove("OPI"));
                }

                if (subtype == "Form" && x.ContainsKey("Ref"))
                {
                    Issue("6.2.9", "a form XObject refers to another file (/Ref); the reference is removed", () => x.Remove("Ref"));
                }

                if (subtype != "Image")
                {
                    continue;
                }

                if (part == 1)
                {
                    if (x.ContainsKey("SMask") || (x.GetInt("SMaskInData") ?? 0) != 0)
                    {
                        Issue("6.4", "PDF/A-1 does not allow images with soft masks (alpha); use PDF/A-2b or later");
                    }

                    if (FilterNames(x).Contains("JPXDecode"))
                    {
                        Issue("6.1.10", "PDF/A-1 does not allow JPEG 2000 images; use PDF/A-2b or later");
                    }
                }
            }
        }

        // Every image, soft masks included, wherever it is reached from.
        foreach (PdfReference reference in document.Objects)
        {
            if (document.Objects[reference] is PdfStream { Dictionary: var image } && image.GetName("Subtype")?.Value == "Image")
            {
                if (image.GetBool("Interpolate") == true)
                {
                    Issue("6.2.8", "an image asks for interpolation; it is turned off", () => image.Remove("Interpolate"));
                }

                if (image.ContainsKey("Alternates"))
                {
                    Issue("6.2.8", "an image has alternate images; they are removed", () => image.Remove("Alternates"));
                }
            }
        }

        InlineInterpolation(forms);

        static bool TransparencyGroup(PdfDictionary dictionary) => dictionary.Get<PdfDictionary>("Group")?.GetName("S")?.Value == "Transparency";
        if (part == 1 && (forms.Any(f => TransparencyGroup(f.Dictionary)) || Enumerable.Range(0, document.Pages.Count).Any(i => TransparencyGroup(document.Pages[i].Dictionary))))
        {
            Issue("6.4", "PDF/A-1 does not allow transparency groups; use PDF/A-2b or later");
        }
    }

    /// <summary>Inline images asking for interpolation (<c>/I true</c>) in pages and form XObjects; the content is written again without it.</summary>
    private void InlineInterpolation(List<PdfStream> forms)
    {
        static bool Interpolates(PdfContent content) => content.OfType<PdfInlineImage>().Any(i => i.Parameters.GetBool("I") == true || i.Parameters.GetBool("Interpolate") == true);
        static void TurnOff(PdfContent content)
        {
            // Each image is replaced, not edited in place, so the content is written from its operations again.
            for (int i = 0; i < content.Count; i++)
            {
                if (content[i] is PdfInlineImage image && (image.Parameters.GetBool("I") == true || image.Parameters.GetBool("Interpolate") == true))
                {
                    PdfDictionary parameters = image.Parameters.Clone();
                    parameters.Remove("I");
                    parameters.Remove("Interpolate");
                    content[i] = new PdfInlineImage(parameters, image.Data);
                }
            }
        }

        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            if (page.Dictionary.Get("Contents") is not null && TryParse(page.GetContent) is PdfContent content && Interpolates(content))
            {
                Issue("6.2.8", "an inline image asks for interpolation; it is turned off", () =>
                {
                    TurnOff(content);
                    page.SetContent(content);
                }, i + 1);
            }
        }

        foreach (PdfStream form in forms)
        {
            if (TryParse(() => PdfContent.Parse(form.GetDecodedBytes())) is PdfContent content && Interpolates(content))
            {
                Issue("6.2.8", "an inline image in a form asks for interpolation; it is turned off", () =>
                {
                    TurnOff(content);
                    form.SetDecodedBytes(content.ToBytes());
                });
            }
        }
    }

    private static byte[]? TryDecode(PdfStream stream)
    {
        try
        {
            return stream.GetDecodedBytes();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return null;
        }
    }

    private static PdfContent? TryParse(Func<PdfContent> parse)
    {
        try
        {
            return parse();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return null;
        }
    }

    private static IEnumerable<string> FilterNames(PdfDictionary dictionary)
    {
        return dictionary.Get("Filter")?.Resolve() switch
        {
            PdfName name => [name.Value],
            PdfArray array => array.Select(f => f.Resolve()).OfType<PdfName>().Select(n => n.Value),
            _ => [],
        };
    }

    /// <summary>Device colours the output intent covers (ISO 19005-2 6.2.4.3): RGB with an RGB intent, CMYK with a CMYK one.</summary>
    private void Colors(List<PdfDictionary> resources, List<PdfStream> forms)
    {
        string intent = ColorScan.IntentSpace(document) ?? (options.OutputIntentProfile is { } given ? ColorScan.ProfileSpace(given.Span) : "RGB");
        ColorScan scan = new(document);
        scan.Run(forms);
        if (scan.UsesCmyk && intent != "CMYK")
        {
            Issue("6.2.4.3", $"the content uses DeviceCMYK colours and the output intent is {intent}; pass a CMYK profile as PdfAConvertOptions.OutputIntentProfile");
        }

        if (scan.UsesRgb && intent != "RGB")
        {
            Issue("6.2.4.3", $"the content uses DeviceRGB colours and the output intent is {intent}");
        }

        _ = resources;
    }

    /// <summary>LZW is not allowed (ISO 19005-2 6.1.7.2): such streams are decoded and written again with Flate.</summary>
    private void Streams()
    {
        int count = 0;
        List<PdfStream> lzw = [];
        foreach (PdfReference reference in document.Objects)
        {
            // LZW, and filters ISO 32000-1 does not define (such as Brotli), are written again with Flate.
            if (document.Objects[reference] is PdfStream stream && stream.Filters.Any(f => f.Value is "LZWDecode" or "LZW" || !StandardFilters.Contains(f.Value)))
            {
                // A stream that does not decode (a filter Egap lacks, or broken data) cannot be written again.
                if (stream.UndecodedFilter is null && TryDecode(stream) is not null)
                {
                    lzw.Add(stream);
                }
                else
                {
                    count++;
                }
            }
        }

        if (lzw.Count > 0)
        {
            Issue("6.1.7.2", $"{lzw.Count} streams use LZW or a filter PDF/A does not know; they are compressed with Flate", () =>
            {
                foreach (PdfStream stream in lzw)
                {
                    byte[] decoded = stream.GetDecodedBytes();
                    stream.Dictionary.Remove("DecodeParms");
                    stream.SetDecodedBytes(decoded);
                }
            });
        }

        if (count > 0)
        {
            Issue("6.1.7.2", $"{count} streams use LZW or an unknown filter that Egap cannot decode or that sits beneath an image filter");
        }
    }
}
