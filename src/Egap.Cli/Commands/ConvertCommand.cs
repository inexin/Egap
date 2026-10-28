using System.CommandLine;
using System.Globalization;
using System.Net;

using Egap.Cli.Host;
using Egap.Documents;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap convert</c>: every direction Egap.Documents supports, chosen by the output's extension (CLI design Q6). HTML,
/// Markdown, DOCX, text and images become PDF through <see cref="PdfConvert"/>, with its options as flags; PDF becomes
/// DOCX, HTML, Markdown or text through layout analysis; and the flow formats convert to each other. HTML reads its images
/// and style sheets from its own folder only, unless <c>--allow-path</c> or <c>--allow-network</c> says otherwise (Q7).
/// </summary>
internal static class ConvertCommand
{
    /// <summary>The formats convert writes, by extension.</summary>
    private static readonly string[] Targets = ["pdf", "docx", "html", "md", "txt"];

    /// <summary>The most one remote resource may be (Q7).</summary>
    private const int MaxRemoteBytes = 50 << 20;

    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new("HTML, Markdown, DOCX, text, image or PDF files, globs (*, ?, **), or folders with --recursive.");
        OutputOptions output = new(inPlace: false);
        SaveOptions save = new();
        SecretOptions password = new("password", "password that opens encrypted PDF inputs");
        Option<string?> to = new("--to") { Description = $"The output format when -o does not say: {string.Join(", ", Targets)}.", HelpName = "format" };
        Option<string?> pageSize = new("--page-size") { Description = "To PDF: the page size, A3, A4, A5, Letter, Legal, Tabloid or WxH such as 210mmx297mm.", HelpName = "size" };
        Option<bool> landscape = new("--landscape") { Description = "To PDF: turn the page size to landscape." };
        Option<string?> margins = new("--margins") { Description = "To PDF: page margins, one length for all sides, two (top and bottom, then the sides) or four (top, right, bottom, left), such as 2cm or 20mm,15mm.", HelpName = "lengths" };
        Option<string?> header = new("--header") { Description = "To PDF: a text header, left|center|right, with {page}, {total-pages}, {label}, {date}, {time}, {title} and {file}.", HelpName = "text" };
        Option<string?> footer = new("--footer") { Description = "To PDF: a text footer, as --header.", HelpName = "text" };
        Option<string?> headerHtml = new("--header-html") { Description = "To PDF: an HTML header styled with the document's own CSS.", HelpName = "html" };
        Option<string?> footerHtml = new("--footer-html") { Description = "To PDF: an HTML footer, as --header-html.", HelpName = "html" };
        Option<string?> toc = new("--toc") { Description = "To PDF: a table of contents at the start, with page numbers (pages) or without (no-pages).", HelpName = "pages|no-pages" };
        Option<string?> pdfa = new("--pdfa") { Description = "To PDF: write PDF/A, one of 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 and 4f.", HelpName = "level" };
        Option<bool> tagged = new("--tagged") { Description = "To PDF: write a tagged PDF with a structure tree, for accessibility." };
        Option<string[]> set = new("--set")
        {
            Description = "To PDF, any export setting as key=value: language, links, named-destinations, outline (none, headings, bookmarks), form-fields, ua.",
            HelpName = "key=value",
            AllowMultipleArgumentsPerToken = false,
        };
        Option<string?> baseDir = new("--base-dir") { Description = "HTML and Markdown: the folder images and style sheets are read from; the input's folder by default.", HelpName = "folder" };
        Option<string[]> allowPath = new("--allow-path") { Description = "HTML: a further folder resources may come from; give it once per folder.", HelpName = "folder", AllowMultipleArgumentsPerToken = false };
        Option<bool> allowNetwork = new("--allow-network") { Description = "HTML: fetch http and https resources (10 s and 50 MB each at most)." };
        Option<string?> mode = new("--mode") { Description = "From PDF: flow (reflowing text, the default) or exact (every line where it stands).", HelpName = "flow|exact" };
        Option<string?> comments = new("--comments") { Description = "To PDF: the document's comments left out (none, the default) or written as annotations a PDF viewer shows.", HelpName = "none|annotations" };
        Option<bool> embedSource = new("--embed-source") { Description = "To PDF: embed a DOCX of the document as the PDF's source (a hybrid PDF that converts back to the document); not with PDF/A-1 or 2." };
        Option<bool> ignoreSource = new("--ignore-embedded-source") { Description = "From PDF: analyse the pages even when the PDF carries an embedded source document Egap can use." };
        Command command = new("convert", "Convert between formats by the output's extension: HTML, Markdown, DOCX, text and images to PDF; PDF to DOCX, HTML, Markdown or text; and the flow formats to each other.");
        inputs.AddTo(command);
        output.AddTo(command);
        command.Options.Add(to);
        foreach (Option option in (Option[])[pageSize, landscape, margins, header, footer, headerHtml, footerHtml, toc, pdfa, tagged, set, baseDir, allowPath, allowNetwork, mode, embedSource, ignoreSource, comments])
        {
            command.Options.Add(option);
        }

        save.AddTo(command, edits: false);
        password.AddTo(command);
        command.SetAction(result =>
        {
            Cli.RegisterFormats();
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate template = output.Template(session, result, files)!;
            string target = Target(result.GetValue(to), template);
            string? secret = password.Read(session, result);
            PdfSaveOptions saveOptions = save.Build(result);
            PdfConvertOptions options = new()
            {
                PageSize = PageSize(result.GetValue(pageSize), result.GetValue(landscape)),
                Margins = Margins(result.GetValue(margins)),
                Header = Band(result.GetValue(header), result.GetValue(headerHtml), "--header"),
                Footer = Band(result.GetValue(footer), result.GetValue(footerHtml), "--footer"),
                TableOfContents = Toc(result.GetValue(toc)),
                Conformance = Conformance(result.GetValue(pdfa)),
                Tagged = result.GetValue(tagged),
                EmbedSource = result.GetValue(embedSource),
                BaseDirectory = result.GetValue(baseDir) is { } folder ? session.ResolvePath(folder) : null,
                Export = Export(result.GetValue(set), saveOptions),
            };
            PdfCommentMode commentMode = result.GetValue(comments) switch
            {
                null or "none" => PdfCommentMode.None,
                "annotations" => PdfCommentMode.Annotations,
                string other => throw new UsageException($"--comments '{other}' is not none or annotations."),
            };
            if (commentMode != PdfCommentMode.None)
            {
                options = options with { Export = (options.Export ?? PdfExportOptions.Default) with { Comments = commentMode } };
            }

            if (options.EmbedSource && (options.Conformance != PdfAConformance.None ? options.Conformance : options.Export?.Conformance) is PdfAConformance.PdfA1b or PdfAConformance.PdfA2a or PdfAConformance.PdfA2b or PdfAConformance.PdfA2u)
            {
                throw new UsageException("--embed-source needs PDF/A-3, PDF/A-4f or no PDF/A: PDF/A-1 and 2 do not allow embedded files.");
            }

            EmbeddedSource embedded = result.GetValue(ignoreSource) ? EmbeddedSource.Ignore : EmbeddedSource.Prefer;
            string[] allowed = [.. (result.GetValue(allowPath) ?? []).Select(session.ResolvePath)];
            bool network = result.GetValue(allowNetwork);
            if ((allowed.Length > 0 || network) && (options.Header is not null || options.Footer is not null || options.TableOfContents != TableOfContentsMode.None))
            {
                throw new UsageException("--header, --footer and --toc are not available with --allow-path or --allow-network yet.");
            }

            PdfToDocumentMode fromPdf = result.GetValue(mode) switch
            {
                null or "flow" => PdfToDocumentMode.Flow,
                "exact" => PdfToDocumentMode.Exact,
                string other => throw new UsageException($"--mode '{other}' is not flow or exact."),
            };

            return Batch.Run(session, files, batch, file =>
            {
                if (file.Path is null)
                {
                    throw new UsageException("convert reads files, not standard input; give the file's name.");
                }

                string path = OutputOptions.PathFor(session, template, file, target);
                bool isPdf = Document.Identify(file.Path) == DocumentFormatKind.Pdf;
                if (target == "pdf")
                {
                    if (isPdf)
                    {
                        throw new UsageException($"{file.Display} is a PDF already; egap convert writes PDF from HTML, Markdown, DOCX, text and images.");
                    }

                    using PdfDocument pdf = allowed.Length > 0 || network ? FromHtml(session, file.Path, options, allowed, network) : PdfConvert.FromFile(file.Path, options);
                    SaveOptions.Save(session, pdf, path, saveOptions);
                    return [path];
                }

                Document document = isPdf ? FromPdf(session, file, secret, fromPdf, embedded) : Document.Load(file.Path, out IReadOnlyList<ConversionWarning> warnings);
                SaveOptions.WriteFile(session, path, target, temporary =>
                {
                    foreach (ConversionWarning warning in document.Save(temporary))
                    {
                        session.Warn($"{file.Display}: {warning.Message}");
                    }
                });
                return [path];
            });
        });
        return command;
    }

    /// <summary>The format to write: <c>--to</c>, else the output's extension.</summary>
    private static string Target(string? to, OutputTemplate template)
    {
        string? format = to?.TrimStart('.').ToLowerInvariant() ?? OutputOptions.ExtensionOf(template);
        format = format switch
        {
            "htm" or "xhtml" => "html",
            "markdown" => "md",
            "text" => "txt",
            _ => format,
        };
        return format is not null && Targets.Contains(format) ? format
            : throw new UsageException(format is null
                ? $"Give --to with the output format ({string.Join(", ", Targets)}) when -o does not end in one."
                : $"'{format}' is not a format convert writes; it writes {string.Join(", ", Targets)}.");
    }

    /// <summary>A PDF read through layout analysis; an encrypted one is opened with its password and decrypted first.</summary>
    private static Document FromPdf(EgapSession session, InputFile file, string? password, PdfToDocumentMode mode, EmbeddedSource embedded)
    {
        PdfToDocumentOptions options = new()
        {
            Mode = mode,
            EmbeddedSource = embedded,
            OnWarning = warning => session.Warn($"{file.Display}: {warning}"),
        };
        using PdfDocument pdf = PdfInput.Open(session, file, password);
        if (!pdf.IsEncrypted)
        {
            return PdfToDocument.LoadPdf(file.Path!, options);
        }

        using MemoryStream plain = new();
        pdf.Save(plain, PdfSaveOptions.Default with { Encryption = PdfEncryption.None });
        plain.Position = 0;
        return PdfToDocument.LoadPdf(plain, options);
    }

    /// <summary>
    /// HTML with resources from more folders or the network (Q7): read with a resolver that keeps to them, then laid out with
    /// the page size, margins, PDF/A and tagging asked for.
    /// </summary>
    private static PdfDocument FromHtml(EgapSession session, string path, PdfConvertOptions options, string[] allowed, bool network)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".html" or ".htm" or ".xhtml"))
        {
            throw new UsageException("--allow-path and --allow-network apply to HTML inputs only.");
        }

        string root = options.BaseDirectory ?? Path.GetDirectoryName(Path.GetFullPath(path))!;
        string[] folders = [root, .. allowed];
        HtmlLoadOptions load = new()
        {
            BaseUri = new Uri(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar),
            ResourceResolver = uri => Resolve(session, uri, folders, network),
        };
        Document document = HtmlFormat.Load(path, out IReadOnlyList<ConversionWarning> warnings, load);
        foreach (ConversionWarning warning in warnings)
        {
            session.Warn($"{path}: {warning.Message}");
        }

        foreach (Section section in document.Sections)
        {
            if (options.PageSize is { } size)
            {
                section.PageSetup.PageWidth = size.Width;
                section.PageSetup.PageHeight = size.Height;
                section.PageSetup.Orientation = size.Width > size.Height ? PageOrientation.Landscape : PageOrientation.Portrait;
            }

            if (options.Margins is { } margins)
            {
                section.PageSetup.MarginTop = margins.Top;
                section.PageSetup.MarginRight = margins.Right;
                section.PageSetup.MarginBottom = margins.Bottom;
                section.PageSetup.MarginLeft = margins.Left;
            }
        }

        PdfExportOptions export = (options.Export ?? PdfExportOptions.Default) with
        {
            Conformance = options.Conformance != PdfAConformance.None ? options.Conformance : (options.Export ?? PdfExportOptions.Default).Conformance,
            Tagged = options.Tagged || (options.Export?.Tagged ?? false),
            EmbedSource = options.EmbedSource || (options.Export?.EmbedSource ?? false),
        };
        return document.ToPdf(export);
    }

    private static byte[]? Resolve(EgapSession session, Uri uri, string[] folders, bool network)
    {
        if (uri.IsFile)
        {
            string full = Path.GetFullPath(uri.LocalPath);
            foreach (string folder in folders)
            {
                if (full.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    session.Paths?.Check(full);
                    return File.Exists(full) ? File.ReadAllBytes(full) : null;
                }
            }

            session.Warn($"left out {uri.LocalPath}: it is outside the folders resources may come from; add --allow-path.");
            return null;
        }

        if (uri.Scheme is "http" or "https")
        {
            if (!network)
            {
                session.Warn($"left out {uri}: add --allow-network to fetch it.");
                return null;
            }

            try
            {
                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(10) };
                using HttpResponseMessage response = client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxRemoteBytes)
                {
                    session.Warn($"left out {uri}: {(int)response.StatusCode} {response.ReasonPhrase}{(response.Content.Headers.ContentLength > MaxRemoteBytes ? ", larger than 50 MB" : string.Empty)}.");
                    return null;
                }

                using Stream body = response.Content.ReadAsStream();
                using MemoryStream buffer = new();
                byte[] chunk = new byte[1 << 16];
                int read;
                while ((read = body.Read(chunk)) > 0)
                {
                    if (buffer.Length + read > MaxRemoteBytes)
                    {
                        session.Warn($"left out {uri}: larger than 50 MB.");
                        return null;
                    }

                    buffer.Write(chunk, 0, read);
                }

                session.Info($"fetched {uri} ({buffer.Length.ToString(CultureInfo.InvariantCulture)} bytes)");
                return buffer.ToArray();
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
            {
                session.Warn($"left out {uri}: {error.Message}");
                return null;
            }
        }

        return null;
    }

    /// <summary>A page size: a name such as A4, or WxH in lengths such as 210mmx297mm or 8.5inx11in.</summary>
    internal static PdfPageSize? PageSize(string? text, bool landscape)
    {
        if (text is null)
        {
            return landscape ? throw new UsageException("--landscape turns --page-size; give a page size too.") : null;
        }

        PdfPageSize size = text.ToUpperInvariant() switch
        {
            "A3" => PdfPageSize.A3,
            "A4" => PdfPageSize.A4,
            "A5" => PdfPageSize.A5,
            "LETTER" => PdfPageSize.Letter,
            "LEGAL" => PdfPageSize.Legal,
            "TABLOID" => PdfPageSize.Tabloid,
            _ => Custom(text),
        };
        return landscape && size.Width < size.Height ? new PdfPageSize { Width = size.Height, Height = size.Width } : size;
    }

    private static PdfPageSize Custom(string text)
    {
        int x = text.IndexOfAny(['x', 'X']);
        if (x > 0 && Length.TryParse(text[..x], out Length width) && Length.TryParse(text[(x + 1)..], out Length height) && width.Points > 0 && height.Points > 0)
        {
            return new PdfPageSize { Width = width.Points, Height = height.Points };
        }

        throw new UsageException($"--page-size '{text}' is not A3, A4, A5, Letter, Legal, Tabloid or WxH such as 210mmx297mm.");
    }

    /// <summary>One length for every side, two (top and bottom, then the sides) or four (top, right, bottom, left).</summary>
    internal static PageMargins? Margins(string? text)
    {
        if (text is null)
        {
            return null;
        }

        Length[] parts = [.. text.Split(',', StringSplitOptions.TrimEntries).Select(part => Length.TryParse(part, out Length length)
            ? length : throw new UsageException($"--margins: '{part}' is not a length such as 20mm, 2cm, 1in or 72pt."))];
        return parts.Length switch
        {
            1 => PageMargins.All(parts[0]),
            2 => PageMargins.Symmetric(parts[0], parts[1]),
            4 => new PageMargins(parts[0], parts[1], parts[2], parts[3]),
            _ => throw new UsageException("--margins takes one, two or four lengths."),
        };
    }

    private static PageBand? Band(string? text, string? html, string option)
    {
        if (text is not null && html is not null)
        {
            throw new UsageException($"Give {option} or {option}-html, not both.");
        }

        if (html is not null)
        {
            return PageBand.Html(html);
        }

        if (text is null)
        {
            return null;
        }

        string[] parts = text.Split('|');
        return parts.Length switch
        {
            1 => PageBand.Text(center: parts[0]),
            2 => PageBand.Text(left: parts[0], right: parts[1]),
            3 => PageBand.Text(NullIfEmpty(parts[0]), NullIfEmpty(parts[1]), NullIfEmpty(parts[2])),
            _ => throw new UsageException($"{option} takes up to three parts, left|center|right."),
        };
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    private static TableOfContentsMode Toc(string? text)
    {
        return text switch
        {
            null or "none" => TableOfContentsMode.None,
            "pages" => TableOfContentsMode.WithPageNumbers,
            "no-pages" => TableOfContentsMode.WithoutPageNumbers,
            _ => throw new UsageException($"--toc '{text}' is not pages, no-pages or none."),
        };
    }

    /// <summary>A PDF/A level such as 2b or 4f.</summary>
    internal static PdfAConformance Conformance(string? text)
    {
        if (text is null)
        {
            return PdfAConformance.None;
        }

        string name = "PdfA" + text.Replace("PDF/A-", string.Empty, StringComparison.OrdinalIgnoreCase).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return Enum.TryParse(name, ignoreCase: true, out PdfAConformance conformance) && conformance != PdfAConformance.None
            ? conformance
            : throw new UsageException($"--pdfa '{text}' is not one of 1b, 2a, 2b, 2u, 3a, 3b, 3u, 4 and 4f.");
    }

    private static PdfExportOptions Export(string[]? settings, PdfSaveOptions save)
    {
        PdfExportOptions export = PdfExportOptions.Default with { Save = save };
        foreach (string text in settings ?? [])
        {
            int equals = text.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new UsageException($"--set '{text}' is not key=value, such as language=en-GB.");
            }

            string key = text[..equals].Trim().ToLowerInvariant(), value = text[(equals + 1)..].Trim();
            export = key switch
            {
                "language" => export with { Language = value },
                "links" => export with { Links = Bool(key, value) },
                "named-destinations" => export with { NamedDestinations = Bool(key, value) },
                "form-fields" => export with { FormFields = Bool(key, value) },
                "ua" => export with { UniversalAccessibility = Bool(key, value) },
                "outline" => export with
                {
                    Outline = value switch
                    {
                        "none" => OutlineFrom.None,
                        "headings" => OutlineFrom.Headings,
                        "bookmarks" => OutlineFrom.Bookmarks,
                        _ => throw new UsageException($"--set outline='{value}' is not none, headings or bookmarks."),
                    },
                },
                _ => throw new UsageException($"--set: unknown setting '{key}'; settings are language, links, named-destinations, outline, form-fields and ua."),
            };
        }

        return export;
    }

    private static bool Bool(string key, string value)
    {
        return bool.TryParse(value, out bool flag) ? flag : throw new UsageException($"--set {key}='{value}' is not true or false.");
    }
}
