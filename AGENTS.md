# Instructions for AI coding agents

Egap is a fully managed, MIT-licensed family of document libraries for .NET 10: Egap.Pdf, Egap.Documents and
Egap.Pdf.Conversion. It draws, decodes images and reads fonts through [Egami](https://github.com/inexin/Egami). Read
[CONTRIBUTING.md](CONTRIBUTING.md) first; this file adds what an agent needs on top of it.

## Hard rules

- **Clean room.** Write code from specifications (ISO 32000 and the specs it references, ECMA-376, WHATWG HTML,
  CommonMark), papers and permissively licensed libraries. Never copy, port, translate or paraphrase code, tests, test files
  or docs from GPL, LGPL or AGPL projects or from code without a license. If you translate code closely from a permissive
  library, add its notice to `THIRD-PARTY-NOTICES.md` in the same change.
- **Managed .NET 10 only.** No P/Invoke, no native binaries, no wrappers around C or C++ libraries. .NET's own APIs are fine.
  `src/` takes no package references except Egami's packages, .NET's own packages and build-time analyzers.
- **No comparisons with other PDF libraries** in this repository (output, speed or conformance).
- **Never publish.** Don't push to NuGet, push tags, create releases or change GitHub Actions settings, unless the maintainer
  explicitly asks for that.
- **Every change goes through a branch and a pull request.** Never commit to `main` or push to it, and never force-push it. The
  maintainer reviews and merges; don't merge your own pull request unless asked.

## Build, test and check

```sh
dotnet build Egap.slnx
dotnet test Egap.slnx
dotnet format Egap.slnx --verify-no-changes      # must pass before anything lands
python tools/update-public-api.py                 # adds new public API to PublicAPI.Unshipped.txt
python tools/check-doc-snippets.py                # every C# block in docs/ builds
docfx docs/docfx.json                             # the docs site, into docs/_site (dotnet tool install -g docfx)
```

- Warnings are errors, and the public API baseline (`PublicAPI.Shipped.txt`, `PublicAPI.Unshipped.txt` in each package) is
  checked by the build. Every new public member needs XML docs and an entry in `PublicAPI.Unshipped.txt`.
- Run only the tests for the area you change while working (`dotnet test --filter FullyQualifiedName~Redaction`), then the
  whole solution before landing.
- Hot code (parsing, filters, rendering, layout) must not get slower or allocate more than `main`; run the benchmarks in
  `benchmarks/Egap.Pdf.Benchmarks` and include the before and after numbers.
- **Fuzzing:** runs are 10 minutes (`tools/fuzz-afl.py`, see `tests/Egap.Pdf.Fuzz/README.md`). Longer runs only when the
  maintainer asks: they load the machine and skew other sessions' speed measurements.
- To build against an unreleased Egami commit, use `tools/pack-egami.py` (see the README's Building section); never commit
  a local Egami version to `Directory.Packages.props`.

## Documentation

Every user-visible change updates, in the same commit, the XML docs, the package's `README.md` in `src/<package>/` where its
summary changes, the guide in `docs/articles/guides/` for the area (new guides go into `docs/articles/toc.yml`), and
`CHANGELOG.md` under the package's next version (`## Egap.Pdf 1.5.0 (unreleased)`; add that heading if it is not there yet).
Write docs in plain, short sentences that say what the user gets. Describe behavior, not the planning history (no phase or
task numbers in docs).

## Code style

Match the surrounding code: its naming, comment density and idioms. `.editorconfig` and `dotnet format` decide formatting.

## Commits and pull requests

- Start every change on a new branch from an up-to-date `main`. Several agents often work at once, each in its own worktree
  and branch; leave other worktrees alone.
- Commit messages: `Area: what changed`, for example `Egap.Pdf optimize: faster duplicate image search`. Sign off every
  commit (`git commit -s`; CONTRIBUTING.md explains the DCO). Stage explicit paths, never `git add -A`.
- Before opening the pull request, rebase the branch onto `main` and run `dotnet format Egap.slnx --verify-no-changes` and
  the tests. Then push the branch and open the pull request, filling in `.github/PULL_REQUEST_TEMPLATE.md`, provenance
  section included. CI on a pull request only builds and tests on Linux x64; list your local results in the description.
- If `main` moves on, rebase the branch onto it, run the tests again and push the branch with `--force-with-lease`. That is
  the only force-push allowed, and only on your own branch.
