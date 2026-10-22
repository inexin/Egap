using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Egap.Cli.Commands;
using Egap.Cli.Host;

namespace Egap.Cli.Tests;

/// <summary>
/// The command reference (docs/articles/cli/commands.md) is the command tree written out, so any change to a name, an
/// option or a description shows in review; and every egap line in the CLI docs must parse. Set EGAP_UPDATE_DOCS=1 to
/// write the reference again after a deliberate change.
/// </summary>
public sealed partial class DocsTests
{
    private static readonly string Docs = Path.Combine(Path.GetDirectoryName(Tool.Files)!, "..", "docs", "articles", "cli");

    [Fact]
    public void TheCommandReferenceMatchesTheCommandTree()
    {
        string expected = Reference();
        string path = Path.Combine(Docs, "commands.md");
        if (Environment.GetEnvironmentVariable("EGAP_UPDATE_DOCS") == "1")
        {
            File.WriteAllText(path, expected);
        }

        Assert.Equal(expected, File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryExampleInTheDocsParses()
    {
        // Cli.Run parses with the invariant culture, so 0.2 is a number whatever the machine's culture.
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        EgapSession session = new(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null);
        Command root = Cli.CreateRoot(session);
        List<string> problems = [];
        int examples = 0;
        foreach (string page in Directory.GetFiles(Docs, "*.md"))
        {
            foreach (Match block in ShellBlock().Matches(File.ReadAllText(page)))
            {
                foreach (string line in block.Groups[1].Value.Split('\n'))
                {
                    string text = line.Split(" # ")[0].Trim();
                    if (!text.StartsWith("egap ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    examples++;
                    string[] words = Words(text)[1..];
                    string? problem = words[0] switch
                    {
                        "run" => RunProblem(words[1..]),
                        "preview" => null,
                        _ => root.Parse(words).Errors is { Count: > 0 } errors ? string.Join(" ", errors.Select(e => e.Message)) : null,
                    };
                    if (problem is not null)
                    {
                        problems.Add($"{Path.GetFileName(page)}: {text}: {problem}");
                    }
                }
            }
        }

        Assert.True(examples > 20, $"only {examples} examples found");
        Assert.True(problems.Count == 0, string.Join(" || ", problems));
    }

    private static string? RunProblem(string[] args)
    {
        try
        {
            (_, List<StepWords> steps, _) = RunCommand.Parse(args);
            foreach (StepWords step in steps)
            {
                Command command = Steps.CommandOf(step.Name, out _, out _);
                if (command.Parse([.. step.Words]).Errors is { Count: > 0 } errors)
                {
                    return string.Join(" ", errors.Select(e => e.Message));
                }
            }

            return null;
        }
        catch (UsageException error)
        {
            return error.Message;
        }
    }

    /// <summary>Splits a shell line into words: spaces separate them, and single or double quotes keep spaces in one.</summary>
    private static string[] Words(string line)
    {
        List<string> words = [];
        StringBuilder word = new();
        char quote = '\0';
        bool any = false;
        foreach (char c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                any = true;
            }
            else if (c == ' ')
            {
                if (word.Length > 0 || any)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    any = false;
                }
            }
            else
            {
                word.Append(c);
            }
        }

        if (word.Length > 0 || any)
        {
            words.Add(word.ToString());
        }

        return [.. words];
    }

    /// <summary>The reference: every command and subcommand with its arguments and options, then every run step.</summary>
    private static string Reference()
    {
        EgapSession session = new(new CliConsole(() => Stream.Null, () => Stream.Null, TextWriter.Null, TextWriter.Null), ".", _ => null);
        Command root = Cli.CreateRoot(session);
        StringBuilder text = new();
        text.Append("# egap command reference\n\n");
        text.Append("Every command, its arguments and its options, as `egap <command> --help` shows them. This page is written from the\n");
        text.Append("command tree by the tests, so it always matches the tool. The [guide](index.md) explains the conventions all commands\n");
        text.Append("share. Options every command takes: ");
        text.Append(string.Join(", ", root.Options.Where(o => o.Recursive).Select(o => $"`{o.Name}`")));
        text.Append(".\n");
        foreach (Command command in root.Subcommands)
        {
            Write(text, command, "egap");
        }

        text.Append("\n## run steps\n\nThe steps `egap run` chains with `+`, each with the options of its command; a workflow file names them the same way,\n");
        text.Append("without the dashes.\n");
        foreach (string name in Steps.Names)
        {
            Command step = Steps.CommandOf(name, out _, out _);
            text.Append(CultureInfo.InvariantCulture, $"\n### {name}\n\n{step.Description}\n");
            Options(text, step);
        }

        return text.ToString();
    }

    private static void Write(StringBuilder text, Command command, string parent)
    {
        string name = $"{parent} {command.Name}";
        if (command.Subcommands.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"\n## {name}\n\n{command.Description}\n");
            foreach (Command child in command.Subcommands)
            {
                Write(text, child, name);
            }

            return;
        }

        string arguments = string.Concat(command.Arguments.Select(a => a.Arity.MaximumNumberOfValues > 1 ? $" <{a.Name}>..." : $" <{a.Name}>"));
        text.Append(CultureInfo.InvariantCulture, $"\n## {name}\n\n`{name}{arguments} [options]`\n\n{command.Description}\n");
        Options(text, command);
    }

    private static void Options(StringBuilder text, Command command)
    {
        if (command.Arguments.Count == 0 && command.Options.Count == 0)
        {
            return;
        }

        text.Append("\n| Argument or option | What it does |\n| --- | --- |\n");
        foreach (Argument argument in command.Arguments)
        {
            text.Append(CultureInfo.InvariantCulture, $"| `<{argument.Name}>` | {Cell(argument.Description)} |\n");
        }

        foreach (Option option in command.Options)
        {
            string aliases = string.Concat(option.Aliases.Order(StringComparer.Ordinal).Select(a => $", `{a}`"));
            string value = option.ValueType == typeof(bool) || option.ValueType == typeof(bool?) ? string.Empty : $" <{option.HelpName ?? "value"}>";
            text.Append(CultureInfo.InvariantCulture, $"| `{option.Name}{value}`{aliases} | {Cell(option.Description)} |\n");
        }
    }

    private static string Cell(string? text) => (text ?? string.Empty).Replace("|", "\\|", StringComparison.Ordinal).Replace("*", "\\*", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    [GeneratedRegex(@"```sh\n(.*?)```", RegexOptions.Singleline)]
    private static partial Regex ShellBlock();
}
