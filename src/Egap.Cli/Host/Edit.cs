using System.CommandLine;

using Egap.Pdf;

namespace Egap.Cli.Host;

/// <summary>What one input's edits share: the session, the input, and how later saves encrypt.</summary>
internal sealed class EditContext(EgapSession session, InputFile file) : IDisposable
{
    private readonly List<IDisposable> _open = [];

    /// <summary>Gets the session.</summary>
    public EgapSession Session { get; } = session;

    /// <summary>Gets the input being edited.</summary>
    public InputFile File { get; } = file;

    /// <summary>Gets or sets the encryption later saves write: the file's own unless an encrypt or decrypt said otherwise.</summary>
    public PdfEncryption Encryption { get; set; } = PdfEncryption.Keep;

    /// <summary>Gets or sets a value indicating whether edits may break signatures (<c>--allow-invalidating-signatures</c>).</summary>
    public bool AllowInvalidatingSignatures { get; set; }

    /// <summary>Gets or sets a value indicating whether an edit set the modification date itself, so a save leaves it.</summary>
    public bool KeepModificationDate { get; set; }

    /// <summary>Keeps a source document open until this input is done, such as one whose pages were imported.</summary>
    public void KeepOpen(IDisposable source) => _open.Add(source);

    /// <summary>Closes the documents kept open.</summary>
    public void Dispose()
    {
        foreach (IDisposable source in _open)
        {
            source.Dispose();
        }

        _open.Clear();
    }

    /// <summary>The save options with what the edits decided.</summary>
    public PdfSaveOptions Apply(PdfSaveOptions options)
    {
        return options with
        {
            Encryption = Encryption.Kind == PdfEncryptionKind.Keep ? options.Encryption : Encryption,
            UpdateModificationDate = options.UpdateModificationDate && !KeepModificationDate,
        };
    }
}

/// <summary>
/// A change to an open document. It may return a new document in its place; the old one is then disposed, or kept open with
/// <see cref="EditContext.KeepOpen"/> when the new one still reads from it.
/// </summary>
internal delegate PdfDocument EditAction(PdfDocument document, EditContext context);

/// <summary>
/// An edit of a PDF that works both as a command over many inputs (<c>egap pages</c>) and as a step of <c>egap run</c>
/// (CLI design Q4): its options are declared once, read once, and applied to each document.
/// </summary>
internal abstract class Edit
{
    /// <summary>Gets the step's name, and the command's unless the command is named otherwise.</summary>
    public abstract string Name { get; }

    /// <summary>Gets the help text.</summary>
    public abstract string Description { get; }

    /// <summary>Adds the edit's own options and arguments to a command.</summary>
    public abstract void AddTo(Command command);

    /// <summary>Reads the options; throws <see cref="UsageException"/> for wrong ones.</summary>
    public abstract EditAction Bind(EgapSession session, ParseResult result);

    /// <summary>A command of its own: inputs, <c>-o</c> or <c>--in-place</c>, the save options, a password and the edit's options.</summary>
    public Command CreateCommand(EgapSession session, string? name = null)
    {
        InputOptions inputs = new();
        OutputOptions output = new();
        SaveOptions save = new();
        SecretOptions password = new("password", "password that opens encrypted inputs");
        Command command = new(name ?? Name, Description);
        inputs.AddTo(command);
        output.AddTo(command);
        AddTo(command);
        save.AddTo(command);
        password.AddTo(command);
        command.SetAction(result =>
        {
            BatchOptions batch = inputs.Batch(session, result, output.IsInPlace(result));
            List<InputFile> files = inputs.Expand(session, result);
            OutputTemplate? template = output.Template(session, result, files);
            EditAction action = Bind(session, result);
            PdfSaveOptions saveOptions = save.Build(result);
            string? secret = password.Read(session, result);
            return Batch.Run(session, files, batch, file =>
            {
                string path = template is null ? file.Path ?? throw new UsageException("--in-place needs files, not standard input.") : OutputOptions.PathFor(session, template, file, "pdf");
                using EditContext context = new(session, file) { AllowInvalidatingSignatures = saveOptions.AllowInvalidatingSignatures };
                PdfDocument document = PdfInput.Open(session, file, secret, inMemory: template is null);
                try
                {
                    document = action(document, context);
                    SaveOptions.Save(session, document, path, context.Apply(saveOptions));
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
}
