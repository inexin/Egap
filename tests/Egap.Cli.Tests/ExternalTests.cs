using System.Text;

namespace Egap.Cli.Tests;

/// <summary><c>egap &lt;name&gt;</c> runs <c>egap-&lt;name&gt;</c> from the PATH (CLI design Q2), so <c>egap preview</c> starts egap-preview.</summary>
public sealed class ExternalTests : IDisposable
{
    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void AnUnknownCommandRunsTheProgramOfThatNameFromThePath()
    {
        string folder = _tool.PathOf("bin");
        Directory.CreateDirectory(folder);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(folder, "egap-hello.cmd"), "@echo hello %1\r\n@exit /b 7\r\n", Encoding.ASCII);
        }
        else
        {
            string script = Path.Combine(folder, "egap-hello");
            File.WriteAllText(script, "#!/bin/sh\necho hello $1\nexit 7\n", Encoding.ASCII);
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _tool.Environment["PATH"] = folder;
        _tool.Environment["PATHEXT"] = ".CMD;.EXE";
        ToolRun run = _tool.Run("hello", "world");
        Assert.Equal(7, run.Code);
        Assert.Equal("hello world", run.Text.Trim());
    }

    [Fact]
    public void AnUnknownCommandWithNoProgramIsAUsageError()
    {
        _tool.Environment["PATH"] = _tool.PathOf("nowhere");
        ToolRun run = _tool.Run("preview");
        Assert.Equal(ExitCodes.Usage, run.Code);
        Assert.Contains("no egap-preview program is on the PATH", run.Error, StringComparison.Ordinal);
    }
}
