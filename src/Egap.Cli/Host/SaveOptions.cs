using System.CommandLine;

using Egap.Pdf;

namespace Egap.Cli.Host;

/// <summary>
/// How an edited PDF is saved (CLI design Q10): the library's defaults (an edit appends an incremental update, a new
/// document is written whole), with <c>--rewrite</c>, <c>--linearize</c> and <c>--compress</c> to override; a save that would
/// break a signature stops unless <c>--allow-invalidating-signatures</c> is given.
/// </summary>
internal sealed class SaveOptions
{
    public Option<bool> Rewrite { get; } = new("--rewrite") { Description = "Write the whole file anew instead of appending the changes (breaks signatures)." };

    public Option<bool> Linearize { get; } = new("--linearize") { Description = "Lay the file out for fast web view; writes the whole file." };

    public Option<bool> Compress { get; } = new("--compress") { Description = "Pack objects into compressed object streams; writes the whole file." };

    public Option<bool> AllowInvalidatingSignatures { get; } = new("--allow-invalidating-signatures")
    {
        Description = "Save even when the change breaks a digital signature in the file.",
    };

    public Option<bool> Deterministic { get; } = new("--deterministic") { Description = "Derive the file ID from the content, so the same input always gives the same bytes." };

    /// <summary>Adds the options to a command; <paramref name="edits"/> adds the ones that matter only for an edited file.</summary>
    public void AddTo(Command command, bool edits = true)
    {
        if (edits)
        {
            command.Options.Add(Rewrite);
        }

        command.Options.Add(Linearize);
        command.Options.Add(Compress);
        if (edits)
        {
            command.Options.Add(AllowInvalidatingSignatures);
        }

        command.Options.Add(Deterministic);
    }

    /// <summary>The library's save options the flags describe, starting from <paramref name="start"/>.</summary>
    public PdfSaveOptions Build(ParseResult result, PdfSaveOptions? start = null)
    {
        PdfSaveOptions options = start ?? PdfSaveOptions.Default;
        if (result.GetResult(Rewrite) is not null && result.GetValue(Rewrite))
        {
            options = options with { Mode = PdfSaveMode.Rewrite };
        }

        if (result.GetValue(Linearize))
        {
            options = options with { Linearize = true };
        }

        if (result.GetValue(Compress))
        {
            options = options with { Compression = PdfCompression.Compact };
        }

        if (result.GetValue(Deterministic))
        {
            options = options with { Deterministic = true };
        }

        return options with { AllowInvalidatingSignatures = result.GetResult(AllowInvalidatingSignatures) is not null && result.GetValue(AllowInvalidatingSignatures) };
    }

    /// <summary>
    /// Saves a document to a full path or <c>-</c> through a temporary file, so a failed save leaves the target as it was.
    /// A save that breaks a signature without <see cref="PdfSaveOptions.AllowInvalidatingSignatures"/> fails the file.
    /// </summary>
    public static void Save(EgapSession session, PdfDocument document, string path, PdfSaveOptions options)
    {
        Output.Write(session, path, stream =>
        {
            int before = document.Warnings.Count;
            document.Save(stream, options);
            if (!options.AllowInvalidatingSignatures && document.Warnings.Skip(before).FirstOrDefault(w => w.Code == PdfWarningCode.SignatureInvalidated) is { } broken)
            {
                throw new InvalidOperationException($"{broken.Message} Nothing was written; add --allow-invalidating-signatures to save anyway.");
            }

            session.Report(path, document.Warnings.Skip(before));
        });
    }

    /// <summary>
    /// Writes a file that a library saves by path and extension (DOCX, HTML, Markdown, text) through a temporary file with
    /// the same extension next to the target, then moves it into place; <c>-</c> copies it to standard output.
    /// </summary>
    public static void WriteFile(EgapSession session, string path, string extension, Action<string> write)
    {
        string folder = path == "-" ? Path.GetTempPath() : Path.GetDirectoryName(path) ?? session.WorkingDirectory;
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, $".egap-{Environment.ProcessId}-{Environment.CurrentManagedThreadId}-{Guid.NewGuid():N}.{extension}");
        try
        {
            write(temporary);
            Output.Write(session, path, stream =>
            {
                using FileStream file = File.OpenRead(temporary);
                file.CopyTo(stream);
            });
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
