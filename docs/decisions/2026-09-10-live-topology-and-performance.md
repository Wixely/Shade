# 2026-09-10: Live topology, identity and performance

- Status: Accepted user requirements; implementation policies remain prototype decisions
- Owner: Codex; product decisions: User
- Review: 2026-09-17
- Supersedes: the initial prototype's restart-on-topology-change approach and session-only settings hypothesis

## Confirmed requirements added during implementation

The user requires low performance impact, specifically efforts to avoid gaming stutter; automatic real-time handling of added displays and size/resolution changes without manual repair; and an attempt to solve repeatable unique monitor identification for saved settings, layout association and Home Assistant control.

## Prototype policy

- One static compositor-managed Win32 layered window per independent desktop surface. No application overlay frame loop, framebuffer uploads, screen capture, global hooks, gamma manipulation or hardware-brightness changes. The native thread blocks in GetMessage when idle. The control document requests refresh only after state changes; unchanged dimming values do nothing. State changes currently force a full control-window repaint to work around software-host accessibility publication being gated on visible pixel damage; see the prototype evidence.
- Read active CCD paths using QueryDisplayConfig and correlate source names with physical EnumDisplayMonitors rectangles. Receive display/device/power broadcasts. Resize/reposition retained overlays, remove disconnected overlays, and create new ones. On inconsistent/transient topology, hide overlays and retry automatically after one second; stable operation has no topology polling timer.
- Hash normalized EDID manufacturer/product/serial identity with an explicit `monitor-v1` schema. Do not include screen order, coordinates, mode, resolution, refresh rate, adapter index or connector in a hardware identity. Missing/unreadable/invalid EDID and known duplicate serials use a separately named connection identity and cannot automatically recall settings.
- Persist the duplicate-serial ledger: after observing a collision, unplugging one duplicate must not make the remaining monitor appear uniquely identifiable. Never expose raw serials or device paths in logs/settings. Hashes are identifiers, not anonymization guarantees; settings remain private local data.
- Mirrored targets share one desktop surface and therefore one grouped overlay ID derived from sorted physical member IDs. Independent software shading of physical mirrored outputs is not promised. Changing a clone group creates a new group identity and starts undimmed unless that exact trusted group is known.
- Keep desired dimming and last observed layout separately from identity. Restore trusted returning screens automatically; new/ambiguous identities start undimmed. Settings save atomically after 750 ms of inactivity rather than on every slider movement. Restore-all also clears disconnected remembered levels so a returning screen cannot undo emergency recovery.

These mechanisms improve matching but cannot create physical information absent from the monitor. A monitor with a duplicate serial that has never been seen beside its twin may still be mistaken for that twin. Firmware changes, docks, virtual displays and EDID overrides require further validation. A UI-assisted durable identity assignment for ambiguous displays remains necessary; it must not require manual configuration-file edits.

## Home Assistant boundary

Future entity IDs should combine a persisted random installation ID with the monitor identity, with layout/state as separately updated attributes. Do not use hostnames, enumeration order, resolution or geometry in entity IDs. Disconnected monitors should become unavailable, not be reassigned; reconnect should reuse the same trusted identity. This is an implementation specification, not working MQTT/discovery code. Ambiguous-display enrollment, group transitions and migration of existing entities remain open.

## Performance acceptance

Low CPU is necessary but insufficient: a topmost alpha surface can change desktop composition and a game's presentation path. Owner: Codex, compare Shade absent, overlays off, overlays on at several levels, and control window visible/minimized, with matched borderless/fullscreen, SDR/HDR and VRR conditions. Measure CPU, GPU, compositor activity, frame-time percentiles and hitch counts, using repeated warmed-up runs. User/Codex must select representative games and agree budgets before declaring the criterion met. No game frame-time result has been obtained yet.
