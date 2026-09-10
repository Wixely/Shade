# Shade investigation plan

- Status: Exploring
- Created / reviewed: 2026-09-10
- Next review: 2026-09-17, or before implementation
- Owner: Codex; product decisions: User
- Delivery dates: TBD

## Confirmed requirements

1. Product and repository name: Shade.
2. Create a separate local repository and begin investigation/planning.
3. Use modern C# on .NET 10 and CupriFace.
4. Build a new version of the multi-screen dimmer without copying code from the reference repository.
5. Support multiple screens with independent variable shading.
6. Provide Home Assistant integration. All shading and integration setup/control must be doable through the UI and verified end to end.
7. Support Windows and Linux, with Windows the primary operating system.

## Facts and decisions

See [dated research](research.md) for verified observations and [the foundation decision](decisions/2026-09-10-project-foundation.md) for confirmed constraints. Remote hosting and licensing remain open. No application behavior is implemented or verified.

## Shared preferences

The [shared preferences applied to Shade](preferences.md) are part of this plan. They adopt the applicable confirmed PLAN requirements for development, tooling/MCP, debugging, assets/accessibility, publishing and trimming, platform support, source control, privacy, and planning. They also identify conditional database, browser, service, and deployment defaults without adding those features to the scope. Canonical PLAN entries remain the source of truth; explicit Shade requirements take precedence. Reviewed 2026-09-10; recheck before the relevant work.

## Required capabilities and proposed supporting features

- A monitor map reflecting desktop arrangement, with readable labels and per-monitor dimming sliders.
- Individual enable/disable, optional linked adjustment, and restore-all action.
- Tray access, a recovery hotkey, and persisted settings with stable monitor matching.
- Required Home Assistant integration, with MQTT as the initial transport to investigate: UI-based setup, connection testing, discovery, per-screen control, status, and actionable errors.
- Required Windows and Linux implementations, with supported Linux desktop/session environments established through testing.
- Accessible keyboard operation, visible numeric values, and mixed-DPI layout.

Multi-screen variable shading, Home Assistant integration, complete UI operation, and both operating systems are confirmed requirements. Linked adjustment, hotkeys, and specific presentation details are proposed supporting features. Scheduling, idle dimming, hardware DDC/CI brightness, gamma changes, remote APIs/MCP, and automatic updates are deferred scope decisions.

## Architecture hypothesis

Use a CupriFace control window and a separate monitor/dimming backend behind small interfaces. On Windows, evaluate one original native layered window per active monitor, with no activation and click-through behavior. Serialize topology and dimming changes through an application coordinator; keep native handles and message-loop details out of UI models.

Proposed boundaries, not committed project structure:

| Area | Responsibility |
| --- | --- |
| Core | Validated dim levels, monitor identity, state transitions, restore-all |
| Desktop | CupriFace layout, bindings, user commands, accessibility |
| Windows | Display topology, overlay lifecycle, hotkeys, session events |
| Settings | Versioned JSON, atomic writes, recovery from invalid files |
| Home Assistant | Discovery, state/command mapping, reconnect and availability; MQTT transport under investigation |

JSON is the initial settings hypothesis; no database requirement is established. The application is an interactive desktop utility. A Windows Service cannot be assumed to own overlays in the user's desktop session. A future unattended/network component would need a separate service/session design and applicable deployment requirements.

## Investigation work packages

| ID | Work / owner | Exit evidence | Status |
| --- | --- | --- | --- |
| I1 | Reference behavior inventory / Codex | Documented capabilities separated from unknowns; original-code boundary recorded | Initial review complete; detailed behavior remains |
| I2 | CupriFace dependency and host spike / Codex | Pin package provenance; original slider changes a model; GPU/software input and UIA checks; determine host lifecycle/thread constraints | Next |
| I3 | Windows overlay spike / Codex | Two monitors dim independently; input passes through; focus stays with other apps; restore hotkey and exit remove overlays | Pending |
| I4 | Topology and state / Codex | Mixed DPI, negative coordinates, rotation, cloned displays, unplug/replug, docking, sleep/resume and identity tests | Pending |
| I5 | Home Assistant implementation specification / Codex | Define UI setup/test/discovery flow, dimming scale, stable identifiers, availability, reconnect, retained commands and credential storage; determine existing-entity migration needs | Pending |
| I6 | Packaging and performance / Codex | Complete the [shared publishing/functional-equivalence requirements](preferences.md); test published executable without .NET installed; record NativeAOT/trim decision, warnings, oracle, size, startup/idle CPU/memory evidence | Pending |
| I7 | Required Linux backend feasibility / Codex | Separate X11 and Wayland evidence, required compositor capabilities and tray/hotkey limitations; choose and test supported environments; escalate gaps without silently dropping Linux | Pending |
| I8 | First-release scope / User, technical synthesis / Codex | Acceptance criteria agreed from evidence; unresolved gaps recorded; implementation backlog ready | Pending |

## Prototype acceptance checks

- [ ] Two or more monitors with different dim levels; disabling one leaves the others unchanged.
- [ ] Define and label the level scale consistently (proposed: 0% dimming means unchanged; maximum TBD). Validate UI, persistence, and MQTT against the same model.
- [ ] Click, scroll, drag, type, and use taskbar controls through every overlay without stolen focus.
- [ ] Restore-all works even when the control window is hidden; hotkey conflicts are surfaced. Establish a conservative maximum and recovery behavior before testing near-black levels.
- [ ] Overlay bounds cover physical monitor rectangles at 100%, 150%, and 200% scaling, including monitors left/above primary and portrait orientation.
- [ ] Hotplug/reordering never transfers saved dimming blindly by display index. Ambiguous/new displays default to undimmed until identified.
- [ ] Exit and forced process termination leave no persistent dimming changes. Resume and lock/unlock do not leave stale overlays.
- [ ] Characterize HDR/SDR, fullscreen/borderless games, screen capture/sharing, RDP, and secure desktop behavior. Record unsupported cases explicitly.
- [ ] Keyboard and UI Automation operate real sliders and restore controls, including the published binary.
- [ ] On Windows and Linux, configure Home Assistant integration entirely through the UI: broker/transport settings, authentication, TLS settings where applicable, test connection, enable/disable, and readable errors. No manual JSON editing. Do not display or log stored credentials.
- [ ] Verify real Home Assistant discovery and per-monitor variable control in both directions: Shade UI changes update reported state; Home Assistant commands update the correct overlay and Shade UI. Specify and test the exact brightness/dimming conversion and on/off restoration semantics.
- [ ] Test invalid credentials, unavailable broker, reconnect, restart, retained stale commands, duplicate identifiers, monitor unplug/replug, and integration disable/removal. Show connection/availability state accurately and preserve local shading during network failures.
- [ ] Repeat core shading, UI configuration, keyboard/accessibility, persistence, and Home Assistant checks on Windows and the selected Linux environments. Assess X11 and Wayland separately; do not infer Linux behavior from CupriFace portability.
- [ ] Baseline and candidate AOT/trimmed builds pass the same behavioral checks, preserving renderer selection, native dependencies, bindings, and accessibility. Measure cold/warm startup and idle resource use before setting budgets.

Use original .NET tests for state/topology logic and targeted interactive Windows checks for overlays. Pure UI snapshots are insufficient. Add VS Code launch/tasks with the first runnable prototype. Keep raw diagnostics outside version control and summarize sanitized results.

## Risks and dependencies

| Risk | Impact | Mitigation |
| --- | --- | --- |
| UI host features differ between local source and released packages | Blocks integration | Inspect exact pinned version and run a small host spike |
| Transparent/topmost UI is mistaken for a complete dimming backend | Input/focus or lifecycle bugs | Prove native overlay behavior independently |
| Reordered or indistinguishable monitors receive wrong settings | Unexpected dark display | Stable identity strategy and undimmed fallback |
| HDR, fullscreen or compositor behavior defeats overlays | Inconsistent dimming | Explicit environment matrix and documented limits |
| NativeAOT/trimming removes binding, rendering or UIA paths | Partially broken release | Published functional oracle; document runtime deployment exception if necessary |
| Reused MQTT retained commands or IDs produce surprising state | Wrong display state or stale entities | Specify reconnect/retention semantics and isolated test identities |
| Linux compositor differences | Portability claim unsupported | Separate platform feasibility gate |

Dependencies: usable pinned CupriFace packages, Windows native interop, Linux display/compositor integration, multi-monitor test environments on both operating systems, and an isolated MQTT/Home Assistant setup. Access to test hardware/broker is not yet verified.

## Open questions

- Must Home Assistant discovery identities remain compatible with existing entities, or may Shade create a fresh device/entity set?
- Should Shade support full blackout, and what recovery interaction is acceptable?
- Should hardware brightness ever be supported, or should Shade remain a software dimmer?
- What Linux desktop/session environments matter?
- What remote host, visibility, and license should eventually be used? No answer is needed for local investigation.

## Next actions

1. Codex: I2, verify CupriFace 0.20.0 package APIs and dependency provenance; produce the smallest original control-window prototype with VS Code debugging.
2. Codex: I3 and I7, test native per-monitor overlays and emergency restore on Windows, then resolve Linux backend feasibility early enough to inform architecture.
3. Codex: Record evidence and refine the first-release backlog; User: resolve scope questions when they affect implementation.

Current request completion: the local repository and initial investigation documents are created. All application implementation, builds, runtime validation, and remote publication remain future work.
