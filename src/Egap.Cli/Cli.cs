using System.CommandLine;
using System.CommandLine.Parsing;

using Egap.Cli.Commands;
using Egap.Cli.Host;

namespace Egap.Cli;

/// <summary>
/// The tool's entry point, shared by the executable and the tests: builds the commands, parses, maps errors to exit codes
/// (0 done, 1 a file failed or a check found a problem, 2 usage, 3 a safety limit) and runs unknown commands as
/// <c>egap-&lt;name&gt;</c> programs from the PATH, so <c>egap preview</c> starts egap-preview (CLI design Q2, Q12).
/// </summary>
internal static class Cli
{
    /// <summary>Runs the tool in this process; <paramref name="setup"/> may set up the session first, as egap mcp does for each call.</summary>
    public static int Run(string[] args, CliConsole console, string workingDirectory, Func<string, string?> environment, Action<CliHostSession>? setup = null)
    {
        EgapSession session = new(console, workingDirectory, environment);
        setup?.Invoke(session);

        // Numbers on a command line are written the same way everywhere: 0.8, never 0,8.
        System.Globalization.CultureInfo culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Command root = CreateRoot(session, args.Length > 0 && !args[0].StartsWith('-') ? args[0] : null);
            if (args.Length > 0 && !args[0].StartsWith('-') && !root.Subcommands.Any(c => c.Name == args[0] || c.Aliases.Contains(args[0]))
                && External.Find("egap", args[0], environment) is { } program)
            {
                return External.Run(program, args[1..], console);
            }

            InvocationConfiguration invocation = new() { Output = console.Text, Error = console.Error, EnableDefaultExceptionHandler = false };
            if (args.Length == 0)
            {
                root.Parse(["--help"]).Invoke(invocation);
                return ExitCodes.Usage;
            }

            // run parses its own arguments, since each step carries options of its own; its help comes from the parser.
            if (args[0] == "run" && !args.Skip(1).Any(a => a is "-h" or "--help" or "-?"))
            {
                return RunCommand.Execute(session, args[1..]);
            }

            ParseResult result = root.Parse(args, new ParserConfiguration { EnablePosixBundling = false });
            if (result.Errors.Count > 0 && result.Action is not System.CommandLine.Help.HelpAction)
            {
                bool atRoot = result.CommandResult.Command == root;
                if (atRoot && !args[0].StartsWith('-'))
                {
                    session.Error($"'{args[0]}' is not an egap command, and no egap-{args[0]} program is on the PATH.");
                }
                else
                {
                    foreach (ParseError error in result.Errors)
                    {
                        session.Error(error.Message);
                    }
                }

                console.Error.WriteLine($"Run '{(atRoot ? "egap" : $"egap {result.CommandResult.Command.Name}")} --help' for usage.");
                return ExitCodes.Usage;
            }

            GlobalOptions.Configure(session, result);
            return result.Invoke(invocation);
        }
        catch (UsageException error)
        {
            session.Error(error.Message);
            return ExitCodes.Usage;
        }
        catch (Exception error) when (session.Failure(error) is { } code)
        {
            session.Error(session.Message(error));
            return code;
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>The commands, by name, in the order help lists them; each is built only when asked for.</summary>
    private static readonly (string Name, Func<EgapSession, Command> Create)[] Commands =
    [
        ("info", InfoCommand.Create),
        ("convert", ConvertCommand.Create),
        ("text", TextCommand.Create),
        ("render", RenderCommand.Create),
        ("merge", MergeCommand.Create),
        ("split", SplitCommand.Create),
        ("pages", session => new PagesEdit().CreateCommand(session)),
        ("encrypt", session => new EncryptEdit().CreateCommand(session)),
        ("decrypt", session => new DecryptEdit().CreateCommand(session)),
        ("metadata", MetadataCommand.Create),
        ("optimize", session => new OptimizeEdit().CreateCommand(session)),
        ("impose", session => new ImposeEdit().CreateCommand(session)),
        ("stamp", session => new StampEdit().CreateCommand(session)),
        ("compare", CompareCommand.Create),
        ("redact", session => new RedactEdit().CreateCommand(session)),
        ("sanitize", session => new SanitizeEdit().CreateCommand(session)),
        ("form", FormCommands.Create),
        ("attach", AttachCommands.Attach),
        ("invoice", AttachCommands.Invoice),
        ("sign", SignCommand.Create),
        ("verify", CheckCommands.Verify),
        ("pdfa", CheckCommands.Pdfa),
        ("ocr", session => new OcrEdit().CreateCommand(session)),
        ("ocr-layer", session => new OcrLayerEdit().CreateCommand(session)),
        ("clean", session => new CleanEdit().CreateCommand(session)),
        ("merge-data", MergeDataCommand.Create),
        ("preflight", PrintCommands.Preflight),
        ("pdfx", PrintCommands.Pdfx),
        ("separations", PrintCommands.Separations),
        ("color", session => new ColorEdit().CreateCommand(session)),
        ("run", _ => RunCommand.Create()),
        ("mcp", McpCommand.Create),
    ];

    /// <summary>
    /// Builds the command tree; the root is named egap whatever the executable is called. With <paramref name="only"/>, the
    /// tree holds just that command, which is all a run needs and keeps start-up short.
    /// </summary>
    public static Command CreateRoot(EgapSession session, string? only = null)
    {
        Command root = new("egap", "Turn HTML, Markdown, DOCX and images into PDF, and inspect, merge, split, encrypt and edit PDFs with Egap, a fully managed PDF library.");
        root.Options.Add(new System.CommandLine.Help.HelpOption());
        root.Options.Add(new VersionOption());
        GlobalOptions.AddTo(root, session);
        bool known = only is not null && Commands.Any(c => c.Name == only);
        foreach ((string name, Func<EgapSession, Command> create) in Commands)
        {
            if (!known || name == only)
            {
                root.Subcommands.Add(create(session));
            }
        }

        return root;
    }

    /// <summary>Registers the HTML and Markdown add-ons, for the commands that read or write those formats (Q3).</summary>
    internal static void RegisterFormats()
    {
        Documents.HtmlFormat.Register();
        Documents.MarkdownFormat.Register();
    }
}
