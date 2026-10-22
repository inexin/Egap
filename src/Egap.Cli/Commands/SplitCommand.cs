using System.CommandLine;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap split</c>: one file per page, per range, or per run of N pages, named by an output template whose
/// <c>{page}</c> is the part's number from 1 (the page number when splitting per page).
/// </summary>
internal static class SplitCommand
{
    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new(inPlace: false);
        Option<string[]> ranges = new("--range")
        {
            Description = "A part of its own, such as 1-3 or 4-z:odd; give it once per part. Without --range or --every, each page is a part.",
            HelpName = "range",
            AllowMultipleArgumentsPerToken = false,
        };
        Option<int> every = new("--every") { Description = "Parts of this many pages each, the last part taking what is left.", HelpName = "count" };
        SaveOptions save = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Command command = new("split", "Split PDFs into one file per page, per range, or every N pages, named by a template such as out/{name}-{page}.pdf.");
        inputs.AddTo(command);
        output.AddTo(command);
        command.Options.Add(ranges);
        command.Options.Add(every);
        save.AddTo(command, edits: false);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result);
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate template = output.Template(session, result, files)!;
            if (template.IsStdout)
            {
                throw new UsageException("split writes several files; give a folder or a template such as out/{name}-{page}.pdf.");
            }

            PdfPageRange[] parts = [.. (result.GetValue(ranges) ?? []).Select(text => PageOption.Parse(text, "--range")!)];
            int size = result.GetValue(every);
            if (size < 0 || (size > 0 && parts.Length > 0))
            {
                throw new UsageException(size < 0 ? "--every must be 1 or more." : "Give --range or --every, not both.");
            }

            PdfSaveOptions saveOptions = save.Build(result);
            string? secret = password.Read(session, result);
            return Batch.Run(session, files, batch, file =>
            {
                using PdfDocument document = PdfInput.Open(session, file, secret);
                List<string> written = [];
                List<IReadOnlyList<int>> groups = Groups(document, parts, size);
                for (int i = 0; i < groups.Count; i++)
                {
                    session.Cancellation.ThrowIfCancellationRequested();
                    string path = PartPath(session, template, file, i + 1);
                    using PdfDocument part = document.ExtractPages(groups[i]);
                    SaveOptions.Save(session, part, path, saveOptions);
                    written.Add(path);
                }

                return written;
            });
        });
        return command;
    }

    /// <summary>The pages of each part: the ranges given, runs of <paramref name="size"/> pages, or one page each.</summary>
    internal static List<IReadOnlyList<int>> Groups(PdfDocument document, PdfPageRange[] ranges, int size)
    {
        if (ranges.Length > 0)
        {
            return [.. ranges.Select(range => PageOption.Indices(document, range))];
        }

        int count = document.Pages.Count, step = size > 0 ? size : 1;
        List<IReadOnlyList<int>> groups = [];
        for (int start = 0; start < count; start += step)
        {
            groups.Add([.. Enumerable.Range(start, Math.Min(step, count - start))]);
        }

        return groups;
    }

    /// <summary>A folder gets <c>{name}-{page}.pdf</c>; a template without <c>{page}</c> gets <c>-{page}</c> before its extension.</summary>
    private static string PartPath(EgapSession session, OutputTemplate template, InputFile file, int part)
    {
        string text = template.Text;
        if (text.EndsWith('/') || text.EndsWith('\\') || (!template.HasPlaceholders && Directory.Exists(session.ResolvePath(text))))
        {
            text = Path.Combine(text, "{name}-{page}.pdf");
        }
        else if (!text.Contains("{page", StringComparison.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(text);
            text = text[..^ext.Length] + "-{page}" + (ext.Length > 0 ? ext : ".pdf");
        }

        return OutputOptions.PathFor(session, OutputTemplate.Parse(text, "page"), file, "pdf", part);
    }
}
