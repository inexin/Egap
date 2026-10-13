using System.Collections;

namespace Egap.Pdf;

/// <summary>
/// The interactive form (12.7): a thin view over the catalog's <c>/AcroForm</c> dictionary, whose <c>/Fields</c> are the roots
/// of the field tree. A document without one has no fields. Fields are read leniently: loops, entries that are not
/// dictionaries and trees deeper than 64 levels are skipped, as pdf.js and qpdf skip them.
/// </summary>
public sealed class PdfForm : IReadOnlyList<PdfField>
{
    /// <summary>How deep the field tree is followed.</summary>
    internal const int MaxDepth = 64;

    private readonly PdfDocument document;

    internal PdfForm(PdfDocument document)
    {
        this.document = document;
    }

    /// <summary>The <c>/AcroForm</c> dictionary, or <see langword="null"/> when the document has no form.</summary>
    public PdfDictionary? Dictionary => document.Catalog.Get<PdfDictionary>("AcroForm");

    /// <summary>Whether the document has a form, an <c>/AcroForm</c> dictionary.</summary>
    public bool Exists => Dictionary is not null;

    /// <summary>The number of top-level fields.</summary>
    public int Count => Roots().Count;

    /// <summary>The top-level field at an index, in <c>/Fields</c> order.</summary>
    public PdfField this[int index] => Roots()[index];

    /// <summary>
    /// Whether viewers are asked to draw the fields' appearances themselves (<c>/NeedAppearances</c>); false when missing.
    /// Setting false removes the entry.
    /// </summary>
    /// <exception cref="InvalidOperationException">Set on a document without a form.</exception>
    public bool NeedAppearances
    {
        get => Dictionary?.GetBool("NeedAppearances") ?? false;
        set => (Dictionary ?? throw new InvalidOperationException("The document has no form."))["NeedAppearances"] = value ? PdfBoolean.True : null;
    }

    /// <summary>The default appearance for fields that give none (<c>/DA</c>), such as <c>/Helv 0 Tf 0 g</c>.</summary>
    public string? DefaultAppearance => Dictionary?.GetText("DA");

    /// <summary>The default alignment for fields that give none (<c>/Q</c>); left when missing.</summary>
    public PdfTextAlignment Alignment => PdfField.AlignmentOf(Dictionary?.GetInt("Q"));

    /// <summary>The resources fields' appearances draw with (<c>/DR</c>), such as their fonts.</summary>
    public PdfDictionary? DefaultResources => Dictionary?.Get<PdfDictionary>("DR");

    /// <summary>The signature flags (<c>/SigFlags</c>): 1 when the document has signatures, 2 when it may only be appended to.</summary>
    public int SignatureFlags => (int)(Dictionary?.GetInt("SigFlags") ?? 0);

    /// <summary>Whether the form also has an XFA form (<c>/XFA</c>, 12.7.8).</summary>
    public bool HasXfa => Dictionary?.Get("XFA") is PdfStream or PdfArray;

    /// <summary>Every field of the tree, parents before their kids, in <c>/Fields</c> and <c>/Kids</c> order.</summary>
    public IReadOnlyList<PdfField> GetAllFields()
    {
        List<PdfField> all = [];
        Stack<PdfField> pending = new(Roots().AsEnumerable().Reverse());
        while (pending.TryPop(out PdfField? field))
        {
            all.Add(field);
            IReadOnlyList<PdfField> kids = field.Kids;
            for (int i = kids.Count - 1; i >= 0; i--)
            {
                pending.Push(kids[i]);
            }
        }

        return all;
    }

    /// <summary>The terminal fields, those that hold values and have widgets, in tree order.</summary>
    public IReadOnlyList<PdfField> GetTerminalFields()
    {
        return [.. GetAllFields().Where(f => f.IsTerminal)];
    }

    /// <summary>The field with a fully qualified name, such as <c>order.customer.name</c>; false when there is none.</summary>
    public bool TryGetField(string fullName, out PdfField field)
    {
        ArgumentNullException.ThrowIfNull(fullName);
        field = GetAllFields().FirstOrDefault(f => f.FullName == fullName)!;
        return field is not null;
    }

    /// <summary>
    /// Resets fields to their default values, as a reset-form action does (12.7.6.3): each terminal field among
    /// <paramref name="fields"/> and their descendants, or every field when none are given, gets its <c>/DV</c> as its value,
    /// or no value when it has none (<c>Off</c> for buttons); list boxes lose their <c>/I</c>; appearances are brought in
    /// step.
    /// </summary>
    public void Reset(IEnumerable<PdfField>? fields = null)
    {
        IEnumerable<PdfField> chosen = fields is null ? GetTerminalFields() : fields.SelectMany(Terminals).Distinct();
        foreach (PdfField field in chosen.ToList())
        {
            PdfObject? reset = field.RawDefaultValue switch
            {
                PdfArray array => array.Clone(),
                PdfString or PdfName or PdfInteger or PdfReal or PdfBoolean => field.RawDefaultValue,
                _ => null,
            };
            field.Dictionary["V"] = reset ?? (field is PdfCheckBoxField or PdfRadioButtonField ? PdfName.Get("Off") : null);
            field.Dictionary.Remove("I");
            field.UpdateAppearances();
        }
    }

    /// <inheritdoc/>
    public IEnumerator<PdfField> GetEnumerator()
    {
        return Roots().GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private List<PdfField> Roots()
    {
        if (Dictionary?.Get<PdfArray>("Fields") is not PdfArray fields)
        {
            return [];
        }

        List<PdfField> roots = [];
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields.Get(i) is PdfDictionary field && seen.Add(field))
            {
                roots.Add(PdfField.Create(field, fields[i] as PdfReference, null, document, [field]));
            }
        }

        return roots;
    }

    /// <summary>A field if it is terminal, else the terminal fields below it.</summary>
    private static IEnumerable<PdfField> Terminals(PdfField field)
    {
        Stack<PdfField> pending = new([field]);
        while (pending.TryPop(out PdfField? next))
        {
            if (next.IsTerminal)
            {
                yield return next;
                continue;
            }

            foreach (PdfField kid in next.Kids.Reverse())
            {
                pending.Push(kid);
            }
        }
    }
}
