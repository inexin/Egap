using System.CommandLine;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap merge</c>: the one many-into-one command (CLI design Q13). Each input may carry its own pages after a <c>#</c>,
/// such as <c>body.pdf#2-z</c>, like a PDF link's <c>#page=</c>; a file whose real name holds the <c>#</c> wins (Q5).
/// </summary>
internal static class MergeCommand
{
    public static Command Create(EgapSession session)
    {
        Argument<string[]> inputs = new("inputs")
        {
            Description = "PDF files or globs, in order, each optionally with its pages after a #, such as cover.pdf body.pdf#2-z appendix.pdf#1-3.",
            Arity = ArgumentArity.OneOrMore,
        };
        Option<string?> output = new("--output", "-o") { Description = "The file to write, or - for standard output.", HelpName = "path", Required = true };
        Option<bool> joinFields = new("--join-fields") { Description = "Join form fields of the same name into one field instead of renaming them apart." };
        SaveOptions save = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Command command = new("merge", "Join PDFs into one, in order, each with its own pages: a.pdf b.pdf#2-z c.pdf#1,3 -o all.pdf.");
        command.Arguments.Add(inputs);
        command.Options.Add(output);
        command.Options.Add(joinFields);
        save.AddTo(command, edits: false);
        password.AddTo(command);
        command.SetAction(result =>
        {
            string? secret = password.Read(session, result);
            List<(InputFile File, PdfPageRange? Range)> parts = Parts(session, result.GetValue(inputs) ?? []);
            string target = result.GetValue(output)!;
            string path = target == "-" ? "-" : session.ResolvePath(target);
            if (parts.Any(p => p.File.Path is { } source && Batch.SamePath(path, source)))
            {
                throw new UsageException($"'{target}' is one of the inputs; egap never writes over an input.");
            }

            PdfImportOptions import = PdfImportOptions.Default with { JoinFields = result.GetValue(joinFields) };
            List<PdfDocument> open = [];
            try
            {
                using PdfDocument merged = PdfDocument.Create();
                for (int i = 0; i < parts.Count; i++)
                {
                    session.Cancellation.ThrowIfCancellationRequested();
                    (InputFile file, PdfPageRange? range) = parts[i];
                    PdfDocument source;
                    try
                    {
                        source = PdfInput.Open(session, file, secret);
                    }
                    catch (Exception error) when (session.Failure(error) is not null)
                    {
                        throw new InvalidOperationException($"{file.Display}: {session.Message(error)}", error);
                    }

                    open.Add(source);
                    merged.Pages.Import(source, PageOption.Indices(source, range), options: import);
                    session.Progress?.Invoke(i + 1, parts.Count);
                }

                SaveOptions.Save(session, merged, path, save.Build(result));
                session.Info($"merged {parts.Count} files, {merged.Pages.Count} pages");
            }
            finally
            {
                foreach (PdfDocument source in open)
                {
                    source.Dispose();
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    /// <summary>
    /// The inputs with their pages: a name that exists as written is a whole file; otherwise the text after the last
    /// <c>#</c> is its pages. A glob before the <c>#</c> gives every file it finds the same pages.
    /// </summary>
    internal static List<(InputFile File, PdfPageRange? Range)> Parts(EgapSession session, IReadOnlyList<string> inputs)
    {
        List<(InputFile, PdfPageRange?)> parts = [];
        foreach (string input in inputs)
        {
            string pattern = input;
            PdfPageRange? range = null;
            int hash = input.LastIndexOf('#');
            if (hash > 0 && input != "-" && !File.Exists(session.ResolvePath(input)))
            {
                pattern = input[..hash];
                range = PageOption.Parse(input[(hash + 1)..], $"'{input}':");
            }

            foreach (InputFile file in Globber.Expand(session, [pattern], recursive: false))
            {
                parts.Add((file with { Index = parts.Count }, range));
            }
        }

        return parts;
    }
}
