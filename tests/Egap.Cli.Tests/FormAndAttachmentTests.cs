using System.Text;
using System.Text.Json;

using Egap.Pdf;

namespace Egap.Cli.Tests;

public sealed class FormAndAttachmentTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void FilledValuesReadBack()
    {
        MakeForm("form.pdf");
        _tool.Write("values.json", """{ "customer.name": "Ada Lovelace", "subscribe": true }"""u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("form", "fill", "form.pdf", "--data", "values.json", "--set", "customer.city=London", "-o", "filled.pdf").Code);
        ToolRun list = _tool.Run("form", "list", "filled.pdf", "--json");
        Assert.Equal(ExitCodes.Success, list.Code);
        Assert.Empty(Schema.Check("egap-form-1.json", list.Text));
        using JsonDocument json = JsonDocument.Parse(list.Text);
        Dictionary<string, string?> values = json.RootElement.GetProperty("files")[0].GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("value").GetString());
        Assert.Equal("Ada Lovelace", values["customer.name"]);
        Assert.Equal("London", values["customer.city"]);
        Assert.Equal("true", values["subscribe"]);

        // form list --json is itself a fill file.
        _tool.Write("listed.json", Encoding.UTF8.GetBytes(list.Text));
        Assert.Equal(ExitCodes.Success, _tool.Run("form", "fill", "form.pdf", "--data", "listed.json", "-o", "again.pdf").Code);
    }

    [Fact]
    public void AnUnknownFieldFailsTheFile()
    {
        MakeForm("form.pdf");
        Assert.Equal(ExitCodes.Failed, _tool.Run("form", "fill", "form.pdf", "--set", "nope=1", "-o", "x.pdf").Code);
    }

    [Fact]
    public void FlattenAndReset()
    {
        MakeForm("form.pdf");
        Assert.Equal(ExitCodes.Success, _tool.Run("run", "form.pdf", "form-fill", "--set", "customer.name=Grace", "+", "save", "filled.pdf", "+", "form-reset", "+", "save", "reset.pdf", "+", "form-flatten", "+", "save", "flat.pdf").Code);
        using PdfDocument reset = PdfDocument.Open(_tool.PathOf("reset.pdf"));
        reset.Form.TryGetField("customer.name", out PdfField field);
        Assert.Null(((PdfTextField)field).Value);
        using PdfDocument flat = PdfDocument.Open(_tool.PathOf("flat.pdf"));
        Assert.Empty(flat.Form);
    }

    [Fact]
    public void AttachmentsExtractByteForByte()
    {
        MakeForm("doc.pdf");
        byte[] data = [.. Enumerable.Range(0, 3000).Select(i => (byte)(i * 7))];
        _tool.Write("data.bin", data);
        _tool.Write("notes.txt", "hello"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("attach", "add", "doc.pdf", "--file", "data.bin", "--file", "notes.txt", "--relationship", "data", "-o", "with.pdf").Code);
        ToolRun list = _tool.Run("attach", "list", "with.pdf");
        Assert.Contains("data.bin (3000 bytes", list.Text, StringComparison.Ordinal);
        Assert.Equal(ExitCodes.Success, _tool.Run("attach", "extract", "with.pdf", "-o", "out").Code);
        Assert.Equal(data, File.ReadAllBytes(_tool.PathOf("out/data.bin")));
        Assert.Equal(ExitCodes.Success, _tool.Run("attach", "remove", "with.pdf", "--name", "notes.txt", "-o", "fewer.pdf").Code);
        using PdfDocument fewer = PdfDocument.Open(_tool.PathOf("fewer.pdf"));
        Assert.Single(fewer.Attachments);
    }

    [Fact]
    public void AnInvoiceRoundTrips()
    {
        _tool.Write("page.md", "# Invoice 2026-118\n"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "page.md", "-o", "page.pdf").Code);
        byte[] xml = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <rsm:CrossIndustryInvoice xmlns:rsm="urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100" xmlns:ram="urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100">
              <rsm:ExchangedDocumentContext>
                <ram:GuidelineSpecifiedDocumentContextParameter><ram:ID>urn:cen.eu:en16931:2017</ram:ID></ram:GuidelineSpecifiedDocumentContextParameter>
              </rsm:ExchangedDocumentContext>
              <rsm:ExchangedDocument><ram:ID>2026-118</ram:ID><ram:TypeCode>380</ram:TypeCode></rsm:ExchangedDocument>
            </rsm:CrossIndustryInvoice>
            """);
        _tool.Write("invoice.xml", xml);
        Assert.Equal(ExitCodes.Success, _tool.Run("invoice", "embed", "page.pdf", "--xml", "invoice.xml", "-o", "einvoice.pdf").Code);
        ToolRun read = _tool.Run("invoice", "read", "einvoice.pdf");
        Assert.Equal(ExitCodes.Success, read.Code);
        Assert.Equal(xml, read.Bytes);
    }

    /// <summary>A one-page PDF with a text field, a second text field and a check box.</summary>
    private void MakeForm(string name)
    {
        using PdfDocument document = PdfDocument.Create();
        document.Pages.AddBlank();
        document.Form.AddTextField("customer.name", 0, new PdfRectangle(72, 700, 300, 720));
        document.Form.AddTextField("customer.city", 0, new PdfRectangle(72, 660, 300, 680));
        document.Form.AddCheckBox("subscribe", 0, new PdfRectangle(72, 620, 90, 638));
        document.Save(_tool.PathOf(name));
    }
}
