using System.CommandLine;
using System.Globalization;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap pages</c>: select, reorder, rotate, delete and insert pages, and set page labels, in that order. An edit keeps
/// the file's signatures where it can, appending the change (CLI design Q10); choosing a page twice makes a new document.
/// </summary>
internal static class PagesCommand
{
    public static Command Create(EgapSession session)
    {
        InputOptions inputs = new();
        OutputOptions output = new();
        Option<string?> select = new("--select") { Description = "Keep these pages in this order, such as 3,1,2 or 1-z,x5; a page named twice is copied.", HelpName = "range" };
        Option<string?> delete = new("--delete") { Description = "Remove these pages, such as 1 or 4-6.", HelpName = "range" };
        Option<string[]> rotate = new("--rotate")
        {
            Description = "Turn pages by 90, 180 or 270 degrees (or -90), all of them or those after a colon: 90 or 90:2-4; give it more than once.",
            HelpName = "angle[:range]",
            AllowMultipleArgumentsPerToken = false,
        };
        Option<string[]> blank = new("--blank")
        {
            Description = "Insert a blank page, the size of its neighbour, before this page number, or at the end with end; give it more than once.",
            HelpName = "page|end",
            AllowMultipleArgumentsPerToken = false,
        };
        Option<string[]> label = new("--label")
        {
            Description = "Number pages from a page on: page:style[:prefix[:first]], style D (1, 2), r (i, ii), R (I, II), a, A or none, such as 1:r or 5:D::1; give it more than once.",
            HelpName = "label",
            AllowMultipleArgumentsPerToken = false,
        };
        SaveOptions save = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Command command = new("pages", "Select, reorder, rotate, delete and insert pages, and set page labels, applied in that order.");
        inputs.AddTo(command);
        output.AddTo(command);
        command.Options.Add(select);
        command.Options.Add(delete);
        command.Options.Add(rotate);
        command.Options.Add(blank);
        command.Options.Add(label);
        save.AddTo(command);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result, output.IsInPlace(result));
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files);
            PdfPageRange? selected = PageOption.Parse(result.GetValue(select), "--select");
            PdfPageRange? deleted = PageOption.Parse(result.GetValue(delete), "--delete");
            List<(int Angle, PdfPageRange? Range)> turns = [.. (result.GetValue(rotate) ?? []).Select(Turn)];
            string[] blanks = result.GetValue(blank) ?? [];
            List<PdfPageLabelRange> labels = [.. (result.GetValue(label) ?? []).Select(Label)];
            if (selected is null && deleted is null && turns.Count == 0 && blanks.Length == 0 && labels.Count == 0)
            {
                throw new UsageException("Nothing to do: give --select, --delete, --rotate, --blank or --label.");
            }

            PdfSaveOptions saveOptions = save.Build(result);
            string? secret = password.Read(session, result);
            return Batch.Run(session, files, batch, file =>
            {
                string path = template is null ? file.Path ?? throw new UsageException("--in-place needs files, not standard input.") : OutputOptions.PathFor(session, template, file, "pdf");
                PdfDocument document = PdfInput.Open(session, file, secret, inMemory: template is null);
                try
                {
                    if (selected is not null)
                    {
                        document = Select(document, PageOption.Indices(document, selected));
                    }

                    if (deleted is not null)
                    {
                        foreach (int index in PageOption.Indices(document, deleted).Distinct().OrderDescending())
                        {
                            document.Pages.RemoveAt(index);
                        }
                    }

                    foreach ((int angle, PdfPageRange? range) in turns)
                    {
                        foreach (int index in PageOption.Indices(document, range).Distinct())
                        {
                            PdfPage page = document.Pages[index];
                            page.Rotate = ((page.Rotate + angle) % 360 + 360) % 360;
                        }
                    }

                    foreach (string at in blanks)
                    {
                        Insert(document, at);
                    }

                    foreach (PdfPageLabelRange range in labels)
                    {
                        if (range.StartIndex >= document.Pages.Count)
                        {
                            throw new InvalidOperationException($"--label starts at page {range.StartIndex + 1}, but the file has {document.Pages.Count} pages.");
                        }

                        document.Pages.SetLabelRange(range);
                    }

                    SaveOptions.Save(session, document, path, saveOptions);
                    return [path];
                }
                finally
                {
                    document.Dispose();
                }
            });
        });
        return command;
    }

    /// <summary>
    /// Keeps the chosen pages in the chosen order. Without repeats the pages are moved and removed in place, so the save stays
    /// incremental; a page chosen twice needs a new document.
    /// </summary>
    private static PdfDocument Select(PdfDocument document, IReadOnlyList<int> chosen)
    {
        if (chosen.Distinct().Count() != chosen.Count)
        {
            PdfDocument copy = document.ExtractPages(chosen);
            using MemoryStream bytes = new();
            copy.Save(bytes, PdfSaveOptions.Default with { Mode = PdfSaveMode.Rewrite });
            copy.Dispose();
            document.Dispose();
            return PdfDocument.Open(bytes.ToArray(), PdfReadOptions.Default);
        }

        PdfPage[] pages = [.. chosen.Select(index => document.Pages[index])];
        for (int index = document.Pages.Count - 1; index >= 0; index--)
        {
            if (!pages.Contains(document.Pages[index]))
            {
                document.Pages.RemoveAt(index);
            }
        }

        for (int target = 0; target < pages.Length; target++)
        {
            int from = document.Pages.IndexOf(pages[target]);
            if (from != target)
            {
                document.Pages.Move(from, target);
            }
        }

        return document;
    }

    private static void Insert(PdfDocument document, string at)
    {
        int count = document.Pages.Count;
        int index = at.Equals("end", StringComparison.OrdinalIgnoreCase) ? count
            : int.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out int page) && page >= 1 ? page - 1
            : throw new UsageException($"--blank '{at}' is not a page number or end.");
        if (index > count)
        {
            throw new InvalidOperationException($"--blank {at}: the file has {count} pages.");
        }

        PdfRectangle? size = count == 0 ? null : document.Pages[Math.Min(index, count - 1)].MediaBox;
        document.Pages.InsertBlank(index, size);
    }

    private static (int Angle, PdfPageRange? Range) Turn(string text)
    {
        int colon = text.IndexOf(':', StringComparison.Ordinal);
        string angle = colon < 0 ? text : text[..colon];
        PdfPageRange? range = colon < 0 ? null : PageOption.Parse(text[(colon + 1)..], "--rotate");
        return int.TryParse(angle, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int degrees) && degrees % 90 == 0
            ? (degrees, range)
            : throw new UsageException($"--rotate '{text}' does not turn by a multiple of 90 degrees, such as 90 or 180:2-4.");
    }

    private static PdfPageLabelRange Label(string text)
    {
        string[] parts = text.Split(':');
        if (parts.Length is < 2 or > 4 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int page) || page < 1)
        {
            throw new UsageException($"--label '{text}' is not page:style[:prefix[:first]], such as 1:r or 5:D::1.");
        }

        PdfPageLabelStyle style = parts[1] switch
        {
            "D" => PdfPageLabelStyle.Arabic,
            "r" => PdfPageLabelStyle.LowerRoman,
            "R" => PdfPageLabelStyle.UpperRoman,
            "a" => PdfPageLabelStyle.LowerLetters,
            "A" => PdfPageLabelStyle.UpperLetters,
            "none" or "" => PdfPageLabelStyle.None,
            _ => throw new UsageException($"--label '{text}': the style is D, r, R, a, A or none."),
        };
        int first = 1;
        if (parts.Length == 4 && (!int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out first) || first < 1))
        {
            throw new UsageException($"--label '{text}': the first number must be 1 or more.");
        }

        string? prefix = parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
        return new PdfPageLabelRange(page - 1, style, prefix, first);
    }
}
