using System.Diagnostics;
using System.Globalization;

using Egap.Documents;
using Egap.Documents.Layout;
using Egap.Preview;

int port = DocumentPreview.DefaultPort;
double dpi = 110;
bool open = true;
string? file = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out int p):
            port = p;
            i++;
            break;
        case "--dpi" when i + 1 < args.Length && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out double d) && d > 0:
            dpi = d;
            i++;
            break;
        case "--no-open":
            open = false;
            break;
        case "-h" or "--help":
            Console.WriteLine("""
                egap-preview [file] [--port 12501] [--dpi 110] [--no-open]

                Shows Egap documents on a local web page. Without a file, it shows what a program sends with
                Document.ShowPreview(); run the program under dotnet watch and the page follows every edit. With a DOCX
                file, it shows the file and draws it again whenever it changes; HTML and Markdown are shown from code,
                with the add-on that reads them and ShowPreview().
                """);
            return 0;
        default:
            file = args[i];
            break;
    }
}

PreviewState state = new();
using PreviewServer server = new(state, port);
Console.WriteLine($"egap-preview on {server.Address} (Ctrl+C to stop)");
using FilePreview? watcher = file is null ? null : new FilePreview(state, Path.GetFullPath(file), dpi);
if (open)
{
    try
    {
        Process.Start(new ProcessStartInfo(server.Address) { UseShellExecute = true });
    }
    catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        // No browser to start: the address above is enough.
    }
}

using CancellationTokenSource stop = new();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
try
{
    await Task.Delay(Timeout.Infinite, stop.Token);
}
catch (OperationCanceledException)
{
}

return 0;

namespace Egap.Preview
{
    /// <summary>A file shown and drawn again whenever it is saved.</summary>
    internal sealed class FilePreview : IDisposable
    {
        private readonly PreviewState state;
        private readonly string path;
        private readonly double dpi;
        private readonly FileSystemWatcher watcher;
        private readonly Timer settle;

        public FilePreview(PreviewState state, string path, double dpi)
        {
            this.state = state;
            this.path = path;
            this.dpi = dpi;
            Render();
            watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };

            // Editors save in bursts (truncate, write, rename): draw once things settle.
            settle = new Timer(_ => Render(), null, Timeout.Infinite, Timeout.Infinite);
            watcher.Changed += (_, _) => settle.Change(200, Timeout.Infinite);
            watcher.Created += (_, _) => settle.Change(200, Timeout.Infinite);
            watcher.Renamed += (_, _) => settle.Change(200, Timeout.Infinite);
            watcher.EnableRaisingEvents = true;
        }

        public void Render()
        {
            string name = Path.GetFileName(path);
            try
            {
                Document document = Document.Load(path);
                state.Update(DocumentPreview.Package(document, new LayoutOptions(), dpi, name));
            }
            catch (Exception e) when (e is IOException or DocumentException or NotSupportedException or UnauthorizedAccessException or InvalidDataException or FormatException)
            {
                state.Set([], name, e.Message);
            }
        }

        public void Dispose()
        {
            watcher.Dispose();
            settle.Dispose();
        }
    }
}
