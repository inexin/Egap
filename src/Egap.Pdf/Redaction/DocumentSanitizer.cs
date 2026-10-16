namespace Egap.Pdf.Redaction;

/// <summary>
/// Removes what a document hides beyond its pages (Phase 5 design Q10; what Acrobat's Sanitize Document removes): metadata,
/// scripts and actions, attachments, hidden layers, comments, form data, thumbnails and private application data. The
/// document then saves only whole with unused objects dropped, which also removes earlier revisions.
/// </summary>
internal static class DocumentSanitizer
{
    /// <summary>The actions kept: going somewhere in the document. Every other kind reaches outside it or runs something.</summary>
    private static readonly HashSet<string> KeptActions = ["GoTo"];

    public static PdfSanitizeResult Apply(PdfDocument document, PdfSanitizeOptions options)
    {
        int metadata = 0, actions = 0, attachments = 0, layers = 0, comments = 0, fields = 0, thumbnails = 0, privateData = 0;
        PdfDictionary catalog = document.Catalog;

        if (options.FormData && document.Form.Exists)
        {
            List<PdfField> terminal = [.. document.Form.GetTerminalFields()];
            document.Form.Reset();
            fields = terminal.Count;
            if (document.Form.Dictionary?.Get("XFA") is not null)
            {
                document.Form.Dictionary!.Remove("XFA");
                fields++;
            }
        }

        if (options.HiddenLayers)
        {
            // A hidden layer's text is taken out as redaction takes it, the glyphs around it kept in place; its other content
            // goes with the layer.
            Rendering.OptionalContent visibility = new(document, PdfRenderIntent.Display, null, null);
            RedactionCounters counters = new();
            for (int i = 0; i < document.Pages.Count; i++)
            {
                PdfPage page = document.Pages[i];
                ContentRedactor redactor = new(document, [], [], PdfRedactionOptions.Default, counters, marked => marked.Any(m => m.OptionalContent is { } group && !visibility.IsVisible(group)));
                if (redactor.Redact(page.GetContent(), page.Resources, PdfMatrix.Identity, 0) is PdfContent redacted)
                {
                    page.SetContent(redacted);
                }
            }

            foreach (PdfLayer layer in document.Layers.Where(l => !l.IsVisible).ToList())
            {
                document.Layers.Remove(layer, removeContent: true);
                layers++;
            }
        }

        if (options.Attachments)
        {
            foreach (string name in document.Attachments.Select(a => a.Name).ToList())
            {
                attachments += document.Attachments.Remove(name) ? 1 : 0;
            }

            attachments += Remove(catalog, "AF") + Remove(catalog, "Collection");
            if (catalog.Get<PdfDictionary>("Names") is { } names)
            {
                attachments += Remove(names, "EmbeddedFiles");
            }
        }

        if (options.JavaScript)
        {
            actions += Remove(catalog, "AA");
            if (catalog.Get<PdfDictionary>("Names") is { } names)
            {
                actions += Remove(names, "JavaScript");
            }

            if (catalog.Get("OpenAction") is { } open && open.Resolve() is PdfDictionary openAction && !Kept(openAction))
            {
                catalog.Remove("OpenAction");
                actions++;
            }

            actions += CleanOutline(catalog.Get<PdfDictionary>("Outlines"));
            if (document.Form.Exists)
            {
                foreach (PdfField field in document.Form.GetAllFields())
                {
                    actions += Remove(field.Dictionary, "AA");
                }
            }
        }

        if (options.Metadata)
        {
            if (document.Trailer.ContainsKey("Info"))
            {
                document.Trailer.Remove("Info");
                metadata++;
            }

            metadata += Remove(catalog, "Metadata");
        }

        if (options.PrivateData)
        {
            privateData += Remove(catalog, "PieceInfo") + Remove(catalog, "SpiderInfo");
        }

        for (int i = 0; i < document.Pages.Count; i++)
        {
            PdfPage page = document.Pages[i];
            if (options.Metadata)
            {
                metadata += Remove(page.Dictionary, "Metadata");
            }

            if (options.Thumbnails)
            {
                thumbnails += Remove(page.Dictionary, "Thumb");
            }

            if (options.PrivateData)
            {
                privateData += Remove(page.Dictionary, "PieceInfo");
            }

            if (options.JavaScript)
            {
                actions += Remove(page.Dictionary, "AA");
            }

            foreach (PdfAnnotation annotation in page.Annotations.ToList())
            {
                bool comment = annotation is PdfMarkupAnnotation or PdfPopupAnnotation;
                bool attachment = annotation is PdfFileAttachmentAnnotation;
                if ((options.Comments && comment) || (options.Attachments && attachment))
                {
                    if (page.Annotations.Remove(annotation))
                    {
                        if (attachment && options.Attachments)
                        {
                            attachments++;
                        }
                        else
                        {
                            comments++;
                        }
                    }

                    continue;
                }

                if (options.JavaScript)
                {
                    actions += Remove(annotation.Dictionary, "AA");
                    if (annotation.Dictionary.Get<PdfDictionary>("A") is { } action && !Kept(action))
                    {
                        annotation.Dictionary.Remove("A");
                        actions++;
                    }
                }
            }

            // Resources' forms and images carry their own metadata and private data.
            if (page.Resources?.Get<PdfDictionary>("XObject") is { } xobjects && (options.Metadata || options.PrivateData))
            {
                foreach ((PdfName _, PdfObject value) in xobjects.Entries)
                {
                    if (value.Resolve() is PdfStream xobject)
                    {
                        metadata += options.Metadata ? Remove(xobject.Dictionary, "Metadata") : 0;
                        privateData += options.PrivateData ? Remove(xobject.Dictionary, "PieceInfo") : 0;
                    }
                }
            }
        }

        document.MarkRedacted();
        return new PdfSanitizeResult(metadata, actions, attachments, layers, comments, fields, thumbnails, privateData);
    }

    /// <summary>Whether an action and every action after it (<c>/Next</c>) only go somewhere in the document.</summary>
    private static bool Kept(PdfDictionary action)
    {
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Queue<PdfDictionary> queue = new([action]);
        while (queue.Count > 0)
        {
            PdfDictionary next = queue.Dequeue();
            if (!seen.Add(next) || seen.Count > 1000)
            {
                continue;
            }

            if (next.GetName("S")?.Value is not string kind || !KeptActions.Contains(kind))
            {
                return false;
            }

            switch (next.Get("Next")?.Resolve())
            {
                case PdfDictionary single:
                    queue.Enqueue(single);
                    break;
                case PdfArray many:
                    foreach (PdfObject item in many)
                    {
                        if (item.Resolve() is PdfDictionary dictionary)
                        {
                            queue.Enqueue(dictionary);
                        }
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>Removes every bookmark action that is not a go-to, keeping the bookmarks themselves.</summary>
    private static int CleanOutline(PdfDictionary? outline)
    {
        int removed = 0;
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        Stack<PdfDictionary?> stack = new([outline?.Get<PdfDictionary>("First")]);
        while (stack.TryPop(out PdfDictionary? item) && seen.Count < 1_000_000)
        {
            if (item is null || !seen.Add(item))
            {
                continue;
            }

            if (item.Get<PdfDictionary>("A") is { } action && !Kept(action))
            {
                item.Remove("A");
                removed++;
            }

            stack.Push(item.Get<PdfDictionary>("Next"));
            stack.Push(item.Get<PdfDictionary>("First"));
        }

        return removed;
    }

    private static int Remove(PdfDictionary dictionary, string key)
    {
        if (!dictionary.ContainsKey(key))
        {
            return 0;
        }

        dictionary.Remove(key);
        return 1;
    }
}
