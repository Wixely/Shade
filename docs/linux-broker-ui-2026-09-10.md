# Linux broker form verification

- Reviewed: 2026-09-10; next review: 2026-09-17
- Owner: Codex
- Scope: Published application under WSLg/Xwayland; ephemeral settings

The Linux UI driver now enters broker fields through targeted X11 key events. It uses AT-SPI to locate and focus inputs, waits for their focused state, and sends key presses/releases only to the published child's PID-verified window. It does not inject global keyboard input or change system keyboard mappings. The original interop follows the [Xlib event specification](https://www.x.org/releases/X11R7.5/doc/libX11/libX11.html). This is synthetic native event testing, not a claim about physical keyboard layouts, IMEs or a real screen reader.

An in-process MQTTnet broker binds only loopback on a temporary port. The test turns TLS off through the UI for this plaintext fixture, types host/port/username/password, invokes Test connection and checks both the broker's exact synthetic credentials and the UI success message. It then replaces the password with an incorrect value, checks rejection, corrects it and verifies another successful connection. A stale success message cannot satisfy the rejection/correction sequence. Broker and owned application are stopped on completion or failure; no real credentials, settings files, trust stores or external integrations are changed.

The published trimmed default and software hosts pass the full password-correction sequence. The final software run also verifies exactly one broker rejection and waits for AT-SPI focused state before typing. Its Cupri debug frame readback was inspected: hostname, port and username render correctly; password characters render as bullets. The accessible name tree is checked for the known synthetic password. This does not yet audit every accessibility property, cache/event payload or non-ASCII password editing.

Two initial driver failures were investigated rather than treated as application regressions: ordinary field text is not exposed as a separate accessibility name, and the fixture initially left the application's default TLS switch on. A Cupri frame capture identified the latter. Existing TLS trust validation tests are separate; this plaintext fixture adds no TLS acceptance claim.

The capture also showed that accessibility focus could leave the bottom of the password field clipped. The host's 0.20.0-shade.4 [focus reveal fix](../third-party/CupriFace-Shell-accessibility.md) now provides field clearance, verified on trimmed default/software builds and through a fresh Cupri capture. A separate visual defect remains: the engine paints its blue focus outline using unscrolled bounds. Next action, Codex: correct and verify focus-outline placement and clipping, including ordinary keyboard navigation. AT-SPI Text/EditableText interfaces remain unfinished.

Save/enable, credential persistence and restart with nonempty passwords still require Linux credential-store verification. The current WSLg environment has not validated a working system keyring. Real Home Assistant acceptance, native Wayland UI, active Linux overlays, physical display changes and game frame-time testing remain separate open gates.

## Missing credential-store workflow, 2026-09-10

The runtime library inventory still contains GLib/GIO but no libsecret. Successful keyring storage therefore remains unverified here. A new opt-in published UI check covers the actual failure path:

```text
Shade.Tests --linux-ui-published <published-Shade> <fresh-evidence-directory> --interact --keyring-unavailable
```

Before launching, the fixture requires libsecret to be absent; if it can load the library, it refuses this scenario before any credential-store operation. It seeds disabled, synthetic broker settings in a fresh evidence-local settings directory and launches the published app with that directory. After the existing native field-entry and authenticated test/correction sequence, it invokes Save and enable. The UI must report that libsecret is required and no password was saved. Broker counters must show no integration connection attempt. After normal application exit, the settings file must remain byte-for-byte equal to the seeded file. Navigation, global exclusions and keyboard controls are still exercised.

This passes on the published trimmed default Linux host. All 24 shared Windows checks and 26 Linux checks (including credential-envelope tampering/Unicode and missing-store behavior) pass. The older headless missing-store fixture now also refuses to run if libsecret is present, so a negative test cannot accidentally create a real keyring item. No libraries or services were installed or configured.

Owner: Codex. Next, verify successful save/enable, keyring unlock/cancellation and restart against a working desktop Secret Service. This missing-dependency result does not establish those outcomes. Remaining native Windows keyboard checks can proceed independently.

## Engine paint follow-up, 2026-09-10

The [engine focus correction](../third-party/CupriFace.Engine/README.md) fixes the misplaced blue outline described above. Published Linux software broker entry, authentication, rejection and correction pass with the new source-built engine. The fresh Cupri screenshot was inspected and shows the outline surrounding the scrolled password field. Next action, Codex: verify ordinary keyboard focus navigation and continue credential persistence/keyring acceptance.

## Native keyboard scrolling, 2026-09-10

The engine now reveals controls reached by Tab and Shift+Tab. The published untrimmed default Linux host passes a forward/backward sequence from Broker hostname through Save and enable, checking actual focused state and viewport bounds after every native key. The test never activates Save and enable. Shared checks pass (24), including horizontal/vertical movement, reverse traversal and wraparound. See the [engine record](../third-party/CupriFace.Engine/README.md). Owner: Codex, next verify credential persistence and remaining platform keyboard cases.
