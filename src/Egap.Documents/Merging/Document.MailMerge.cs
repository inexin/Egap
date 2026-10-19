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
}
