using System.IO.Compression;
using System.Net.Http;
using System.Text;

using Egami;
using Egami.Formats.Png;
using Egami.Pixels;

using Egap.Documents.Layout;

namespace Egap.Documents;

/// <summary>Where <see cref="DocumentPreview.ShowPreview"/> sends a document's pages, and how sharp they are drawn.</summary>
public sealed class PreviewOptions
{
    /// <summary>The port the egap-preview tool listens on, on this machine; 12500 unless set.</summary>
    public int Port { get; init; } = DocumentPreview.DefaultPort;

    /// <summary>The resolution the pages are drawn at, in dots per inch; 110 unless set.</summary>
    public double Dpi { get; init; } = 110;

    /// <summary>The layout options; the document's <see cref="Document.DefaultLayoutOptions"/> when null.</summary>
    public LayoutOptions? Layout { get; init; }

    /// <summary>The name the preview page shows; the document's title when null.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// Sends a document's pages to the egap-preview tool (<c>dotnet tool install -g Egap.Preview</c>), which shows them on a
/// local web page and shows them again each time new pages arrive: run the code under <c>dotnet watch</c> and the page
/// follows every edit.
/// </summary>
public static class DocumentPreview
{
    /// <summary>The port the tool listens on unless told otherwise.</summary>
    public const int DefaultPort = 12500;

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>
    /// Lays the document out, draws its pages with Egap's own renderer and sends them to the egap-preview tool on this
    /// machine. Nothing leaves the machine: the tool listens on the loopback address only.
    /// </summary>
    /// <param name="document">The document.</param>
    /// <param name="options">The tool's port, the resolution and the layout options; null for the defaults.</param>
    /// <returns>Whether the tool took the pages; false when it is not running.</returns>
    /// <exception cref="LayoutException">The layout is strict and the document does not fit; the tool is told why first.</exception>
    public static bool ShowPreview(this Document document, PreviewOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PreviewOptions();
        byte[] package;
        try
        {
            package = Package(document, options.Layout, options.Dpi, options.Name);
        }
        catch (LayoutException e)
        {
            Send(Error(options.Name ?? document.Properties.Title, e.Message), options.Port);
            throw;
        }

        return Send(package, options.Port);
    }

    /// <summary>
    /// The pages as the tool takes them: a ZIP of <c>name.txt</c> and <c>page-1.png</c>, <c>page-2.png</c> and on, each page
    /// drawn at <paramref name="dpi"/>.
    /// </summary>
    internal static byte[] Package(Document document, LayoutOptions? layout, double dpi, string? name)
    {
        DocumentLayout pages = document.Layout(layout);
        using MemoryStream zip = new();
        using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Text(archive, "name.txt", name ?? document.Properties.Title ?? document.FileName ?? "Document");
            for (int i = 0; i < pages.Pages.Count; i++)
            {
                using Raster<Rgba8> raster = pages.Pages[i].Render(dpi);
                using Stream entry = archive.CreateEntry($"page-{i + 1}.png", CompressionLevel.NoCompression).Open();
                raster.Save(entry, PngFormat.Info);
            }
        }

        return zip.ToArray();
    }

    /// <summary>A package that holds no pages, only why there are none.</summary>
    internal static byte[] Error(string? name, string message)
    {
        using MemoryStream zip = new();
        using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            Text(archive, "name.txt", name ?? "Document");
            Text(archive, "error.txt", message);
        }

        return zip.ToArray();
    }

    private static void Text(ZipArchive archive, string name, string text)
    {
        using StreamWriter writer = new(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static bool Send(byte[] package, int port)
    {
        try
        {
            using ByteArrayContent content = new(package);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/zip");
            using HttpResponseMessage response = Client.PostAsync(new Uri($"http://127.0.0.1:{port}/api/document"), content).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // The tool is not running: a preview is a convenience, never a failure of the program.
            return false;
        }
    }
}
