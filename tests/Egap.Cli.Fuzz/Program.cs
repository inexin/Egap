using System.Text;

using Egap.Cli;
using Egap.Cli.Commands;
using Egap.Cli.Host;
using Egap.Pdf;
using Egap.Pdf.Fuzz;

using SharpFuzz;

// Fuzz targets for egap's own parsers: page ranges, file#range inputs, output templates, run's words and workflow files. Each
// may only refuse its input with a UsageException (or, for a range past the file's end, an InvalidOperationException);
// anything else is a finding.
Dictionary<string, Action<Stream>> targets = new(StringComparer.Ordinal)
{
    ["pagerange"] = PageRange,
    ["filerange"] = FileRange,
    ["template"] = Template,
    ["words"] = Words,
    ["workflow"] = Workflow,
};

if (args is ["mutate", string mutateTarget, string seconds, string seeds, string findings])
{
    return Mutator.Run(targets[mutateTarget], TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)), seeds, findings);
}

if (args is [string target, string file])
{
    using FileStream input = File.OpenRead(file);
    targets[target](input);
    return 0;
}

if (args is [string stdinTarget])
{
    Fuzzer.OutOfProcess.Run(targets[stdinTarget]);
    return 0;
}

Console.Error.WriteLine($"Usage: Egap.Cli.Fuzz <target> [file] | mutate <target> <seconds> <seeds> <findings>. Targets: {string.Join(", ", targets.Keys)}.");
return 2;

static string Text(Stream input)
{
    using MemoryStream buffer = new();
    input.CopyTo(buffer);
    return Encoding.UTF8.GetString(buffer.ToArray());
}

static void PageRange(Stream input)
{
    string text = Text(input);
    int count = text.Length == 0 ? 1 : text[0] % 50;
    using PdfDocument document = PdfDocument.Create();
    for (int i = 0; i < count; i++)
    {
        document.Pages.AddBlank();
    }

    try
    {
        PageOption.Indices(document, PageOption.Parse(text.Length > 0 ? text[1..] : text, "--pages"));
    }
    catch (Exception error) when (error is UsageException or InvalidOperationException)
    {
    }
}

static void FileRange(Stream input)
{
    string folder = Path.Combine(Path.GetTempPath(), "egap-cli-fuzz");
    Directory.CreateDirectory(folder);
    File.WriteAllBytes(Path.Combine(folder, "a.pdf"), []);
    EgapSession session = new(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), folder, _ => null);
    try
    {
        MergeCommand.Parts(session, Text(input).Split('\n'));
    }

    // A path the system can't hold (a NUL in it) is an ArgumentException, which the tool reports as a failed file.
    catch (Exception error) when (error is UsageException or FileNotFoundException or DirectoryNotFoundException or IOException or ArgumentException)
    {
    }
}

static void Template(Stream input)
{
    string text = Text(input);
    try
    {
        OutputTemplate template = OutputTemplate.Parse(text, "page");
        template.Resolve(new InputFile(Path.Combine(Path.GetTempPath(), "in.pdf"), "in.pdf", "dir", 3), "pdf", 7);
    }
    catch (UsageException)
    {
    }
}

static void Words(Stream input)
{
    string[] args = Text(input).Split('\0');
    try
    {
        (_, List<StepWords> steps, _) = RunCommand.Parse(args);
        foreach (StepWords step in steps)
        {
            Steps.CommandOf(step.Name, out _, out _).Parse([.. step.Words]);
        }
    }
    catch (UsageException)
    {
    }
}

static void Workflow(Stream input)
{
    using MemoryStream buffer = new();
    input.CopyTo(buffer);
    try
    {
        List<StepWords> steps = RunCommand.ReadWorkflow(buffer.ToArray(), "fuzz.json");
        EgapSession session = new(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), Path.GetTempPath(), _ => null);
        _ = RunCommand.WriteWorkflow(steps, session);
    }
    catch (UsageException)
    {
    }
}
