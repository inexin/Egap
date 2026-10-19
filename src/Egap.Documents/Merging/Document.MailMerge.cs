namespace Egap.Documents;

/// <summary>Mail merge.</summary>
public sealed partial class Document
{
    /// <summary>
    /// Fills this document's merge fields (MERGEFIELD) from one record, in place: each field becomes its value as plain
    /// text in the field's formatting, written with the field's number (<c>\#</c>), date (<c>\@</c>) and text (<c>\*</c>)
    /// formats and the text it puts before and after a value (<c>\b</c>, <c>\f</c>). IF fields are then updated.
    /// </summary>
    /// <param name="record">The record's values.</param>
    /// <param name="options">How the fields are filled; null for the defaults.</param>
    /// <returns>The fields the record had no value for, which were removed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is null.</exception>
    public IReadOnlyList<ConversionWarning> FillMergeFields(IMergeData record, MailMergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        MailMerger merger = new(this, options ?? new MailMergeOptions());
        merger.Fill(record);
        return merger.Warnings;
    }

    /// <summary>
    /// Makes one document per record, each a copy of this template filled as <see cref="FillMergeFields"/> fills it, made
    /// as the sequence is read; the template itself is not changed.
    /// </summary>
    /// <param name="records">The records, read once, as the documents are asked for.</param>
    /// <param name="options">How the fields are filled, and where warnings go; null for the defaults.</param>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is null.</exception>
    public IEnumerable<Document> MailMerge(IEnumerable<IMergeData> records, MailMergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        return Merged(records, options ?? new MailMergeOptions());

        IEnumerable<Document> Merged(IEnumerable<IMergeData> records, MailMergeOptions options)
        {
            foreach (IMergeData record in records)
            {
                Document copy = Clone();
                Report(copy.FillMergeFields(record, options), options);
                yield return copy;
            }
        }
    }

    /// <summary>
    /// Makes one document of every record: the template's sections once per record, filled, each record starting as
    /// <see cref="MailMergeOptions.Separator"/> says (a new page by default), as for letters or statements printed together.
    /// </summary>
    /// <param name="records">The records.</param>
    /// <param name="options">How the fields are filled and the records separated, and where warnings go; null for the defaults.</param>
    /// <returns>The merged document; with no records, a document with one empty section.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="records"/> is null.</exception>
    public Document MailMergeAll(IEnumerable<IMergeData> records, MailMergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(records);
        options ??= new MailMergeOptions();
        Document result = Clone();
        while (result.Sections.Count > 0)
        {
            result.Sections[^1].Remove();
        }

        foreach (IMergeData record in records)
        {
            Document copy = Clone();
            Report(copy.FillMergeFields(record, options), options);
            bool first = true;
            foreach (Section section in copy.Sections)
            {
                Section imported = result.Import(section);
                if (first && result.Sections.Count > 0)
                {
                    imported.PageSetup.SectionStart = options.Separator;
                }

                first = false;
                result.Sections.Add(imported);
            }
        }

        if (result.Sections.Count == 0)
        {
            result.AddSection();
        }

        return result;
    }

    private static void Report(IReadOnlyList<ConversionWarning> warnings, MailMergeOptions options)
    {
        if (options.OnWarning is { } report)
        {
            foreach (ConversionWarning warning in warnings)
            {
                report(warning);
            }
        }
    }
}
