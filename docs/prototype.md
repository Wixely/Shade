# First feasibility prototype

- Started: 2026-09-10; review: 2026-09-17
- Owner: Codex
- Scope: I2, initial I3/I4 and initial performance evidence; not a release or platform-support claim

## Dependency and implementation decision

Pin CupriFace and CupriFace.Shell to the official v0.20.0 release packages, whose manifests identify source commit `56a9850decbb3e07b1cbd5f409f3d988e406b94b` and MIT licensing. Download into ignored `packages-local/`, verify SHA-256, and lock the complete NuGet graph. Do not reference the mutable sibling checkout. See [dependency provenance](dependencies.md).

Use original Win32 layered overlays on a dedicated message thread, behind a small backend interface. The UI reads levels from that backend so emergency restoration is reflected on refresh. Cap the prototype at 80% and require a registered Ctrl+Alt+Shift+R recovery hotkey before enabling dimming. Closing the application destroys its windows.

The initial session-only/no-persistence and restart-on-topology-change choices were superseded during this work by explicit user requirements. The [live-topology/identity/performance decision](decisions/2026-09-10-live-topology-and-performance.md) records the resulting automatic reconciliation and trusted hardware-identity settings policy. New displays start undimmed; remembered unique hardware identities recall their saved level. Raw serials and device paths are never persisted.

The 80% limit and recovery key are reversible prototype choices, not confirmed full-blackout product requirements. A normal .NET build is the functional baseline; NativeAOT, trimming and single-file publishing remain I6 work. No release pipeline is being selected.

## Test procedure defined before implementation

1. Restore exact dependency packages; build in Debug and Release. Run original .NET checks for level validation, independent state and real CupriFace slider/button input against a fake backend.
2. Run opt-in native smoke checks: enumerate physical monitor rectangles, create overlays, apply low independent levels briefly, check alpha/bounds/styles/no activation, restore and destroy. Keep identifying raw evidence in ignored artifacts.
3. Open the actual control window on GPU and software hosts. Check real keyboard input and UI Automation, independent displays, click/scroll/drag/type through overlays, hotkey restoration while another app has focus, and exit cleanup. Native assertions and headless rendering do not substitute for these interactive checks.
4. Test hotkey collision, mixed DPI, negative coordinates, topology and session changes, forced termination, HDR and full-screen behavior. Unsupported/unavailable environments stay explicitly unverified.
5. Verify VS Code launch with the C# debugger; configuration/build-path inspection alone is insufficient to claim a debugger session passed.

## Evidence

Verified on 2026-09-10 using .NET SDK 10.0.300 and Windows PowerShell 5.1:

| Check | Result and limit |
| --- | --- |
| Dependency restore | Official release package hashes verified; lockfile restore succeeds |
| Debug and Release compilation | Passed with zero warnings and errors; formatting verification also passed |
| Core/identity/settings tests | Level limits, EDID checksum validation, distinct serials, geometry/connector-independent identity, missing/duplicate serials, persistent collision ledger, deterministic clone groups, settings round trip and corrupt-file preservation passed |
| Recovery after settings write failure | Store-level retry test passed; corrupt/unknown-version files remain protected |
| Real CupriFace input, headless | Slider writeback, independent levels, individual/global restoration, arrow-key adjustment, changed display rows and no periodic clean-state redraw passed |
| Native current hardware | Six active desktop displays enumerated; distinct low levels applied; alpha, visibility, physical rectangles and click-through/no-activate styles verified; foreground window unchanged |
| Native topology injection | Real native windows and the WM_DISPLAYCHANGE path exercised with synthetic add, move, resize, remove and reconnect; settings survive restart; temporary catalog failure hides shading then recovers automatically via timer |
| Recovery/conflict | WM_HOTKEY handler restores every overlay; a second backend detects registration conflict and cannot dim. Physical keypress while gaming remains unverified |
| Live Windows UI Automation | GPU and forced SDL software hosts exposed six sliders; set/restore and normal close passed. Script checks each display independently |
| Overlay idle sample | 0.0 process CPU ms at timer resolution during approximately 3 seconds with six low-level overlays, after test work settled. No backend revision changes while idle. This is not a game performance benchmark |
| Complete UI host idle samples | Approximately 203-484 process CPU ms over 5 seconds for GPU samples; 234-344 ms over 5 seconds for software samples. These are short, noisy CPU-time samples, not percentages of the whole machine or GPU measurements |
| Layout inspection | Synthetic app-only rendered image inspected; sliders, numeric values, restore buttons, identity notes and status were readable. Scrollable control content accommodates more displays |
| VS Code | C# extension found; launch/tasks paths align with actual successful restore/build/run commands. F5/breakpoint attachment not tested |

The repeated-slider writeback test initially failed because the package's repeat binding did not qualify a row-local path for root-model writes. Shade now supplies an explicit ID-based binding path and implements the package's bindable-accessor interface. Keyboard reads use the same path. This avoids both silent write failure and index-based routing after reordering without patching CupriFace.

The expanded software-host test also found stale offscreen slider values in UI Automation. The host publishes accessibility after pixel damage; a scrolled-out value change can produce no damage. Shade invalidates the retained control-window frame only when a new backend revision is presented, so that state changes reach UIA. The six-slider software test then passed. This temporarily trades a full control-window repaint on state changes for correct accessibility, while idle still has no redraw requests. Owner: Codex, remove the workaround when a tested dependency fix decouples accessibility publication from pixel damage. The dependency's existing event-loop wakeups remain a separate performance investigation.

## Remaining validation and next actions

- Codex/User: physically unplug/replug, change modes/orientation, dock, reboot, and suspend/resume; verify stable IDs, exact overlay geometry and settings continuity on real hardware. Native injected tests do not prove driver/device behavior.
- Codex/User: test actual pointer/scroll/drag/typing through dimmed screens, recovery key under another app, forced process termination and no leftover windows.
- Codex: investigate CupriFace host wakeup/idle CPU overhead; run matched gaming frame-time/GPU/compositor tests as specified in the decision. No claim of stutter-free gaming yet.
- Codex: add UI enrollment and recovery for ambiguous monitors and invalid settings, Home Assistant discovery/setup/control, and required Linux backends. The arrangement map and Advanced controls are now implemented; see [control validation](controls-2026-09-10.md). Broker/desktop test environments remain unverified.
- Codex: complete VS Code debugger and eventual published NativeAOT/trim functional checks.

Recommended next action: Codex, validate real hardware identity continuity and gaming performance before integrating Home Assistant entity identities. Review by 2026-09-17.
