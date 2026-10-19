using System.Globalization;
using System.Text;

using Egami.Pixels;

using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Pdf.Conversion;

/// <summary>
/// Form controls made PDF form fields on export: each control's laid-out text gives the widget's rectangle (on the page
/// where the control starts), the text is redacted out of the page content, and a field of the matching kind is added there
/// with the control's value. Check boxes sharing a radio group become one radio group field.
/// </summary>
internal static class FormFieldExport
{
    public static void Add(DocumentLayout layout, PdfDocument pdf, Dictionary<PageLayout, PdfPage> pageOf)
    {
        List<Control> controls = [];
        foreach (Node node in layout.Document.Descendants())
        {
            ContentControlProperties? properties = node switch
            {
                InlineContentControl inline => inline.Properties,
                ContentControl block => block.Properties,
                _ => null,
            };
            if (properties?.Kind is not (ContentControlKind.PlainText or ContentControlKind.RichText or ContentControlKind.Date
                or ContentControlKind.DropDownList or ContentControlKind.ComboBox or ContentControlKind.CheckBox))
            {
                continue;
            }

            if (Place(layout, node) is not (PageLayout page, LayoutRect bounds))
            {
                continue;
            }

            PdfPage pdfPage = pageOf[page];
            PdfRectangle rectangle = new(bounds.X, page.Height - bounds.Bottom, bounds.Right, page.Height - bounds.Y);
            controls.Add(new Control(node, properties, pdf.Pages.IndexOf(pdfPage), rectangle, Value(node, properties), FontSize(node)));
        }

        if (controls.Count == 0)
        {
            return;
        }

        // The controls' own text goes, so only the fields show their values.
        foreach (Control control in controls)
        {
            pdf.Pages[control.Page].Annotations.Add(new PdfRedactAnnotation(control.Rectangle));
        }

        pdf.ApplyRedactions(new PdfRedactionOptions { Images = PdfRedactImages.None, Paths = PdfRedactPaths.None, GlyphOverlap = 0.5 });

        HashSet<string> names = [.. pdf.Form.GetAllFields().Select(f => f.FullName)];
        int unnamed = 0;
        string Name(string? wanted)
        {
            string name = string.IsNullOrWhiteSpace(wanted) ? "field" + (++unnamed).ToString(CultureInfo.InvariantCulture) : wanted.Trim().Replace('.', '_');
            string unique = name;
            for (int n = 2; !names.Add(unique); n++)
            {
                unique = name + "_" + n.ToString(CultureInfo.InvariantCulture);
            }

            return unique;
        }

        foreach (IGrouping<string, Control> group in controls.Where(c => c.Properties is { Kind: ContentControlKind.CheckBox, RadioGroup: not null }).GroupBy(c => c.Properties.RadioGroup!))
        {
            List<Control> buttons = [.. group];
            List<(int, PdfRectangle, string)> places = [];
            HashSet<string> values = [];
            for (int i = 0; i < buttons.Count; i++)
            {
                string value = buttons[i].Properties.Tag is { Length: > 0 } tag && values.Add(tag) ? tag : (i + 1).ToString(CultureInfo.InvariantCulture);
                values.Add(value);
                places.Add((buttons[i].Page, Square(buttons[i].Rectangle), value));
            }

            string? selected = buttons.Select((b, i) => (b, i)).Where(p => p.b.Properties.IsChecked == true).Select(p => places[p.i].Item3).FirstOrDefault();
            pdf.Form.AddRadioGroup(Name(group.Key), places, selected, Options(buttons[0]));
        }

        foreach (Control control in controls.Where(c => c.Properties.RadioGroup is null || c.Properties.Kind != ContentControlKind.CheckBox))
        {
            ContentControlProperties p = control.Properties;
            string name = Name(p.Tag ?? p.Title);
            switch (p.Kind)
            {
                case ContentControlKind.CheckBox:
                    pdf.Form.AddCheckBox(name, control.Page, Square(control.Rectangle), Options(control) is var o
                        ? new PdfButtonOptions { Checked = p.IsChecked == true, Tooltip = o.Tooltip, BorderColor = o.BorderColor, FontSize = 0 }
                        : null);
                    break;
                case ContentControlKind.DropDownList or ContentControlKind.ComboBox:
                    List<string> items = [.. p.Items ?? []];
                    if (control.Value.Length > 0 && !items.Contains(control.Value))
                    {
                        items.Insert(0, control.Value);
                    }

                    PdfFieldOptions choice = Options(control);
                    pdf.Form.AddComboBox(name, control.Page, control.Rectangle, items, editable: p.Kind == ContentControlKind.ComboBox, new PdfChoiceFieldOptions
                    {
                        Value = control.Value.Length > 0 ? control.Value : null,
                        Tooltip = choice.Tooltip,
                        FontSize = choice.FontSize,
                        TextColor = choice.TextColor,
                    });
                    break;
                default:
                    PdfFieldOptions text = Options(control);
                    pdf.Form.AddTextField(name, control.Page, control.Rectangle, new PdfTextFieldOptions
                    {
                        Value = control.Value.Length > 0 ? control.Value : null,
                        Multiline = p.Kind == ContentControlKind.RichText,
                        Tooltip = text.Tooltip,
                        FontSize = text.FontSize,
                        TextColor = text.TextColor,
                    });
                    break;
            }
        }
    }

    /// <summary>Where a control is laid out: the box round its text on the first page it is on.</summary>
    private static (PageLayout Page, LayoutRect Bounds)? Place(DocumentLayout layout, Node node)
    {
        List<(PageLayout Page, LayoutRect Bounds)> rects = node is Inline inline
            ? [.. layout.GetTextRects(inline).Select(r => (r.Page, r.Bounds))]
            : [.. layout.FindBoxes(node).Select(b => (b.Page, b.Box.Bounds))];
        rects.RemoveAll(r => !(r.Bounds.Width > 0 && r.Bounds.Height > 0));
        if (rects.Count == 0)
        {
            return null;
        }

        PageLayout first = rects[0].Page;
        List<LayoutRect> on = [.. rects.Where(r => r.Page == first).Select(r => r.Bounds)];
        double x = on.Min(r => r.X), y = on.Min(r => r.Y), right = on.Max(r => r.Right), bottom = on.Max(r => r.Bottom);
        return (first, new LayoutRect(x, y, right - x, bottom - y));
    }

    /// <summary>The control's value: its text with no-break padding taken off; nothing while it shows its placeholder.</summary>
    private static string Value(Node node, ContentControlProperties properties)
    {
        if (properties.ShowingPlaceholder || properties.Kind == ContentControlKind.CheckBox)
        {
            return string.Empty;
        }

        StringBuilder text = new();
        foreach (Node inner in node.Descendants())
        {
            switch (inner)
            {
                case Run run:
                    text.Append(run.Text);
                    break;
                case Break when properties.Kind == ContentControlKind.RichText:
                    text.Append('\n');
                    break;
                case Paragraph when text.Length > 0 && properties.Kind == ContentControlKind.RichText:
                    text.Append('\n');
                    break;
            }
        }

        string[] lines = text.ToString().Split('\n').Select(l => l.Trim(' ', ' ', ' ')).ToArray();
        return string.Join('\n', lines).Trim('\n');
    }

    private static double FontSize(Node node)
    {
        return node.Descendants().OfType<Run>().FirstOrDefault()?.Format.Size is double size and > 0 ? size : 0;
    }

    private static PdfFieldOptions Options(Control control)
    {
        Rgba8 black = new(0, 0, 0, 255);
        Rgba8 color = control.Node.Descendants().OfType<Run>().FirstOrDefault()?.Format.Color is DocumentColor c ? c.Resolve(null, black) : black;
        return new PdfFieldOptions { Tooltip = control.Properties.Title, FontSize = control.FontSize, TextColor = color };
    }

    /// <summary>A check box's square, as tall as the text it replaces and centred where it was.</summary>
    private static PdfRectangle Square(PdfRectangle r)
    {
        double side = Math.Min(r.Width, r.Height);
        double x = r.Left + ((r.Width - side) / 2), y = r.Bottom + ((r.Height - side) / 2);
        return new PdfRectangle(x, y, x + side, y + side);
    }

    private sealed record Control(Node Node, ContentControlProperties Properties, int Page, PdfRectangle Rectangle, string Value, double FontSize);
}
