using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace Egap.Documents.Markdown;

/// <summary>
/// Markdig pipelines for each set of <see cref="MarkdownExtensions"/>: CommonMark with the chosen GitHub extensions and front
/// matter. A pipeline is immutable and thread safe once built, so each set is built once and shared.
/// </summary>
internal static class MarkdownPipelines
{
    private static readonly MarkdownPipeline?[] Pipelines = new MarkdownPipeline?[(int)MarkdownExtensions.GitHub + 1];

    /// <summary>The pipeline for <paramref name="extensions"/>.</summary>
    public static MarkdownPipeline For(MarkdownExtensions extensions)
    {
        int index = (int)(extensions & MarkdownExtensions.GitHub);
        return Pipelines[index] ??= Build((MarkdownExtensions)index);
    }

    /// <summary>
    /// Parses <paramref name="markdown"/>, or returns null when Markdig refuses it as nested too deeply (more than
    /// <see cref="MarkdownPipelineBuilder.MaximumNestingDepth"/> levels of quotes, lists or brackets, which no real file has).
    /// </summary>
    public static MarkdownDocument? TryParse(string markdown, MarkdownExtensions extensions)
    {
        try
        {
            return Markdig.Markdown.Parse(markdown, For(extensions));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static MarkdownPipeline Build(MarkdownExtensions extensions)
    {
        MarkdownPipelineBuilder builder = new();
        if (extensions.HasFlag(MarkdownExtensions.FrontMatter))
        {
            builder.UseYamlFrontMatter();
        }

        if (extensions.HasFlag(MarkdownExtensions.Tables))
        {
            // GitHub's rules: rows run on until a blank line or another block, and the header row decides the column count.
            builder.UsePipeTables(new PipeTableOptions { UseGfmRules = true, UseHeaderForColumnCount = true });
        }

        if (extensions.HasFlag(MarkdownExtensions.Strikethrough))
        {
            builder.UseEmphasisExtras(EmphasisExtraOptions.Strikethrough);
        }

        if (extensions.HasFlag(MarkdownExtensions.TaskLists))
        {
            builder.UseTaskLists();
        }

        if (extensions.HasFlag(MarkdownExtensions.Autolinks))
        {
            builder.UseAutoLinks();
            builder.DocumentProcessed += GfmAutolinks.Apply;
        }

        if (extensions.HasFlag(MarkdownExtensions.Footnotes))
        {
            builder.UseFootnotes();
        }

        return builder.Build();
    }
}

/// <summary>
/// Renders Markdown as the HTML the CommonMark and GFM specs give for each example, through the same pipeline the reader
/// uses, so the compare repo can run the spec examples (CC BY-SA, kept out of Egap) against it. Not the HTML writer: that one
/// works from the model.
/// </summary>
internal static partial class MdHtmlRenderer
{
    public static string Render(string markdown, MarkdownExtensions extensions)
    {
        // Markdig writes a cell's alignment as a style; the spec examples use the align attribute, which means the same.
        return AlignStyleRegex().Replace(Markdig.Markdown.ToHtml(markdown, MarkdownPipelines.For(extensions)), " align=\"$1\"");
    }

    [System.Text.RegularExpressions.GeneratedRegex(" style=\"text-align: (left|center|right);\"")]
    private static partial System.Text.RegularExpressions.Regex AlignStyleRegex();
}
