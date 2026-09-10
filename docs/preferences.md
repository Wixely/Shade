# Shared preferences applied to Shade

- Status: Applicable confirmed PLAN preferences adopted; conditional requirements identified below
- Source: User request on 2026-09-10 and the canonical PLAN files linked below
- Reviewed: 2026-09-10
- Next review: 2026-09-17, and before implementation, packaging, hosting, or publication decisions
- Owner: Codex (application and verification); User (scope and unresolved conflicts)

This document is part of Shade's plan. PLAN remains the source of truth for reusable preferences; these are project-specific applications of those preferences, not new global preference records. Read the current canonical entries before the relevant work. Explicit current user requirements take precedence; document project exceptions and preserve superseded decisions. Do not treat assumptions or provisional entries as confirmed preferences.

Links assume Shade and PLAN are sibling checkouts. If a checkout moves, resolve the canonical PLAN repository instead of silently treating broken links as permission to ignore its requirements.

## Development and product requirements

Source: [Development preferences](../../PLAN/preferences/development.md).

| Preference | Application to Shade / evidence required |
| --- | --- |
| C#/.NET 10 and top-level statements | Required stack, reinforced by the explicit request. Add the top-level entry point with the first prototype. |
| Native publishing | Prefer NativeAOT and one self-contained executable per supported platform/architecture, requiring no installed .NET runtime. Prove dependency and runtime compatibility; document any exception. A successful publish alone is insufficient. |
| Windows primary, Linux secondary | Both are required by the user's explicit Shade request; Windows has priority. Validate both operating systems and identify supported Linux sessions. macOS is outside current scope. |
| No Python/Node.js tooling without explicit permission | Applies to development, tests, builds, asset pipelines, dependencies' build tooling, and external testing. Do not introduce Tailwind or equivalent tooling transitively. Check exposed MCP capabilities and local MCPHub before using or proposing either runtime; that check does not authorize their use or integration installation. |
| PowerShell 5.1 automation | Keep local Windows scripts compatible with the installed Windows PowerShell 5.1. Do not assume `pwsh` exists. Document actual Linux commands and validate them separately. |
| VS Code debugging | Add repository-local launch/tasks configuration for each runnable project/sample, aligned with normal build/run commands. Verify the first prototype's debug path. There is no runnable project to debug yet. |
| Vendored assets | Keep third-party UI CSS, fonts, icons, JavaScript if ever needed, and other assets local; pin versions, preserve licenses/provenance, and document updates. Apply this to CupriFace assets as well as any future browser samples. |
| Focus behavior | Avoid `tabindex="-1"` on non-input elements in UI markup. Use native semantics; document an explicit accessibility exception if one is required. Verify keyboard and assistive-technology operation. |

CupriFace is explicitly selected for Shade's desktop UI. Its use of HTML/CSS does not make this a browser application or trigger a switch to Blazor.

## Publishing and functional equivalence

Sources: [Application trimming](../../PLAN/preferences/application-trimming.md) and [development/release preferences](../../PLAN/preferences/development.md).

Apply the complete canonical trimming workflow during I6; this checklist records its Shade deliverables:

1. Capture the release-equivalent baseline publish command, binary size, loose-output size breakdown, and verbatim stdout/stderr. Keep sensitive raw evidence out of Git and PLAN.
2. Define the functional oracle before changing flags. Exercise actual published shading, topology handling, UI bindings, GPU/software rendering, native integration, accessibility, settings, and Home Assistant workflows. Identify every logged-and-continued fallback path.
3. Gate experimental trimming inside the application project with a custom property, such as `<PublishTrimmed Condition="'$(Trim)' == 'true'">true</PublishTrimmed>`. Do not apply global trim flags to generators/analyzers; preserve the existing default baseline during evaluation.
4. Capture and investigate every trim/AOT warning; use the canonical warning guidance and verify remedies against the actual dependencies. Do not equate trim-compatible COM behavior with NativeAOT compatibility.
5. Change one variable at a time, rerun the oracle, compare output, and investigate new behavior. Compare untrimmed and trimmed controls back-to-back in the same environment before attributing a failure to that environment.
6. Evaluate single-file compression separately and together with trimming, including native extraction behavior and cold/warm launch times.
7. Report baseline versus candidate size, all functional checks, startup measurements, regressions, and unverified behavior. Present the exact release-pipeline change for the user's decision before altering that pipeline, as required by the canonical trimming workflow.

For future GitHub-built standalone releases, enable compatible trimming once functional equivalence is established; document incompatibility exceptions. Release packages should contain only useful user-facing files, excluding PDBs and unnecessary output. Include meaningful release notes, known limitations, and migration instructions. Exclude unused IIS assets with `PublishIISAssets=false` when applicable.

If GitHub Actions is chosen, every artifact upload must explicitly set `retention-days`: 1 for inter-job artifacts, 3 for ordinary development builds, 7 for test/diagnostic evidence, and at most 14 for documented expensive-to-reproduce artifacts. Put durable deliverables in GitHub Releases. Avoid unnecessary caches and uncontrolled cache growth.

## Conditional architecture defaults

Source: [Development preferences](../../PLAN/preferences/development.md). These requirements do not add features to Shade by themselves.

| Trigger | Requirement and present applicability |
| --- | --- |
| A database becomes necessary in a non-AOT application | Default to SQLite with Dapper and a project-owned manifest with ordered migrations. Evaluate DnaX's database-manifest/migration functionality first. Current versioned JSON settings are a hypothesis; no database need is established. |
| A browser application is added | Default to Blazor unless explicitly overridden. Only Blazor WebAssembly uses AOT; server-side Blazor uses normal runtime deployment. For heavy non-Blazor WebAssembly justified by requirements, prefer LLVM-based compilation and measured performance. No browser application is currently required. |
| A service/daemon component is added | Support interactive execution plus Windows Service and Linux systemd operation. A networked service also needs a Docker deployment option. The current interactive desktop utility's MQTT connection alone does not require creating a service. Keep desktop overlays in the user session. |
| A self-hosted service/API is proposed | Ask whether a supported HTTP API and MCP server add useful automation. If selected, use a versioned HTTP API with narrow MCP operations sharing domain/authorization paths; evaluate the canonical Ithaca pattern and DnaX remote-access capability. Current Home Assistant integration is required; extra HTTP/MCP surfaces remain a separate scope decision. |

## MCP and browser automation

Sources: [Browser automation](../../PLAN/preferences/browser-automation.md) and [global agent instructions](../../PLAN/global/AGENTS.md).

Before looking for another MCP server, using live browser control (including Home Assistant UI tests), or proposing Python/Node.js, inspect already exposed tools and check local MCPHub for the required capability. Prefer an appropriate existing service. Never install, register, enable, connect, or configure an MCP server, connector, or equivalent integration without explicit approval. If MCPHub is unavailable, unhealthy, or lacks the required capability, report what was checked and ask how to proceed; do not silently install a replacement.

## Hosting, identity, and disclosure

Sources: [Source control](../../PLAN/preferences/source-control.md), [GitLab](../../PLAN/preferences/gitlab.md), and [security/privacy](../../PLAN/preferences/security-and-privacy.md).

- Shade remains local while host and visibility are undecided. GitHub is for intentionally open-source projects, with MIT the usual license. The configured local GitLab is for private closed-source projects. Do not infer publication authorization from the public reference project or copy private work across that boundary.
- For any new local GitLab project, grant `agents` the Developer role, verify effective access, and preserve it through membership, namespace, visibility, and permission changes. Read the canonical GitLab entry for the configured endpoint when needed.
- Before commits for a Wixely GitHub repository, configure the repository-local Wixely name and exact noreply address from the canonical source-control entry. Before pushing, repair incorrect identity on outgoing user commits only and repeat the review; preserve upstream legacy history.
- Before every branch/tag push, identify all outgoing commits, refs, and objects. Review messages, author/committer metadata, filenames, modes, full changed content, credentials, personal context, internal endpoints, local paths, logs, and generated output.
- Inspect images, documents, archives, symbols, and other binaries for visible information and embedded metadata. Text searches alone do not complete this review.
- Scrub unnecessary sensitive information from the current tree and unpushed history, then rerun the full review. If safe removal is uncertain, functionality would be damaged, or shared history must change, stop and ask before pushing. Report findings by category/location without echoing sensitive values. Never commit secrets.

## Planning and completion

Sources: [Planning](../../PLAN/preferences/planning.md), [preference conventions](../../PLAN/preferences/README.md), and [PLAN agent instructions](../../PLAN/AGENTS.md).

Read repository instructions, README, relevant knowledge/preferences, current source, and recent decisions before work; inspect status and preserve unrelated changes. Separate facts, assumptions, decisions, risks, dependencies, and open questions. Record externally obtained facts with sources and verification dates, rechecking time-sensitive facts. Keep durable shared facts in PLAN knowledge and reusable preferences in their most specific canonical preference file, with status/source/date; do not duplicate them as independent truths. Preserve conflicts for confirmation and decision history through superseding records.

Maintain ISO dates, review dates, and concrete actions with an owner or `TBD`. Once hosting is chosen, promote the PLAN inbox pointer into `projects/<host>/shade/`; archive completed/abandoned work without rewriting history.

Validate in proportion to risk and distinguish tested results from assumptions. Keep edits/commits focused. Each planned-build handoff must list remaining work and recommend a concrete next action with an owner; if nothing remains, explicitly say so and recommend verification, release, or operational follow-up.

## Application checkpoints

- [ ] I2 / Codex: Verify original C#/.NET 10/CupriFace implementation, dependency/asset provenance, authorized toolchain, and working VS Code debugging.
- [ ] I3-I5 and I7 / Codex: Verify complete UI workflows, both operating systems, accessibility, recovery, monitor changes, and Home Assistant behavior using supported tooling.
- [ ] I6 / Codex: Complete the canonical publish/trim functional oracle and evidence report, including documented exceptions.
- [ ] Before any remote/release work / Codex: Re-read hosting/privacy preferences and apply the applicable identity, access, retention, and release requirements.

Preference incorporation is complete as of the review date. These implementation and publication checkpoints remain pending because Shade is still in investigation.
