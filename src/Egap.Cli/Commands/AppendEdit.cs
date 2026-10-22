using System.CommandLine;

using Egap.Cli.Host;
using Egap.Pdf;

namespace Egap.Cli.Commands;

/// <summary>The <c>append</c> step of <c>egap run</c>: the pages of other PDFs after the document's own, each with its own pages after a #.</summary>
internal sealed class AppendEdit : Edit
{
    private readonly Argument<string[]> _files = new("files")
    {
        Description = "PDF files or globs, each optionally with its pages after a #, such as appendix.pdf#1-3.",
        Arity = ArgumentArity.OneOrMore,
    };

    private readonly Option<bool> _joinFields = new("--join-fields") { Description = "Join form fields of the same name into one field instead of renaming them apart." };

    /// <inheritdoc/>
    public override string Name => "append";

    /// <inheritdoc/>
    public override string Description => "Add the pages of other PDFs at the end, each with its own pages after a #, as merge takes them.";

    /// <inheritdoc/>
    public override void AddTo(Command command)
    {
        command.Arguments.Add(_files);
        command.Options.Add(_joinFields);
    }

    /// <inheritdoc/>
    public override EditAction Bind(EgapSession session, ParseResult result)
    {
        string[] files = result.GetValue(_files) ?? [];
        PdfImportOptions options = PdfImportOptions.Default with { JoinFields = result.GetValue(_joinFields) };
        return (document, context) =>
        {
            foreach ((InputFile file, PdfPageRange? range) in MergeCommand.Parts(session, files))
            {
                // The source stays open until the document is written, which ends this input's run.
                PdfDocument source = PdfInput.Open(session, file, null);
                context.KeepOpen(source);
                document.Pages.Import(source, PageOption.Indices(source, range), options: options);
            }

            return document;
        };
    }
}
