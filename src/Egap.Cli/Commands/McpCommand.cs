using System.CommandLine;
using System.Text.Json;

using Egami;
using Egami.Cli.Hosting.Mcp;
using Egami.Pixels;

using Egap.Cli.Host;
using Egap.Pdf;

using ModelContextProtocol.Protocol;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap mcp</c> (CLI design Q16): the commands as tools for an AI assistant over the Model Context Protocol, on the
/// server egami mcp uses. Tools read and write only inside the folders the assistant opened and those given with
/// <c>--allow</c>; sign, encrypt and decrypt are tools only with <c>--allow-sign</c>, and secrets come only by environment
/// variable name.
/// </summary>
internal static class McpCommand
{
    /// <summary>The most inputs one call takes unless <c>--max-inputs</c> says otherwise.</summary>
    public const int DefaultMaxInputs = 1000;

    /// <summary>The tools that act in someone's name or lock files, which need <c>--allow-sign</c>.</summary>
    private static readonly HashSet<string> Signing = new(StringComparer.Ordinal) { "sign", "encrypt", "decrypt" };

    /// <summary>
    /// The tools that run a program named in the call (ocr's --engine-command), which an assistant could turn into any command:
    /// never offered.
    /// </summary>
    private static readonly HashSet<string> RunsPrograms = new(StringComparer.Ordinal) { "ocr" };

    public static Command Create(EgapSession session)
    {
        Option<string[]> allow = new("--allow")
        {
            Description = "A folder the tools may read and write, besides the folders the assistant opened; give it once per folder. With neither, the working folder.",
            HelpName = "folder",
            AllowMultipleArgumentsPerToken = false,
        };
        Option<int> maxInputs = new("--max-inputs")
        {
            Description = $"The most files one tool call may take; {DefaultMaxInputs} by default.",
            HelpName = "count",
            DefaultValueFactory = _ => DefaultMaxInputs,
        };
        Option<bool> allowSign = new("--allow-sign") { Description = "Offer the sign, encrypt and decrypt tools, and the encrypt and decrypt steps of run." };
        Command command = new(
            "mcp",
            "Serve egap's commands as tools to an AI assistant over the Model Context Protocol, on standard input and output. "
            + "Tools read and write only inside the folders the assistant opened and those given with --allow, and never replace an input unless asked.");
        command.Options.Add(allow);
        command.Options.Add(maxInputs);
        command.Options.Add(allowSign);
        command.SetAction(result =>
        {
            GlobalOptions.Configure(session, result);
            string? limit = session.Globals is { } globals ? result.GetValue(globals.Limit) : null;
            McpCatalog catalog = Catalog(result.GetValue(allowSign));
            return McpServerHost.Run(session, catalog, result.GetValue(allow) ?? [], result.GetValue(maxInputs), limit);
        });
        return command;
    }

    /// <summary>The tools a server offers: every command, without sign, encrypt and decrypt unless <paramref name="allowSign"/>.</summary>
    public static McpCatalog Catalog(bool allowSign)
    {
        McpCatalog all = McpCatalog.Build(CreateTools(allowSign)).Where(tool => !RunsPrograms.Contains(tool.Name));
        return allowSign ? all : all.Where(tool => !Signing.Contains(tool.Name));
    }

    private static McpToolSet CreateTools(bool allowSign)
    {
        (JsonElement body, JsonElement definitions) = LoadWorkflowSchema();
        HashSet<string> excluded = [.. McpToolSet.DefaultExcluded, "--strict"];

        // Secrets come only by environment variable name (Q16): never as text in a call, nor from a file the call names.
        foreach (string secret in (string[])["password", "save-password", "user-password", "owner-password", "cert-password", "new-password"])
        {
            excluded.Add("--" + secret);
            excluded.Add("--" + secret + "-file");
        }

        return new McpToolSet
        {
            Name = "egap",
            Title = "Egap",
            WebsiteUrl = "https://github.com/inexin/Egap",
            Instructions = "egap turns HTML, Markdown, DOCX, RTF, workbooks and images into PDF, reads workbook cells (cells), recalculates workbooks (calc), compares two documents of any of those formats into tracked changes or a redline (compare with output or redline), and inspects, merges, splits, stamps, redacts, fills and checks PDFs on this computer with Egap, a fully managed .NET PDF library. "
                + "Every tool is an egap command; paths are files, globs or folders inside the folders the user allowed, and outputs never replace inputs unless in_place is true. "
                + "Start with info; run takes any chain of edit steps as a workflow object and needs a save step at the end (list_steps lists them). "
                + "Pages are written as 1-3,z (z is the last page). Passwords come only from environment variables named with the *_env arguments. "
                + "Every result is an object: exitCode (0 success, 1 a file failed or a check such as compare, verify or pdfa_check found a problem, 2 a usage error, 3 a safety limit), "
                + "written (the files written), output (the command's JSON or text) and messages (warnings and errors).",
            CreateRoot = () => Cli.CreateRoot(new EgapSession(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null)),
            Run = (args, console, workingDirectory, environment, setup) => Cli.Run(args, console, workingDirectory, environment, setup),
            Failure = EgapSession.FailureOf,
            Excluded = excluded,
            ReadOnly = new HashSet<string>(StringComparer.Ordinal) { "info", "cells", "text", "verify", "pdfa_check", "form_list", "attach_list", "metadata_show", "invoice_read" },
            Checks = new HashSet<string>(StringComparer.Ordinal) { "compare", "verify", "pdfa_check" },
            Workflow = new McpWorkflow(
                "PDF files, globs (*, ?, **) or folders with recursive.",
                "The steps, as a workflow object: { \"egap\": 1, \"steps\": [ { \"op\": \"pages\", \"rotate\": \"90:1\" }, { \"op\": \"stamp\", \"page-numbers\": true }, { \"op\": \"save\", \"path\": \"out/{name}.pdf\" } ] }. list_steps lists the steps and their options.",
                "List every step with its options, instead of running anything.",
                "{ \"egap\": 1, \"steps\": [ { \"op\": \"pages\", \"rotate\": \"90\" }, { \"op\": \"save\", \"path\": \"out.pdf\" } ] }",
                body,
                definitions,
                (inputs, json) =>
                {
                    List<StepWords> steps = RunCommand.ReadWorkflow(json, "workflow");
                    if (steps.FirstOrDefault(s => RunsPrograms.Contains(s.Name)) is { } runs)
                    {
                        throw new UsageException($"The {runs.Name} step runs a program named in the call, so assistants cannot use it; run egap {runs.Name} yourself.");
                    }

                    if (!allowSign && steps.FirstOrDefault(s => Signing.Contains(s.Name)) is { } step)
                    {
                        throw new UsageException($"The {step.Name} step needs the server to be started with --allow-sign.");
                    }

                    return new RunRequest(inputs, steps);
                },
                (session, request) => ((EgapSession)session).Run = request),
            Preview = Preview,
            PreviewDescription = "Also return the first page of the first file written as a picture, fit to 1024 pixels.",
            Resources = Resources(),
            Prompts = Prompts(),
        };
    }

    /// <summary>The documents egap mcp serves: the workflow schema, the formats convert reads and writes, and the agent guide.</summary>
    private static McpResourceEntry[] Resources()
    {
        System.Reflection.Assembly assembly = typeof(McpCommand).Assembly;
        return
        [
            new("egap://schema/workflow", "workflow-schema", "egap workflow schema", "The JSON schema of a workflow object, as the run tool takes it and egap run --workflow reads it.", "application/schema+json", McpResourceEntry.Embedded(assembly, "egap-workflow-1.json")),
            new("egap://formats", "formats", "Formats egap converts", "The formats convert reads, with their extensions, and the formats it writes, with what each is written from and the settings it takes through set.", "application/json", Formats),
            new("egap://guide", "guide", "egap guide for agents", "How to call egap from scripts and through this server: commands, workflow files, exit codes and common mistakes.", "text/markdown", McpResourceEntry.Embedded(assembly, "egap-guide.md")),
        ];
    }

    /// <summary>The formats convert reads, by kind and extension.</summary>
    private static readonly (string Format, string[] Extensions, string Note)[] Reads =
    [
        ("pdf", [".pdf"], "Read through layout analysis when written as another document format."),
        ("docx", [".docx", ".dotx", ".docm", ".dotm"], ""),
        ("doc", [".doc", ".dot"], "Word 97 to 2003, Word 6 and Word 95; read only."),
        ("rtf", [".rtf"], ""),
        ("odt", [".odt", ".ott", ".fodt"], ""),
        ("html", [".html", ".htm", ".xhtml"], "Images and style sheets come from the page's own folder unless allow_path or allow_network says otherwise."),
        ("mhtml", [".mht", ".mhtml"], ""),
        ("epub", [".epub"], ""),
        ("md", [".md", ".markdown"], ""),
        ("txt", [".txt"], ""),
        ("docling-json", [".json"], "A DoclingDocument JSON file."),
        ("xlsx", [".xlsx", ".xlsm", ".xltx", ".xltm"], "A workbook: written as PDF, XLSX, CSV or images."),
        ("csv", [".csv", ".tsv", ".tab"], "A workbook: written as PDF, XLSX, CSV or images."),
    ];

    /// <summary>The egap://formats document: what convert reads and writes, the writes from the command's own lists.</summary>
    private static string Formats()
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter json = new(stream, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteStartArray("reads");
            foreach ((string format, string[] extensions, string note) in Reads)
            {
                json.WriteStartObject();
                json.WriteString("format", format);
                json.WriteStartArray("extensions");
                foreach (string extension in extensions)
                {
                    json.WriteStringValue(extension);
                }

                json.WriteEndArray();
                if (note.Length > 0)
                {
                    json.WriteString("note", note);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("writes");
            foreach (string target in ConvertCommand.Targets)
            {
                json.WriteStartObject();
                json.WriteString("format", target);
                json.WriteString("from", target switch
                {
                    "pdf" => "documents and workbooks",
                    "xlsx" or "csv" => "workbooks",
                    _ when WorkbookConvert.Images.Contains(target) => "workbooks, a picture per page (render writes pictures of PDF pages)",
                    _ => "documents and PDFs",
                });
                json.WriteStartArray("settings");
                foreach (string key in ConvertCommand.SettingKeys.GetValueOrDefault(target, []))
                {
                    json.WriteStringValue(key);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The tasks egap mcp offers as prompts.</summary>
    private static McpPromptEntry[] Prompts()
    {
        McpPromptArgument folder = new("folder", "The folder that holds the files.");
        return
        [
            new(
                "convert_folder",
                "Convert a folder",
                "Convert every document or workbook in a folder to another format, such as PDF.",
                [folder, new("format", "The format to write, such as pdf, docx, html, md or txt; pdf by default.", Required: false)],
                ["info", "convert"],
                a => $"Convert every document in {a["folder"]} to {Or(a, "format", "pdf")}. "
                    + "Read the resource egap://formats for the formats convert reads and writes and the settings each takes. "
                    + $"Call info on the folder to see what it holds; then call convert with inputs [\"{a["folder"]}\"], to \"{Or(a, "format", "pdf")}\" "
                    + $"and output \"{{dir}}/{Or(a, "format", "pdf")}/{{name}}.{{format}}\", so the copies go to a new folder beside the originals."),
            new(
                "mail_merge",
                "Mail merge",
                "Fill a DOCX template's merge fields from a JSON or CSV file of records, one letter per record.",
                [new("template", "The DOCX template, with MERGEFIELD fields."), new("data", "The records: a JSON array of objects or a CSV file with a header row.")],
                ["merge_data"],
                a => $"Fill the template {a["template"]} from the records in {a["data"]}, one PDF per record. "
                    + $"Call merge_data with template \"{a["template"]}\", data \"{a["data"]}\", each true and output \"letters/letter-{{index}}.pdf\" "
                    + "(or name_field set to a field of the records, to name each file by it). For one DOCX that holds them all, leave each out and write a .docx. "
                    + "Tell the user how many files were written, and any field the records do not fill."),
            new(
                "compare_documents",
                "Compare two versions",
                "Show what changed between two versions of a document or PDF.",
                [new("old", "The earlier version."), new("new", "The later version.")],
                ["compare"],
                a => $"Show what changed from {a["old"]} to {a["new"]}. "
                    + $"Call compare with old \"{a["old"]}\", new \"{a["new"]}\"; the result's output lists the changes. "
                    + "Two PDFs are compared page by page: add markup \"changes.pdf\" for a copy with the changes marked, or as_documents true to compare their text as documents. "
                    + "Two documents (DOCX, RTF, ODT, HTML or others) are compared by words: add redline \"changes.pdf\" for a marked copy, or output \"changes.docx\" for one with tracked changes. "
                    + "exitCode 1 means they differ, which is a result, not a failure. Sum the changes up for the user."),
            new(
                "redact_pdf",
                "Redact a PDF",
                "Remove words or patterns, such as names or account numbers, from a PDF for good.",
                [new("file", "The PDF to redact."), new("phrases", "What to remove: phrases, separated by commas, or a description such as 'every email address'.")],
                ["text", "redact"],
                a => $"Remove {a["phrases"]} from {a["file"]}. "
                    + $"Call text on \"{a["file"]}\" to find where they appear, and turn a description into text phrases or a .NET regular expression. "
                    + $"Call redact with inputs [\"{a["file"]}\"], text (or regex), mark_only true and output \"{{dir}}/{{name}}-marked.pdf\" first, and tell the user which pages match. "
                    + "Once they agree, call redact again without mark_only and with output \"{dir}/{name}-redacted.pdf\", "
                    + "then call text on the result to confirm the words are gone."),
            new(
                "shrink_pdf",
                "Make a PDF smaller",
                "Make PDFs smaller for email or the web.",
                [new("inputs", "The PDFs, a glob or a folder."), new("preset", "lossless (the default; nothing changes how it looks), print, ebook or screen (smallest).", Required: false)],
                ["info", "optimize"],
                a => $"Make {a["inputs"]} smaller with the {Or(a, "preset", "lossless")} preset. "
                    + $"Call info on \"{a["inputs"]}\" to see the sizes; then call optimize with inputs [\"{a["inputs"]}\"], preset \"{Or(a, "preset", "lossless")}\" "
                    + "and output \"{dir}/smaller/{name}.pdf\". The ebook and screen presets recompress images, which loses detail: ask the user before using them. "
                    + "Tell the user the sizes before and after."),
        ];
    }

    private static string Or(IReadOnlyDictionary<string, string> arguments, string name, string fallback)
        => arguments.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    /// <summary>The first page of a written PDF, or a written image, fit to 1024 pixels, as PNG.</summary>
    private static ImageContentBlock Preview(string path)
    {
        using MemoryStream png = new();
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using PdfDocument document = PdfDocument.Open(path);
            using Raster<Rgba8> page = document.Pages[0].Render(PdfRenderOptions.Default with { FitWidth = 1024, FitHeight = 1024 });
            page.Save(png, "png");
        }
        else
        {
            using Raster image = Raster.Load(path);
            image.Save(png, "png");
        }

        return ImageContentBlock.FromBytes(png.ToArray(), "image/png");
    }

    private static (JsonElement Body, JsonElement Definitions) LoadWorkflowSchema()
    {
        using Stream stream = typeof(McpCommand).Assembly.GetManifestResourceStream("egap-workflow-1.json")
            ?? throw new InvalidOperationException("The workflow schema is not built into the tool.");
        return McpWorkflow.SplitSchema(stream);
    }
}
