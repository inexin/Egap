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
    static Cli()
    {
        // All four packages (Q3): HTML and Markdown come from their add-ons once registered.
        Documents.HtmlFormat.Register();
        Documents.MarkdownFormat.Register();
    }

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
            Command root = CreateRoot(session);
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

    /// <summary>Builds the command tree; the root is named egap whatever the executable is called.</summary>
    public static Command CreateRoot(EgapSession session)
    {
        Command root = new("egap", "Turn HTML, Markdown, DOCX and images into PDF, and inspect, merge, split, encrypt and edit PDFs with Egap, a fully managed PDF library.");
        root.Options.Add(new System.CommandLine.Help.HelpOption());
        root.Options.Add(new VersionOption());
        GlobalOptions.AddTo(root, session);
        root.Subcommands.Add(InfoCommand.Create(session));
        root.Subcommands.Add(ConvertCommand.Create(session));
        root.Subcommands.Add(TextCommand.Create(session));
        root.Subcommands.Add(RenderCommand.Create(session));
        root.Subcommands.Add(MergeCommand.Create(session));
        root.Subcommands.Add(SplitCommand.Create(session));
        root.Subcommands.Add(new PagesEdit().CreateCommand(session));
        root.Subcommands.Add(new EncryptEdit().CreateCommand(session));
        root.Subcommands.Add(new DecryptEdit().CreateCommand(session));
        root.Subcommands.Add(MetadataCommand.Create(session));
        root.Subcommands.Add(new OptimizeEdit().CreateCommand(session));
        root.Subcommands.Add(new ImposeEdit().CreateCommand(session));
        root.Subcommands.Add(new StampEdit().CreateCommand(session));
        root.Subcommands.Add(CompareCommand.Create(session));
        root.Subcommands.Add(new RedactEdit().CreateCommand(session));
        root.Subcommands.Add(new SanitizeEdit().CreateCommand(session));
        root.Subcommands.Add(FormCommands.Create(session));
        root.Subcommands.Add(AttachCommands.Attach(session));
        root.Subcommands.Add(AttachCommands.Invoice(session));
        root.Subcommands.Add(SignCommand.Create(session));
        root.Subcommands.Add(CheckCommands.Verify(session));
        root.Subcommands.Add(CheckCommands.Pdfa(session));
        root.Subcommands.Add(new OcrLayerEdit().CreateCommand(session));
        root.Subcommands.Add(new CleanEdit().CreateCommand(session));
        root.Subcommands.Add(MergeDataCommand.Create(session));
        root.Subcommands.Add(PrintCommands.Preflight(session));
        root.Subcommands.Add(PrintCommands.Pdfx(session));
        root.Subcommands.Add(PrintCommands.Separations(session));
        root.Subcommands.Add(new ColorEdit().CreateCommand(session));
        root.Subcommands.Add(RunCommand.Create());
        root.Subcommands.Add(McpCommand.Create(session));
        return root;
    }
}
