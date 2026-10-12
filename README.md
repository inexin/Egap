# Egap

Egap is an open source family of document libraries for .NET, starting with **Egap.Pdf** for reading and writing PDF files.
It is a sister project to [Egami](https://github.com/inexin/Egami), the 2D imaging library.

- **MIT-licensed**, with no strings for commercial use.
- **Fully managed C#**: no native binaries, no P/Invoke, no wrapped native libraries.
- **Safe on untrusted files**: readers are built to be fuzz-tested from the start.

> **Status:** early development (0.1.0 preview). Egap.Pdf reads PDF files, damaged and encrypted ones included (RC4 and
> AES, revisions 2 to 6), into a lazily loaded object graph with change tracking, and saves them incrementally or by full
> rewrite, with or without object streams, keeping, dropping or applying AES-256 encryption. Nothing is published to NuGet.

```csharp
using Egap.Pdf;

using PdfDocument pdf = PdfDocument.Open("in.pdf", new PdfReadOptions { Password = "secret" });
foreach (PdfWarning warning in pdf.Warnings) Console.WriteLine(warning);

PdfDictionary catalog = pdf.Catalog;
PdfDictionary? info = pdf.Trailer.Get<PdfDictionary>("Info");
PdfReference added = pdf.Objects.Add(new PdfDictionary { ["Type"] = PdfName.Get("Example") });
catalog["Example"] = added;
pdf.Save("out.pdf");                                    // incremental: the original bytes plus the changes
pdf.Save("small.pdf", new PdfSaveOptions { Mode = PdfSaveMode.Rewrite, Compression = PdfCompression.Compact });
pdf.Save("locked.pdf", new PdfSaveOptions { Encryption = PdfEncryption.Aes256("user", "owner", PdfPermissions.Print) });
```

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
