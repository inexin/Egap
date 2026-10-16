namespace Egap.Pdf.PdfA;

/// <summary>
/// The PDF/UA-1 basics Egap checks for documents it creates (ISO 14289-1, Phase 5 design Q14): identification, title and
/// language, a tagged structure with every piece of content tagged or an artifact, figures described, annotations tagged,
/// fonts embedded. Run like <see cref="PdfARules"/>: checking, or fixing what can be put right without changing content.
/// </summary>
internal sealed class PdfUARules
{
    private static readonly HashSet<string> Painting = ["S", "s", "f", "F", "f*", "B", "B*", "b", "b*", "sh", "Do", "Tj", "TJ", "'", "\"", "BI"];

    private readonly PdfDocument document;
    private readonly bool fixing;
    private readonly List<PdfAIssue> issues = [];

    private PdfUARules(PdfDocument document, bool fixing)
    {
        this.document = document;
        this.fixing = fixing;
    }

    /// <summary>Runs every rule; in fixing mode puts right what it can.</summary>
    public static List<PdfAIssue> Run(PdfDocument document, bool fixing)
    {
        PdfUARules rules = new(document, fixing);
        rules.RunAll();
        return rules.issues;
    }

    private void Issue(string clause, string description, Action? fix = null, int? page = null)
    {
        if (fix is not null && fixing)
        {
            fix();
        }

        issues.Add(new PdfAIssue(clause, description, page, fix is not null, fix is not null && fixing));
    }

    private void RunAll()
    {
        PdfMetadata metadata = document.Metadata;
        if (metadata.GetPdfUAId() != "1")
        {
            Issue("5", "the XMP metadata does not identify the file as PDF/UA-1; the identification is written", () => metadata.SetPdfUAId(1));
        }

        if (string.IsNullOrWhiteSpace(metadata.Title))
        {
            Issue("7.1", "the document has no title (Metadata.Title)");
        }

        PdfDictionary catalog = document.Catalog;
        if (catalog.Get<PdfDictionary>("ViewerPreferences")?.GetBool("DisplayDocTitle") != true)
        {
            Issue("7.1", "viewers are not asked to show the title rather than the file name; DisplayDocTitle is set", () =>
            {
                PdfDictionary preferences = catalog.Get<PdfDictionary>("ViewerPreferences") ?? [];
                preferences["DisplayDocTitle"] = PdfBoolean.True;
                catalog["ViewerPreferences"] = preferences;
            });
        }

        PdfStructureTree structure = document.Structure;
        if (catalog.Get("Lang") is null)
        {
            if (structure.Root?.Language is string language)
            {
                Issue("7.2", $"the document gives no language; its top element's ({language}) is written to the catalog", () => catalog["Lang"] = PdfString.FromText(language));
            }
            else
            {
                Issue("7.2", "the document gives no language (catalog /Lang, or the top structure element's Language)");
            }
        }

        if (!structure.Exists)
        {
            Issue("7.1", "the document is not tagged: it has no structure tree (tag content with PdfCanvas.BeginTag)");
            return;
        }

        if (!structure.IsMarked)
        {
            Issue("7.1", "the document does not say it is tagged (/MarkInfo /Marked); it is set", () => structure.IsMarked = true);
        }

        List<PdfStructureElement> elements = [.. structure.Descendants().Take(200_000)];
        int figures = elements.Count(e => e.StandardType == PdfStructureType.Figure && e.AlternateText is null && e.ActualText is null);
        if (figures > 0)
        {
            Issue("7.3", $"{figures} figures have no alternate text");
        }

        HashSet<string> unmapped = [.. elements.Where(e => !e.StandardType.IsStandard).Select(e => e.Type.Name)];
        if (unmapped.Count > 0)
        {
            Issue("7.1", $"structure types neither standard nor role-mapped to one: {string.Join(", ", unmapped.Take(10))}");
        }

        HashSet<PdfObject> taggedObjects = new(elements.SelectMany(e => e.Children.OfType<PdfObjectReference>()).Select(o => o.Target).OfType<PdfObject>(), ReferenceEqualityComparer.Instance);
        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            int untagged = Untagged(page);
            if (untagged > 0)
            {
                Issue("7.1", $"{untagged} pieces of content are neither tagged nor marked as artifacts", page: i + 1);
            }

            List<PdfAnnotation> annotations = [.. page.Annotations.Where(a => a.Dictionary.GetName("Subtype")?.Value is not "Popup" && (a.Flags & PdfAnnotationFlags.Hidden) == 0)];
            int unlinked = annotations.Count(a => !taggedObjects.Contains(a.Dictionary));
            if (unlinked > 0)
            {
                Issue("7.18.1", $"{unlinked} annotations are not in the structure tree", page: i + 1);
            }

            if (annotations.Count > 0 && page.Dictionary.GetName("Tabs")?.Value != "S")
            {
                Issue("7.18.3", "the page has annotations but no structure tab order; /Tabs /S is set", () => page.Dictionary["Tabs"] = PdfName.Get("S"), i + 1);
            }
        }

        (List<PdfDictionary> resources, _) = ResourceWalker.Walk(document);
        foreach (PdfDictionary font in ResourceWalker.Fonts(resources))
        {
            if (font.GetName("Subtype")?.Value != "Type3" && !StandardFontEmbedder.IsEmbedded(font))
            {
                string name = font.GetName("BaseFont")?.Value ?? "(unnamed)";
                if (StandardFontEmbedder.PlanFor(font, PdfAConvertOptions.Default) is not null)
                {
                    Issue("7.21.4.1", $"the font {name} is not embedded; it is embedded", () => StandardFontEmbedder.TryEmbed(document, font, PdfAConvertOptions.Default));
                }
                else
                {
                    Issue("7.21.4.1", $"the font {name} is not embedded and no program for it was found");
                }
            }
        }
    }

    /// <summary>Painting operations outside any marked-content sequence that has an MCID or is an artifact.</summary>
    private static int Untagged(PdfPage page)
    {
        PdfContent content;
        try
        {
            content = page.GetContent();
        }
        catch (PdfException e) when (e is not PdfLimitException)
        {
            return 0;
        }

        Stack<bool> covered = [];
        int count = 0;
        foreach (PdfOperation operation in content)
        {
            switch (operation.Operator)
            {
                case "BDC":
                    bool mcid = operation.Operands.Count > 1 && operation.Operands[1].Resolve() is PdfDictionary properties && properties.GetInt("MCID") is not null;
                    covered.Push(mcid || operation.GetName(0)?.Value == "Artifact" || (covered.Count > 0 && covered.Peek()));
                    break;
                case "BMC":
                    covered.Push(operation.GetName(0)?.Value == "Artifact" || (covered.Count > 0 && covered.Peek()));
                    break;
                case "EMC" when covered.Count > 0:
                    covered.Pop();
                    break;
                default:
                    if (Painting.Contains(operation.Operator) && (covered.Count == 0 || !covered.Peek()))
                    {
                        count++;
                    }

                    break;
            }
        }

        return count;
    }
}
