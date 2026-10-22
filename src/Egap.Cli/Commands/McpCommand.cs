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
        McpCatalog all = McpCatalog.Build(CreateTools(allowSign));
        return allowSign ? all : all.Where(tool => !Signing.Contains(tool.Name));
    }

    private static McpToolSet CreateTools(bool allowSign)
    {
        (JsonElement body, JsonElement definitions) = LoadWorkflowSchema();
        HashSet<string> excluded = [.. McpToolSet.DefaultExcluded, "--strict"];

        // Secrets come only by environment variable name (Q16): never as text in a call, nor from a file the call names.
        foreach (string secret in (string[])["password", "user-password", "owner-password", "cert-password", "new-password"])
        {
            excluded.Add("--" + secret);
            excluded.Add("--" + secret + "-file");
        }

        return new McpToolSet
        {
            Name = "egap",
            Title = "Egap",
            WebsiteUrl = "https://github.com/inexin/Egap",
            Instructions = "egap turns HTML, Markdown, DOCX and images into PDF, and inspects, merges, splits, stamps, redacts, fills and checks PDFs on this computer with Egap, a fully managed .NET PDF library. "
                + "Every tool is an egap command; paths are files, globs or folders inside the folders the user allowed, and outputs never replace inputs unless in_place is true. "
                + "Start with info; run takes any chain of edit steps as a workflow object and needs a save step at the end (list_steps lists them). "
                + "Pages are written as 1-3,z (z is the last page). Passwords come only from environment variables named with the *_env arguments. "
                + "Every result is an object: exitCode (0 success, 1 a file failed or a check such as compare, verify or pdfa_check found a problem, 2 a usage error, 3 a safety limit), "
                + "written (the files written), output (the command's JSON or text) and messages (warnings and errors).",
            CreateRoot = () => Cli.CreateRoot(new EgapSession(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null)),
            Run = (args, console, workingDirectory, environment, setup) => Cli.Run(args, console, workingDirectory, environment, setup),
            Failure = EgapSession.FailureOf,
            Excluded = excluded,
            ReadOnly = new HashSet<string>(StringComparer.Ordinal) { "info", "text", "verify", "pdfa_check", "form_list", "attach_list", "metadata_show", "invoice_read" },
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
                    if (!allowSign && steps.FirstOrDefault(s => Signing.Contains(s.Name)) is { } step)
                    {
                        throw new UsageException($"The {step.Name} step needs the server to be started with --allow-sign.");
                    }

                    return new RunRequest(inputs, steps);
                },
                (session, request) => ((EgapSession)session).Run = request),
            Preview = Preview,
            PreviewDescription = "Also return the first page of the first file written as a picture, fit to 1024 pixels.",
        };
    }

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
