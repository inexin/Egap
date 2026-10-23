using System.CommandLine;

namespace Egap.Cli.Host;

/// <summary>The inputs of a command and how a batch over them runs (CLI design Q13).</summary>
internal sealed class InputOptions
{
    public InputOptions(string description = "PDF files, globs (*, ?, **) expanded the same way in every shell, folders with --recursive, or - for standard input.")
    {
        Inputs = new("inputs") { Description = description, Arity = ArgumentArity.OneOrMore };
    }

    public Argument<string[]> Inputs { get; }

    public Option<bool> Recursive { get; } = new("--recursive", "-r") { Description = "Take every file egap reads in folders given as inputs, at any depth." };

    public Option<int> Jobs { get; } = new("--jobs", "-j") { Description = "How many files to work on at once; 0 (the default) for one per processor.", HelpName = "count" };

    public Option<bool> FailFast { get; } = new("--fail-fast") { Description = "Stop at the first file that fails." };

    /// <summary>Adds the inputs and the batch options.</summary>
    public void AddTo(Command command)
    {
        command.Arguments.Add(Inputs);
        command.Options.Add(Recursive);
        command.Options.Add(Jobs);
        command.Options.Add(FailFast);
    }

    /// <summary>The inputs, expanded.</summary>
    public List<InputFile> Expand(EgapSession session, ParseResult result)
    {
        return Globber.Expand(session, result.GetValue(Inputs) ?? [], result.GetValue(Recursive));
    }

    /// <summary>The batch options; <paramref name="inPlace"/> says whether the command allows outputs to replace inputs.</summary>
    public BatchOptions Batch(EgapSession session, ParseResult result, bool inPlace = false)
    {
        session.InPlace = inPlace;
        int jobs = result.GetValue(Jobs);
        if (jobs < 0)
        {
            throw new UsageException("--jobs must be 0 or more.");
        }

        return new BatchOptions(jobs, result.GetValue(FailFast), inPlace);
    }
}
