# Release automation

Reviewed 2026-09-26. Owner: Codex. Review when changing dependencies or release behavior.

Push an annotated `v<Version>` tag whose version matches `src/Shade/Shade.csproj` and has a `docs/release-<Version>.md` file. The Release workflow checks out the exact tagged commit, installs the SDK from `global.json`, verifies vendored sources, restores locked dependencies, runs shared/MQTT tests, and packages Windows x64. It publishes a ZIP and SHA-256 checksum through GitHub Releases. Versions containing a hyphen are published as prereleases and are never marked latest; versions without one are published as stable releases and become the repository's latest release. Existing tags and releases are never overwritten.

## Version bump checklist

Raising `<Version>` in `src/Shade/Shade.csproj` also requires updating the About-dialog version assertion in `tests/Shade.Tests/Program.cs`, which compares the exact version string. The workflow's own test step fails on a mismatch, so the bump and the assertion must be committed together, along with the matching `docs/release-<Version>.md` notes and the README version line.

The workflow uses Windows PowerShell 5.1, Git, the .NET SDK and GitHub CLI. It does not use Node/Python actions, caches or workflow-artifact uploads; durable files live in Releases. The job has a 30-minute timeout, serializes runs per tag, and grants only `contents: write`. The GitHub token is passed only to the release step. A release stays a draft until both uploads succeed. If a run fails after creating a draft, inspect that draft before rerunning; automatic overwrite is intentionally disabled.

The SDK installer is pinned to the official dotnet/install-scripts commit `47940ac9fc30a2f2dd19167165d0bb0774625f67`. Microsoft documents this PowerShell installer for [CI SDK installation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script). Release creation uses the documented [GitHub CLI](https://cli.github.com/manual/gh_release_create) draft, prerelease, notes-file and verify-tag options.

## Windows archives

Each release publishes two Windows archives from the same source and the same gates:
`Shade-<Version>-win-x64.zip`, the untrimmed self-contained single file every release has shipped, and
`Shade-<Version>-win-x64-aot.zip`, compiled ahead of time with NativeAOT. The untrimmed one stays the
recommended download; the ahead-of-time one is offered beside it, not in place of it, so a machine it
misbehaves on still has a build that runs.

`prepare-release.ps1 -Aot` produces the second. NativeAOT compiles and trims in one step, so
`PublishTrimmed` and `PublishSingleFile` take no part; the project's `EmbedNativeDependencies` target
embeds the unmanaged dependencies instead, and `package-release.ps1` refuses an ahead-of-time
candidate containing more than one executable, which is what proves the embedding happened rather than
the libraries being silently dropped. `TreatWarningsAsErrors` is relaxed for that publish alone,
because Silk.NET's native-asset resolver raises `IL3000`/`IL3002` against APIs that return nothing in
a single-file application; the warnings are deliberately left visible in the log rather than
suppressed. NativeAOT needs the MSVC linker, which the `windows-2025` runner provides.

Ahead-of-time compilation cannot cross-compile between operating systems, so a Linux ahead-of-time
build would have to be produced on Linux, and the embedded-library restore is Windows-only.

Locally, run `./scripts/package-release.ps1` in a workspace with fresh output directories. It verifies executable version metadata, collects dependency/runtime notices, excludes debug symbols and unexpected file types, and generates the archive checksum. Release archives contain only the executable, license notices, release notes and checksums. No screenshots, settings or test logs are included. A successful pipeline does not establish physical display, gaming or Linux desktop acceptance; the release notes retain these limits.

Before pushing a release tag, review all outgoing commits and assets for privacy. Build-generated binary output must contain no user settings, credentials, PDB files or local diagnostic output. License attribution is preserved intentionally. Never move a published tag to retry a release; fix forward with a new version when source changes are required.
