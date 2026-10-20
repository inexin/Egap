# Contributing to Egap

Thanks for helping build Egap, a fully managed, MIT-licensed family of document libraries for .NET.

## The three rules

1. **Only clean sources.** Write your own code from specifications (ISO 32000 and the specs it references), textbooks and
   papers, and permissively licensed libraries. Never copy, port, translate or paraphrase GPL, LGPL or AGPL code, or code
   without a license. If you translate permissively licensed code closely, add its license notice to
   `THIRD-PARTY-NOTICES.md` in the same PR.
2. **No wrapped native code.** Egap is C# all the way: no P/Invoke, no native binaries, no wrappers around C or C++
   libraries. Using .NET's own APIs (for example `System.IO.Compression.ZLibStream`) is fine.
3. **Every contributed commit is signed off.** Add `Signed-off-by: Your Name <you@example.com>` to each commit in a pull
   request (`git commit -s`). This is the [Developer Certificate of Origin](https://developercertificate.org/): you certify
   that you wrote the change or have the right to contribute it under the MIT license. The maintainer's own commits are
   exempt.

Comparisons with other PDF libraries (output, speed, conformance) do not belong in this repository.

## Building and testing

You need the .NET 10 SDK. Egap draws, decodes images and reads fonts through [Egami](https://github.com/inexin/Egami),
whose packages come from nuget.org.

```
dotnet build
dotnet test
```

To try an unreleased Egami commit, `python tools/pack-egami.py --update` packs an Egami checkout next to this one into
`../egami-feed` and points `Directory.Packages.props` at it; then build with
`dotnet build -p:RestoreAdditionalProjectSources=../egami-feed`.

Benchmarks: `dotnet run -c Release --project benchmarks/Egap.Pdf.Benchmarks -- --filter "*"` for PDF, and
`benchmarks/Egap.Documents.Benchmarks` the same way for loading HTML and Markdown.

Fuzzing: see [tests/Egap.Pdf.Fuzz/README.md](tests/Egap.Pdf.Fuzz/README.md).

## Pull requests

- One topic per PR. Fill in the PR template, including the provenance section.
- Format with `dotnet format Egap.slnx` before committing; the build treats warnings as errors.
- New public API needs XML docs and an entry in `PublicAPI.Unshipped.txt`. `PublicAPI.Shipped.txt` holds released API:
  changing or removing anything in it is a breaking change and needs a maintainer's agreement first.
- Egap.Pdf and Egap.Documents take no package references except build-time analyzers, Egami's own packages and .NET's own
  packages. A format add-on (`Egap.Documents.<Format>`) may also use a managed MIT, BSD or Apache-2.0 package the maintainer
  approved by name, listed in [docs/references.md](docs/references.md). Propose any new one in an issue first.
- Changes to hot code (parsing, filters, rendering) need benchmark results. They must not be slower or allocate more than
  `main`.
- New test files must have a clear license.
