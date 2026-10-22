namespace Egap.Cli.Tests;

/// <summary>What one run of the tool did.</summary>
/// <param name="Code">The exit code.</param>
/// <param name="Text">Standard output as text.</param>
/// <param name="Bytes">Standard output as bytes (files written to -o -).</param>
/// <param name="Error">Standard error.</param>
internal sealed record ToolRun(int Code, string Text, byte[] Bytes, string Error);

/// <summary>Runs the tool in this process, through the same entry point the executable uses, in a scratch folder.</summary>
internal sealed class Tool : IDisposable
{
    public Tool()
    {
        Folder = Path.Combine(Path.GetTempPath(), "egap-cli-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
    }

    /// <summary>Gets the scratch folder the tool runs in.</summary>
    public string Folder { get; }

    /// <summary>Gets the environment variables the tool sees.</summary>
    public Dictionary<string, string> Environment { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the repository's test files folder.</summary>
    public static string Files { get; } = FindFiles();

    /// <summary>Copies a test file into the scratch folder and returns its name there.</summary>
    public string Copy(string file, string? name = null)
    {
        name ??= Path.GetFileName(file);
        string target = Path.Combine(Folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(Files, file), target, overwrite: true);
        return name;
    }

    /// <summary>Writes a file into the scratch folder and returns its name there.</summary>
    public string Write(string name, byte[] bytes)
    {
        string target = Path.Combine(Folder, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, bytes);
        return name;
    }

    /// <summary>A path in the scratch folder.</summary>
    public string PathOf(string name) => Path.Combine(Folder, name);

    /// <summary>Runs the tool with arguments.</summary>
    public ToolRun Run(params string[] args) => Run(args, null);

    /// <summary>Runs the tool with arguments and standard input.</summary>
    public ToolRun Run(string[] args, byte[]? stdin)
    {
        MemoryStream output = new();
        StringWriter text = new() { NewLine = "\n" };
        StringWriter error = new() { NewLine = "\n" };
        CliConsole console = new(() => new MemoryStream(stdin ?? []), () => new KeepOpen(output), text, TextWriter.Synchronized(error));
        int code = Cli.Run(args, console, Folder, name => Environment.TryGetValue(name, out string? value) ? value : null);
        return new ToolRun(code, text.ToString(), output.ToArray(), error.ToString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FindFiles()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !Directory.Exists(Path.Combine(folder, "tests", "files")))
        {
            folder = Path.GetDirectoryName(folder);
        }

        return Path.Combine(folder ?? throw new DirectoryNotFoundException("tests/files"), "tests", "files");
    }

    /// <summary>A stream that ignores Dispose, so a test can read what the tool wrote to standard output.</summary>
    private sealed class KeepOpen(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
