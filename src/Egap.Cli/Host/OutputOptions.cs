using System.CommandLine;

namespace Egap.Cli.Host;

/// <summary>
/// Where a command writes (CLI design conventions): <c>-o</c> with a file, a folder, a template such as
/// <c>out/{name}.pdf</c>, or <c>-</c> for standard output; inputs are never replaced without <c>--in-place</c>.
/// </summary>
internal sealed class OutputOptions
{
    public OutputOptions(bool inPlace = true, string part = "page")
    {
        Part = part;
        InPlace = inPlace ? new("--in-place") { Description = "Write each output over its input." } : null;
    }

    /// <summary>Gets the name of the template's part placeholder, such as <c>page</c> for <c>{page}</c>.</summary>
    public string Part { get; }

    public Option<string?> Output { get; } = new("--output", "-o")
    {
        Description = "Where to write: a file, a folder, a template with {name}, {ext}, {dir}, {index} and {format}, or - for standard output.",
        HelpName = "path",
    };

    public Option<bool>? InPlace { get; }

    /// <summary>Adds the options to a command.</summary>
    public void AddTo(Command command)
    {
        command.Options.Add(Output);
        if (InPlace is not null)
        {
            command.Options.Add(InPlace);
        }
    }

    /// <summary>Gets a value indicating whether <c>--in-place</c> was given.</summary>
    public bool IsInPlace(ParseResult result) => InPlace is not null && result.GetValue(InPlace);

    /// <summary>
    /// The output template, checked against the inputs: several inputs need a folder or a template that names each, and
    /// standard output takes one file. Throws <see cref="UsageException"/> when no output was given and none is in place.
    /// </summary>
    public OutputTemplate? Template(EgapSession session, ParseResult result, IReadOnlyList<InputFile> inputs, bool required = true)
    {
        string? text = result.GetValue(Output);
        if (text is null)
        {
            if (required && !IsInPlace(result))
            {
                throw new UsageException("No output: give -o with a file, a folder, a template such as out/{name}.pdf, or - for standard output.");
            }

            return null;
        }

        if (IsInPlace(result))
        {
            throw new UsageException("Give -o or --in-place, not both.");
        }

        OutputTemplate template = OutputTemplate.Parse(text, Part);
        if (inputs.Count > 1)
        {
            if (template.IsStdout)
            {
                throw new UsageException("-o - writes one file to standard output; with several inputs give a folder or a template such as out/{name}.pdf.");
            }

            if (!IsFolder(session, text, template) && !template.IsPerInput)
            {
                throw new UsageException($"{inputs.Count} inputs would all be written to '{text}'; give a folder or a template with {{name}}, such as out/{{name}}.pdf.");
            }
        }

        return template;
    }

    /// <summary>
    /// The full path (or <c>-</c>) for one input: a folder gets <c>{name}.&lt;extension&gt;</c>; anything else is the
    /// template resolved. Refuses to write over the input unless in place.
    /// </summary>
    public static string PathFor(EgapSession session, OutputTemplate template, InputFile input, string extension, int part = 0)
    {
        if (template.IsStdout)
        {
            return "-";
        }

        string text = IsFolder(session, template.Text, template)
            ? Path.Combine(template.Text, $"{input.Name}.{extension}")
            : template.Resolve(input, extension, part);
        string path = session.ResolvePath(text);
        if (!session.InPlace && input.Path is { } source && Batch.SamePath(path, source))
        {
            throw new UsageException($"'{text}' is the input {input.Display}; egap never writes over an input unless --in-place is given.");
        }

        return path;
    }

    /// <summary>The extension an output path asks for, lower case and without its dot, or <see langword="null"/>.</summary>
    public static string? ExtensionOf(OutputTemplate template)
    {
        if (template.IsStdout)
        {
            return null;
        }

        string extension = Path.GetExtension(template.Text).TrimStart('.').ToLowerInvariant();
        return extension.Length == 0 || extension.Contains('{', StringComparison.Ordinal) ? null : extension;
    }

    private static bool IsFolder(EgapSession session, string text, OutputTemplate template)
    {
        return text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text)));
    }
}
