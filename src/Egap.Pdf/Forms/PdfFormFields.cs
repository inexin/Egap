using System.Globalization;

using Egami.Pixels;

using Egap.Pdf.Annotations;
using Egap.Pdf.Forms;

namespace Egap.Pdf;

/// <summary>What every new form field can have: a tooltip, flags, and how its widget looks.</summary>
public record PdfFieldOptions
{
    /// <summary>Gets the text a viewer shows on hover and reads to screen readers (<c>/TU</c>); null for none.</summary>
    public string? Tooltip { get; init; }

    /// <summary>Gets whether the field must be filled before the form is submitted.</summary>
    public bool Required { get; init; }

    /// <summary>Gets whether the user cannot change the field.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Gets the text size in points; 0 (the default) to fit the text to the widget.</summary>
    public double FontSize { get; init; }

    /// <summary>Gets the text colour. Defaults to black.</summary>
    public Rgba8 TextColor { get; init; } = new(0, 0, 0, 255);

    /// <summary>Gets the border colour; null for no border. Defaults to a mid grey.</summary>
    public Rgba8? BorderColor { get; init; } = new Rgba8(128, 128, 128, 255);

    /// <summary>Gets the background colour; null (the default) for none.</summary>
    public Rgba8? BackgroundColor { get; init; }

    /// <summary>Gets the border width in points. Defaults to 1.</summary>
    public double BorderWidth { get; init; } = 1;
}

/// <summary>How a new text field works.</summary>
public sealed record PdfTextFieldOptions : PdfFieldOptions
{
    /// <summary>Gets the value it starts with; null for empty.</summary>
    public string? Value { get; init; }

    /// <summary>Gets the most characters it takes; null for no limit.</summary>
    public int? MaxLength { get; init; }

    /// <summary>Gets whether it takes several lines.</summary>
    public bool Multiline { get; init; }

    /// <summary>Gets whether what is typed is hidden.</summary>
    public bool Password { get; init; }

    /// <summary>Gets whether the characters sit in <see cref="MaxLength"/> evenly spaced boxes, as on paper forms.</summary>
    public bool Comb { get; init; }

    /// <summary>Gets the alignment of the text.</summary>
    public PdfTextAlignment Alignment { get; init; }
}

/// <summary>How a new check box or radio group works.</summary>
public sealed record PdfButtonOptions : PdfFieldOptions
{
    /// <summary>Gets whether a check box starts checked; for a radio group, see the value passed when adding it.</summary>
    public bool Checked { get; init; }

    /// <summary>Gets the name of the checked state, the value a checked box exports. Defaults to <c>Yes</c>.</summary>
    public string OnState { get; init; } = "Yes";
}

/// <summary>How a new combo box or list box works.</summary>
public sealed record PdfChoiceFieldOptions : PdfFieldOptions
{
    /// <summary>Gets the item chosen at the start; null for none.</summary>
    public string? Value { get; init; }

    /// <summary>Gets whether the items are sorted.</summary>
    public bool Sort { get; init; }
}

/// <summary>Adding new fields to the form, each with a widget and a generated appearance.</summary>
public sealed partial class PdfForm
{
    /// <summary>
    /// Adds a text field on a page. The name may have dots, such as <c>customer.name</c>, which makes it the kid
    /// <c>name</c> of a field <c>customer</c>, made when it is missing.
    /// </summary>
    /// <param name="name">The fully qualified name.</param>
    /// <param name="page">The 0-based index of the page.</param>
    /// <param name="rectangle">The widget's rectangle, in the page's default user space.</param>
    /// <param name="options">How it works and looks.</param>
    /// <example>
    /// <code>
    /// PdfTextField name = pdf.Form.AddTextField("customer.name", 0, new PdfRectangle(72, 700, 300, 720),
    ///     new PdfTextFieldOptions { MaxLength = 60, Required = true });
    /// </code>
    /// </example>
    /// <exception cref="ArgumentException">The name is empty or taken, or a size in the options is not positive.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    public PdfTextField AddTextField(string name, int page, PdfRectangle rectangle, PdfTextFieldOptions? options = null)
    {
        options ??= new PdfTextFieldOptions();
        if (options.MaxLength is < 1 || (options.Comb && options.MaxLength is null))
        {
            throw new ArgumentException("MaxLength must be positive, and a comb field needs one.", nameof(options));
        }

        PdfFieldFlags flags = Flags(options)
            | (options.Multiline ? PdfFieldFlags.Multiline : 0)
            | (options.Password ? PdfFieldFlags.Password : 0)
            | (options.Comb ? PdfFieldFlags.Comb : 0);
        PdfDictionary field = NewField(name, "Tx", flags, options);
        field["DA"] = PdfString.FromText(Appearance(options));
        if (options.Alignment != PdfTextAlignment.Left)
        {
            field["Q"] = new PdfInteger((int)options.Alignment);
        }

        if (options.MaxLength is int max)
        {
            field["MaxLen"] = new PdfInteger(max);
        }

        if (options.Value is string value)
        {
            field["V"] = PdfString.FromText(value);
        }

        PdfTextField created = (PdfTextField)Register(name, field, page, rectangle, options);
        created.UpdateAppearances();
        return created;
    }

    /// <summary>A check box on a page, drawn with a check mark when checked; otherwise as <see cref="AddTextField"/>.</summary>
    /// <exception cref="ArgumentException">The name is empty or taken.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    public PdfCheckBoxField AddCheckBox(string name, int page, PdfRectangle rectangle, PdfButtonOptions? options = null)
    {
        options ??= new PdfButtonOptions();
        PdfDictionary field = NewField(name, "Btn", Flags(options), options);
        field["DA"] = PdfString.FromText(Appearance(options, "ZaDb"));
        string on = OnName(options.OnState);
        field["V"] = PdfName.Get(options.Checked ? on : "Off");
        PdfField created = Register(name, field, page, rectangle, options, widget =>
        {
            widget["AS"] = PdfName.Get(options.Checked ? on : "Off");
            ButtonAppearance(widget, on, options, round: false);
        });
        return (PdfCheckBoxField)created;
    }

    /// <summary>
    /// A radio group: one field with a button for each of <paramref name="buttons"/>, each exporting its value; choosing one
    /// clears the others. <paramref name="selected"/> is the value chosen at the start, or null for none.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or taken, there are no buttons, or two share a value.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A page does not exist.</exception>
    public PdfRadioButtonField AddRadioGroup(string name, IReadOnlyList<(int Page, PdfRectangle Rectangle, string Value)> buttons, string? selected = null, PdfFieldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        options ??= new PdfFieldOptions();
        if (buttons.Count == 0 || buttons.Select(b => b.Value).Distinct(StringComparer.Ordinal).Count() != buttons.Count)
        {
            throw new ArgumentException("A radio group needs buttons, each with its own value.", nameof(buttons));
        }

        PdfDictionary field = NewField(name, "Btn", Flags(options) | PdfFieldFlags.Radio | PdfFieldFlags.NoToggleToOff, options);
        field["DA"] = PdfString.FromText(Appearance(options, "ZaDb"));
        field["V"] = PdfName.Get(selected is null ? "Off" : OnName(selected));
        PdfReference reference = Store(name, field);
        PdfArray kids = [];
        foreach ((int pageIndex, PdfRectangle rectangle, string value) in buttons)
        {
            string on = OnName(value);
            PdfDictionary widget = [];
            Widget(widget, rectangle, options);
            widget["Parent"] = reference;
            widget["AS"] = PdfName.Get(on == OnName(selected ?? "\0") ? on : "Off");
            ButtonAppearance(widget, on, options, round: true);
            kids.Add(Page(pageIndex).Annotations.AddWidget(widget));
        }

        field["Kids"] = kids;
        return (PdfRadioButtonField)Field(reference);
    }

    /// <summary>A combo box: a drop-down list of <paramref name="items"/>, which the user may also type into when <paramref name="editable"/>.</summary>
    /// <exception cref="ArgumentException">The name is empty or taken.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    public PdfComboBoxField AddComboBox(string name, int page, PdfRectangle rectangle, IReadOnlyList<string> items, bool editable = false, PdfChoiceFieldOptions? options = null)
    {
        options ??= new PdfChoiceFieldOptions();
        PdfDictionary field = ChoiceField(name, items, Flags(options) | PdfFieldFlags.Combo | (editable ? PdfFieldFlags.Edit : 0), options);
        PdfComboBoxField created = (PdfComboBoxField)Register(name, field, page, rectangle, options);
        created.UpdateAppearances();
        return created;
    }

    /// <summary>A list box showing <paramref name="items"/>, of which the user picks one, or several when <paramref name="multiSelect"/>.</summary>
    /// <exception cref="ArgumentException">The name is empty or taken.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    public PdfListBoxField AddListBox(string name, int page, PdfRectangle rectangle, IReadOnlyList<string> items, bool multiSelect = false, PdfChoiceFieldOptions? options = null)
    {
        options ??= new PdfChoiceFieldOptions();
        PdfDictionary field = ChoiceField(name, items, Flags(options) | (multiSelect ? PdfFieldFlags.MultiSelect : 0), options);
        if (options.Value is string value && items.ToList().IndexOf(value) is int index and >= 0)
        {
            field["I"] = new PdfArray([new PdfInteger(index)]);
        }

        PdfListBoxField created = (PdfListBoxField)Register(name, field, page, rectangle, options);
        created.UpdateAppearances();
        return created;
    }

    /// <summary>A push button with a caption, which runs <paramref name="action"/> when pressed, such as a reset or a link.</summary>
    /// <exception cref="ArgumentException">The name is empty or taken.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    public PdfPushButtonField AddPushButton(string name, int page, PdfRectangle rectangle, string caption, PdfAction? action = null, PdfFieldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(caption);
        options ??= new PdfFieldOptions { BackgroundColor = new Rgba8(220, 220, 220, 255) };
        PdfDictionary field = NewField(name, "Btn", Flags(options) | PdfFieldFlags.PushButton, options);
        field["DA"] = PdfString.FromText(Appearance(options));
        PdfField created = Register(name, field, page, rectangle, options, widget =>
        {
            widget.Get<PdfDictionary>("MK")!["CA"] = PdfString.FromText(caption);
            if (action is not null)
            {
                widget["A"] = action.Dictionary.Clone();
            }

            CaptionAppearance(widget, caption, options);
        });
        return (PdfPushButtonField)created;
    }

    /// <summary>
    /// An empty signature field, for a signer to sign later in a viewer or with <see cref="PdfDocument.SignAsync"/>; the
    /// rectangle is where the signature's appearance will go.
    /// </summary>
    /// <exception cref="ArgumentException">The name is empty or taken.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The page does not exist.</exception>
    public PdfSignatureField AddSignatureField(string name, int page, PdfRectangle rectangle, PdfFieldOptions? options = null)
    {
        options ??= new PdfFieldOptions();
        PdfDictionary field = NewField(name, "Sig", Flags(options), options);
        return (PdfSignatureField)Register(name, field, page, rectangle, options, widget => EmptyAppearance(widget, options));
    }

    private static PdfFieldFlags Flags(PdfFieldOptions options)
    {
        if (!(options.BorderWidth >= 0 && double.IsFinite(options.BorderWidth)) || !(options.FontSize >= 0 && double.IsFinite(options.FontSize)))
        {
            throw new ArgumentException("The border width and font size must not be negative.", nameof(options));
        }

        return (options.Required ? PdfFieldFlags.Required : 0) | (options.ReadOnly ? PdfFieldFlags.ReadOnly : 0);
    }

    private PdfDictionary ChoiceField(string name, IReadOnlyList<string> items, PdfFieldFlags flags, PdfChoiceFieldOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        PdfDictionary field = NewField(name, "Ch", flags | (options.Sort ? PdfFieldFlags.Sort : 0), options);
        field["DA"] = PdfString.FromText(Appearance(options));
        field["Opt"] = new PdfArray([.. items.Select(i => (PdfObject)PdfString.FromText(i ?? throw new ArgumentException("An item is null.", nameof(items))))]);
        if (options.Value is string value)
        {
            field["V"] = PdfString.FromText(value);
        }

        return field;
    }

    /// <summary>A field dictionary of a type, its last name part as <c>/T</c>, with flags and tooltip; not stored yet.</summary>
    private PdfDictionary NewField(string name, string type, PdfFieldFlags flags, PdfFieldOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Split('.').Any(p => p.Length == 0))
        {
            throw new ArgumentException($"The field name '{name}' has an empty part.", nameof(name));
        }

        if (TryGetField(name, out _))
        {
            throw new ArgumentException($"The form has a field named '{name}' already.", nameof(name));
        }

        PdfDictionary field = new()
        {
            ["FT"] = PdfName.Get(type),
            ["T"] = PdfString.FromText(name[(name.LastIndexOf('.') + 1)..]),
        };
        if (flags != 0)
        {
            field["Ff"] = new PdfInteger((int)flags);
        }

        if (options.Tooltip is string tooltip)
        {
            field["TU"] = PdfString.FromText(tooltip);
        }

        return field;
    }

    /// <summary>Stores a field merged with its one widget on a page, under its parents, and returns its typed view.</summary>
    private PdfField Register(string name, PdfDictionary field, int page, PdfRectangle rectangle, PdfFieldOptions options, Action<PdfDictionary>? finish = null)
    {
        Widget(field, rectangle, options);
        finish?.Invoke(field);
        PdfReference reference = Page(page).Annotations.AddWidget(field);
        Link(name, reference);
        return Field(reference);
    }

    /// <summary>Stores a field without a widget of its own (a radio group) under its parents.</summary>
    private PdfReference Store(string name, PdfDictionary field)
    {
        PdfReference reference = document.Objects.Add(field);
        Link(name, reference);
        return reference;
    }

    /// <summary>Puts a stored field under its parent fields (made when missing) or in <c>/Fields</c>.</summary>
    private void Link(string name, PdfReference reference)
    {
        PdfDictionary form = EnsureForm();
        string[] parts = name.Split('.');
        PdfArray siblings = form.Get<PdfArray>("Fields")!;
        PdfReference? parent = null;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            PdfReference? found = siblings.OfType<PdfReference>().FirstOrDefault(r => r.Resolve() is PdfDictionary d && d.GetText("T") == parts[i]);
            if (found is null)
            {
                PdfDictionary node = new() { ["T"] = PdfString.FromText(parts[i]), ["Kids"] = new PdfArray() };
                if (parent is not null)
                {
                    node["Parent"] = parent;
                }

                found = document.Objects.Add(node);
                siblings.Add(found);
            }

            PdfDictionary nodeDictionary = (PdfDictionary)found.Resolve()!;
            if (nodeDictionary.Get("FT") is not null || nodeDictionary.Get<PdfArray>("Kids") is not PdfArray kids)
            {
                throw new ArgumentException($"'{string.Join('.', parts[..(i + 1)])}' is a field with a value, so it cannot hold '{name}'.", nameof(name));
            }

            siblings = kids;
            parent = found;
        }

        if (parent is not null)
        {
            ((PdfDictionary)document.Objects[reference])["Parent"] = parent;
        }

        siblings.Add(reference);
    }

    private PdfField Field(PdfReference reference)
    {
        PdfDictionary dictionary = (PdfDictionary)document.Objects[reference];
        PdfField? parent = dictionary.Get<PdfDictionary>("Parent") is PdfDictionary p
            ? GetAllFields().FirstOrDefault(f => ReferenceEquals(f.Dictionary, p))
            : null;
        return PdfField.Create(dictionary, reference, parent, document, [dictionary]);
    }

    /// <summary>The form dictionary, made with Helvetica and ZapfDingbats in its resources when the document has none.</summary>
    private PdfDictionary EnsureForm()
    {
        if (Dictionary is not PdfDictionary form)
        {
            form = [];
            document.Catalog["AcroForm"] = form;
        }

        if (form.Get<PdfArray>("Fields") is null)
        {
            form["Fields"] = new PdfArray();
        }

        if (form.Get<PdfDictionary>("DR") is not PdfDictionary resources)
        {
            resources = [];
            form["DR"] = resources;
        }

        if (resources.Get<PdfDictionary>("Font") is not PdfDictionary fonts)
        {
            fonts = [];
            resources["Font"] = fonts;
        }

        foreach ((string key, string font) in (ReadOnlySpan<(string, string)>)[("Helv", "Helvetica"), ("ZaDb", "ZapfDingbats")])
        {
            if (fonts.Get(key) is null)
            {
                fonts[key] = document.Objects.Add(AppearanceBuilder.StandardFontDictionary(Fonts.Standard14Font.Get(font)!));
            }
        }

        if (form.Get("DA") is null)
        {
            form["DA"] = PdfString.FromText("/Helv 0 Tf 0 g");
        }

        return form;
    }

    private PdfPage Page(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index, "page");
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, document.Pages.Count, "page");
        EnsureForm();
        return document.Pages[index];
    }

    /// <summary>Writes a widget's entries: its rectangle, printable, its border and colours.</summary>
    private static void Widget(PdfDictionary widget, PdfRectangle rectangle, PdfFieldOptions options)
    {
        PdfRectangle r = rectangle.Normalize();
        if (!(r.Width > 0 && r.Height > 0))
        {
            throw new ArgumentException("The widget's rectangle must have a size.", nameof(rectangle));
        }

        PdfDictionary mk = [];
        if (options.BorderColor is Rgba8 border)
        {
            mk["BC"] = Color(border);
        }

        if (options.BackgroundColor is Rgba8 background)
        {
            mk["BG"] = Color(background);
        }

        widget["Type"] = PdfName.Get("Annot");
        widget["Subtype"] = PdfName.Get("Widget");
        widget["Rect"] = r.ToArray();
        widget["F"] = new PdfInteger(4);
        widget["MK"] = mk;
        widget["BS"] = new PdfDictionary { ["W"] = new PdfReal(options.BorderColor is null ? 0 : options.BorderWidth), ["S"] = PdfName.Get("S") };
    }

    private static PdfArray Color(Rgba8 color) => [new PdfReal(color.R / 255.0), new PdfReal(color.G / 255.0), new PdfReal(color.B / 255.0)];

    private static string Appearance(PdfFieldOptions options, string font = "Helv")
    {
        Rgba8 c = options.TextColor;
        return string.Create(CultureInfo.InvariantCulture, $"/{font} {options.FontSize:0.##} Tf {c.R / 255.0:0.###} {c.G / 255.0:0.###} {c.B / 255.0:0.###} rg");
    }

    /// <summary>A state name for a value: PDF names cannot be empty, and <c>Off</c> means unchecked.</summary>
    private static string OnName(string value)
    {
        return value.Length == 0 || value == "Off" ? "On" : value;
    }

    /// <summary>The checked and unchecked appearances of a check box (a check mark) or a radio button (a dot).</summary>
    private void ButtonAppearance(PdfDictionary widget, string on, PdfFieldOptions options, bool round)
    {
        (double width, double height) = Size(widget);
        PdfDictionary mk = widget.Get<PdfDictionary>("MK")!;
        mk["CA"] = PdfString.FromText(round ? "l" : "4");
        double border = options.BorderColor is null ? 0 : options.BorderWidth;
        double size = options.FontSize > 0 ? options.FontSize : Math.Min(width, height) * 0.8;
        PdfReference checkedForm = AppearanceForm(width, height, content =>
        {
            Frame(content, width, height, border, options, round);
            Fill(content, options.TextColor);
            if (round)
            {
                double d = Math.Min(width, height) * 0.5;
                content.Ellipse((width - d) / 2, (height - d) / 2, d, d).Fill();
            }
            else
            {
                Fonts.Standard14Font dingbats = Fonts.Standard14Font.Get("ZapfDingbats")!;
                double markWidth = dingbats.Width((byte)'4') / 1000 * size;
                content.BeginText().Font(PdfName.Get("ZaDb"), size).MoveText((width - markWidth) / 2, (height - (size * 0.7)) / 2).ShowText("4"u8).EndText();
            }
        }, dingbats: !round);
        PdfReference offForm = AppearanceForm(width, height, content => Frame(content, width, height, border, options, round));
        widget["AP"] = new PdfDictionary { ["N"] = new PdfDictionary { [on] = checkedForm, ["Off"] = offForm } };
    }

    private void CaptionAppearance(PdfDictionary widget, string caption, PdfFieldOptions options)
    {
        (double width, double height) = Size(widget);
        double border = options.BorderColor is null ? 0 : options.BorderWidth;
        Fonts.Standard14Font helvetica = Fonts.Standard14Font.Get("Helvetica")!;
        byte[] codes = Fonts.WinAnsiEncoding.TryEncode(caption) ?? Fonts.WinAnsiEncoding.TryEncode(new string([.. caption.Select(c => c < 128 ? c : '?')]))!;
        double size = options.FontSize > 0 ? options.FontSize : Math.Max(4, Math.Min(12, height * 0.6));
        double textWidth = codes.Sum(c => helvetica.Width(c)) / 1000 * size;
        PdfReference form = AppearanceForm(width, height, content =>
        {
            Frame(content, width, height, border, options, round: false);
            Fill(content, options.TextColor);
            content.BeginText().Font(PdfName.Get("Helv"), size).MoveText((width - textWidth) / 2, (height - (size * 0.7)) / 2).ShowText(codes).EndText();
        }, helvetica: true);
        widget["AP"] = new PdfDictionary { ["N"] = form };
    }

    private void EmptyAppearance(PdfDictionary widget, PdfFieldOptions options)
    {
        (double width, double height) = Size(widget);
        double border = options.BorderColor is null ? 0 : options.BorderWidth;
        widget["AP"] = new PdfDictionary { ["N"] = AppearanceForm(width, height, content => Frame(content, width, height, border, options, round: false)) };
    }

    private static (double Width, double Height) Size(PdfDictionary widget)
    {
        PdfRectangle r = (PdfRectangle.FromArray(widget.Get<PdfArray>("Rect")) ?? default).Normalize();
        return (r.Width, r.Height);
    }

    private static void Frame(PdfContentWriter content, double width, double height, double border, PdfFieldOptions options, bool round)
    {
        if (options.BackgroundColor is Rgba8 background)
        {
            content.Save();
            Fill(content, background);
            _ = round ? content.Ellipse(0, 0, width, height) : content.Rectangle(0, 0, width, height);
            content.Fill().Restore();
        }

        if (border > 0 && options.BorderColor is Rgba8 color)
        {
            content.Save().Rgb(color.R / 255.0, color.G / 255.0, color.B / 255.0, stroke: true).LineWidth(border);
            double inset = border / 2;
            _ = round ? content.Ellipse(inset, inset, width - border, height - border) : content.Rectangle(inset, inset, width - border, height - border);
            content.Stroke().Restore();
        }
    }

    private static void Fill(PdfContentWriter content, Rgba8 color) => content.Rgb(color.R / 255.0, color.G / 255.0, color.B / 255.0);

    private PdfReference AppearanceForm(double width, double height, Action<PdfContentWriter> draw, bool dingbats = false, bool helvetica = false)
    {
        PdfContentWriter content = new();
        draw(content);
        PdfDictionary fonts = [];
        PdfDictionary formFonts = EnsureForm().Get<PdfDictionary>("DR")!.Get<PdfDictionary>("Font")!;
        if (dingbats)
        {
            fonts["ZaDb"] = formFonts["ZaDb"]!;
        }

        if (helvetica)
        {
            fonts["Helv"] = formFonts["Helv"]!;
        }

        PdfStream stream = new(new PdfDictionary
        {
            ["Type"] = PdfName.Get("XObject"),
            ["Subtype"] = PdfName.Get("Form"),
            ["BBox"] = new PdfRectangle(0, 0, width, height).ToArray(),
            ["Resources"] = fonts.Count > 0 ? new PdfDictionary { ["Font"] = fonts } : new PdfDictionary(),
        });
        stream.SetDecodedBytes(content.ToArray());
        return document.Objects.Add(stream);
    }
}
