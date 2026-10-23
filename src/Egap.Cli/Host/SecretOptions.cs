using System.CommandLine;
using System.Text;

namespace Egap.Cli.Host;

/// <summary>
/// One secret, such as a password, given without putting it on the command line (CLI design Q9): <c>--&lt;name&gt;-env NAME</c>
/// reads an environment variable, <c>--&lt;name&gt;-file path</c> the first line of a file, and with neither egap asks on the
/// terminal. A plain <c>--&lt;name&gt; value</c> works too, with a warning that it lands in shell history.
/// </summary>
internal sealed class SecretOptions
{
    private readonly string _what;

    /// <summary>Creates the three options for <paramref name="name"/>, such as <c>password</c>, describing it as <paramref name="what"/>.</summary>
    public SecretOptions(string name, string what)
    {
        _what = what;
        Plain = new($"--{name}") { Description = $"The {what}. Prefer --{name}-env or --{name}-file: a value here lands in shell history.", HelpName = "text" };
        Env = new($"--{name}-env") { Description = $"Read the {what} from this environment variable.", HelpName = "NAME" };
        File = new($"--{name}-file") { Description = $"Read the {what} from the first line of this file.", HelpName = "path" };
    }

    public Option<string?> Plain { get; }

    public Option<string?> Env { get; }

    public Option<string?> File { get; }

    /// <summary>Adds the options to a command.</summary>
    public void AddTo(Command command)
    {
        command.Options.Add(Plain);
        command.Options.Add(Env);
        command.Options.Add(File);
    }

    /// <summary>The secret the options give, or <see langword="null"/> when none of them was used.</summary>
    public string? Read(EgapSession session, ParseResult result)
    {
        string? plain = result.GetValue(Plain), env = result.GetValue(Env), file = result.GetValue(File);
        if ((plain is null ? 0 : 1) + (env is null ? 0 : 1) + (file is null ? 0 : 1) > 1)
        {
            throw new UsageException($"Give the {_what} one way: {Plain.Name}, {Env.Name} or {File.Name}.");
        }

        if (plain is not null)
        {
            session.Warn($"{Plain.Name} puts the {_what} in shell history and the process list; {Env.Name} or {File.Name} keep it out.");
            return plain;
        }

        if (env is not null)
        {
            return session.Environment(env) ?? throw new UsageException($"{Env.Name}: the environment variable {env} is not set.");
        }

        if (file is not null)
        {
            string text = System.IO.File.ReadAllText(session.ResolvePath(file));
            int end = text.IndexOfAny(['\r', '\n']);
            return end < 0 ? text : text[..end];
        }

        return null;
    }

    /// <summary>
    /// Asks for a secret on the terminal without showing it, or returns <see langword="null"/> when standard input is not a
    /// terminal (a script, a pipe, or <c>egap mcp</c>).
    /// </summary>
    public static string? Prompt(EgapSession session, string label)
    {
        if (!ReferenceEquals(session.Console, CliConsole.System) || Console.IsInputRedirected || Console.IsErrorRedirected)
        {
            return null;
        }

        session.Console.Error.Write($"{label}: ");
        StringBuilder text = new();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        session.Console.Error.WriteLine();
        return text.ToString();
    }
}
