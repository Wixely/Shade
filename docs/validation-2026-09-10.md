# Windows prototype validation: 2026-09-10

- Owner: Codex
- Review: 2026-09-17, or after related code/dependency changes
- Scope: First implementation checkpoint; current Windows working tree with pinned CupriFace 0.20.0 packages
- Boundary: Concurrent implementation edits were present and preserved. These results describe the commands run in this session, not every later working-tree change or a released build.

## Fixes made during validation

- Settings now distinguish an unreadable/corrupt file from a temporary write failure. Unreadable files remain protected; a later save can recover from a transient I/O obstruction and clear the error. Added a regression check that blocks the destination with an empty synthetic directory, removes the obstruction, retries, and verifies the saved value.
- Dependency downloads now use a unique temporary file, verify its pinned hash before moving it into the local feed, and remove that invocation's partial file on failure. Existing packages are preserved. An interrupted-download simulation confirmed that neither a partial package nor a temporary download remained.

## Results

| Command/check | Result |
| --- | --- |
| `scripts/restore.ps1` | Passed: release package SHA-256 checks and locked restore |
| `dotnet build Shade.slnx --no-restore -c Debug` | Passed: zero warnings/errors |
| `dotnet run --project tests/Shade.Tests --no-build` | Nine checks passed, including transient-write recovery and actual CupriFace pointer/keyboard dispatch against a fake backend |
| `dotnet build Shade.slnx --no-restore -c Release` | Passed: zero warnings/errors |
| `dotnet run --project tests/Shade.Tests -c Release --no-build -- --native` | Eleven checks passed: nine regular checks plus physical display/native overlays and synthetic topology driving real Windows overlays |
| `scripts/smoke-ui.ps1` | Passed: six live sliders discovered through UI Automation, low-level set/restore, normal process exit |
| `scripts/smoke-ui.ps1 -Software` | Passed: same six-slider workflow and normal exit with the software host requested |
| Restore interruption simulation | Passed using a synthetic download failure in an isolated temporary directory; cleanup and error propagation verified |
| PowerShell parse, VS Code JSON, diff whitespace | Passed |

Native checks used low shading levels on six physical displays and verified window geometry, alpha, styles, unchanged foreground focus and restoration. Synthetic topology checks cover add, move, resize, removal, reconnect, saved state reload and restore-all. They do not establish physical docking or cable hotplug behavior.

The synthetic `artifacts/prototype-ui.png` was visually inspected: two readable monitor rows, distinct numeric levels and sliders, and individual/global restore controls. It contains generated monitor data and no desktop capture. A real six-monitor window was exercised through UI Automation, not visually screenshot-reviewed.

The native Release test process used 0.0 ms reported CPU over approximately 3 seconds after settling. The default/software live processes used 390.6/62.5 ms reported CPU over 5 seconds. These are single process measurements; they do not establish gaming frame-time impact, GPU/compositor usage, or the absence of fallback rendering. Renderer identity needs explicit evidence.

## Remaining work and next action

Required: physical pointer/scroll/drag/keyboard pass-through, physical recovery-key delivery, hotplug/dock/reboot identity continuity, mixed-DPI and session changes, forced-exit cleanup, HDR/fullscreen/capture/RDP, screen-reader use, gaming performance, and actual debugger launch. The native test invokes the recovery path directly; it is not a physical hotkey test.

Home Assistant integration, Linux shading, remaining UI workflows and standalone/AOT packaging still need implementation and end-to-end validation. Existing WSL distribution presence was observed, but no Linux desktop runtime was started or tested here. No browser automation, new integration installation, remote connection, or publication was performed.

Recommended next action — Codex: finish physical Windows acceptance and implement the Home Assistant setup/discovery workflow with isolated test infrastructure, while resolving the required Linux display/session backend. Keep new test results separate from these dated observations.

## Native Windows keyboard navigation, 2026-09-10

The published Windows single-file app passes native Tab and Shift+Tab through broker fields and actions on both software and default GPU hosts. The test verifies UI Automation focus and DPI-adjusted viewport bounds after each key. It does not activate Save and enable. See the [keyboard record](keyboard-2026-09-10.md). Owner: Codex, next address the missing Windows IsPassword property and continue remaining acceptance gates.

## Windows password semantics, 2026-09-10

The host now exposes IsPassword for masked textboxes and rejects protected Value reads. The prior published host fails the new flag assertion; the patched software build passes password and keyboard checks. All 24 shared checks pass. See [implementation and limitations](../third-party/CupriFace-Shell-windows-password.md). Owner: Codex, continue complete text accessibility and remaining integration/platform acceptance.

Password follow-up: the trimmed default GPU host passes as well. The untrimmed software broker setup workflow passes authenticated testing, encrypted save/enable, discovery removal and password forgetting with the new protected-value semantics. No real Home Assistant acceptance is implied. Owner: Codex, continue remaining packaging and integration acceptance.
