# Egap

Egap is an open source family of document libraries for .NET, starting with **Egap.Pdf** for reading and writing PDF files.
It is a sister project to [Egami](https://github.com/inexin/Egami), the 2D imaging library.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke, no wrapped native libraries.
- **Safe on untrusted files**: readers are built to be fuzz-tested from the start.

> **Status:** early development (0.1.0 preview). The repository holds the project scaffold; no PDF code yet. Nothing is
> published to NuGet.

## Projects

| Project | What it is |
| --- | --- |
| `src/Egap.Pdf` | The PDF library (package `Egap.Pdf`). |
| `tests/Egap.Pdf.Tests` | Unit tests (xUnit v3). |
| `tests/Egap.Pdf.Fuzz` | SharpFuzz harness for AFL++. |
| `benchmarks/Egap.Pdf.Benchmarks` | BenchmarkDotNet benchmarks. |

## Building

You need the .NET 10 SDK.

```
dotnet build
dotnet test
```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE).
