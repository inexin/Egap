using Egap.Documents;
using Egap.Documents.Layout;

namespace Egap.Pdf.Conversion.Tests;

/// <summary>HTML form controls and DOCX content controls exported as PDF form fields.</summary>
public sealed class FormFieldExportTests
{
    private static readonly PdfExportOptions WithFields = new()
    {
        FormFields = true,
        Layout = new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false } },
    };

    [Fact]
    public void HtmlControlsBecomeFieldsWhereTheyAreLaidOut()
    {
        Document html = HtmlFormat.Parse("""
            <form>
              <p>Name: <input name="name" value="Ola Nordmann" title="Full name"></p>
              <p>Email: <input type="email" name="email"></p>
              <p><input type="checkbox" name="terms" checked> I agree</p>
              <p><input type="radio" name="size" value="S"> S <input type="radio" name="size" value="M" checked> M</p>
              <p>Country: <select name="country"><option>Norway</option><option selected>Sweden</option></select></p>
              <p><textarea name="notes" rows="3" cols="30">First line
            Second line</textarea></p>
              <input type="hidden" name="secret" value="x">
            </form>
            """);

        using PdfDocument pdf = Reopen(html.ToPdf(WithFields));

        Dictionary<string, PdfField> fields = pdf.Form.GetTerminalFields().ToDictionary(f => f.FullName);
        Assert.Equal(["country", "email", "name", "notes", "size", "terms"], fields.Keys.Order());
        PdfTextField name = Assert.IsType<PdfTextField>(fields["name"]);
        Assert.Equal("Ola Nordmann", name.Value);
        Assert.Equal("Full name", name.AlternateName);
        Assert.Null(Assert.IsType<PdfTextField>(fields["email"]).Value);
        Assert.True(Assert.IsType<PdfCheckBoxField>(fields["terms"]).IsChecked);
        PdfRadioButtonField size = Assert.IsType<PdfRadioButtonField>(fields["size"]);
        Assert.Equal(["S", "M"], size.Options);
        Assert.Equal("M", size.Value);
        PdfComboBoxField country = Assert.IsType<PdfComboBoxField>(fields["country"]);
        Assert.Equal("Sweden", country.Value);
        Assert.Equal(["Norway", "Sweden"], country.Options.Select(o => o.DisplayText));
        PdfTextField notes = Assert.IsType<PdfTextField>(fields["notes"]);
        Assert.True(notes.IsMultiline);
        Assert.Equal("First line\nSecond line", notes.Value);

        // The values live in the fields, not in the page text; the labels stay.
        string text = pdf.Pages[0].ExtractText();
        Assert.DoesNotContain("Nordmann", text, StringComparison.Ordinal);
        Assert.Contains("Name:", text, StringComparison.Ordinal);
        Assert.Contains("I agree", text, StringComparison.Ordinal);

        // The name field sits right of its label, on the label's line.
        PdfTextLine label = pdf.Pages[0].GetText().Lines.First(l => l.Text.StartsWith("Name:", StringComparison.Ordinal));
        PdfRectangle widget = name.Widgets[0].Rectangle;
        Assert.True(widget.Left >= label.Box.Right - 1, $"{widget} after {label.Box}");
        Assert.True(widget.Bottom < label.Box.Top && widget.Top > label.Box.Bottom, $"{widget} on {label.Box}");
    }

    [Fact]
    public void WithoutTheOptionControlsStayText()
    {
        using PdfDocument pdf = Reopen(HtmlFormat.Parse("<p><input name='a' value='kept'></p>").ToPdf(WithFields with { FormFields = false }));

        Assert.False(pdf.Form.Exists);
        Assert.Contains("kept", pdf.Pages[0].ExtractText(), StringComparison.Ordinal);
    }

    [Fact]
    public void DocxContentControlsBecomeFields()
    {
        Document word = new();
        Paragraph paragraph = word.AddSection().Body.AddParagraph("City: ");
        InlineContentControl city = new(word);
        city.Properties.Kind = ContentControlKind.PlainText;
        city.Properties.Tag = "city";
        city.AddRun("Oslo");
        paragraph.Inlines.Add(city);
        InlineContentControl choice = new(word);
        choice.Properties.Kind = ContentControlKind.DropDownList;
        choice.Properties.Tag = "colour";
        choice.Properties.Items = ["Red", "Green"];
        choice.AddRun("Green");
        paragraph.Inlines.Add(choice);

        // The list items survive a DOCX round trip.
        using MemoryStream docx = new();
        DocxFormat.Save(word, docx);
        docx.Position = 0;
        Document reread = DocxFormat.Load(docx);
        Assert.Equal(["Red", "Green"], reread.Descendants().OfType<InlineContentControl>().Single(c => c.Properties.Tag == "colour").Properties.Items);

        using PdfDocument pdf = Reopen(reread.ToPdf(WithFields));
        Assert.Equal("Oslo", Assert.IsType<PdfTextField>(pdf.Form.GetTerminalFields().Single(f => f.FullName == "city")).Value);
        Assert.Equal("Green", Assert.IsType<PdfComboBoxField>(pdf.Form.GetTerminalFields().Single(f => f.FullName == "colour")).Value);
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using (pdf)
        {
            using MemoryStream saved = new();
            pdf.Save(saved);
            return PdfDocument.Open(saved.ToArray());
        }
    }
}
