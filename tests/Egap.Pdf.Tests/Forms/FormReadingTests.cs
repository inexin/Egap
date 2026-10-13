using Egap.Pdf.Tests.Parsing;

namespace Egap.Pdf.Tests.Forms;

/// <summary>The field tree, typed fields, values, flags and widgets (12.7): Phase 2 task 14.</summary>
public sealed class FormReadingTests
{
    private static readonly PdfReadOptions Strict = new() { Strict = true };

    /// <summary>
    /// order (10) has kids customer (11, a text field with two widgets 20 and 21) and agree (12, a check box merged with its
    /// widget); size (13) is a radio group of widgets 22 and 23; colour (14) a combo box; extras (15) a list box; sign (16) a
    /// signature field; push (17) a push button; 18 is a nameless node above text field 19.
    /// </summary>
    private static byte[] File()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R 13 0 R 14 0 R 15 0 R 16 0 R 17 0 R 18 0 R 10 0 R 99 0 R] /DA (/Helv 0 Tf 0 g) /Q 1"
                + " /NeedAppearances true /SigFlags 3 /DR << /Font << /Helv << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [20 0 R 21 0 R 12 0 R 22 0 R 23 0 R] >>")
            .Object(10, "<< /T (order) /Kids [11 0 R 12 0 R] /Ff 1 >>")
            .Object(11, "<< /T (customer) /Parent 10 0 R /FT /Tx /V <FEFF004C00E60072> /DV (none) /MaxLen 20 /Ff 4096 /TU (Customer name) /TM (cust) /DA (/Helv 9 Tf 0 g) /Kids [20 0 R 21 0 R 11 0 R] >>")
            .Object(12, "<< /T (agree) /Parent 10 0 R /FT /Btn /V /Yes /Subtype /Widget /Rect [0 0 10 10] /AS /Yes /AP << /N << /Yes 30 0 R /Off 30 0 R >> >> /F 4 >>")
            .Object(13, "<< /T (size) /FT /Btn /Ff 49152 /V /M /Kids [22 0 R 23 0 R] >>")
            .Object(14, "<< /T (colour) /FT /Ch /Ff 393216 /V (Teal) /Opt [(Red) [(G) (Green)] 7 [(B)]] /Q 2 >>")
            .Object(15, "<< /T (extras) /FT /Ch /Ff 2097152 /V [(A) (C)] /I [0 2] /TI 1 /Opt [(A) (B) (C)] >>")
            .Object(16, "<< /T (sign) /FT /Sig /V << /Type /Sig >> >>")
            .Object(17, "<< /T (push) /FT /Btn /Ff 65536 /Subtype /Widget /Rect [0 0 10 10] /MK << /CA (Go) >> >>")
            .Object(18, "<< /Kids [19 0 R] >>")
            .Object(19, "<< /T (inner) /Parent 18 0 R /FT /Tx >>")
            .Object(20, "<< /Type /Annot /Subtype /Widget /Parent 11 0 R /Rect [0 0 100 20] /P 3 0 R >>")
            .Object(21, "<< /Type /Annot /Subtype /Widget /Parent 11 0 R /Rect [0 30 100 50] >>")
            .Object(22, "<< /Type /Annot /Subtype /Widget /Parent 13 0 R /Rect [0 0 10 10] /AS /Off /AP << /N << /S 30 0 R /Off 30 0 R >> >> >>")
            .Object(23, "<< /Type /Annot /Subtype /Widget /Parent 13 0 R /Rect [20 0 30 10] /AS /M /AP << /N << /M 30 0 R /Off 30 0 R >> >> >>")
            .Stream(30, "/Type /XObject /Subtype /Form /BBox [0 0 10 10]", "q Q"u8.ToArray());
        return file.StartXref(file.XrefTable("/Size 31 /Root 1 0 R")).ToArray();
    }

    [Fact]
    public void ReadsTheTreeAndNames()
    {
        using PdfDocument pdf = PdfDocument.Open(File(), Strict);
        PdfForm form = pdf.Form;

        Assert.True(form.Exists);
        Assert.Equal(7, form.Count);
        Assert.Equal(["order", "order.customer", "order.agree", "size", "colour", "extras", "sign", "push", "", "inner"], form.GetAllFields().Select(f => f.FullName));
        Assert.Equal(8, form.GetTerminalFields().Count);
        Assert.False(form[0].IsTerminal);
        Assert.Equal(["customer", "agree"], form[0].Kids.Select(k => k.PartialName));
        Assert.Equal(form[0], form[0].Kids[0].Parent);
        Assert.True(form.TryGetField("order.customer", out PdfField customer));
        Assert.Equal(("Customer name", "cust"), (customer.AlternateName, customer.MappingName));
        Assert.False(form.TryGetField("order.nobody", out _));
        Assert.Equal((true, "/Helv 0 Tf 0 g", PdfTextAlignment.Center, 3, false), (form.NeedAppearances, form.DefaultAppearance, form.Alignment, form.SignatureFlags, form.HasXfa));
        Assert.NotNull(form.DefaultResources);
    }

    [Fact]
    public void TypesTerminalFieldsAndInherits()
    {
        using PdfDocument pdf = PdfDocument.Open(File(), Strict);
        PdfForm form = pdf.Form;

        PdfTextField customer = Assert.IsType<PdfTextField>(form[0].Kids[0]);
        Assert.Equal(("L\u00E6r", "none", 20), (customer.Value, customer.DefaultValue, customer.MaxLength));
        Assert.True(customer.IsMultiline);
        Assert.False(customer.IsReadOnly); // Its own /Ff replaces the parent's, which has the read-only bit.
        Assert.False(customer.IsComb);
        Assert.Equal("/Helv 9 Tf 0 g", customer.DefaultAppearance);
        Assert.Equal(PdfTextAlignment.Center, customer.Alignment);
        Assert.Equal("Tx", customer.FieldType);

        PdfCheckBoxField agree = Assert.IsType<PdfCheckBoxField>(form[0].Kids[1]);
        Assert.Equal(("Yes", "Yes", true), (agree.Value, agree.OnState, agree.IsChecked));

        PdfRadioButtonField size = Assert.IsType<PdfRadioButtonField>(form[1]);
        Assert.Equal(["S", "M"], size.Options);
        Assert.Equal("M", size.Value);
        Assert.True(size.IsNoToggleToOff);

        PdfComboBoxField colour = Assert.IsType<PdfComboBoxField>(form[2]);
        Assert.Equal([new PdfChoiceOption("Red", "Red"), new PdfChoiceOption("G", "Green"), new PdfChoiceOption("B", "B")], colour.Options);
        Assert.Equal(("Teal", true, PdfTextAlignment.Right), (colour.Value, colour.IsEditable, colour.Alignment));

        PdfListBoxField extras = Assert.IsType<PdfListBoxField>(form[3]);
        Assert.Equal(["A", "C"], extras.Values);
        Assert.Equal([0, 2], extras.SelectedIndices);
        Assert.Equal((true, 1), (extras.IsMultiSelect, extras.TopIndex));

        Assert.True(Assert.IsType<PdfSignatureField>(form[4]).IsSigned);
        Assert.Equal("Go", Assert.IsType<PdfPushButtonField>(form[5]).Caption);
        Assert.Equal(typeof(PdfField), form[6].GetType());
        Assert.IsType<PdfTextField>(form[6].Kids[0]);
    }

    [Fact]
    public void ListsWidgetsAndFindsTheirFields()
    {
        using PdfDocument pdf = PdfDocument.Open(File(), Strict);
        PdfForm form = pdf.Form;
        PdfField customer = form[0].Kids[0];

        Assert.Equal([new PdfReference(20, 0), new PdfReference(21, 0)], customer.Widgets.Select(w => w.Reference));
        Assert.Equal([new PdfReference(12, 0)], form[0].Kids[1].Widgets.Select(w => w.Reference));
        Assert.Empty(form[0].Widgets);

        PdfAnnotationCollection annotations = pdf.Pages[0].Annotations;
        Assert.Equal(customer, ((PdfWidgetAnnotation)annotations[0]).Field);
        Assert.Equal("order.agree", ((PdfWidgetAnnotation)annotations[2]).Field!.FullName);
        Assert.IsType<PdfRadioButtonField>(((PdfWidgetAnnotation)annotations[4]).Field);
        Assert.Equal(pdf.Pages[0], customer.Widgets[0].Page);
    }

    [Fact]
    public void ADocumentWithoutAFormHasNoFields()
    {
        using PdfDocument pdf = PdfDocument.Create();

        Assert.False(pdf.Form.Exists);
        Assert.Empty(pdf.Form);
        Assert.Empty(pdf.Form.GetAllFields());
        Assert.False(pdf.Form.NeedAppearances);
    }

    [Fact]
    public void LoopsInTheTreeEnd()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R] >> >>")
            .Object(2, "<< /Type /Pages /Kids [] /Count 0 >>")
            .Object(10, "<< /T (a) /Kids [11 0 R] /Parent 11 0 R >>")
            .Object(11, "<< /T (b) /Kids [10 0 R] /Parent 10 0 R >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 12 /Root 1 0 R")).ToArray());

        Assert.Equal(2, pdf.Form.GetAllFields().Count);
        Assert.Equal("a.b", pdf.Form[0].Kids[0].FullName);
    }

    [Fact]
    public void AWidgetWithoutAParentFindsTheFieldThatListsIt()
    {
        FileBuilder file = new FileBuilder()
            .Object(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [10 0 R] >> >>")
            .Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [12 0 R] >>")
            .Object(10, "<< /T (mixed) /FT /Tx /Kids [11 0 R 12 0 R] >>")
            .Object(11, "<< /T (named) /Parent 10 0 R >>")
            .Object(12, "<< /Type /Annot /Subtype /Widget /Rect [0 0 5 5] >>");
        using PdfDocument pdf = PdfDocument.Open(file.StartXref(file.XrefTable("/Size 13 /Root 1 0 R")).ToArray());

        PdfField mixed = pdf.Form[0];
        Assert.False(mixed.IsTerminal);
        Assert.Equal(mixed, Assert.Single(mixed.Widgets).Field);
        Assert.Equal(mixed, ((PdfWidgetAnnotation)pdf.Pages[0].Annotations[0]).Field);
    }
}
