using System.Text;

using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Forms;

/// <summary>Filling fields, their appearances by the Q12 font rule, and resetting the form: Phase 2 task 15.</summary>
public sealed class FormFillingTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>
    /// name (10, text, two widgets), agree (12, check box), size (13, radio of 22 and 23), colour (14, combo), extras (15,
    /// list), code (16, comb of 5), note (17, multiline, in Times-Roman with /Widths from /DR).
    /// </summary>
    private static byte[] File()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R 12 0 R 13 0 R 14 0 R 15 0 R 16 0 R 17 0 R] /DA (/Helv 0 Tf 0 g)"
                + " /DR << /Font << /Helv 40 0 R /TiRo 41 0 R >> >> >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [20 0 R 21 0 R 12 0 R 22 0 R 23 0 R 14 0 R 15 0 R 16 0 R 17 0 R] >>")
            .Object(10, "<< /T (name) /FT /Tx /DV (Nobody) /MaxLen 30 /Kids [20 0 R 21 0 R] >>")
            .Object(12, "<< /T (agree) /FT /Btn /Subtype /Widget /Rect [0 0 10 10] /AS /Off /AP << /N << /On 30 0 R /Off 30 0 R >> >> >>")
            .Object(13, "<< /T (size) /FT /Btn /Ff 49152 /DV /M /Kids [22 0 R 23 0 R] >>")
            .Object(14, "<< /T (colour) /FT /Ch /Ff 131072 /Subtype /Widget /Rect [0 100 100 120] /Opt [[(r) (Red)] [(g) (Green)]] /MK << /BG [1 1 0] /BC [0 0 1] >> >>")
            .Object(15, "<< /T (extras) /FT /Ch /Ff 2097152 /Subtype /Widget /Rect [0 130 100 190] /Opt [(A) (B) (C)] /DA (/Helv 10 Tf 0 g) >>")
            .Object(16, "<< /T (code) /FT /Tx /Ff 16777216 /MaxLen 5 /Subtype /Widget /Rect [0 200 100 220] /DA (/Helv 12 Tf 1 0 0 rg) >>")
            .Object(17, "<< /T (note) /FT /Tx /Ff 4096 /Subtype /Widget /Rect [100 0 200 100] /DA (/TiRo 10 Tf 0 g) /Q 1 /MK << /R 90 >> >>")
            .Object(20, "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /Rect [0 40 100 60] >>")
            .Object(21, "<< /Type /Annot /Subtype /Widget /Parent 10 0 R /Rect [0 70 200 90] >>")
            .Object(22, "<< /Type /Annot /Subtype /Widget /Parent 13 0 R /Rect [0 0 10 10] /AS /Off /AP << /N << /S 30 0 R /Off 30 0 R >> >> >>")
            .Object(23, "<< /Type /Annot /Subtype /Widget /Parent 13 0 R /Rect [20 0 30 10] /AS /Off /AP << /N << /M 30 0 R /Off 30 0 R >> >> >>")
            .Stream(30, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "q Q"u8.ToArray())
            .Object(40, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
            .Object(41, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding /FirstChar 32 /LastChar 34 /Widths [250 333 408] >>");
        return file.StartXref(file.XrefTable("/Size 42 /Root 1 0 R")).ToArray();
    }

    private static byte[] Save(PdfDocument pdf)
    {
        using MemoryStream output = new();
        pdf.Save(output);
        return output.ToArray();
    }

    private static string Appearance(PdfAnnotation widget)
    {
        return Encoding.Latin1.GetString(widget.GetAppearance()!.GetDecodedBytes());
    }

    private static T Field<T>(PdfDocument pdf, string name)
        where T : PdfField
    {
        Assert.True(pdf.Form.TryGetField(name, out PdfField field));
        return Assert.IsType<T>(field);
    }

    [Fact]
    public void FillsATextFieldAndDrawsEveryWidget()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfTextField name = Field<PdfTextField>(pdf, "name");

        name.Value = "J\u00F8rgen";

        Assert.All(name.Widgets, w => Assert.Contains("(J\u00F8rgen) Tj", Appearance(w), StringComparison.Ordinal));
        string first = Appearance(name.Widgets[0]);
        Assert.StartsWith("/Tx BMC\nq\n", first, StringComparison.Ordinal);
        Assert.Contains("/Helv ", first, StringComparison.Ordinal);
        Assert.Equal("[0 0 100 20]", name.Widgets[0].GetAppearance()!.Dictionary["BBox"]!.ToString());
        Assert.Throws<ArgumentException>(() => name.Value = new string('x', 31));

        using PdfDocument reopened = PdfDocument.Open(Save(pdf), Strict);
        Assert.Equal("J\u00F8rgen", Field<PdfTextField>(reopened, "name").Value);
        Assert.False(reopened.Form.NeedAppearances);
    }

    [Fact]
    public void AutoSizeShrinksLongTextToFit()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfTextField name = Field<PdfTextField>(pdf, "name");

        name.Value = "Short";
        double shortSize = FontSize(Appearance(name.Widgets[0]));
        name.Value = "A much longer name than fits";
        double longSize = FontSize(Appearance(name.Widgets[0]));

        Assert.Equal(12, shortSize);
        Assert.True(longSize < shortSize && longSize >= 4);
    }

    private static double FontSize(string content)
    {
        string line = content.Split('\n').First(l => l.EndsWith(" Tf", StringComparison.Ordinal));
        return double.Parse(line.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void ChecksABoxAndSelectsARadioButton()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfCheckBoxField agree = Field<PdfCheckBoxField>(pdf, "agree");
        PdfRadioButtonField size = Field<PdfRadioButtonField>(pdf, "size");

        agree.IsChecked = true;
        size.Value = "M";

        Assert.Equal(("On", "On"), (agree.Value, agree.Widgets[0].AppearanceState));
        Assert.Equal(["Off", "M"], size.Widgets.Select(w => w.AppearanceState));
        Assert.Throws<ArgumentException>(() => size.Value = "XL");

        size.Value = null;
        agree.IsChecked = false;
        Assert.Equal(["Off", "Off"], size.Widgets.Select(w => w.AppearanceState));
        Assert.Equal("Off", agree.Widgets[0].AppearanceState);
        Assert.Null(size.Value);
    }

    [Fact]
    public void SelectsChoices()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfComboBoxField colour = Field<PdfComboBoxField>(pdf, "colour");
        PdfListBoxField extras = Field<PdfListBoxField>(pdf, "extras");

        colour.Value = "g";
        Assert.Contains("(Green) Tj", Appearance(colour.Widgets[0]), StringComparison.Ordinal);
        Assert.Contains("1 1 0 rg", Appearance(colour.Widgets[0]), StringComparison.Ordinal);
        Assert.Contains("0 0 1 RG", Appearance(colour.Widgets[0]), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => colour.Value = "blue");

        extras.Select("B");
        Assert.Equal(["B"], extras.Values);
        Assert.Equal([1], extras.SelectedIndices);
        Assert.Contains(".6 .757 .855 rg", Appearance(extras.Widgets[0]), StringComparison.Ordinal);
        extras.Select("C", "A");
        Assert.Equal(["A", "C"], extras.Values);
        Assert.Equal([0, 2], extras.SelectedIndices);
        Assert.Throws<ArgumentException>(() => extras.Select("Z"));
        extras.Select();
        Assert.Empty(extras.Values);
    }

    [Fact]
    public void DrawsCombAndMultilineFields()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfTextField code = Field<PdfTextField>(pdf, "code");
        PdfTextField note = Field<PdfTextField>(pdf, "note");

        code.Value = "AB12";
        string comb = Appearance(code.Widgets[0]);
        Assert.Equal(4, comb.Split('\n').Count(l => l.EndsWith(" Tj", StringComparison.Ordinal)));
        Assert.Contains("1 0 0 rg", comb, StringComparison.Ordinal);

        note.Value = "\"! \"!";
        PdfStream appearance = note.Widgets[0].GetAppearance()!;
        Assert.Contains("/TiRo 10 Tf", Appearance(note.Widgets[0]), StringComparison.Ordinal);
        Assert.Equal("[0 1 -1 0 100 0]", appearance.Dictionary["Matrix"]!.ToString());
        Assert.Equal("[0 0 100 100]", appearance.Dictionary["BBox"]!.ToString());
        Assert.Same(appearance.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("TiRo"), pdf.Form.DefaultResources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("TiRo"));
    }

    [Fact]
    public void TextTheFieldFontCannotShowIsDrawnInHelvetica()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        pdf.Form.DefaultResources!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("TiRo")!["Encoding"] = PdfName.Get("MacRomanEncoding");
        PdfTextField note = Field<PdfTextField>(pdf, "note");

        note.Value = "Zebra";

        string content = Appearance(note.Widgets[0]);
        Assert.Contains("/EgapHelv 10 Tf", content, StringComparison.Ordinal);
        Assert.Equal("Helvetica", note.Widgets[0].GetAppearance()!.Dictionary.Get<PdfDictionary>("Resources")!.Get<PdfDictionary>("Font")!.Get<PdfDictionary>("EgapHelv")!.GetName("BaseFont")?.Value);
    }

    [Fact]
    public void TextNoStandardFontCanShowSetsNeedAppearances()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfTextField name = Field<PdfTextField>(pdf, "name");

        name.Value = "\u4E2D\u6587";

        Assert.True(pdf.Form.NeedAppearances);
        Assert.All(name.Widgets, w => Assert.Null(w.GetAppearance()));
        Assert.Contains(pdf.Warnings, w => w.Code == PdfWarningCode.AppearanceNotDrawn);
        Assert.Equal("\u4E2D\u6587", name.Value);
    }

    [Fact]
    public void ResetsToDefaults()
    {
        using PdfDocument pdf = PdfDocument.Open(File());
        PdfTextField name = Field<PdfTextField>(pdf, "name");
        PdfRadioButtonField size = Field<PdfRadioButtonField>(pdf, "size");
        PdfCheckBoxField agree = Field<PdfCheckBoxField>(pdf, "agree");
        name.Value = "Someone";
        size.Value = "S";
        agree.IsChecked = true;

        pdf.Form.Reset([name, size]);
        Assert.Equal(("Nobody", "M", true), (name.Value, size.Value, agree.IsChecked));
        Assert.Contains("(Nobody) Tj", Appearance(name.Widgets[0]), StringComparison.Ordinal);

        pdf.Form.Reset();
        Assert.False(agree.IsChecked);
        Assert.Equal("Off", agree.Widgets[0].AppearanceState);
    }

    [Fact]
    public void SetsNeedAppearances()
    {
        using PdfDocument pdf = PdfDocument.Open(File());

        pdf.Form.NeedAppearances = true;
        Assert.True(pdf.Form.NeedAppearances);
        pdf.Form.NeedAppearances = false;
        Assert.False(pdf.Form.Dictionary!.ContainsKey("NeedAppearances"));

        using PdfDocument empty = PdfDocument.Create();
        Assert.Throws<InvalidOperationException>(() => empty.Form.NeedAppearances = true);
    }
}
