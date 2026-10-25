using System.IO.Pipes;
using System.Text.Json;

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
        McpClient client = await McpClient.CreateAsync(new StreamClientTransport(toServer, fromServer), cancellationToken: Token);
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
