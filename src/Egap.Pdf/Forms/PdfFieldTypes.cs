namespace Egap.Pdf;

/// <summary>A text field (12.7.5.3): text the user types, on one line or several.</summary>
public sealed class PdfTextField : PdfField
{
    internal PdfTextField(PdfDictionary dictionary, PdfReference? reference, PdfField? parent, PdfDocument document, HashSet<PdfDictionary> path)
        : base(dictionary, reference, parent, document, path)
    {
    }

    /// <summary>
    /// The text (<c>/V</c>, inherited); a text stream is read too. <see langword="null"/> when there is none. Setting it
    /// redraws the widgets (<see cref="PdfField.UpdateAppearances"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The text is longer than <see cref="MaxLength"/>.</exception>
    public string? Value
    {
        get => Text(RawValue);
        set => SetValue(value, updateData: true);
    }

    /// <summary>The text a reset restores (<c>/DV</c>, inherited).</summary>
    public string? DefaultValue
    {
        get => Text(RawDefaultValue);
        set => Dictionary["DV"] = value is null ? null : PdfString.FromText(value);
    }

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

    /// <summary>Sets the value and redraws the widgets; the XFA data follows when asked.</summary>
    internal void SetValue(string? value, bool updateData)
    {
        if (value is not null && MaxLength is int max && value.Length > max)
        {
            throw new ArgumentException($"Field {FullName} takes at most {max} characters.", nameof(value));
        }

        Dictionary["V"] = value is null ? null : PdfString.FromText(value);
        UpdateAppearances();
        if (updateData)
        {
            Document.Form.Xfa?.Update(this);
        }
    }

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

    /// <summary>
    /// Whether the box is checked: its value is a state other than <c>Off</c>. Setting it writes <see cref="OnState"/> or
    /// <c>Off</c> and sets each widget's appearance state to match.
    /// </summary>
    public bool IsChecked
    {
        get => Value is string value && value != "Off";
        set => SetChecked(value, updateData: true);
    }

    /// <summary>Checks or clears the box and sets the widgets' states; the XFA data follows when asked.</summary>
    internal void SetChecked(bool value, bool updateData)
    {
        Dictionary["V"] = PdfName.Get(value ? OnState : "Off");
        UpdateAppearances();
        if (updateData)
        {
            Document.Form.Xfa?.Update(this);
        }
    }
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

    /// <summary>
    /// The selected button's state (<c>/V</c>, inherited), or <see langword="null"/> when none is selected. Setting one of
    /// <see cref="Options"/> selects that button, and every button with the same state; setting none selects none.
    /// </summary>
    /// <exception cref="ArgumentException">The state is not one of <see cref="Options"/>.</exception>
    public string? Value
    {
        get => ((RawValue as PdfName)?.Value ?? (RawValue as PdfString)?.ToText()) is string value && value != "Off" ? value : null;
        set => SetValue(value, updateData: true);
    }

    /// <summary>Selects a button by its state, or none, and sets the widgets' states; the XFA data follows when asked.</summary>
    internal void SetValue(string? value, bool updateData)
    {
        if (value is not null && !Options.Contains(value))
        {
            throw new ArgumentException($"Radio group {FullName} has no button with state {value}.", nameof(value));
        }

        Dictionary["V"] = PdfName.Get(value ?? "Off");
        UpdateAppearances();
        if (updateData)
        {
            Document.Form.Xfa?.Update(this);
        }
    }

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

    /// <summary>
    /// The selected value, or the typed text of an editable combo box; <see langword="null"/> when there is none. Setting it
    /// selects the option with that export value, or for an editable combo box takes any text, and redraws the widgets.
    /// </summary>
    /// <exception cref="ArgumentException">The combo box is not editable and has no option with that value.</exception>
    public string? Value
    {
        get => Values.Count > 0 ? Values[0] : null;
        set => SetValue(value, updateData: true);
    }

    /// <summary>Selects an option or takes typed text, and redraws the widgets; the XFA data follows when asked.</summary>
    internal void SetValue(string? value, bool updateData)
    {
        if (value is not null && !IsEditable && !Options.Any(o => o.ExportValue == value))
        {
            throw new ArgumentException($"Combo box {FullName} has no option {value}.", nameof(value));
        }

        Dictionary["V"] = value is null ? null : PdfString.FromText(value);
        Dictionary.Remove("I");
        UpdateAppearances();
        if (updateData)
        {
            Document.Form.Xfa?.Update(this);
        }
    }

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

    /// <summary>
    /// Selects options by export value, none for an empty list: writes <c>/V</c> (one text, or an array for several) and
    /// <c>/I</c>, the indices of the options selected, then redraws the widgets.
    /// </summary>
    /// <exception cref="ArgumentException">A value is not an option, or several are given to a list box that is not multi-select.</exception>
    public void Select(params IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        IReadOnlyList<PdfChoiceOption> options = Options;
        List<int> indices = [];
        foreach (string value in values.Distinct())
        {
            int index = Enumerable.Range(0, options.Count).FirstOrDefault(i => options[i].ExportValue == value, -1);
            if (index < 0)
            {
                throw new ArgumentException($"List box {FullName} has no option {value}.", nameof(values));
            }

            indices.Add(index);
        }

        if (indices.Count > 1 && !IsMultiSelect)
        {
            throw new ArgumentException($"List box {FullName} takes one selection.", nameof(values));
        }

        indices.Sort();
        Dictionary["V"] = indices.Count switch
        {
            0 => null,
            1 => PdfString.FromText(options[indices[0]].ExportValue),
            _ => new PdfArray(indices.Select(i => (PdfObject)PdfString.FromText(options[i].ExportValue))),
        };
        Dictionary["I"] = indices.Count == 0 ? null : new PdfArray(indices.Select(i => (PdfObject)new PdfInteger(i)));
        UpdateAppearances();
        Document.Form.Xfa?.Update(this);
    }
}

/// <summary>A signature field (12.7.5.5). <see cref="PdfDocument.Signatures"/> reads the signatures in signed fields (Phase 5).</summary>
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

    /// <summary>The fields signing this one will lock (<c>/Lock</c>, 12.7.5.5); <see langword="null"/> when none.</summary>
    public PdfFieldLock? Lock => PdfFieldLock.Read(Dictionary.Get<PdfDictionary>("Lock"));

    /// <summary>The constraints on how the field may be signed (<c>/SV</c>, 12.7.5.5); <see langword="null"/> when none.</summary>
    public PdfSeedValue? SeedValue => Dictionary.Get<PdfDictionary>("SV") is { } seed ? new PdfSeedValue(seed) : null;
}

/// <summary>An option of a choice field: the value exported when it is selected, and the text shown.</summary>
/// <param name="ExportValue">The value the field takes when the option is selected.</param>
/// <param name="DisplayText">The text shown for the option.</param>
public readonly record struct PdfChoiceOption(string ExportValue, string DisplayText);
