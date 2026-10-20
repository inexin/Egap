using Egap.Documents.Layout;
using Egap.Documents.Pdf;
using Egap.Pdf;

namespace Egap.Documents.Tests.Pdf;

/// <summary>HTML form controls and DOCX content controls exported as PDF form fields.</summary>
public sealed class FormFieldExportTests
{
    private static readonly PdfExportOptions WithFields = new()
    {
        FormFields = true,
        Layout = new LayoutOptions { Fonts = new FontSettings { UseSystemFonts = false } },
    };

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
