# Approved packages

Egap.Pdf and Egap.Documents reference only Egami's packages, .NET's own packages and build-time analyzers. A format add-on
(`Egap.Documents.<Format>`) may also reference one of the packages below. Each is managed code under a permissive licence and
was approved by name by the maintainer. Versions are minimums, set in `Directory.Packages.props`; raise one on purpose, with a
full test run.

| Package | Licence | Minimum version | Used by |
| --- | --- | --- | --- |
| [AngleSharp](https://github.com/AngleSharp/AngleSharp) | MIT | 1.8.5 | Egap.Documents.Html (HTML parsing) |
| [Markdig](https://github.com/xoofx/markdig) | BSD-2-Clause | 1.4.1 | Egap.Documents.Markdown (Markdown parsing) |
| [System.CommandLine](https://github.com/dotnet/command-line-api) | MIT | 2.0.13 | Egap.Cli (the egap command line, as in egami) |
| [ModelContextProtocol.Core](https://github.com/modelcontextprotocol/csharp-sdk) | MIT | 2.3.0 | Egap.Cli (egap mcp, as in egami mcp) |

Egap.Cli, the egap tool, is the one package with references outside the family besides the add-ons; it also compiles in
Egami.Cli.Host, egami's command host as source.

All of them ship as their own NuGet packages, so their licences travel with them; THIRD-PARTY-NOTICES.md lists only code copied
into this repository.

Egami's packages and .NET's own packages need no entry here. Egap.Pdf uses Microsoft's System.Security.Cryptography.Pkcs
(MIT) for signatures.
