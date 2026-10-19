using Egami;
using Egami.Pixels;

namespace Egap.Pdf.Tests.Forms;

/// <summary>Form fields made in code.</summary>
public sealed class FormFieldCreationTests
{
    [Fact]
    public void EveryKindOfFieldReadsBackTyped()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank(PdfPageSize.A4.ToRectangle());
        pdf.Pages.AddBlank(PdfPageSize.A4.ToRectangle());
        PdfForm form = pdf.Form;

        form.AddTextField("customer.name", 0, new PdfRectangle(72, 700, 300, 720), new PdfTextFieldOptions { Value = "Ola", MaxLength = 60, Required = true, Tooltip = "Your name" });
        form.AddTextField("customer.notes", 0, new PdfRectangle(72, 600, 300, 690), new PdfTextFieldOptions { Multiline = true });
        form.AddCheckBox("terms", 0, new PdfRectangle(72, 560, 86, 574), new PdfButtonOptions { Checked = true });
        form.AddRadioGroup("size", [(0, new PdfRectangle(72, 520, 86, 534), "S"), (0, new PdfRectangle(100, 520, 114, 534), "M"), (1, new PdfRectangle(72, 520, 86, 534), "L")], selected: "M");
        form.AddComboBox("country", 0, new PdfRectangle(72, 480, 200, 496), ["Norway", "Sweden"], editable: false, new PdfChoiceFieldOptions { Value = "Sweden" });
        form.AddListBox("colours", 0, new PdfRectangle(72, 400, 200, 470), ["Red", "Green", "Blue"], multiSelect: true);
        form.AddPushButton("reset", 0, new PdfRectangle(220, 480, 300, 500), "Reset", new PdfUriAction("https://egap.dev"));
        form.AddSignatureField("approval", 1, new PdfRectangle(72, 100, 272, 160));

        using PdfDocument saved = Reopen(pdf);
        Dictionary<string, PdfField> fields = saved.Form.GetTerminalFields().ToDictionary(f => f.FullName);
        Assert.Equal(["approval", "colours", "country", "customer.name", "customer.notes", "reset", "size", "terms"], fields.Keys.Order());

        PdfTextField name = Assert.IsType<PdfTextField>(fields["customer.name"]);
        Assert.Equal("Ola", name.Value);
        Assert.Equal(60, name.MaxLength);
        Assert.True(name.IsRequired);
        Assert.Equal("Your name", name.AlternateName);
        Assert.Equal("customer", name.Parent!.PartialName);
        Assert.True(Assert.IsType<PdfTextField>(fields["customer.notes"]).IsMultiline);
        Assert.True(Assert.IsType<PdfCheckBoxField>(fields["terms"]).IsChecked);
        PdfRadioButtonField size = Assert.IsType<PdfRadioButtonField>(fields["size"]);
        Assert.Equal("M", size.Value);
        Assert.Equal(["S", "M", "L"], size.Options);
        Assert.Single(saved.Pages[1].Annotations.OfType<PdfWidgetAnnotation>(), w => w.Field!.FullName == "size");
        PdfComboBoxField country = Assert.IsType<PdfComboBoxField>(fields["country"]);
        Assert.Equal("Sweden", country.Value);
        Assert.Equal(["Norway", "Sweden"], country.Options.Select(o => o.DisplayText));
        Assert.True(Assert.IsType<PdfListBoxField>(fields["colours"]).IsMultiSelect);
        Assert.Equal("Reset", Assert.IsType<PdfPushButtonField>(fields["reset"]).Caption);
        Assert.False(Assert.IsType<PdfSignatureField>(fields["approval"]).IsSigned);
    }

    [Fact]
    public void FieldsAreDrawnAndFillable()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank(new PdfRectangle(0, 0, 200, 100));
        PdfCheckBoxField box = pdf.Form.AddCheckBox("agree", 0, new PdfRectangle(10, 10, 50, 50), new PdfButtonOptions { Checked = true, BorderColor = null });
        PdfTextField text = pdf.Form.AddTextField("name", 0, new PdfRectangle(60, 10, 190, 50), new PdfTextFieldOptions { FontSize = 20 });

        text.Value = "XXXX";
        using PdfDocument saved = Reopen(pdf);
        using Raster<Rgba8> shown = saved.Pages[0].Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);

        Assert.True(Dark(shown, 10, 50, 50, 90) > 50, "the check mark is drawn");
        Assert.True(Dark(shown, 60, 50, 190, 90) > 50, "the typed text is drawn");

        PdfCheckBoxField reopened = (PdfCheckBoxField)saved.Form.GetTerminalFields().Single(f => f.FullName == "agree");
        reopened.IsChecked = false;
        using Raster<Rgba8> unchecked_ = saved.Pages[0].Render(new PdfRenderOptions { Dpi = 72 }, TestContext.Current.CancellationToken);
        Assert.True(Dark(unchecked_, 12, 52, 48, 88) < 5, "no check mark once cleared");
        Assert.NotNull(box);
    }

    [Fact]
    public void RefusesTakenNamesAndMissingPages()
    {
        using PdfDocument pdf = PdfDocument.Create();
        pdf.Pages.AddBlank();
        pdf.Form.AddTextField("a.b", 0, new PdfRectangle(0, 0, 10, 10));

        Assert.Throws<ArgumentException>(() => pdf.Form.AddTextField("a.b", 0, new PdfRectangle(0, 0, 10, 10)));
        Assert.Throws<ArgumentException>(() => pdf.Form.AddTextField("a.b.c", 0, new PdfRectangle(0, 0, 10, 10)));
        Assert.Throws<ArgumentException>(() => pdf.Form.AddTextField("a..c", 0, new PdfRectangle(0, 0, 10, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => pdf.Form.AddCheckBox("c", 3, new PdfRectangle(0, 0, 10, 10)));
        Assert.Throws<ArgumentException>(() => pdf.Form.AddRadioGroup("r", [(0, new PdfRectangle(0, 0, 10, 10), "x"), (0, new PdfRectangle(20, 0, 30, 10), "x")]));
        Assert.Throws<ArgumentException>(() => pdf.Form.AddTextField("comb", 0, new PdfRectangle(0, 0, 10, 10), new PdfTextFieldOptions { Comb = true }));
        pdf.Form.AddTextField("a.c", 0, new PdfRectangle(0, 20, 10, 30));
        Assert.Equal(2, Assert.Single(pdf.Form).Kids.Count);
    }

    /// <summary>The number of dark pixels in a box of a render, y down.</summary>
    private static int Dark(Raster<Rgba8> raster, int left, int top, int right, int bottom)
    {
        int dark = 0;
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                Rgba8 p = raster.GetPixel(x, y);
                if (p.R + p.G + p.B < 200)
                {
                    dark++;
                }
            }
        }

        return dark;
    }

    private static PdfDocument Reopen(PdfDocument pdf)
    {
        using MemoryStream saved = new();
        pdf.Save(saved);
        return PdfDocument.Open(saved.ToArray());
    }
}
