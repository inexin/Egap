using System.CommandLine;
using System.Text;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>The inputs and steps of a <c>run</c> given as values, so no input is mistaken for a step or an option (<c>egap mcp</c>).</summary>
/// <param name="Inputs">The inputs: files, globs or folders.</param>
/// <param name="Steps">The steps, as a workflow file gives them.</param>
internal sealed record RunRequest(IReadOnlyList<string> Inputs, List<StepWords> Steps);

/// <summary>
/// <c>egap run</c>: steps over each input separated by a bare <c>+</c>, each step's options right after its name, with
/// <c>save</c> steps that write, as many as needed (CLI design Q4); or the same steps from a JSON workflow file
/// (egap-workflow-1.json, Q13).
/// </summary>
internal static class RunCommand
{
    /// <summary>The version of the workflow file.</summary>
    public const int WorkflowVersion = 1;

    /// <summary>The options of <c>run</c> itself, name → takes a value; every other option belongs to the step it follows.</summary>
    private static readonly Dictionary<string, bool> RunOptions = new(StringComparer.Ordinal)
    {
        ["--workflow"] = true,
        ["-w"] = true,
        ["--save-workflow"] = true,
        ["--dry-run"] = false,
        ["-r"] = false,
        ["--recursive"] = false,
        ["-j"] = true,
        ["--jobs"] = true,
        ["--in-place"] = false,
        ["--fail-fast"] = false,
        ["--password"] = true,
        ["--password-env"] = true,
        ["--password-file"] = true,
        ["-v"] = false,
        ["--verbose"] = false,
        ["-vv"] = false,
        ["--debug"] = false,
        ["-q"] = false,
        ["--quiet"] = false,
        ["--limit"] = true,
        ["--strict"] = false,
    };

    /// <summary>The command, for help; the tool parses <c>run</c>'s arguments itself, since steps carry their own options.</summary>
    public static Command Create()
    {
        Command command = new(
            "run",
            "Run steps over PDFs, separated by +: 'egap run in.pdf pages --rotate 90:1 + metadata --title Report + save out.pdf --linearize'. "
            + "Every edit command is a step ('egap run --list'); save writes, and may come more than once.")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        command.Arguments.Add(new Argument<string[]>("inputs-and-steps") { Description = "Inputs (files, globs, -), then steps separated by +.", Arity = ArgumentArity.ZeroOrMore });
        command.Options.Add(new Option<string?>("--workflow", "-w") { Description = "Take the steps from a JSON workflow file (egap-workflow-1.json).", HelpName = "file" });
        command.Options.Add(new Option<string?>("--save-workflow") { Description = "Write the steps to a JSON workflow file, then run them unless --dry-run.", HelpName = "file" });
        command.Options.Add(new Option<bool>("--dry-run") { Description = "Print the steps and the files each input would write, and stop." });
        command.Options.Add(new Option<bool>("--list") { Description = "List every step with its options; --list <step> shows one." });
        command.Options.Add(new Option<bool>("--recursive", "-r") { Description = "Take every file egap reads in folders given as inputs." });
        command.Options.Add(new Option<int>("--jobs", "-j") { Description = "How many files to work on at once; 0 for one per processor.", HelpName = "count" });
        command.Options.Add(new Option<bool>("--in-place") { Description = "Allow a save to replace its input." });
        command.Options.Add(new Option<bool>("--fail-fast") { Description = "Stop at the first file that fails." });
        command.Options.Add(new Option<string?>("--password-env") { Description = "Read the password that opens encrypted inputs from this environment variable; --password-file and --password work too.", HelpName = "NAME" });
        command.SetAction(_ => ExitCodes.Usage);
        return command;
    }

    /// <summary>Runs <c>run</c> from its raw arguments (everything after the word run).</summary>
    public static int Execute(EgapSession session, string[] args)
    {
        if (args is ["--list", ..])
        {
            Steps.List(session.Console.Text, args.Length > 1 ? args[1] : null);
            return ExitCodes.Success;
        }

        (List<string> inputs, List<StepWords> words, Dictionary<string, string?> options) = Parse(args);
        Verbosity verbosity = options.ContainsKey("-q") ? Verbosity.Quiet : options.ContainsKey("-vv") ? Verbosity.Debug : options.ContainsKey("-v") ? Verbosity.Verbose : Verbosity.Normal;
        session.Configure(verbosity, options.GetValueOrDefault("--limit"), options.ContainsKey("--strict"));
        session.InPlace = options.ContainsKey("--in-place");

        if (session.Run is RunRequest given)
        {
            if (words.Count > 0 || inputs.Count > 0 || options.ContainsKey("--workflow"))
            {
                throw new UsageException("Give the inputs and steps as values or as words, not both.");
            }

            words = given.Steps;
            inputs.AddRange(given.Inputs);
        }
        else if (options.GetValueOrDefault("--workflow") is { } workflowPath)
        {
            if (words.Count > 0)
            {
                throw new UsageException("Give the steps either on the command line or with --workflow, not both.");
            }

            words = ReadWorkflow(File.ReadAllBytes(session.ResolvePath(workflowPath)), workflowPath);
        }

        if (words.Count == 0)
        {
            throw new UsageException("No steps: 'egap run in.pdf pages --rotate 90 + save out.pdf', or --workflow file.json. 'egap run --list' lists the steps.");
        }

        List<BoundStep> plan = [.. words.Select(step => Steps.Bind(session, step))];
        if (plan[^1] is not SaveStep)
        {
            throw new UsageException("The last step must be a save, such as + save out/{name}.pdf; egap writes only where a save says.");
        }

        if (options.GetValueOrDefault("--save-workflow") is { } savePath)
        {
            string full = session.ResolvePath(savePath);
            Output.Write(session, full, stream => stream.Write(Encoding.UTF8.GetBytes(WriteWorkflow(words, session))));
        }

        int jobs = options.GetValueOrDefault("--jobs") is { } jobsText
            ? int.TryParse(jobsText, out int count) && count >= 0 ? count : throw new UsageException("--jobs must be 0 or more.")
            : 0;
        if (inputs.Count == 0)
        {
            if (options.ContainsKey("--save-workflow"))
            {
                return ExitCodes.Success;
            }

            throw new UsageException("No inputs: 'egap run in.pdf pages --rotate 90 + save out.pdf'.");
        }

        List<InputFile> files = Globber.Expand(session, inputs, options.ContainsKey("-r"));
        Dictionary<SaveStep, OutputTemplate> templates = [];
        foreach (SaveStep save in plan.OfType<SaveStep>())
        {
            OutputTemplate template = OutputTemplate.Parse(save.Path, "page");
            if (files.Count > 1 && (template.IsStdout || !template.IsPerInput))
            {
                throw new UsageException($"save {save.Path}: with {files.Count} inputs, give a template that names each, such as out/{{name}}.pdf.");
            }

            templates[save] = template;
        }

        string? password = options.GetValueOrDefault("--password") is { } plain
            ? Warned(session, plain)
            : options.GetValueOrDefault("--password-env") is { } env ? session.Environment(env) ?? throw new UsageException($"--password-env: the environment variable {env} is not set.")
            : options.GetValueOrDefault("--password-file") is { } file ? File.ReadAllLines(session.ResolvePath(file)).FirstOrDefault() ?? string.Empty
            : null;
        if (options.ContainsKey("--dry-run"))
        {
            DryRun(session, files, plan, templates);
            return ExitCodes.Success;
        }

        BatchOptions batch = new(jobs, options.ContainsKey("--fail-fast"), session.InPlace);
        return Batch.Run(session, files, batch, input => Execute(session, input, plan, templates, password));
    }

    /// <summary>Runs the plan over one input and returns the files written.</summary>
    private static List<string> Execute(EgapSession session, InputFile input, List<BoundStep> plan, Dictionary<SaveStep, OutputTemplate> templates, string? password)
    {
        List<string> written = [];
        using EditContext context = new(session, input) { AllowInvalidatingSignatures = plan.OfType<SaveStep>().Any(s => s.Options.AllowInvalidatingSignatures) };
        PdfDocument document = PdfInput.Open(session, input, password, inMemory: session.InPlace);
        try
        {
            foreach (BoundStep step in plan)
            {
                session.Cancellation.ThrowIfCancellationRequested();
                if (step is SaveStep save)
                {
                    string path = OutputOptions.PathFor(session, templates[save], input, "pdf");
                    SaveOptions.Save(session, document, path, context.Apply(save.Options));
                    written.Add(path);
                }
                else
                {
                    session.Info($"{input.Display}: {step.Source.Name}");
                    document = ((EditStep)step).Action(document, context);
                }
            }

            return written;
        }
        finally
        {
            document.Dispose();
        }
    }

    private static string Warned(EgapSession session, string password)
    {
        session.Warn("--password puts the password in shell history and the process list; --password-env or --password-file keep it out.");
        return password;
    }

    private static void DryRun(EgapSession session, List<InputFile> files, List<BoundStep> plan, Dictionary<SaveStep, OutputTemplate> templates)
    {
        TextWriter output = session.Console.Text;
        output.WriteLine("steps:");
        foreach (BoundStep step in plan)
        {
            output.WriteLine($"  {step.Source.Name}{string.Concat(step.Source.Words.Select(w => " " + Quote(w)))}");
        }

        foreach (InputFile file in files)
        {
            IEnumerable<string> targets = plan.OfType<SaveStep>().Select(save => OutputOptions.PathFor(session, templates[save], file, "pdf"));
            output.WriteLine($"{file.Display} -> {string.Join(", ", targets.Select(t => t == "-" ? "standard output" : Path.GetRelativePath(session.WorkingDirectory, t).Replace('\\', '/')))}");
        }
    }

    private static string Quote(string word) => word.Length == 0 || word.Any(char.IsWhiteSpace) ? $"\"{word}\"" : word;

    /// <summary>Splits the words into run's options, the inputs, and the steps between bare + signs.</summary>
    internal static (List<string> Inputs, List<StepWords> Steps, Dictionary<string, string?> Options) Parse(string[] args)
    {
        List<string> inputs = [];
        List<StepWords> steps = [];
        Dictionary<string, string?> options = new(StringComparer.Ordinal);
        string? name = null;
        List<string> words = [];
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (name is null)
            {
                if (RunOptions.TryGetValue(arg, out bool takesValue))
                {
                    string key = arg switch { "-w" => "--workflow", "-r" => "-r", "--recursive" => "-r", "-j" => "--jobs", "--verbose" => "-v", "--debug" => "-vv", "--quiet" => "-q", _ => arg };
                    options[key] = takesValue ? i + 1 < args.Length ? args[++i] : throw new UsageException($"{arg} needs a value.") : null;
                }
                else if (Steps.IsStep(arg))
                {
                    name = arg;
                }
                else if (arg == "+")
                {
                    throw new UsageException("A + must come between two steps, such as pages --rotate 90 + save out.pdf.");
                }
                else if (arg.StartsWith('-') && arg != "-")
                {
                    throw new UsageException($"'{arg}' is not an option of run; a step's options come after its name. 'egap run --list' lists the steps.");
                }
                else
                {
                    inputs.Add(arg);
                }

                continue;
            }

            if (arg == "+")
            {
                steps.Add(new StepWords(name, [.. words]));
                words.Clear();
                name = ++i < args.Length ? args[i] : throw new UsageException("A + must be followed by a step.");
                if (!Steps.IsStep(name))
                {
                    throw new UsageException($"'{name}' is not a step; 'egap run --list' lists them.");
                }

                continue;
            }

            words.Add(arg);
        }

        if (name is not null)
        {
            steps.Add(new StepWords(name, [.. words]));
        }

        return (inputs, steps, options);
    }

    /// <summary>Reads a workflow file: <c>{ "egap": 1, "steps": [ { "op": "pages", "rotate": "90" }, { "op": "save", "path": "out.pdf" } ] }</c>.</summary>
    internal static List<StepWords> ReadWorkflow(byte[] json, string source)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("egap", out JsonElement version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != WorkflowVersion)
            {
                throw new UsageException($"{source} is not an egap workflow: {{ \"egap\": 1, \"steps\": [ ... ] }}.");
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name is not ("egap" or "steps" or "description" or "$schema"))
                {
                    throw new UsageException($"{source}: '{property.Name}' is not part of a workflow; it holds egap, description and steps.");
                }
            }

            if (!root.TryGetProperty("steps", out JsonElement steps) || steps.ValueKind != JsonValueKind.Array)
            {
                throw new UsageException($"{source} has no steps array.");
            }

            return [.. steps.EnumerateArray().Select(step => Steps.FromJson(step, source))];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw new UsageException($"{source} is not valid JSON: {error.Message}");
        }
    }

    /// <summary>Writes steps as a workflow file.</summary>
    internal static string WriteWorkflow(IReadOnlyList<StepWords> steps, EgapSession session)
    {
        StringWriter text = new() { NewLine = "\n" };
        JsonOut.Write(text, json =>
        {
            json.WriteStartObject();
            json.WriteString("$schema", "https://raw.githubusercontent.com/inexin/Egap/main/docs/schemas/egap-workflow-1.json");
            json.WriteNumber("egap", WorkflowVersion);
            json.WriteStartArray("steps");
            foreach (StepWords step in steps)
            {
                Steps.ToJson(json, step, session);
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
        return text.ToString();
    }
}
