# Release automation

Reviewed 2026-09-12. Owner: Codex. Review when changing dependencies or release behavior.

Push an annotated `v<Version>` tag whose version matches `src/Shade/Shade.csproj` and has a `docs/release-<Version>.md` file. The Release workflow checks out the exact tagged commit, installs the SDK from `global.json`, verifies vendored sources, restores locked dependencies, runs shared/MQTT tests, and packages Windows x64. It publishes a ZIP and SHA-256 checksum through GitHub Releases. Versions containing a hyphen are prereleases. Existing tags and releases are never overwritten.

The workflow uses Windows PowerShell 5.1, Git, the .NET SDK and GitHub CLI. It does not use Node/Python actions, caches or workflow-artifact uploads; durable files live in Releases. The job has a 30-minute timeout, serializes runs per tag, and grants only `contents: write`. The GitHub token is passed only to the release step. A release stays a draft until both uploads succeed. If a run fails after creating a draft, inspect that draft before rerunning; automatic overwrite is intentionally disabled.

The SDK installer is pinned to the official dotnet/install-scripts commit `47940ac9fc30a2f2dd19167165d0bb0774625f67`. Microsoft documents this PowerShell installer for [CI SDK installation](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script). Release creation uses the documented [GitHub CLI](https://cli.github.com/manual/gh_release_create) draft, prerelease, notes-file and verify-tag options.

Locally, run `./scripts/package-release.ps1` in a workspace with fresh output directories. It verifies executable version metadata, collects dependency/runtime notices, excludes debug symbols and unexpected file types, and generates the archive checksum. Release archives contain only the executable, license notices, release notes and checksums. No screenshots, settings or test logs are included. A successful pipeline does not establish physical display, gaming or Linux desktop acceptance; the release notes retain these limits.

Before pushing a release tag, review all outgoing commits and assets for privacy. Build-generated binary output must contain no user settings, credentials, PDB files or local diagnostic output. License attribution is preserved intentionally. Never move a published tag to retry a release; fix forward with a new version when source changes are required.
