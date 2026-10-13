namespace Egap.Pdf;

/// <summary>A text field (12.7.5.3): text the user types, on one line or several.</summary>
public sealed class PdfTextField : PdfField
{
    internal PdfTextField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>The text (<c>/V</c>, inherited); a text stream is read too. <see langword="null"/> when there is none.</summary>
    public string? Value => Text(RawValue);

    /// <summary>The text a reset restores (<c>/DV</c>, inherited).</summary>
    public string? DefaultValue => Text(RawDefaultValue);

    /// <summary>The rich text value (<c>/RV</c>, XHTML), as stored.</summary>
    public string? RichValue => Text(Inherited("RV"));

    /// <summary>The most characters the text may have (<c>/MaxLen</c>, inherited); <see langword="null"/> for no limit.</summary>
    public int? MaxLength => Objects.ObjectValues.Int(Inherited("MaxLen")) is long max and >= 0 and <= int.MaxValue ? (int)max : null;

    /// <summary>Whether the text may have several lines.</summary>
    public bool IsMultiline => Flags.HasFlag(PdfFieldFlags.Multiline);

    /// <summary>Whether the text is hidden as it is typed.</summary>
    public bool IsPassword => Flags.HasFlag(PdfFieldFlags.Password);

    /// <summary>Whether the box is split into <see cref="MaxLength"/> equal cells, one character each.</summary>
    public bool IsComb => Flags.HasFlag(PdfFieldFlags.Comb) && MaxLength is not null && !IsMultiline && !IsPassword && !Flags.HasFlag(PdfFieldFlags.FileSelect);

    /// <summary>Whether the value is a file path.</summary>
    public bool IsFileSelect => Flags.HasFlag(PdfFieldFlags.FileSelect);

    /// <summary>A text string, or the text of a text stream.</summary>
    internal static string? Text(PdfObject? value)
    {
        return value switch
        {
            PdfString text => text.ToText(),
            PdfStream stream => PdfString.FromBytes(stream.GetDecodedBytes()).ToText(),
            _ => null,
        };
    }
}

/// <summary>A check box (12.7.5.2.3): on or off.</summary>
public sealed class PdfCheckBoxField : PdfField
{
    internal PdfCheckBoxField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>The state name when checked: the first widget's appearance state other than <c>Off</c>; <c>Yes</c> when there is none.</summary>
    public string OnState => Widgets.SelectMany(OnStates).FirstOrDefault() ?? "Yes";

    /// <summary>The value (<c>/V</c>, inherited): <c>Off</c>, an on state, or <see langword="null"/> when there is none.</summary>
    public string? Value => (RawValue as PdfName)?.Value ?? (RawValue as PdfString)?.ToText();

    /// <summary>Whether the box is checked: its value is a state other than <c>Off</c>.</summary>
    public bool IsChecked => Value is string value && value != "Off";
}

/// <summary>A radio button group (12.7.5.2.4): one widget per button, at most one on.</summary>
public sealed class PdfRadioButtonField : PdfField
{
    internal PdfRadioButtonField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>The on state of each button, in widget order: the state name its widget shows when it is the one selected.</summary>
    public IReadOnlyList<string> Options => [.. Widgets.Select(w => OnStates(w).FirstOrDefault() ?? "")];

    /// <summary>The selected button's state (<c>/V</c>, inherited), or <see langword="null"/> when none is selected.</summary>
    public string? Value => ((RawValue as PdfName)?.Value ?? (RawValue as PdfString)?.ToText()) is string value && value != "Off" ? value : null;

    /// <summary>Whether one button must always be on.</summary>
    public bool IsNoToggleToOff => Flags.HasFlag(PdfFieldFlags.NoToggleToOff);

    /// <summary>Whether buttons with the same on state turn on and off together.</summary>
    public bool IsRadiosInUnison => Flags.HasFlag(PdfFieldFlags.RadiosInUnison);
}

/// <summary>A push button (12.7.5.2.2): runs an action and has no value.</summary>
public sealed class PdfPushButtonField : PdfField
{
    internal PdfPushButtonField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>The caption on the first widget (<c>/MK /CA</c>).</summary>
    public string? Caption => Widgets.Select(w => w.Dictionary.Get<PdfDictionary>("MK")?.GetText("CA")).FirstOrDefault(c => c is not null);
}

/// <summary>A choice field (12.7.5.4): a combo box or list box of options.</summary>
public abstract class PdfChoiceField : PdfField
{
    private protected PdfChoiceField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>
    /// The options (<c>/Opt</c>, inherited), in order: each a text, or a pair of the value exported and the text shown.
    /// Entries of neither form are left out.
    /// </summary>
    public IReadOnlyList<PdfChoiceOption> Options
    {
        get
        {
            if (Inherited("Opt") is not PdfArray options)
            {
                return [];
            }

            List<PdfChoiceOption> list = new(options.Count);
            for (int i = 0; i < options.Count; i++)
            {
                switch (options.Get(i))
                {
                    case PdfString text:
                        list.Add(new PdfChoiceOption(text.ToText(), text.ToText()));
                        break;
                    case PdfArray { Count: >= 2 } pair when pair.Get<PdfString>(0) is PdfString export && pair.Get<PdfString>(1) is PdfString display:
                        list.Add(new PdfChoiceOption(export.ToText(), display.ToText()));
                        break;
                    case PdfArray { Count: 1 } single when single.Get<PdfString>(0) is PdfString only:
                        list.Add(new PdfChoiceOption(only.ToText(), only.ToText()));
                        break;
                }
            }

            return list;
        }
    }

    /// <summary>The selected values (<c>/V</c>, inherited): one text, or several for a multiple selection.</summary>
    public IReadOnlyList<string> Values => RawValue switch
    {
        PdfString text => [text.ToText()],
        PdfName name => [name.Value],
        PdfArray array => [.. Enumerable.Range(0, array.Count).Select(array.Get<PdfString>).OfType<PdfString>().Select(s => s.ToText())],
        _ => [],
    };

    /// <summary>The indices of the selected options (<c>/I</c>), for options that share a value.</summary>
    public IReadOnlyList<int> SelectedIndices => Dictionary.Get<PdfArray>("I") is PdfArray indices
        ? [.. Enumerable.Range(0, indices.Count).Select(indices.GetInt).OfType<long>().Where(i => i is >= 0 and <= int.MaxValue).Select(i => (int)i)]
        : [];

    /// <summary>Whether the options are sorted.</summary>
    public bool IsSorted => Flags.HasFlag(PdfFieldFlags.Sort);
}

/// <summary>A combo box (12.7.5.4): a drop-down list, editable or not.</summary>
public sealed class PdfComboBoxField : PdfChoiceField
{
    internal PdfComboBoxField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>The selected value, or the typed text of an editable combo box; <see langword="null"/> when there is none.</summary>
    public string? Value => Values.Count > 0 ? Values[0] : null;

    /// <summary>Whether the user may type a value of their own.</summary>
    public bool IsEditable => Flags.HasFlag(PdfFieldFlags.Edit);
}

/// <summary>A list box (12.7.5.4): a scrolling list, with one or several selected.</summary>
public sealed class PdfListBoxField : PdfChoiceField
{
    internal PdfListBoxField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>Whether several options may be selected.</summary>
    public bool IsMultiSelect => Flags.HasFlag(PdfFieldFlags.MultiSelect);

    /// <summary>The index of the first option shown (<c>/TI</c>); 0 when missing.</summary>
    public int TopIndex => Dictionary.GetInt("TI") is long top and >= 0 and <= int.MaxValue ? (int)top : 0;
}

/// <summary>A signature field (12.7.5.5). Phase 2 reads it only; signing and verifying come in 5.x.</summary>
public sealed class PdfSignatureField : PdfField
{
    internal PdfSignatureField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>The signature dictionary (<c>/V</c>), or <see langword="null"/> when the field is not signed.</summary>
    public PdfDictionary? Signature => RawValue as PdfDictionary;

    /// <summary>Whether the field holds a signature.</summary>
    public bool IsSigned => Signature is not null;
}

/// <summary>An option of a choice field: the value exported when it is selected, and the text shown.</summary>
/// <param name="ExportValue">The value the field takes when the option is selected.</param>
/// <param name="DisplayText">The text shown for the option.</param>
public readonly record struct PdfChoiceOption(string ExportValue, string DisplayText);
