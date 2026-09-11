# Linux accessibility activation and password roles

- Status: Integrated in the vendored host, 0.20.0-shade.5
- Owner: Codex
- Reviewed: 2026-09-10; next review: 2026-09-17
- Upstream and license: [host provenance](CupriFace.Shell/README.md), MIT, Copyright (c) 2026 Wixely

The live Linux interaction check found that AT-SPI accepted an action for an off-screen **Use global** switch, but its checked state never changed. The engine activates accessibility nodes through normal hit testing. Clicking the centre of a node clipped by a scroll container therefore does nothing.

The [host patch](CupriFace-Shell-accessibility.patch) reveals the target before activating it. It walks only the target's scrollable ancestors, uses their viewport bounds to calculate the required scroll, and calls the engine's normal scroll operation. It resolves nodes again after each scroll because scrolling can refresh the render tree. Activation still uses the normal click path. The work occurs only when an accessibility action is requested; no recurring scroll/render work was added. This patch does not add a general AT-SPI ScrollTo interface or textbox editing.

## Accessibility focus follow-up, 2026-09-10

AT-SPI GrabFocus now reveals its target after the engine focuses it. Focus can rebuild the render tree; scrolling from those unlaid-out bounds silently does nothing. The helper uses the public, non-dispatching HitTest API to ensure current layout before inspecting bounds and again after any ancestor scroll. It leaves a 24-CSS-pixel margin for the editable child's outline/caret and containing field padding. Ordinary activation uses no extra margin. This is triggered only by accessibility requests and adds no idle work. Ordinary keyboard Tab navigation is not changed by this host patch.

The live broker driver focuses the initially clipped password field, returns to the hostname, and checks every focused input against the known content viewport of its fixed 720x740 window, including 12 pixels of clearance. The prior published host fails specifically at password clearance. A looser bounds check passed despite the clipped outline in Cupri's screenshot, so it was strengthened instead of being accepted as sufficient evidence.

The final trimmed default and software builds pass input clearance and the entire authenticated broker typing/rejection/correction sequence. All 22 shared checks pass, locked restore and source verification pass, and the revised patch applies cleanly to the pinned archive. The fresh Cupri screenshot confirms that the password field itself now has clearance.

Visual focus acceptance is still incomplete: the blue outline remains at its unscrolled position. Inspection of the pinned engine's `CupriDocument.AppendFocusRing` shows it uses `HitTesting.AbsoluteBox`, which does not apply ancestor scrolling, while the field and accessibility bounds move. This is an engine paint issue, distinct from the host's focus reveal. Next action, Codex: evaluate an independently reviewed engine correction using scroll-aware bounds and clipping, then verify keyboard and accessibility focus on Windows and Linux. Do not claim the screenshot proves correct focus-ring placement.

## Live evidence

### Password role follow-up, 2026-09-10

The broker password was reported as an ordinary entry even though Cupri masks its rendered text. The updated patch captures `data-mask` on textbox render nodes while publishing the UI-thread snapshot. AT-SPI reads that snapshot to return password role 40 consistently from GetRole, role-name methods and cache records. It retains the engine's textbox editing/focus states and never reads the live document from a D-Bus thread. Ordinary textboxes remain entry role 79. This follows the [AT-SPI role definitions](https://gnome.pages.gitlab.gnome.org/at-spi2-core/libatspi/enum.Role.html), reviewed 2026-09-10.

The live test failed against the prior published host specifically at the broker password role assertion, then passed against the patched untrimmed default host. The trimmed software host also passes, including role name and editable-state assertions. Shared Windows checks pass (22). The revised source patch applies cleanly to the pinned upstream archive; the provenance verifier checks all 20 vendored source files. The software Cupri frame readback was inspected and still shows global 42%, every screen off and the first screen opted out of global.

This is a role correction, not proof of credential entry or complete screen-reader support. AT-SPI Text/EditableText remain unimplemented; live broker typing, secure-value inspection with nonempty credentials and connection/save/reconnect remain next actions for Codex. The current test uses empty broker fields and does not establish nonempty password confidentiality. Windows password-role behavior is unchanged.

### Earlier activation checks

`--linux-ui-published <application> <new-evidence-directory> --interact` uses the existing Linux accessibility bus. It identifies the published child by its bus connection's Unix PID and interacts only with that application's tree. The reader selects controls by their advertised action/value interfaces, verifies state changes after asynchronous actions, and retries a stale subtree at most three times during navigation. The test uses ephemeral settings; it does not configure a broker or enable overlays.

Passing checks on WSLg/Xwayland, using untrimmed default, untrimmed software and trimmed default hosts:

- Global values change to 41 and 42 through AT-SPI Value.Set, with every screen remaining disabled.
- Advanced opens and exposes every individual slider; disabled screen levels remain zero.
- The initially off-screen first **Use global** switch becomes unchecked after activation and stays unchecked after another global change.
- Home Assistant opens and returns to the screen controls, retaining the global selection.
- Existing resize/stable-geometry and normal-close checks continue to pass.

The software driver forces additional presentations through window resizing and saves a fresh Cupri debug readback after opt-out. The inspected image shows global 42%, off screen buttons, an unchecked Use global switch, its Independent level hint, and the content scrollbar. Private evidence is under `artifacts/packaging/linux-controls-accessibility-{stable,software,trim}`; the image is `controls-global-opt-out.png` in the software directory. Application stderr is empty; software stdout contains only the expected software-selection announcement. The 22 shared checks and locked restore/source-provenance verification also pass.

Earlier runs are failure evidence: the first reader confused a heading with a same-named slider; the typed reader then exposed the off-screen action failure; a later navigation read crossed an accessibility-tree replacement and was fixed with bounded stale-object retries. No failed run is counted as acceptance.

Reference: [GNOME AT-SPI interfaces](https://gnome.pages.gitlab.gnome.org/at-spi2-core/libatspi/), reviewed 2026-09-10. The test uses the existing Tmds.DBus.Protocol dependency and original C# calls; no accessibility software, MCP service or other integration was installed.

Remaining / next action: Codex to implement and verify Linux textbox/password accessibility and broker-form interaction, then complete supported-desktop shading and automation acceptance. This check does not establish active overlays, nonzero independent dimming, real keyboard/Orca operation, native Wayland UI, real Home Assistant, physical topology or gaming performance.

## Engine focus-paint correction, 2026-09-10

The [vendored engine correction](CupriFace.Engine/README.md) now replaces unscrolled outline coordinates with screen coordinates and clips the outline through overflow ancestors. The previous outline finding above is retained as investigation history. The new raster regression fails with the original engine and passes with the correction; the published Linux Cupri screenshot confirms the outline surrounds the scrolled password field. General keyboard auto-scrolling and complete accessibility acceptance remain open.
