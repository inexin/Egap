using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>One step of <c>egap run</c> as written: its name and its words (CLI design Q4).</summary>
/// <param name="Name">The step's name, such as <c>pages</c> or <c>save</c>.</param>
/// <param name="Words">The step's options and arguments, as on the command line.</param>
internal sealed record StepWords(string Name, IReadOnlyList<string> Words);

/// <summary>A step ready to run: an edit, or a save to a path.</summary>
internal abstract record BoundStep(StepWords Source);

/// <summary>An edit step.</summary>
internal sealed record EditStep(StepWords Source, EditAction Action) : BoundStep(Source);

/// <summary>A save step: where to write and how.</summary>
internal sealed record SaveStep(StepWords Source, string Path, PdfSaveOptions Options) : BoundStep(Source);

/// <summary>
/// The steps of <c>egap run</c>: every edit command (pages, metadata, strip-metadata, encrypt, decrypt, and the later stages'
/// edits), <c>append</c>, and <c>save</c>. Each step's options come from the same definitions its command uses, so a step
/// takes exactly what its command takes, and a workflow file names them the same way (Q13).
/// </summary>
internal static class Steps
{
    /// <summary>The edits, by step name.</summary>
    public static IReadOnlyList<Edit> Edits { get; } =
    [
        new PagesEdit(), new AppendEdit(), new MetadataEdit(), new StripMetadataEdit(), new EncryptEdit(), new DecryptEdit(),
        new OptimizeEdit(), new ImposeEdit(), new StampEdit(),
    ];

    /// <summary>Gets every step name, <c>save</c> included.</summary>
    public static IEnumerable<string> Names => Edits.Select(e => e.Name).Append("save");

    /// <summary>Whether a word names a step.</summary>
    public static bool IsStep(string word) => word == "save" || Edits.Any(e => e.Name == word);

    /// <summary>The command a step's words are parsed with: its options and arguments, nothing else.</summary>
    public static Command CommandOf(string name, out Edit? edit, out SaveOptions? save)
    {
        Command command = new(name);
        save = null;
        edit = Edits.FirstOrDefault(e => e.Name == name);
        if (edit is not null)
        {
            command.Description = edit.Description;
            edit.AddTo(command);
            return command;
        }

        if (name != "save")
        {
            throw new UsageException($"'{name}' is not a step; 'egap run --list' lists them.");
        }

        command.Description = "Write the document as it stands, to a file or a template such as out/{name}.pdf; it may come more than once.";
        command.Arguments.Add(new Argument<string>("path") { Description = "Where to write: a file, a template with {name}, {ext}, {dir} and {index}, or -." });
        save = new SaveOptions();
        save.AddTo(command);
        return command;
    }

    /// <summary>Parses and binds one step.</summary>
    public static BoundStep Bind(EgapSession session, StepWords step)
    {
        Command command = CommandOf(step.Name, out Edit? edit, out SaveOptions? save);
        ParseResult result = command.Parse([.. step.Words], new ParserConfiguration { EnablePosixBundling = false });
        if (result.Errors.Count > 0)
        {
            throw new UsageException($"{step.Name}: {string.Join(" ", result.Errors.Select(e => e.Message))} 'egap run --list {step.Name}' shows its options.");
        }

        if (edit is not null)
        {
            return new EditStep(step, edit.Bind(session, result));
        }

        string path = result.GetValue<string>("path") ?? throw new UsageException("save needs a path, such as save out/{name}.pdf.");
        return new SaveStep(step, path, save!.Build(result));
    }

    /// <summary>Lists the steps, or one step's options.</summary>
    public static void List(TextWriter output, string? name)
    {
        foreach (string step in name is null ? Names : [name])
        {
            Command command = CommandOf(step, out _, out _);
            output.WriteLine($"{step}: {command.Description}");
            foreach (Argument argument in command.Arguments)
            {
                output.WriteLine($"  <{argument.Name}>  {argument.Description}");
            }

            foreach (Option option in command.Options)
            {
                output.WriteLine($"  {option.Name}{(option.ValueType == typeof(bool) ? string.Empty : $" <{option.HelpName ?? "value"}>")}  {option.Description}");
            }

            if (name is null)
            {
                output.WriteLine();
            }
        }
    }

    /// <summary>A step's words from a workflow object: properties are options by their names, and the step's arguments by theirs.</summary>
    public static StepWords FromJson(JsonElement step, string source)
    {
        if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("op", out JsonElement op) || op.ValueKind != JsonValueKind.String)
        {
            throw new UsageException($"{source}: every step is an object with an op, such as {{ \"op\": \"pages\", \"rotate\": \"90\" }}.");
        }

        string name = op.GetString()!;
        Command command = CommandOf(name, out _, out _);
        List<string> words = [];
        List<string> positional = [];
        foreach (JsonProperty property in step.EnumerateObject())
        {
            if (property.Name == "op")
            {
                continue;
            }

            if (command.Arguments.FirstOrDefault(a => a.Name == property.Name) is not null)
            {
                positional.AddRange(Values(property, source, name));
                continue;
            }

            Option option = command.Options.FirstOrDefault(o => o.Name == "--" + property.Name)
                ?? throw new UsageException($"{source}: the step {name} has no option '{property.Name}'; 'egap run --list {name}' shows them.");
            if (option.ValueType == typeof(bool))
            {
                if (property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new UsageException($"{source}: {name}.{property.Name} is true or false.");
                }

                if (property.Value.ValueKind == JsonValueKind.True)
                {
                    words.Add(option.Name);
                }

                continue;
            }

            foreach (string value in Values(property, source, name))
            {
                words.Add(option.Name);
                words.Add(value);
            }
        }

        // Every positional value comes after --, so a file named -x.pdf is never taken for an option.
        return new StepWords(name, positional.Count > 0 ? [.. words, "--", .. positional] : words);
    }

    /// <summary>Writes a step as a workflow object; a password given as plain text is left out, with a warning.</summary>
    public static void ToJson(Utf8JsonWriter json, StepWords step, EgapSession session)
    {
        Command command = CommandOf(step.Name, out _, out _);
        ParseResult result = command.Parse([.. step.Words]);
        json.WriteStartObject();
        json.WriteString("op", step.Name);
        foreach (SymbolResult child in result.CommandResult.Children)
        {
            switch (child)
            {
                case ArgumentResult argument:
                    WriteValues(json, argument.Argument.Name, argument.Tokens.Select(t => t.Value).ToList(), argument.Argument.Arity.MaximumNumberOfValues > 1);
                    break;
                case OptionResult option when option.Option.ValueType == typeof(bool):
                    json.WriteBoolean(option.Option.Name.TrimStart('-'), option.Tokens.Count == 0 || bool.Parse(option.Tokens[0].Value));
                    break;
                case OptionResult option when option.Option.Name.EndsWith("password", StringComparison.Ordinal):
                    session.Warn($"the workflow leaves out {option.Option.Name}; use {option.Option.Name}-env or {option.Option.Name}-file to keep it in a workflow.");
                    break;
                case OptionResult option:
                    WriteValues(json, option.Option.Name.TrimStart('-'), option.Tokens.Select(t => t.Value).ToList(), option.Option.ValueType.IsArray);
                    break;
            }
        }

        json.WriteEndObject();
    }

    private static void WriteValues(Utf8JsonWriter json, string name, List<string> values, bool many)
    {
        if (many)
        {
            json.WriteStartArray(name);
            foreach (string value in values)
            {
                json.WriteStringValue(value);
            }

            json.WriteEndArray();
        }
        else if (values.Count > 0)
        {
            json.WriteString(name, values[0]);
        }
    }

    private static IEnumerable<string> Values(JsonProperty property, string source, string step)
    {
        static string One(JsonElement value, string where) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => throw new UsageException($"{where} is text, a number, true or false, or a list of them."),
        };

        string where = $"{source}: {step}.{property.Name}";
        return property.Value.ValueKind == JsonValueKind.Array
            ? [.. property.Value.EnumerateArray().Select(v => One(v, where))]
            : [One(property.Value, where)];
    }
}
