using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Egap.Preview;

/// <summary>The pages being shown: the latest a program or a file gave, or why there are none.</summary>
internal sealed class PreviewState
{
    private readonly Lock gate = new();
    private IReadOnlyList<byte[]> pages = [];
    private string name = "egap-preview";
    private string? error;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Counts the updates, so the page asks for new images and not cached ones.</summary>
    public int Generation { get; private set; }

    public (IReadOnlyList<byte[]> Pages, string Name, string? Error, int Generation) Snapshot()
    {
        lock (gate)
        {
            return (pages, name, error, Generation);
        }
    }

    /// <summary>The most pages a package may hold.</summary>
    internal const int MaxPages = 2000;

    /// <summary>The most bytes a package's entries may unpack to, all together: 1 GB.</summary>
    internal const long MaxUnpacked = 1L << 30;

    /// <summary>The most bytes the name or the error may unpack to.</summary>
    private const int MaxText = 64 * 1024;

    /// <summary>
    /// Takes a package as <see cref="Egap.Documents.DocumentPreview"/> sends it: name, error, page images. A package that
    /// holds too many pages or unpacks to too much is refused whole, so a hostile one cannot fill the memory.
    /// </summary>
    /// <exception cref="InvalidDataException">The package is not a ZIP, or is too large unpacked.</exception>
    public void Update(byte[] package)
    {
        using ZipArchive archive = new(new MemoryStream(package), ZipArchiveMode.Read);
        long budget = MaxUnpacked;
        string? newName = Read(archive, "name.txt", ref budget);
        string? newError = Read(archive, "error.txt", ref budget);
        List<byte[]> newPages = [];
        for (int i = 1; archive.GetEntry($"page-{i}.png") is { } entry; i++)
        {
            if (i > MaxPages)
            {
                throw new InvalidDataException($"A package holds at most {MaxPages} pages.");
            }

            newPages.Add(Unpack(entry, int.MaxValue, ref budget));
        }

        Set(newPages, newName ?? "Document", newError);
    }

    public void Set(IReadOnlyList<byte[]> newPages, string newName, string? newError)
    {
        TaskCompletionSource done;
        lock (gate)
        {
            // An error keeps the last pages on show under the message, so the page does not go blank while code is fixed.
            pages = newError is null ? newPages : pages;
            name = newName;
            error = newError;
            Generation++;
            done = changed;
            changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        done.TrySetResult();
    }

    /// <summary>Completes on the next update.</summary>
    public Task NextChange()
    {
        lock (gate)
        {
            return changed.Task;
        }
    }

    private static string? Read(ZipArchive archive, string name, ref long budget)
    {
        return archive.GetEntry(name) is { } entry ? Encoding.UTF8.GetString(Unpack(entry, MaxText, ref budget)) : null;
    }

    /// <summary>An entry's bytes, at most <paramref name="limit"/> and what is left of <paramref name="budget"/>, whatever its header claims.</summary>
    private static byte[] Unpack(ZipArchiveEntry entry, int limit, ref long budget)
    {
        long most = Math.Min(limit, budget);
        using Stream stream = entry.Open();
        using MemoryStream bytes = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0)
        {
            if (bytes.Length + read > most)
            {
                throw new InvalidDataException("The package is too large unpacked.");
            }

            bytes.Write(buffer, 0, read);
        }

        budget -= bytes.Length;
        return bytes.ToArray();
    }
}

/// <summary>
/// A small HTTP server on the loopback address only: the preview page, its page images, an event stream that tells the page
/// to reload, and the endpoint programs send pages to.
/// </summary>
internal sealed class PreviewServer : IDisposable
{
    /// <summary>The largest package a program may send: 200 MB of page images.</summary>
    private const int MaxBody = 200 * 1024 * 1024;

    private readonly TcpListener listener;
    private readonly CancellationTokenSource stop = new();

    public PreviewServer(PreviewState state, int port)
    {
        State = state;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    public PreviewState State { get; }

    public int Port { get; }

    public string Address => $"http://127.0.0.1:{Port}/";

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
        stop.Dispose();
    }

    private async Task AcceptLoop()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                Request request = await ReadRequest(stream);
                byte[] body = new byte[Math.Clamp(request.Length, 0, MaxBody)];
                await stream.ReadExactlyAsync(body, stop.Token);
                if (!Trusted(request))
                {
                    // Only this machine's own pages and programs: a web page elsewhere, or one reached through a
                    // rebound host name, may neither send pages nor read them.
                    await Send(stream, 403, "text/plain", "Only requests to 127.0.0.1 or localhost from the preview page itself."u8.ToArray());
                    return;
                }

                await Respond(stream, request, body);
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or InvalidDataException or ObjectDisposedException or EndOfStreamException)
            {
                // A closed tab or a bad request ends that connection only.
            }
        }
    }

    /// <summary>A request's line and the headers the server looks at.</summary>
    private sealed record Request(string Method, string Path, int Length, string? Host, string? Origin, string? ContentType);

    /// <summary>
    /// Whether a request may be served: its Host names the loopback address or localhost on this port (no DNS rebinding),
    /// and any Origin is the preview page itself. Programs that send pages send no Origin; browsers send one with every POST.
    /// </summary>
    private bool Trusted(Request request)
    {
        string[] hosts = [$"127.0.0.1:{Port}", $"localhost:{Port}", $"[::1]:{Port}"];
        if (request.Host is null || !hosts.Contains(request.Host, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return request.Origin is null || hosts.Any(h => string.Equals(request.Origin, "http://" + h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The request line, the body's length and the headers looked at; the headers are read up to the blank line that ends them.</summary>
    private async Task<Request> ReadRequest(NetworkStream stream)
    {
        StringBuilder head = new();
        byte[] one = new byte[1];
        while (head.Length < 16384)
        {
            if (await stream.ReadAsync(one, stop.Token) == 0)
            {
                throw new EndOfStreamException();
            }

            head.Append((char)one[0]);
            if (head.Length >= 4 && head[^1] == '\n' && head[^2] == '\r' && head[^3] == '\n' && head[^4] == '\r')
            {
                break;
            }
        }

        string[] lines = head.ToString().Split("\r\n");
        string[] request = lines[0].Split(' ');
        if (request.Length < 2)
        {
            throw new InvalidDataException("Not an HTTP request.");
        }

        int length = 0;
        string? host = null, origin = null, contentType = null;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            string field = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (field.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int parsed))
            {
                length = parsed;
            }
            else if (field.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                host = value;
            }
            else if (field.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                origin = value;
            }
            else if (field.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
        }

        if (length > MaxBody)
        {
            throw new InvalidDataException("The body is too large.");
        }

        return new Request(request[0], request[1], length, host, origin, contentType);
    }

    private async Task Respond(NetworkStream stream, Request request, byte[] body)
    {
        string method = request.Method;
        string route = request.Path.Split('?')[0];
        if (method == "POST" && route == "/api/document")
        {
            // application/zip is not a type a page may send without asking first (CORS), so no page can post unasked.
            if (request.ContentType?.Split(';')[0].Trim().Equals("application/zip", StringComparison.OrdinalIgnoreCase) != true)
            {
                await Send(stream, 415, "text/plain", "Pages come as application/zip."u8.ToArray());
                return;
            }

            try
            {
                State.Update(body);
            }
            catch (InvalidDataException e)
            {
                await Send(stream, 400, "text/plain", Encoding.UTF8.GetBytes(e.Message));
                return;
            }

            await Send(stream, 204, "text/plain", []);
            return;
        }

        if (method != "GET")
        {
            await Send(stream, 405, "text/plain", "Only GET and POST /api/document."u8.ToArray());
            return;
        }

        (IReadOnlyList<byte[]> pages, string name, string? error, int generation) = State.Snapshot();
        if (route == "/")
        {
            await Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(PreviewPage.Html(name, pages.Count, error, generation)));
        }
        else if (route.StartsWith("/page/", StringComparison.Ordinal) && route.EndsWith(".png", StringComparison.Ordinal)
            && int.TryParse(route[6..^4], out int number) && number >= 1 && number <= pages.Count)
        {
            await Send(stream, 200, "image/png", pages[number - 1]);
        }
        else if (route == "/events")
        {
            await Events(stream, generation);
        }
        else
        {
            await Send(stream, 404, "text/plain", "Not found."u8.ToArray());
        }
    }

    /// <summary>Server-sent events: one "reload" each time new pages arrive, until the page goes away.</summary>
    private async Task Events(NetworkStream stream, int seen)
    {
        byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: keep-alive\r\n\r\n: ok\n\n");
        await stream.WriteAsync(head, stop.Token);
        while (!stop.IsCancellationRequested)
        {
            Task change = State.NextChange();
            if (State.Snapshot().Generation == seen)
            {
                // A comment now and then finds out when the tab is gone.
                if (await Task.WhenAny(change, Task.Delay(TimeSpan.FromSeconds(15), stop.Token)) != change)
                {
                    await stream.WriteAsync(": ping\n\n"u8.ToArray(), stop.Token);
                    continue;
                }
            }

            seen = State.Snapshot().Generation;
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"data: {seen}\n\n"), stop.Token);
        }
    }

    private async Task Send(NetworkStream stream, int status, string type, byte[] body)
    {
        string reason = status switch { 200 => "OK", 204 => "No Content", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 415 => "Unsupported Media Type", _ => "Error" };
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, stop.Token);
        await stream.WriteAsync(body, stop.Token);
    }
}

/// <summary>The preview page: the pages one under another, reloaded when the server says new ones arrived.</summary>
internal static class PreviewPage
{
    public static string Html(string name, int pages, string? error, int generation)
    {
        StringBuilder html = new();
        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>")
            .Append(WebUtility.HtmlEncode(name)).Append(" - egap-preview</title><style>")
            .Append("body{margin:0;background:#e8e8e8;font:14px system-ui,sans-serif}header{position:sticky;top:0;background:#333;color:#fff;padding:8px 16px}")
            .Append("main{display:flex;flex-direction:column;align-items:center;gap:16px;padding:16px}img{max-width:100%;box-shadow:0 1px 4px rgba(0,0,0,.3);background:#fff}")
            .Append(".error{background:#fdecea;color:#611a15;border:1px solid #f5c2c0;padding:12px 16px;white-space:pre-wrap;max-width:800px}")
            .Append("</style></head><body><header>").Append(WebUtility.HtmlEncode(name)).Append(" &middot; ").Append(pages)
            .Append(pages == 1 ? " page" : " pages").Append("</header><main>");
        if (error is not null)
        {
            html.Append("<div class=\"error\">").Append(WebUtility.HtmlEncode(error)).Append("</div>");
        }

        if (pages == 0 && error is null)
        {
            html.Append("<p>Waiting for a document: call <code>document.ShowPreview()</code>, or start <code>egap-preview file.docx</code>.</p>");
        }

        for (int i = 1; i <= pages; i++)
        {
            html.Append("<img alt=\"Page ").Append(i).Append("\" src=\"/page/").Append(i).Append(".png?v=").Append(generation).Append("\">");
        }

        html.Append("</main><script>new EventSource('/events').onmessage=()=>location.reload();</script></body></html>");
        return html.ToString();
    }
}
