using System.CommandLine;
using System.Text.Json;

using Egami;
using Egami.Pixels;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>
/// <c>egap compare</c>: what changed between two PDFs, in their text and in how their pages look; exit code 1 when they
/// differ, a markup PDF and difference images on request, and JSON with schema egap-compare-1.json.
/// </summary>
internal static class CompareCommand
{
    /// <summary>The version of the <c>compare --json</c> schema.</summary>
    public const int Schema = 1;

    public static Command Create(EgapSession session)
    {
        Argument<string> oldFile = new("old") { Description = "The earlier PDF." };
        Argument<string> newFile = new("new") { Description = "The later PDF." };
        Option<bool> textOnly = new("--text-only") { Description = "Compare the text only." };
        Option<bool> visualOnly = new("--visual-only") { Description = "Compare how the pages look only." };
        Option<double?> dpi = new("--dpi") { Description = "The resolution pages are compared at; 72 by default.", HelpName = "dpi" };
        Option<int?> tolerance = new("--tolerance") { Description = "How much a pixel's channels may differ and still count as the same, 0 to 255.", HelpName = "level" };
        Option<int?> minPixels = new("--min-changed-pixels") { Description = "The fewest changed pixels that make a page differ.", HelpName = "count" };
        Option<string?> markup = new("--markup") { Description = "Write the new PDF with the changes marked to this file.", HelpName = "file" };
        Option<string?> diffImages = new("--diff-images") { Description = "Write a difference image per changed page into this folder.", HelpName = "folder" };
        Option<bool> json = new("--json") { Description = $"Write the changes as JSON (schema {Schema}, docs/schemas/egap-compare-1.json)." };
        SecretOptions oldPassword = new("password", "password that opens the old file (and the new one, unless --new-password is given)");
        SecretOptions newPassword = new("new-password", "password that opens the new file");
        Command command = new("compare", "Show what changed between two PDFs, in text and in how pages look; exit code 1 when they differ.");
        command.Arguments.Add(oldFile);
        command.Arguments.Add(newFile);
        foreach (Option option in (Option[])[textOnly, visualOnly, dpi, tolerance, minPixels, markup, diffImages, json])
        {
            command.Options.Add(option);
        }

        oldPassword.AddTo(command);
        newPassword.AddTo(command);
        command.SetAction(result =>
        {
            if (result.GetValue(textOnly) && result.GetValue(visualOnly))
            {
                throw new UsageException("Give --text-only or --visual-only, not both.");
            }

            string? markupPath = result.GetValue(markup) is { } m ? session.ResolvePath(m) : null;
            string? imageFolder = result.GetValue(diffImages) is { } d ? session.ResolvePath(d) : null;
            PdfCompareOptions options = PdfCompareOptions.Default with
            {
                Text = !result.GetValue(visualOnly),
                Visual = !result.GetValue(textOnly),
                DifferenceImages = imageFolder is not null,
            };
            options = options with
            {
                Dpi = result.GetValue(dpi) ?? options.Dpi,
                Tolerance = result.GetValue(tolerance) is int t ? t is >= 0 and <= 255 ? t : throw new UsageException("--tolerance runs from 0 to 255.") : options.Tolerance,
                MinChangedPixels = result.GetValue(minPixels) ?? options.MinChangedPixels,
            };
            string? secret = oldPassword.Read(session, result);
            string? newSecret = newPassword.Read(session, result) ?? secret;
            InputFile oldInput = Single(session, result.GetValue(oldFile)!);
            InputFile newInput = Single(session, result.GetValue(newFile)!);
            using PdfDocument before = PdfInput.Open(session, oldInput, secret);
            using PdfDocument after = PdfInput.Open(session, newInput, newSecret);
            using PdfComparison comparison = PdfCompare.Run(before, after, options, session.Cancellation);
            if (markupPath is not null)
            {
                Output.Write(session, markupPath, comparison.WriteMarkup);
            }

            if (imageFolder is not null)
            {
                foreach (PdfVisualChange change in comparison.VisualChanges)
                {
                    if (change.Difference is Raster<Rgba8> image)
                    {
                        string path = Path.Combine(imageFolder, $"diff-{change.OldPage}-{change.NewPage}.png");
                        Output.Write(session, path, stream => image.Save(stream, "png"));
                    }
                }
            }

            if (result.GetValue(json))
            {
                WriteJson(session.Console.Text, oldInput, newInput, comparison);
            }
            else
            {
                WriteText(session.Console.Text, comparison);
            }

            return comparison.HasChanges ? ExitCodes.Failed : ExitCodes.Success;
        });
        return command;
    }

    private static InputFile Single(EgapSession session, string pattern)
    {
        List<InputFile> files = Globber.Expand(session, [pattern], recursive: false);
        return files.Count == 1 ? files[0] : throw new UsageException($"'{pattern}' names {files.Count} files; compare takes one old and one new file.");
    }

    private static void WriteText(TextWriter output, PdfComparison comparison)
    {
        if (!comparison.HasChanges)
        {
            output.WriteLine("no differences");
            return;
        }

        foreach (PdfPageMatch page in comparison.Pages)
        {
            if (page.OldPage is null)
            {
                output.WriteLine($"page {page.NewPage}: added");
            }
            else if (page.NewPage is null)
            {
                output.WriteLine($"page {page.OldPage}: removed");
            }
        }

        foreach (PdfTextChange change in comparison.TextChanges)
        {
            string where = change.NewPage is { } p ? $"page {p}" : $"old page {change.OldPage}";
            output.WriteLine(change.Kind switch
            {
                PdfTextChangeKind.Inserted => $"{where}: inserted \"{change.NewText}\"",
                PdfTextChangeKind.Deleted => $"{where}: deleted \"{change.OldText}\"",
                PdfTextChangeKind.Moved => $"{where}: moved \"{change.NewText}\" from page {change.OldPage}",
                _ => $"{where}: replaced \"{change.OldText}\" with \"{change.NewText}\"",
            });
        }

        foreach (PdfVisualChange change in comparison.VisualChanges)
        {
            output.WriteLine($"page {change.NewPage}: looks different in {change.Boxes.Count} area{(change.Boxes.Count == 1 ? string.Empty : "s")}");
        }
    }

    private static void WriteJson(TextWriter output, InputFile oldInput, InputFile newInput, PdfComparison comparison)
    {
        JsonOut.Write(output, json =>
        {
            json.WriteStartObject();
            json.WriteNumber("schema", Schema);
            json.WriteString("old", oldInput.Display);
            json.WriteString("new", newInput.Display);
            json.WriteBoolean("identical", !comparison.HasChanges);
            json.WriteStartArray("pages");
            foreach (PdfPageMatch page in comparison.Pages)
            {
                json.WriteStartObject();
                json.Number("old", page.OldPage);
                json.Number("new", page.NewPage);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("textChanges");
            foreach (PdfTextChange change in comparison.TextChanges)
            {
                json.WriteStartObject();
                json.WriteString("kind", InfoCommand.Kebab(change.Kind.ToString()));
                json.Number("oldPage", change.OldPage);
                json.Number("newPage", change.NewPage);
                json.WriteString("oldText", change.OldText);
                json.WriteString("newText", change.NewText);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteStartArray("visualChanges");
            foreach (PdfVisualChange change in comparison.VisualChanges)
            {
                json.WriteStartObject();
                json.WriteNumber("oldPage", change.OldPage);
                json.WriteNumber("newPage", change.NewPage);
                json.WriteStartArray("boxes");
                foreach (PdfRectangle box in change.Boxes)
                {
                    WriteBox(json, box);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        });
    }

    private static void WriteBox(Utf8JsonWriter json, PdfRectangle box)
    {
        json.WriteStartArray();
        foreach (double value in (double[])[box.Left, box.Bottom, box.Left + box.Width, box.Bottom + box.Height])
        {
            json.WriteNumberValue(Math.Round(value, 2));
        }

        json.WriteEndArray();
    }
}
