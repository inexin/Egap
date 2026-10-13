using System.Collections;

using Egap.Pdf.Content;
using Egap.Pdf.IO;

namespace Egap.Pdf;

/// <summary>
/// The operations of a content stream (8.2): a page's content, a form XObject's, a Type 3 glyph's, or content built from
/// scratch. It is a plain list (Phase 3 design Q3): operations can be read, inserted, removed and replaced, and operators the
/// spec does not define are kept, so content round-trips whatever it holds.
/// </summary>
/// <remarks>
/// A page's content may be several streams (<c>/Contents</c> as an array); they are read as one sequence, since a token may
/// run from one into the next. The list remembers which operations came from which stream, so writing it back rewrites only
/// the streams whose operations changed (Phase 3 design Q4).
/// </remarks>
public sealed class PdfContent : IList<PdfOperation>, IReadOnlyList<PdfOperation>
{
    private readonly List<PdfOperation> operations;
    private readonly PdfWarning[] warnings;
    private int version;

    /// <summary>An empty content stream.</summary>
    public PdfContent()
    {
        operations = [];
        warnings = [];
    }

    /// <summary>Content holding the given operations.</summary>
    public PdfContent(IEnumerable<PdfOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        this.operations = [.. operations];
        foreach (PdfOperation operation in this.operations)
        {
            ArgumentNullException.ThrowIfNull(operation, nameof(operations));
        }

        warnings = [];
    }

    private PdfContent(List<PdfOperation> operations, PdfWarning[] warnings, Source? source)
    {
        this.operations = operations;
        this.warnings = warnings;
        Origin = source;
    }

    /// <summary>The number of operations.</summary>
    public int Count => operations.Count;

    /// <summary>What was repaired while reading the content: stray bytes, wrong operand counts, damaged inline images.</summary>
    public IReadOnlyList<PdfWarning> Warnings => warnings;

    /// <summary>Whether the operations have changed since the content was read or made.</summary>
    public bool IsChanged => version != 0;

    bool ICollection<PdfOperation>.IsReadOnly => false;

    /// <summary>Where the content was read from, for writing back only what changed; <see langword="null"/> for new content.</summary>
    internal Source? Origin { get; }

    /// <summary>The operation at <paramref name="index"/>.</summary>
    public PdfOperation this[int index]
    {
        get => operations[index];
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            operations[index] = value;
            version++;
        }
    }

    /// <summary>Reads content from its bytes, already decoded.</summary>
    /// <param name="data">The content stream's decoded bytes.</param>
    /// <param name="options">The limits and strictness to read with; <see cref="PdfReadOptions.Default"/> when <see langword="null"/>.</param>
    /// <exception cref="PdfException">The content is damaged and <see cref="PdfReadOptions.Strict"/> is set.</exception>
    /// <exception cref="PdfLimitException">Operands nest deeper than the options allow.</exception>
    public static PdfContent Parse(ReadOnlySpan<byte> data, PdfReadOptions? options = null)
    {
        options ??= PdfReadOptions.Default;
        WarningLog log = new(options.Strict);
        List<PdfOperation> operations = ParseAll(data.ToArray(), data.Length, options, log, null);
        return new PdfContent(operations, log.ToArray(), null);
    }

    /// <summary>
    /// Reads the content of a stream, such as a form XObject or a Type 3 glyph procedure, with the read options of the file it
    /// came from.
    /// </summary>
    /// <exception cref="PdfException">The stream cannot be decoded, or its content is damaged and the file was opened in strict mode.</exception>
    public static PdfContent Read(PdfStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        PdfReadOptions options = stream.Origin?.Options ?? PdfReadOptions.Default;
        WarningLog log = new(options.Strict);
        byte[] data = stream.GetDecodedBytes();
        List<PdfOperation> operations = ParseAll(data, data.Length, options, log, null);
        return new PdfContent(operations, log.ToArray(), new Source([new Segment(stream, stream, [.. operations])], Joined: true));
    }

    /// <summary>
    /// Reads a page's content: its <c>/Contents</c> stream, or the streams of its <c>/Contents</c> array joined by line breaks.
    /// A stream that cannot be decoded is skipped with a warning.
    /// </summary>
    internal static PdfContent Read(PdfPage page)
    {
        PdfReadOptions options = page.Document.Options;
        WarningLog log = new(options.Strict);
        List<(PdfStream Stream, PdfObject Entry)> streams = ContentStreams(page, log);
        List<byte[]> parts = [];
        List<(PdfStream Stream, PdfObject Entry)> read = [];
        foreach ((PdfStream stream, PdfObject entry) in streams)
        {
            try
            {
                parts.Add(stream.GetDecodedBytes());
                read.Add((stream, entry));
            }
            catch (PdfException e) when (e is not PdfLimitException)
            {
                log.Add(PdfWarningCode.MalformedContent, $"A content stream could not be decoded and was skipped: {e.Message}");
            }
        }

        // The streams are one sequence, each followed by a line break, so tokens do not run together and a comment at the end
        // of one stream does not swallow the next (7.8.2).
        int length = parts.Sum(p => p.Length + 1);
        byte[] data = new byte[length];
        int[] ends = new int[parts.Count];
        int offset = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            parts[i].CopyTo(data, offset);
            offset += parts[i].Length;
            data[offset++] = (byte)'\n';
            ends[i] = offset;
        }

        List<(int Start, int End)> spans = [];
        List<PdfOperation> operations = ParseAll(data, length, options, log, spans, out bool endedOpen);

        // Which stream each operation came from; one that starts in one stream and ends in the next ties the two together.
        // Content that ends inside a string or array would swallow a stream added after it, so it is rewritten whole.
        List<Segment> segments = [];
        bool joined = streams.Count != read.Count || endedOpen;
        int next = 0;
        for (int s = 0; s < read.Count; s++)
        {
            int first = next;
            while (next < operations.Count && spans[next].Start < ends[s])
            {
                joined |= spans[next].End > ends[s];
                next++;
            }

            segments.Add(new Segment(read[s].Stream, read[s].Entry, operations.GetRange(first, next - first).ToArray()));
        }

        return new PdfContent(operations, log.ToArray(), new Source(segments, joined, page.Dictionary));
    }

    /// <summary>
    /// The streams a page's <c>/Contents</c> names, in order, each with the entry that names it (its reference); anything that
    /// is not a stream is skipped with a warning.
    /// </summary>
    internal static List<(PdfStream Stream, PdfObject Entry)> ContentStreams(PdfPage page, WarningLog? log)
    {
        List<(PdfStream, PdfObject)> streams = [];
        PdfObject? contents = page.Dictionary["Contents"];
        switch (contents?.Resolve())
        {
            case null or PdfNull:
                break;
            case PdfStream stream:
                streams.Add((stream, contents));
                break;
            case PdfArray array:
                for (int i = 0; i < array.Count; i++)
                {
                    if (array[i].Resolve() is PdfStream item)
                    {
                        streams.Add((item, array[i]));
                    }
                    else
                    {
                        log?.Add(PdfWarningCode.MalformedContent, "A /Contents array holds something other than a stream; it was skipped.");
                    }
                }

                break;
            default:
                log?.Add(PdfWarningCode.MalformedContent, "/Contents is neither a stream nor an array; the page was read as empty.");
                break;
        }

        return streams;
    }

    /// <summary>Writes content back to a page: unchanged streams kept, everything else in new streams (Phase 3 design Q4).</summary>
    internal static void WriteBack(PdfPage page, PdfContent content)
    {
        Source? source = content.Origin;
        bool fromPage = source?.Page is not null && ReferenceEquals(source.Page, page.Dictionary);
        if (fromPage && !content.IsChanged)
        {
            return;
        }

        bool compress = !(fromPage && source!.Segments.Count > 0 && source.Segments.All(s => s.Stream.Filters.Count == 0));
        List<PdfOperation> operations = content.operations;
        PdfArray contents = [];
        int next = 0;
        if (fromPage && !source!.Joined)
        {
            foreach (Segment segment in source.Segments)
            {
                int at = FindRun(operations, next, segment.Operations);
                if (at < 0)
                {
                    continue;
                }

                AddNew(page, contents, operations, next, at, compress);
                contents.Add(segment.Entry);
                next = at + segment.Operations.Length;
            }
        }

        AddNew(page, contents, operations, next, operations.Count, compress);
        page.Dictionary["Contents"] = contents.Count switch
        {
            0 => null,
            1 => contents[0],
            _ => contents,
        };
    }

    /// <summary>Where a stream's operations stand together in <paramref name="operations"/> from <paramref name="start"/>, or -1.</summary>
    private static int FindRun(List<PdfOperation> operations, int start, PdfOperation[] run)
    {
        if (run.Length == 0)
        {
            return -1;
        }

        for (int i = start; i + run.Length <= operations.Count; i++)
        {
            if (!ReferenceEquals(operations[i], run[0]))
            {
                continue;
            }

            for (int k = 1; k < run.Length; k++)
            {
                if (!ReferenceEquals(operations[i + k], run[k]))
                {
                    return -1;
                }
            }

            return i;
        }

        return -1;
    }

    private static void AddNew(PdfPage page, PdfArray contents, List<PdfOperation> operations, int start, int end, bool compress)
    {
        if (end <= start)
        {
            return;
        }

        // After a kept stream, start on a new line, in case that stream ends inside a comment.
        byte[] bytes = ContentSyntax.ToBytes(operations.GetRange(start, end - start));
        PdfStream stream = new();
        stream.SetDecodedBytes(contents.Count > 0 ? [(byte)'\n', .. bytes] : bytes, compress);
        contents.Add(page.Document.Objects.Add(stream));
    }

    /// <summary>Adds an operation at the end.</summary>
    public void Add(PdfOperation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        operations.Add(item);
        version++;
    }

    /// <summary>Adds operations at the end.</summary>
    public void AddRange(IEnumerable<PdfOperation> items)
    {
        InsertRange(operations.Count, items);
    }

    /// <summary>Inserts an operation at <paramref name="index"/>.</summary>
    public void Insert(int index, PdfOperation item)
    {
        ArgumentNullException.ThrowIfNull(item);
        operations.Insert(index, item);
        version++;
    }

    /// <summary>Inserts operations at <paramref name="index"/>.</summary>
    public void InsertRange(int index, IEnumerable<PdfOperation> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        PdfOperation[] added = [.. items];
        foreach (PdfOperation item in added)
        {
            ArgumentNullException.ThrowIfNull(item, nameof(items));
        }

        operations.InsertRange(index, added);
        version++;
    }

    /// <summary>Removes the operation at <paramref name="index"/>.</summary>
    public void RemoveAt(int index)
    {
        operations.RemoveAt(index);
        version++;
    }

    /// <summary>Removes <paramref name="count"/> operations from <paramref name="index"/>.</summary>
    public void RemoveRange(int index, int count)
    {
        operations.RemoveRange(index, count);
        version++;
    }

    /// <summary>Removes the first occurrence of an operation (the same instance).</summary>
    public bool Remove(PdfOperation item)
    {
        int index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    /// <summary>Removes every operation that matches; returns how many.</summary>
    public int RemoveAll(Predicate<PdfOperation> match)
    {
        int removed = operations.RemoveAll(match);
        if (removed > 0)
        {
            version++;
        }

        return removed;
    }

    /// <summary>Removes every operation.</summary>
    public void Clear()
    {
        operations.Clear();
        version++;
    }

    /// <summary>The index of an operation (the same instance), or -1.</summary>
    public int IndexOf(PdfOperation item)
    {
        for (int i = 0; i < operations.Count; i++)
        {
            if (ReferenceEquals(operations[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Whether the content holds the operation (the same instance).</summary>
    public bool Contains(PdfOperation item)
    {
        return IndexOf(item) >= 0;
    }

    /// <inheritdoc/>
    public void CopyTo(PdfOperation[] array, int arrayIndex)
    {
        operations.CopyTo(array, arrayIndex);
    }

    /// <inheritdoc/>
    public IEnumerator<PdfOperation> GetEnumerator()
    {
        return operations.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    /// The content as bytes in the compact form of Phase 3 design Q4: one space between tokens, a newline after each operator,
    /// numbers in their shortest form. Reading them back gives the same operations.
    /// </summary>
    public byte[] ToBytes()
    {
        return ContentSyntax.ToBytes(operations);
    }

    private static List<PdfOperation> ParseAll(byte[] data, int length, PdfReadOptions options, WarningLog log, List<(int Start, int End)>? spans)
    {
        return ParseAll(data, length, options, log, spans, out _);
    }

    private static List<PdfOperation> ParseAll(byte[] data, int length, PdfReadOptions options, WarningLog log, List<(int Start, int End)>? spans, out bool endedOpen)
    {
        ContentParser parser = new(data, length, options, log);
        List<PdfOperation> operations = new(Math.Max(16, length / 16));
        while (parser.Next() is PdfOperation operation)
        {
            operations.Add(operation);
            spans?.Add((parser.OperationStart, parser.Position));
        }

        endedOpen = parser.EndedOpen;
        return operations;
    }

    /// <summary>The operations one stream held when read, and the <c>/Contents</c> entry (its reference) that named it.</summary>
    internal sealed record Segment(PdfStream Stream, PdfObject Entry, PdfOperation[] Operations);

    /// <summary>
    /// The streams content was read from, and the page dictionary when it was a page's. <paramref name="Joined"/> is set when
    /// the streams cannot be told apart (an operation runs across two, or one could not be decoded), so any change rewrites
    /// them as one.
    /// </summary>
    internal sealed record Source(List<Segment> Segments, bool Joined, PdfDictionary? Page = null);
}
