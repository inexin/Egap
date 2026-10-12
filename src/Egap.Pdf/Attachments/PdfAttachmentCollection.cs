using System.Collections;
using System.Security.Cryptography;

using Egap.Pdf.Metadata;
using Egap.Pdf.Trees;

namespace Egap.Pdf;

/// <summary>
/// The document's embedded files (7.11.4): the catalog's <c>/Names /EmbeddedFiles</c> name tree, in key order. Adding and
/// removing edit the tree in place (Phase 2 Q3) and keep the catalog's <c>/AF</c> associated files (PDF 2.0) in step.
/// </summary>
public sealed class PdfAttachmentCollection : IReadOnlyList<PdfAttachment>
{
    private readonly PdfDocument document;

    internal PdfAttachmentCollection(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>The number of attachments.</summary>
    public int Count => List().Count;

    /// <summary>The attachment at an index, in key order.</summary>
    public PdfAttachment this[int index] => List()[index];

    /// <summary>The attachment with a name; false when there is none.</summary>
    public bool TryGet(string name, out PdfAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(name);
        attachment = null!;
        if (Tree() is NameTree tree && Find(tree, name) is (PdfString, PdfObject value) && value.Resolve() is PdfDictionary spec)
        {
            attachment = new PdfAttachment(name, spec, value as PdfReference);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The tree entry for a name: its key as a text string, else as UTF-8, else any key that reads as the same text (a key
    /// written in another encoding of the same characters).
    /// </summary>
    private static (PdfString Key, PdfObject Value)? Find(NameTree tree, string name)
    {
        foreach (PdfString key in new[] { PdfString.FromText(name), PdfString.FromBytes(System.Text.Encoding.UTF8.GetBytes(name)) })
        {
            if (tree.TryGet(key, out PdfObject value))
            {
                return (key, value);
            }
        }

        foreach (KeyValuePair<PdfString, PdfObject> entry in tree.Entries())
        {
            if (entry.Key.ToText() == name)
            {
                return (entry.Key, entry.Value);
            }
        }

        return null;
    }

    /// <summary>
    /// Embeds a file under a name, replacing any attachment of that name. The embedded file stream records the size, MD5
    /// checksum, MIME type and dates; the file specification gets the name as <c>/F</c> and <c>/UF</c>. In a PDF 2.0 document,
    /// or one that already lists associated files, the file is also added to the catalog's <c>/AF</c>.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "The spec makes /CheckSum an MD5 digest (7.11.4.2); it checks integrity, not authenticity.")]
    public PdfAttachment Add(string name, ReadOnlySpan<byte> bytes, string? mimeType = null, string? description = null, DateTimeOffset? modified = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Remove(name);

        PdfDictionary parameters = new()
        {
            ["Size"] = new PdfInteger(bytes.Length),
            ["CheckSum"] = PdfString.FromBytes(MD5.HashData(bytes), hex: true),
        };
        if (modified is DateTimeOffset date)
        {
            parameters["ModDate"] = PdfString.FromText(PdfDate.FormatPdf(date));
        }

        PdfStream stream = new(new PdfDictionary { ["Type"] = PdfName.Get("EmbeddedFile"), ["Params"] = parameters });
        if (mimeType is not null)
        {
            stream.Dictionary["Subtype"] = PdfName.Get(mimeType);
        }

        stream.SetDecodedBytes(bytes);
        PdfReference streamReference = document.Objects.Add(stream);
        PdfDictionary spec = new()
        {
            ["Type"] = PdfName.Get("Filespec"),
            ["F"] = PdfString.FromText(name),
            ["UF"] = PdfString.FromText(name),
            ["EF"] = new PdfDictionary { ["F"] = streamReference, ["UF"] = streamReference },
        };
        if (description is not null)
        {
            spec["Desc"] = PdfString.FromText(description);
        }

        PdfReference reference = document.Objects.Add(spec);
        (Tree() ?? CreateTree()).Set(PdfString.FromText(name), reference);
        // An associated file says how it relates to the document (14.13.2); without more to go on, Unspecified.
        if (document.Catalog.Get<PdfArray>("AF") is PdfArray associated)
        {
            spec["AFRelationship"] = PdfName.Get("Unspecified");
            associated.Add(reference);
        }
        else if (string.CompareOrdinal(document.Version, "2.0") >= 0)
        {
            spec["AFRelationship"] = PdfName.Get("Unspecified");
            document.Catalog["AF"] = new PdfArray([reference]);
        }

        return new PdfAttachment(name, (PdfDictionary)document.Objects[reference], reference);
    }

    /// <summary>Removes an attachment from the name tree and the catalog's <c>/AF</c>. Its objects stay until nothing reaches them.</summary>
    /// <returns>Whether there was one.</returns>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!TryGet(name, out PdfAttachment attachment))
        {
            return false;
        }

        NameTree tree = Tree()!;
        tree.Remove(Find(tree, name)!.Value.Key);
        if (document.Catalog.Get<PdfArray>("AF") is PdfArray associated)
        {
            for (int i = associated.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(associated.Get(i), attachment.FileSpecification))
                {
                    associated.RemoveAt(i);
                }
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public IEnumerator<PdfAttachment> GetEnumerator()
    {
        return List().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>Every entry of the tree whose value is a file specification dictionary.</summary>
    private List<PdfAttachment> List()
    {
        return Tree() is NameTree tree
            ? [.. tree.Entries().Where(e => e.Value.Resolve() is PdfDictionary).Select(e => new PdfAttachment(e.Key.ToText(), (PdfDictionary)e.Value.Resolve(), e.Value as PdfReference))]
            : [];
    }

    private NameTree? Tree()
    {
        return document.Catalog.Get<PdfDictionary>("Names")?.Get<PdfDictionary>("EmbeddedFiles") is PdfDictionary root ? new(root, document.Objects, document.Log) : null;
    }

    private NameTree CreateTree()
    {
        if (document.Catalog.Get<PdfDictionary>("Names") is not PdfDictionary names)
        {
            names = [];
            document.Catalog["Names"] = names;
        }

        PdfReference root = document.Objects.Add(new PdfDictionary { ["Names"] = new PdfArray() });
        names["EmbeddedFiles"] = root;
        return new((PdfDictionary)document.Objects[root], document.Objects, document.Log);
    }
}
