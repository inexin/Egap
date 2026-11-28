using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Egap.Cli.Tests;

/// <summary>
/// egap mcp: the server run in this process through the same entry point as the executable, with the SDK's client on the
/// other end of a pair of pipes. Every call must do what the command line does, and stay inside the allowed folders.
/// </summary>
public sealed class McpTests : IAsyncDisposable
{
    private static readonly string[] PlainPdf = ["plain.pdf"];

    private readonly Tool _tool = new();

    private readonly List<IAsyncDisposable> _servers = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (IAsyncDisposable server in _servers)
        {
            await server.DisposeAsync();
        }

        _tool.Dispose();
    }

    [Fact]
    public async Task ListsOneToolPerCommandWithoutSigningByDefault()
    {
        McpClient client = await Connect();
        IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: Token);
        string[] names = [.. tools.Select(t => t.Name)];
        Assert.Contains("info", names);
        Assert.Contains("convert", names);
        Assert.Contains("form_fill", names);
        Assert.Contains("run", names);
        Assert.DoesNotContain("sign", names);
        Assert.DoesNotContain("encrypt", names);
        Assert.DoesNotContain("mcp", names);
        foreach (McpClientTool tool in tools)
        {
            Assert.Equal("object", tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
            Assert.False(tool.ProtocolTool.InputSchema.GetProperty("additionalProperties").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            string schema = tool.ProtocolTool.InputSchema.GetRawText();
            Assert.DoesNotContain("\"password\"", schema, StringComparison.Ordinal);
            Assert.DoesNotContain("password_file", schema, StringComparison.Ordinal);
        }

        McpClient signing = await Connect("--allow-sign");
        string[] all = [.. (await signing.ListToolsAsync(cancellationToken: Token)).Select(t => t.Name)];
        Assert.Contains("sign", all);

        // ocr runs a program named in the call, so it is never a tool.
        Assert.DoesNotContain("ocr", names);
        Assert.DoesNotContain("ocr", all);
        Assert.Contains("ocr_layer", all);
    }

    [Fact]
    public async Task InfoReturnsTheCommandsJson()
    {
        _tool.Copy("encrypted/plain.pdf");
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = PlainPdf }, cancellationToken: Token);
        Assert.NotEqual(true, result.IsError);
        JsonElement output = result.StructuredContent!.Value.GetProperty("output");
        Assert.Equal(1, output.GetProperty("files")[0].GetProperty("pages").GetInt32());
    }

    [Fact]
    public async Task CellsReadARangeAsJsonAndWriteNothing()
    {
        Egap.Spreadsheets.Workbook book = Egap.Spreadsheets.Workbook.Create();
        book.Worksheets[0]["A1"].Value = 7;
        book.Worksheets[0]["B1"].Value = "seven";
        book.Save(_tool.PathOf("book.xlsx"));
        byte[] before = File.ReadAllBytes(_tool.PathOf("book.xlsx"));

        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync("cells", new Dictionary<string, object?> { ["inputs"] = (string[])["book.xlsx"], ["range"] = "A1:B1" }, cancellationToken: Token);
        Assert.NotEqual(true, result.IsError);
        JsonElement row = result.StructuredContent!.Value.GetProperty("output").GetProperty("files")[0].GetProperty("rows")[0];
        Assert.Equal(7, row[0].GetDouble());
        Assert.Equal("seven", row[1].GetString());
        Assert.Equal(before, File.ReadAllBytes(_tool.PathOf("book.xlsx")));
    }

    [Fact]
    public async Task APasswordComesFromTheEnvironmentOnly()
    {
        _tool.Copy("encrypted/aes-256.pdf");
        McpClient client = await Connect();
        CallToolResult plain = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = (string[])["aes-256.pdf"], ["password"] = "user" }, cancellationToken: Token);
        Assert.True(plain.IsError);
    }

    [Fact]
    public async Task RunWritesAndCanPreview()
    {
        _tool.Copy("encrypted/plain.pdf");
        McpClient client = await Connect();
        JsonElement workflow = JsonDocument.Parse("""{ "egap": 1, "steps": [ { "op": "pages", "rotate": "90" }, { "op": "save", "path": "out/{name}.pdf" } ] }""").RootElement;
        CallToolResult result = await client.CallToolAsync("run", new Dictionary<string, object?> { ["inputs"] = PlainPdf, ["workflow"] = workflow, ["preview"] = true }, cancellationToken: Token);
        Assert.NotEqual(true, result.IsError);
        Assert.True(File.Exists(_tool.PathOf("out/plain.pdf")));
        Assert.Contains(result.Content, c => c is ImageContentBlock);
    }

    [Fact]
    public async Task RunRefusesEncryptWithoutAllowSign()
    {
        _tool.Copy("encrypted/plain.pdf");
        McpClient client = await Connect();
        JsonElement workflow = JsonDocument.Parse("""{ "egap": 1, "steps": [ { "op": "encrypt", "owner-password-env": "X" }, { "op": "save", "path": "o.pdf" } ] }""").RootElement;
        CallToolResult result = await client.CallToolAsync("run", new Dictionary<string, object?> { ["inputs"] = PlainPdf, ["workflow"] = workflow }, cancellationToken: Token);
        Assert.True(result.IsError);
        Assert.Contains("--allow-sign", JsonSerializer.Serialize(result.StructuredContent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunRefusesTheOcrStep()
    {
        _tool.Copy("encrypted/plain.pdf");
        McpClient client = await Connect("--allow-sign");
        JsonElement workflow = JsonDocument.Parse("""{ "egap": 1, "steps": [ { "op": "ocr", "engine-command": "calc {image}" }, { "op": "save", "path": "o.pdf" } ] }""").RootElement;
        CallToolResult result = await client.CallToolAsync("run", new Dictionary<string, object?> { ["inputs"] = PlainPdf, ["workflow"] = workflow }, cancellationToken: Token);
        Assert.True(result.IsError);
        Assert.Contains("runs a program", JsonSerializer.Serialize(result.StructuredContent), StringComparison.Ordinal);
        Assert.False(File.Exists(_tool.PathOf("o.pdf")));
    }

    [Fact]
    public async Task APathOutsideTheAllowedFoldersIsRefused()
    {
        McpClient client = await Connect();
        string outside = Path.Combine(Path.GetTempPath(), "egap-outside.pdf");
        CallToolResult result = await client.CallToolAsync("info", new Dictionary<string, object?> { ["inputs"] = (string[])[outside] }, cancellationToken: Token);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task CompareReportsADifferenceWithoutAnError()
    {
        _tool.Write("a.md", "# One\n"u8.ToArray());
        _tool.Write("b.md", "# Two\n"u8.ToArray());
        Assert.Equal(ExitCodes.Success, _tool.Run("convert", "a.md", "b.md", "-o", "{name}.pdf").Code);
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync("compare", new Dictionary<string, object?> { ["old"] = "a.pdf", ["new"] = "b.pdf" }, cancellationToken: Token);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, result.StructuredContent!.Value.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task CompareTurnsTwoDocumentsIntoTrackedChanges()
    {
        _tool.Write("v1.md", "Alpha bravo charlie.\n"u8.ToArray());
        _tool.Write("v2.md", "Alpha delta charlie.\n"u8.ToArray());
        McpClient client = await Connect();
        CallToolResult result = await client.CallToolAsync(
            "compare",
            new Dictionary<string, object?> { ["old"] = "v1.md", ["new"] = "v2.md", ["output"] = "redline.docx" },
            cancellationToken: Token);
        Assert.True(result.IsError != true, result.StructuredContent?.ToString());
        Assert.Equal(1, result.StructuredContent!.Value.GetProperty("exitCode").GetInt32());
        Assert.True(File.Exists(_tool.PathOf("redline.docx")));
        Assert.NotEmpty(Egap.Documents.Document.Load(_tool.PathOf("redline.docx")).Revisions);
    }

    [Fact]
    public async Task ServesItsResources()
    {
        McpClient client = await Connect();
        Assert.NotNull(client.ServerCapabilities.Resources);
        IList<McpClientResource> resources = await client.ListResourcesAsync(cancellationToken: Token);
        Assert.Equal(["egap://schema/workflow", "egap://formats", "egap://guide"], resources.Select(r => r.Uri));
        Dictionary<string, string> texts = [];
        foreach (McpClientResource resource in resources)
        {
            Assert.False(string.IsNullOrWhiteSpace(resource.ProtocolResource.Description));
            ReadResourceResult read = await client.ReadResourceAsync(resource.Uri, cancellationToken: Token);
            TextResourceContents contents = Assert.IsType<TextResourceContents>(Assert.Single(read.Contents));
            Assert.Equal(resource.Uri, contents.Uri);
            Assert.Equal(resource.MimeType, contents.MimeType);
            texts[resource.Uri] = contents.Text;
        }

        // The schema and the guide byte for byte as the repository holds them.
        string repository = Path.Combine(Path.GetDirectoryName(Tool.Files)!, "..");
        Assert.Equal(File.ReadAllBytes(Path.Combine(repository, "docs", "schemas", "egap-workflow-1.json")), Encoding.UTF8.GetBytes(texts["egap://schema/workflow"]));
        Assert.Equal(File.ReadAllBytes(Path.Combine(repository, "docs", "agents", "Egap.Cli.md")), Encoding.UTF8.GetBytes(texts["egap://guide"]));

        // The formats: every one convert writes, as its --to lists them, with the settings --set takes.
        using JsonDocument formats = JsonDocument.Parse(texts["egap://formats"]);
        JsonElement[] writes = [.. formats.RootElement.GetProperty("writes").EnumerateArray()];
        string listed = Regex.Match(_tool.Run("convert", "--help").Text, "The output format when -o does not say: ([a-z, ]+)\\.").Groups[1].Value;
        Assert.Equal(listed.Split(", "), writes.Select(w => w.GetProperty("format").GetString()));
        JsonElement pdf = writes.First(w => w.GetProperty("format").GetString() == "pdf");
        Assert.Contains("language", pdf.GetProperty("settings").EnumerateArray().Select(s => s.GetString()));
        string[] extensions = [.. formats.RootElement.GetProperty("reads").EnumerateArray().SelectMany(r => r.GetProperty("extensions").EnumerateArray().Select(e => e.GetString()!))];
        Assert.Contains(".docx", extensions);
        Assert.Contains(".pdf", extensions);
        Assert.Equal(extensions.Length, extensions.Distinct().Count());
        await Assert.ThrowsAnyAsync<McpException>(async () => await client.ReadResourceAsync("egap://nothing", cancellationToken: Token));
    }

    [Fact]
    public async Task PromptsNameOnlyItsTools()
    {
        McpClient client = await Connect();
        Assert.NotNull(client.ServerCapabilities.Prompts);
        Dictionary<string, McpClientTool> tools = (await client.ListToolsAsync(cancellationToken: Token)).ToDictionary(t => t.Name);
        IList<McpClientPrompt> prompts = await client.ListPromptsAsync(cancellationToken: Token);
        Assert.Equal(["convert_folder", "mail_merge", "compare_documents", "redact_pdf", "shrink_pdf"], prompts.Select(p => p.Name));
        foreach (McpClientPrompt prompt in prompts)
        {
            Assert.False(string.IsNullOrWhiteSpace(prompt.Description));
            IList<PromptArgument> arguments = prompt.ProtocolPrompt.Arguments ?? [];
            Assert.Contains(arguments, a => a.Required == true);
            Dictionary<string, object?> values = arguments.Where(a => a.Required == true).ToDictionary(a => a.Name, a => (object?)$"some {a.Name}");
            GetPromptResult result = await client.GetPromptAsync(prompt.Name, values, cancellationToken: Token);
            PromptMessage message = Assert.Single(result.Messages);
            Assert.Equal(Role.User, message.Role);
            string text = Assert.IsType<TextContentBlock>(message.Content).Text;
            foreach (string value in values.Values.Cast<string>())
            {
                Assert.Contains(value, text, StringComparison.Ordinal);
            }

            string named = Regex.Match(text, "Tools to call: ([a-z_, ]+)\\.").Groups[1].Value;
            Assert.NotEmpty(named);
            foreach (string tool in named.Split(", "))
            {
                Assert.True(tools.ContainsKey(tool), $"{prompt.Name} names {tool}, which is not a tool.");
            }

            // A required argument left out is a mistake the client hears about.
            await Assert.ThrowsAnyAsync<McpException>(async () => await client.GetPromptAsync(prompt.Name, new Dictionary<string, object?>(), cancellationToken: Token));
        }

        // The arguments the prompts tell the assistant to pass are the tools' own.
        (string Tool, string[] Arguments)[] used =
        [
            ("convert", ["inputs", "to", "output"]),
            ("merge_data", ["template", "data", "each", "output", "name_field"]),
            ("compare", ["old", "new", "markup", "as_documents", "redline", "output"]),
            ("redact", ["inputs", "text", "regex", "mark_only", "output"]),
            ("text", ["inputs"]),
            ("optimize", ["inputs", "preset", "output"]),
        ];
        foreach ((string tool, string[] names) in used)
        {
            JsonElement properties = tools[tool].ProtocolTool.InputSchema.GetProperty("properties");
            foreach (string name in names)
            {
                Assert.True(properties.TryGetProperty(name, out _), $"{tool} has no argument {name}.");
            }
        }
    }

    /// <summary>Starts egap mcp in this process on a pair of pipes and connects the SDK's client to it.</summary>
    private async Task<McpClient> Connect(params string[] options)
    {
        AnonymousPipeServerStream toServer = new(PipeDirection.Out);
        AnonymousPipeClientStream serverIn = new(PipeDirection.In, toServer.ClientSafePipeHandle);
        AnonymousPipeServerStream fromServer = new(PipeDirection.In);
        AnonymousPipeClientStream serverOut = new(PipeDirection.Out, fromServer.ClientSafePipeHandle);
        StringWriter error = new();
        CliConsole console = new(() => serverIn, () => serverOut, TextWriter.Null, TextWriter.Synchronized(error));
        Task<int> server = Task.Run(() => Cli.Run(["mcp", "--allow", _tool.Folder, .. options], console, _tool.Folder, _ => null), Token);
        // The SDK's client gives server/discover 5 seconds before it falls back to initialize, which a server that already
        // answered discover refuses. A busy CI machine can take longer than that to start the server, so wait for discover.
        McpClientOptions clientOptions = new() { DiscoverProbeTimeout = Timeout.InfiniteTimeSpan };
        McpClient client = await McpClient.CreateAsync(new StreamClientTransport(toServer, fromServer), clientOptions, cancellationToken: Token);
        _servers.Add(new Server(client, server, [toServer, fromServer, serverIn, serverOut]));
        return client;
    }

    private sealed class Server(McpClient client, Task<int> run, Stream[] streams) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await client.DisposeAsync();
            streams[0].Dispose();
            Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
            foreach (Stream stream in streams)
            {
                stream.Dispose();
            }
        }
    }
}
