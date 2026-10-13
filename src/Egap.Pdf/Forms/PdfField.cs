namespace Egap.Pdf;

/// <summary>
/// A form field (12.7.4): a thin view over its field dictionary. Terminal fields are typed by <c>/FT</c> and their flags:
/// <see cref="PdfTextField"/>, <see cref="PdfCheckBoxField"/>, <see cref="PdfRadioButtonField"/>,
/// <see cref="PdfPushButtonField"/>, <see cref="PdfComboBoxField"/>, <see cref="PdfListBoxField"/> and
/// <see cref="PdfSignatureField"/>; non-terminal fields, and terminal fields of an unknown type, are plain
/// <see cref="PdfField"/>s. Two views are equal when they wrap the same dictionary.
/// </summary>
/// <remarks>
/// Inheritable entries (<c>/FT</c>, <c>/Ff</c>, <c>/V</c>, <c>/DV</c>, <c>/DA</c>, <c>/Q</c> and <c>/Opt</c>) are read from
/// the field or the nearest ancestor that has them through <c>/Parent</c>, as qpdf and pdf.js read them; <c>/DA</c> and
/// <c>/Q</c> fall back to the form's. A kid without a name (<c>/T</c>) that is a widget is one of its parent's widgets, not
/// a field of its own.
/// </remarks>
public class PdfField : IEquatable<PdfField>
{
    private static readonly PdfName ParentKey = "Parent";

    /// <summary>The dictionaries from the root down to this field, against loops in <c>/Kids</c>.</summary>
    private readonly HashSet<PdfDictionary> path;

    private protected PdfField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
    {
        Dictionary = dictionary;
        Reference = reference;
        Parent = parent;
        Document = document;
        this.path = path;
    }

    /// <summary>The field's dictionary.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>The reference to the field's dictionary, or <see langword="null"/> for one written directly in its parent.</summary>
    public PdfReference? Reference { get; }

    /// <summary>The field above this one in the tree, or <see langword="null"/> for a top-level field.</summary>
    public PdfField? Parent { get; }

    /// <summary>The field's own name (<c>/T</c>); empty when it has none.</summary>
    public string PartialName => Dictionary.GetText("T") ?? "";

    /// <summary>
    /// The fully qualified name: the partial names from the top-level field down, joined with periods, leaving out fields
    /// without a name (12.7.4.2).
    /// </summary>
    public string FullName
    {
        get
        {
            List<string> names = [];
            foreach (PdfDictionary node in Ancestry())
            {
                if (node.GetText("T") is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }

            names.Reverse();
            return string.Join('.', names);
        }
    }

    /// <summary>The name shown to users, such as in tool tips (<c>/TU</c>).</summary>
    public string? AlternateName => Dictionary.GetText("TU");

    /// <summary>The name used when exporting the field's data (<c>/TM</c>).</summary>
    public string? MappingName => Dictionary.GetText("TM");

    /// <summary>The field type (<c>/FT</c>, inherited): <c>Btn</c>, <c>Tx</c>, <c>Ch</c> or <c>Sig</c>; <see langword="null"/> when none is given.</summary>
    public string? FieldType => (Inherited("FT") as PdfName)?.Value;

    /// <summary>The field flags (<c>/Ff</c>, inherited).</summary>
    public PdfFieldFlags Flags => (PdfFieldFlags)(int)(Objects.ObjectValues.Int(Inherited("Ff")) ?? 0);

    /// <summary>Whether the user may not change the value.</summary>
    public bool IsReadOnly => Flags.HasFlag(PdfFieldFlags.ReadOnly);

    /// <summary>Whether the field must have a value when the form is submitted.</summary>
    public bool IsRequired => Flags.HasFlag(PdfFieldFlags.Required);

    /// <summary>The value as stored (<c>/V</c>, inherited, references followed), for any field type.</summary>
    public PdfObject? RawValue => Inherited("V");

    /// <summary>The value a reset restores (<c>/DV</c>, inherited, references followed).</summary>
    public PdfObject? RawDefaultValue => Inherited("DV");

    /// <summary>The default appearance (<c>/DA</c>), inherited, else the form's.</summary>
    public string? DefaultAppearance => (Inherited("DA") as PdfString)?.ToText() ?? Document.Form.DefaultAppearance;

    /// <summary>How the text is aligned (<c>/Q</c>), inherited, else the form's.</summary>
    public PdfTextAlignment Alignment => Inherited("Q") is PdfObject q ? AlignmentOf(Objects.ObjectValues.Int(q)) : Document.Form.Alignment;

    /// <summary>Whether the field is terminal: it has no kids that are fields, only widgets.</summary>
    public bool IsTerminal => !KidDictionaries().Any(k => IsField(k.Dictionary));

    /// <summary>The kids that are fields, in <c>/Kids</c> order.</summary>
    public IReadOnlyList<PdfField> Kids
    {
        get
        {
            if (path.Count >= PdfForm.MaxDepth)
            {
                return [];
            }

            return [.. KidDictionaries().Where(k => IsField(k.Dictionary))
                .Select(k => Create(k.Dictionary, k.Reference, this, Document, [.. path, k.Dictionary]))];
        }
    }

    /// <summary>
    /// The widget annotations that show the field: the field itself when field and widget are merged into one dictionary,
    /// and its kids that are widgets without names of their own.
    /// </summary>
    public IReadOnlyList<PdfWidgetAnnotation> Widgets
    {
        get
        {
            List<PdfWidgetAnnotation> widgets = [];
            if (IsWidget(Dictionary))
            {
                widgets.Add(new PdfWidgetAnnotation(Dictionary, Reference, Document));
            }

            foreach ((PdfDictionary kid, PdfReference? reference) in KidDictionaries())
            {
                if (!IsField(kid))
                {
                    widgets.Add(new PdfWidgetAnnotation(kid, reference, Document));
                }
            }

            return widgets;
        }
    }

    /// <summary>The document the field belongs to.</summary>
    internal PdfDocument Document { get; }

    /// <summary>Whether both views wrap the same field dictionary.</summary>
    public bool Equals(PdfField? other)
    {
        return other is not null && ReferenceEquals(Dictionary, other.Dictionary);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj)
    {
        return Equals(obj as PdfField);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(Dictionary);
    }

    /// <summary>The fully qualified name and type, for debugging.</summary>
    public override string ToString()
    {
        return $"{FieldType ?? "untyped"} field {FullName}";
    }

    /// <summary>The view for a field dictionary, typed when it is terminal.</summary>
    internal static PdfField Create(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
    {
        PdfField plain = new(dictionary, reference, parent, document, path);
        if (!plain.IsTerminal)
        {
            return plain;
        }

        PdfFieldFlags flags = plain.Flags;
        return plain.FieldType switch
        {
            "Tx" => new PdfTextField(dictionary, reference, parent, document, path),
            "Btn" when flags.HasFlag(PdfFieldFlags.PushButton) => new PdfPushButtonField(dictionary, reference, parent, document, path),
            "Btn" when flags.HasFlag(PdfFieldFlags.Radio) => new PdfRadioButtonField(dictionary, reference, parent, document, path),
            "Btn" => new PdfCheckBoxField(dictionary, reference, parent, document, path),
            "Ch" when flags.HasFlag(PdfFieldFlags.Combo) => new PdfComboBoxField(dictionary, reference, parent, document, path),
            "Ch" => new PdfListBoxField(dictionary, reference, parent, document, path),
            "Sig" => new PdfSignatureField(dictionary, reference, parent, document, path),
            _ => plain,
        };
    }

    /// <summary>
    /// The field for a widget: the widget itself when it is a field, else the field its <c>/Parent</c> names, else the field
    /// of the form whose widgets include it; null when there is none.
    /// </summary>
    internal static PdfField? ForWidget(PdfDictionary widget, PdfReference? reference, PdfDocument document)
    {
        (PdfDictionary field, PdfReference? fieldReference) = widget.Get("T") is not null || widget.Get("FT") is not null || widget.Get<PdfDictionary>(ParentKey) is null
            ? (widget, reference)
            : (widget.Get<PdfDictionary>(ParentKey)!, widget[ParentKey] as PdfReference);
        PdfField? found = field.Get("T") is null && field.Get("FT") is null && field.Get("Kids") is null
            ? null
            : Create(field, fieldReference, null, document, [field]);
        if (found is not null && found.Widgets.Any(w => ReferenceEquals(w.Dictionary, widget)))
        {
            return found;
        }

        // A widget whose /Parent is missing or wrong is still the widget of the field whose /Kids list it.
        return document.Form.GetAllFields().FirstOrDefault(f => f.Widgets.Any(w => ReferenceEquals(w.Dictionary, widget))) ?? found;
    }

    internal static PdfTextAlignment AlignmentOf(long? q)
    {
        return q switch
        {
            1 => PdfTextAlignment.Center,
            2 => PdfTextAlignment.Right,
            _ => PdfTextAlignment.Left,
        };
    }

    /// <summary>An inheritable entry from this field or the nearest ancestor through <c>/Parent</c> that has it, resolved.</summary>
    private protected PdfObject? Inherited(PdfName key)
    {
        foreach (PdfDictionary node in Ancestry())
        {
            if (node.Get(key) is PdfObject value and not PdfNull)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// A widget's appearance states other than <c>Off</c>: the names in its <c>/AP /N</c> (or <c>/D</c>) sub-dictionary,
    /// which a check box or radio button is set to when on.
    /// </summary>
    private protected static IEnumerable<string> OnStates(PdfAnnotation widget)
    {
        PdfDictionary? appearances = widget.Dictionary.Get<PdfDictionary>("AP");
        return ((appearances?.Get<PdfDictionary>("N") ?? appearances?.Get<PdfDictionary>("D"))?.Keys ?? [])
            .Select(k => k.Value).Where(k => k != "Off");
    }

    /// <summary>This field's dictionary, then its ancestors through <c>/Parent</c>, stopping at a loop or the depth limit.</summary>
    private IEnumerable<PdfDictionary> Ancestry()
    {
        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        for (PdfDictionary? node = Dictionary; node is not null && seen.Count < PdfForm.MaxDepth && seen.Add(node); node = node.Get<PdfDictionary>(ParentKey))
        {
            yield return node;
        }
    }

    /// <summary>The kids that are dictionaries and not already on the path from the root, each once.</summary>
    private IEnumerable<(PdfDictionary Dictionary, PdfReference? Reference)> KidDictionaries()
    {
        if (Dictionary.Get<PdfArray>("Kids") is not PdfArray kids)
        {
            yield break;
        }

        HashSet<PdfDictionary> seen = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < kids.Count; i++)
        {
            if (kids.Get(i) is PdfDictionary kid && !path.Contains(kid) && seen.Add(kid))
            {
                yield return (kid, kids[i] as PdfReference);
            }
        }
    }

    /// <summary>
    /// Whether a kid is a field rather than only a widget: it has a name, or kids of its own, or is not a widget at all.
    /// </summary>
    private static bool IsField(PdfDictionary kid)
    {
        return kid.Get("T") is not null || kid.Get("Kids") is not null || !IsWidget(kid);
    }

    /// <summary>Whether a dictionary is a widget annotation: <c>/Subtype /Widget</c>, or no subtype but a rectangle.</summary>
    private static bool IsWidget(PdfDictionary dictionary)
    {
        return dictionary.GetName("Subtype")?.Value is string subtype ? subtype == "Widget" : dictionary.Get("Rect") is PdfArray;
    }
}

/// <summary>Field flags (12.7.4.1, tables 227, 229, 231 and 233). Some bits mean different things for different field types.</summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The spec names these the field flags.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1069", Justification = "The spec gives bit 26 two meanings, by field type.")]
public enum PdfFieldFlags
{
    /// <summary>The user may not change the value.</summary>
    ReadOnly = 1,

    /// <summary>The field must have a value when the form is submitted.</summary>
    Required = 1 << 1,

    /// <summary>The field is not submitted.</summary>
    NoExport = 1 << 2,

    /// <summary>A text field of several lines.</summary>
    Multiline = 1 << 12,

    /// <summary>A text field whose text is not shown.</summary>
    Password = 1 << 13,

    /// <summary>A radio group where one button is always on.</summary>
    NoToggleToOff = 1 << 14,

    /// <summary>A button field that is a radio group.</summary>
    Radio = 1 << 15,

    /// <summary>A button field that is a push button.</summary>
    PushButton = 1 << 16,

    /// <summary>A choice field that is a combo box.</summary>
    Combo = 1 << 17,

    /// <summary>A combo box that also takes typed text.</summary>
    Edit = 1 << 18,

    /// <summary>A choice field whose options are sorted.</summary>
    Sort = 1 << 19,

    /// <summary>A text field whose value is a file path.</summary>
    FileSelect = 1 << 20,

    /// <summary>A list box where several options can be selected.</summary>
    MultiSelect = 1 << 21,

    /// <summary>The text is not spell checked.</summary>
    DoNotSpellCheck = 1 << 22,

    /// <summary>A text field that does not scroll beyond its box.</summary>
    DoNotScroll = 1 << 23,

    /// <summary>A text field split into <c>/MaxLen</c> equal cells.</summary>
    Comb = 1 << 24,

    /// <summary>A text field whose value is rich text (<c>/RV</c>).</summary>
    RichText = 1 << 25,

    /// <summary>Radio buttons with the same on state turn on and off together.</summary>
    RadiosInUnison = 1 << 25,

    /// <summary>A choice field's value is committed as soon as the selection changes.</summary>
    CommitOnSelectionChange = 1 << 26,
}
