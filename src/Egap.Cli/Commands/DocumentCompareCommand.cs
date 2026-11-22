using System.CommandLine;

using Egap.Cli.Host;
using Egap.Documents;
using Egap.Documents.Comparing;
using Egap.Documents.Layout;
using Egap.Documents.Odf;
using Egap.Documents.Pdf;
using Egap.Documents.Rtf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap compare</c> for documents (compare design Q8): two documents of any format Egap reads, a PDF through layout
/// analysis, compared with <see cref="DocumentCompare"/>. The result is written with tracked changes (<c>--output</c>: DOCX,
/// RTF or ODT) or as a redline in the Markup view (<c>--redline</c>: PDF, HTML, EPUB or Markdown), and the change list as
/// text or as JSON with schema egap-document-compare-1.json.
/// </summary>
internal static class DocumentCompareCommand
{
    /// <summary>The version of the document <c>compare --json</c> schema.</summary>
    public const int Schema = 1;

    /// <summary>The formats <c>--output</c> writes, with tracked changes.</summary>
    private static readonly string[] Outputs = ["docx", "rtf", "odt", "fodt"];

    /// <summary>The formats <c>--redline</c> writes, in the Markup view.</summary>
    private static readonly string[] Redlines = ["pdf", "html", "epub", "md"];

    /// <summary>The options that apply to documents only.</summary>
    internal sealed class Options
    {
        public Option<bool> AsDocuments { get; } = new("--as-documents") { Description = "Compare two PDFs as documents, read through layout analysis, instead of by page." };

        public Option<string?> Output { get; } = new("--output") { Description = "Documents: write the result with tracked changes to this file, DOCX, RTF or ODT.", HelpName = "file" };

        public Option<string?> Redline { get; } = new("--redline") { Description = "Documents: write the result with the changes marked to this file, PDF, HTML, EPUB or Markdown.", HelpName = "file" };

        public Option<string?> Granularity { get; } = new("--granularity") { Description = "Documents: mark whole words that changed (word, the default) or only the letters (character).", HelpName = "word|character" };

        public Option<string?> Base { get; } = new("--base") { Description = "Documents: the document the result is a copy of, with its styles and page setup: revised (the default) or original.", HelpName = "original|revised" };

        public Option<string?> Author { get; } = new("--author") { Description = "Documents: the author of the tracked changes; Egap by default.", HelpName = "name" };

        public Option<bool> NoFormatting { get; } = new("--no-formatting") { Description = "Documents: leave formatting changes out (they are left out anyway when the two come from different formats)." };

        public Option<bool> NoCase { get; } = new("--no-case") { Description = "Documents: ignore changes of case only." };

        public Option<bool> NoWhitespace { get; } = new("--no-whitespace") { Description = "Documents: ignore changes of spacing only." };

        public Option<bool> NoMoves { get; } = new("--no-moves") { Description = "Documents: show moved paragraphs as deleted and inserted." };

        public Option<bool> NoHeaders { get; } = new("--no-headers") { Description = "Documents: leave headers and footers out." };

        public Option<bool> NoNotes { get; } = new("--no-notes") { Description = "Documents: leave the text of footnotes and endnotes out." };

        public Option<bool> NoTextBoxes { get; } = new("--no-textboxes") { Description = "Documents: leave the text of text boxes out." };

        public Option<bool> NoFields { get; } = new("--no-fields") { Description = "Documents: compare fields by their results only, not their codes." };

        public Option<bool> NoComments { get; } = new("--no-comments") { Description = "Documents: keep the base document's comments instead of the revised one's." };

        private Option[] All => [AsDocuments, Output, Redline, Granularity, Base, Author, NoFormatting, NoCase, NoWhitespace, NoMoves, NoHeaders, NoNotes, NoTextBoxes, NoFields, NoComments];

        public void AddTo(Command command)
        {
            foreach (Option option in All)
            {
                command.Options.Add(option);
            }
        }

        /// <summary>The name of a document option the command line gives, or null.</summary>
        public string? Given(ParseResult result)
        {
            return All.FirstOrDefault(option => result.GetResult(option) is { Implicit: false })?.Name;
        }
    }

    public static int Run(EgapSession session, ParseResult result, Options options, InputFile oldInput, InputFile newInput, string? oldPassword, string? newPassword, bool json)
    {
        Cli.RegisterFormats();
        string? outputPath = Target(session, result.GetValue(options.Output), Outputs, "--output", "writes DOCX, RTF or ODT with tracked changes", out string? outputFormat);
        string? redlinePath = Target(session, result.GetValue(options.Redline), Redlines, "--redline", "writes PDF, HTML, EPUB or Markdown", out string? redlineFormat);
        DocumentCompareOptions compare = new()
        {
            Granularity = result.GetValue(options.Granularity) switch
            {
                null or "word" => CompareGranularity.Word,
                "character" => CompareGranularity.Character,
                string other => throw new UsageException($"--granularity '{other}' is not word or character."),
            },
            Base = result.GetValue(options.Base) switch
            {
                null or "revised" => CompareBase.Revised,
                "original" => CompareBase.Original,
                string other => throw new UsageException($"--base '{other}' is not original or revised."),
            },
            Author = result.GetValue(options.Author) is { Length: > 0 } author ? author : "Egap",
            Formatting = result.GetValue(options.NoFormatting) ? false : null,
            CaseChanges = !result.GetValue(options.NoCase),
            Whitespace = !result.GetValue(options.NoWhitespace),
            Moves = !result.GetValue(options.NoMoves),
            HeadersFooters = !result.GetValue(options.NoHeaders),
            Notes = !result.GetValue(options.NoNotes),
            TextBoxes = !result.GetValue(options.NoTextBoxes),
            Fields = !result.GetValue(options.NoFields),
            Comments = !result.GetValue(options.NoComments),
        };
        Document original = Load(session, oldInput, oldPassword);
        Document revised = Load(session, newInput, newPassword);
        session.Cancellation.ThrowIfCancellationRequested();
        DocumentComparison comparison = DocumentCompare.Run(original, revised, compare);
        foreach (DocumentCompareWarning warning in comparison.Warnings)
        {
            session.Warn(warning.Count > 1 ? $"{warning.Message} ({warning.Count} times)" : warning.Message);
        }

        if (outputPath is not null)
        {
            Write(session, comparison.Document, outputPath, outputFormat!);
        }

        if (redlinePath is not null)
        {
            Write(session, comparison.Document, redlinePath, redlineFormat!);
        }

        if (json)
        {
            WriteJson(session.Console.Text, oldInput, newInput, comparison);
        }
        else
        {
            WriteText(session.Console.Text, comparison);
        }

        return comparison.HasChanges ? ExitCodes.Failed : ExitCodes.Success;
    }

    /// <summary>The full path of an output and its format by extension; null when the option is not given.</summary>
    private static string? Target(EgapSession session, string? path, string[] formats, string option, string what, out string? format)
    {
        format = null;
        if (path is null)
        {
            return null;
        }

        string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
        {
            "htm" or "xhtml" => "html",
            "markdown" => "md",
            string e => e,
        };
        format = formats.Contains(extension) ? extension : throw new UsageException($"{option} {what}; '{path}' does not end in .{string.Join(", .", formats)}.");
        return session.ResolvePath(path);
    }

    /// <summary>A document from a file: a PDF through layout analysis, anything else as its format reads it.</summary>
    private static Document Load(EgapSession session, InputFile input, string? password)
    {
        if (input.Path is null)
        {
            throw new UsageException("compare reads documents from files, not standard input; give the file's name.");
        }

        if (Document.Identify(input.Path) == DocumentFormatKind.Pdf)
        {
            return ConvertCommand.FromPdf(session, input, password, PdfToDocumentMode.Flow, EmbeddedSourceMode.Prefer);
        }

        Document document = Document.Load(input.Path, out IReadOnlyList<ConversionWarning> warnings, new DocumentLoadOptions { Password = password });
        foreach (ConversionWarning warning in warnings)
        {
            session.Warn($"{input.Display}: {warning.Message}");
        }

        return document;
    }

    /// <summary>Writes the result: DOCX, RTF and ODT with its tracked changes, the others in the Markup view.</summary>
    private static void Write(EgapSession session, Document document, string path, string format)
    {
        IReadOnlyList<ConversionWarning> warnings = [];
        Output.Write(session, path, stream =>
        {
            warnings = format switch
            {
                "docx" => DocxFormat.Save(document, stream),
                "rtf" => RtfFormat.Save(document, stream),
                "odt" => OdtFormat.Save(document, stream),
                "fodt" => OdtFormat.Save(document, stream, new OdtSaveOptions { Kind = OdtKind.FlatDocument }),
                "html" => HtmlFormat.Save(document, stream, new HtmlSaveOptions { RevisionView = RevisionView.Markup }),
                "epub" => EpubFormat.Save(document, stream, new EpubSaveOptions { RevisionView = RevisionView.Markup }),
                "md" => MarkdownFormat.Save(document, stream, new MarkdownSaveOptions { RevisionView = RevisionView.Markup }),
                _ => Pdf(document, stream),
            };
        });
        foreach (ConversionWarning warning in warnings)
        {
            session.Warn($"{Path.GetFileName(path)}: {warning.Message}");
        }
    }

    private static IReadOnlyList<ConversionWarning> Pdf(Document document, Stream stream)
    {
        document.SaveAsPdf(stream, PdfExportOptions.Default with { RevisionView = RevisionView.Markup });
        return [];
    }

    /// <summary>Where a change is: body, header, footer, footnote, endnote, textbox or comment.</summary>
    internal static string StoryName(Story story)
    {
        return story switch
        {
            HeaderFooter { IsHeader: true } => "header",
            HeaderFooter => "footer",
            NoteStory { Note.Kind: NoteKind.Endnote } => "endnote",
            NoteStory => "footnote",
            TextBoxStory => "textbox",
            CommentStory => "comment",
            _ => "body",
        };
    }

    private static string KindName(CompareChangeKind kind) => kind switch
    {
        CompareChangeKind.Inserted => "inserted",
        CompareChangeKind.Deleted => "deleted",
        CompareChangeKind.Replaced => "replaced",
        CompareChangeKind.Formatted => "formatted",
        _ => "moved",
    };

    private static void WriteText(TextWriter output, DocumentComparison comparison)
    {
        if (!comparison.HasChanges)
        {
            output.WriteLine("no differences");
            return;
        }

        foreach (DocumentChange change in comparison.Changes)
        {
            string where = StoryName(change.Story);
            output.WriteLine(change.Kind switch
            {
                CompareChangeKind.Inserted => $"{where}: inserted \"{Line(change.NewText)}\"",
                CompareChangeKind.Deleted => $"{where}: deleted \"{Line(change.OldText)}\"",
                CompareChangeKind.Moved => $"{where}: moved \"{Line(change.NewText)}\"",
                CompareChangeKind.Formatted => $"{where}: formatted \"{Line(change.NewText)}\"",
                _ => $"{where}: replaced \"{Line(change.OldText)}\" with \"{Line(change.NewText)}\"",
            });
        }

        output.WriteLine(comparison.ToString());
    }

    /// <summary>A change's text on one line: paragraph breaks and other control characters as spaces.</summary>
    private static string Line(string text)
    {
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
            }
        });
    }

    private static void WriteJson(TextWriter output, InputFile oldInput, InputFile newInput, DocumentComparison comparison)
    {
        JsonOut.Write(output, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("schema", Schema);
            json.WriteString("kind", "documents");
            json.WriteString("old", oldInput.Display);
            json.WriteString("new", newInput.Display);
            json.WriteBoolean("identical", !comparison.HasChanges);
            json.WriteStartArray("changes");
            foreach (DocumentChange change in comparison.Changes)
            {
                json.WriteStartObject();
                json.WriteString("kind", KindName(change.Kind));
                json.WriteString("story", StoryName(change.Story));
                json.WriteString("oldText", change.OldText);
                json.WriteString("newText", change.NewText);
                json.WriteNumber("trackedChanges", change.TrackedChanges.Count);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            DocumentCompareStatistics counts = comparison.Statistics;
            json.WriteStartObject("statistics");
            json.WriteNumber("insertedWords", counts.InsertedWords);
            json.WriteNumber("deletedWords", counts.DeletedWords);
            json.WriteNumber("movedWords", counts.MovedWords);
            json.WriteNumber("formattingChanges", counts.FormattingChanges);
            json.WriteNumber("paragraphsTouched", counts.ParagraphsTouched);
            json.WriteEndObject();
            json.WriteStartArray("warnings");
            foreach (DocumentCompareWarning warning in comparison.Warnings)
            {
                json.WriteStartObject();
                json.WriteString("kind", InfoCommand.Kebab(warning.Kind.ToString()));
                json.WriteString("message", warning.Message);
                json.WriteNumber("count", warning.Count);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
    }
}
