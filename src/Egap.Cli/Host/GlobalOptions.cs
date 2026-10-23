using System.CommandLine;

namespace Egap.Cli.Host;

/// <summary>
/// The options every command shares (CLI design Q12): verbosity, safety limits and strict reading. One set per command tree,
/// since an option belongs to the commands it is added to.
/// </summary>
internal sealed class GlobalOptions
{
    public Option<bool> Verbose { get; } = new("--verbose", "-v") { Description = "Write information to standard error.", Recursive = true };

    public Option<bool> Debug { get; } = new("--debug", "-vv") { Description = "Write debugging detail to standard error.", Recursive = true };

    public Option<bool> Quiet { get; } = new("--quiet", "-q") { Description = "Write only errors to standard error.", Recursive = true };

    public Option<string?> Limit { get; } = new("--limit")
    {
        Description = "Safety limits, such as objects=2M,decoded=512MB,ratio=200 (names: objects, decoded, ratio, depth, xref). EGAP_LIMITS sets them too.",
        HelpName = "name=value,...",
        Recursive = true,
    };

    public Option<bool> Strict { get; } = new("--strict")
    {
        Description = "Refuse damaged files instead of repairing them; a repair then fails the file (exit code 1).",
        Recursive = true,
    };

    /// <summary>Adds the shared options to the root command and keeps them on the session.</summary>
    public static void AddTo(Command root, EgapSession session)
    {
        GlobalOptions options = new();
        root.Options.Add(options.Verbose);
        root.Options.Add(options.Debug);
        root.Options.Add(options.Quiet);
        root.Options.Add(options.Limit);
        root.Options.Add(options.Strict);
        session.Globals = options;
    }

    /// <summary>Configures the session from the shared options.</summary>
    public static void Configure(EgapSession session, ParseResult result)
    {
        GlobalOptions options = session.Globals ?? throw new InvalidOperationException("The command tree has no shared options.");
        Verbosity verbosity = result.GetValue(options.Quiet) ? Verbosity.Quiet
            : result.GetValue(options.Debug) ? Verbosity.Debug
            : result.GetValue(options.Verbose) ? Verbosity.Verbose
            : Verbosity.Normal;
        session.Configure(verbosity, result.GetValue(options.Limit), result.GetValue(options.Strict));
    }
}
