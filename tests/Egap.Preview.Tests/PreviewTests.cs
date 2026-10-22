using System.Net.Http;

using Egap.Documents;
using Egap.Documents.Layout;
using Egap.Documents.Reports;

namespace Egap.Preview.Tests;

/// <summary>The preview tool's server, its page and its file mode, and ShowPreview sending pages to it.</summary>
public sealed class PreviewTests
{
    private static readonly HttpClient Client = new();

    [Fact]
    public async Task ShowPreviewSendsPagesTheServerShows()
    {
        PreviewState state = new();
        using PreviewServer server = new(state, 0);
        Document report = Report.Create(r => r.Page(p => p.Content(c => c.Column(col =>
        {
            col.Item().Text("First page");
            col.Item().PageBreak();
            col.Item().Text("Second page");
        }))));

        Task change = state.NextChange();
        Assert.True(report.ShowPreview(new PreviewOptions { Port = server.Port, Dpi = 36, Name = "Test report" }));
        await change.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        string page = await Client.GetStringAsync(server.Address, TestContext.Current.CancellationToken);
        Assert.Contains("Test report", page, StringComparison.Ordinal);
        Assert.Contains("/page/2.png", page, StringComparison.Ordinal);
        Assert.DoesNotContain("/page/3.png", page, StringComparison.Ordinal);
        byte[] png = await Client.GetByteArrayAsync(server.Address + "page/1.png", TestContext.Current.CancellationToken);
        Assert.Equal(0x89, png[0]);
        HttpResponseMessage missing = await Client.GetAsync(server.Address + "page/9.png", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task TheEventStreamTellsThePageToReload()
    {
        PreviewState state = new();
        using PreviewServer server = new(state, 0);
        using HttpResponseMessage events = await Client.GetAsync(server.Address + "events", HttpCompletionOption.ResponseHeadersRead, TestContext.Current.CancellationToken);
        using StreamReader reader = new(await events.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken));
        Assert.Equal(": ok", await reader.ReadLineAsync(TestContext.Current.CancellationToken));

        Document document = new();
        document.AddSection().Body.AddParagraph("Hello");
        Assert.True(document.ShowPreview(new PreviewOptions { Port = server.Port, Dpi = 24, Layout = new LayoutOptions { Fonts = FontSettings.Portable } }));

        string? line;
        do
        {
            line = await reader.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        while (line is not null && !line.StartsWith("data:", StringComparison.Ordinal));

        Assert.Equal("data: 1", line);
    }

    [Fact]
    public void ShowPreviewWithoutTheToolReturnsFalse()
    {
        Document document = new();
        document.AddSection().Body.AddParagraph("Nobody listens");

        Assert.False(document.ShowPreview(new PreviewOptions { Port = 1, Dpi = 24, Layout = new LayoutOptions { Fonts = FontSettings.Portable } }));
    }

    [Fact]
    public void AStrictLayoutFailureIsShownAndThrown()
    {
        PreviewState state = new();
        using PreviewServer server = new(state, 0);
        Document tooWide = Report.Create(r => r.Page(p => p.Size(80, 80).Margins(5).Content(c => c.Text(new string('W', 100)))));

        Assert.Throws<LayoutException>(() => tooWide.ShowPreview(new PreviewOptions { Port = server.Port }));

        Assert.NotNull(state.Snapshot().Error);
        Assert.Contains("class=\"error\"", PreviewPage.Html("x", 0, state.Snapshot().Error, 1), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilesAreShownAndDrawnAgainWhenSaved()
    {
        string folder = Directory.CreateTempSubdirectory("egap-preview-").FullName;
        try
        {
            string file = Path.Combine(folder, "notes.docx");
            Save(file, 1);
            PreviewState state = new();
            using FilePreview preview = new(state, file, 24);
            Assert.Single(state.Snapshot().Pages);

            Task change = state.NextChange();
            Save(file, 120);
            await change.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            await Task.Delay(500, TestContext.Current.CancellationToken);

            Assert.True(state.Snapshot().Pages.Count > 1);
            Assert.Null(state.Snapshot().Error);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }

        static void Save(string file, int paragraphs)
        {
            Document document = new();
            DocumentBuilder builder = new(document);
            for (int i = 0; i < paragraphs; i++)
            {
                builder.Writeln("Paragraph of text.");
            }

            document.Save(file);
        }
    }
}
