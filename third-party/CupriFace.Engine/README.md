# Vendored CupriFace engine

Shade builds the CupriFace 0.20.0 engine from the same pinned public source revision as its host: `56a9850decbb3e07b1cbd5f409f3d988e406b94b`. License: MIT, Copyright (c) 2026 Wixely, preserved in [CupriFace-LICENSE.txt](../CupriFace-LICENSE.txt). No source comes from a sibling working checkout or ScreenDimmer projects.

The 88 engine C# files, one binding-generator C# file and `SkiaNativeAssets.props` are recorded with original/current hashes in [provenance.json](provenance.json). Two files differ from upstream: `AccessibilityTree.cs` preserves exact single-line accessible values, and `CupriDocument.cs` contains the focus and editing changes described below. In particular, the [focus patch](../CupriFace-Engine-focus.patch) positions the focus outline using scroll-aware bounds and reapplies ancestor overflow clips, stopping at a top-layer boundary. It uses the same padding-box clip geometry and radius as normal painting. It adds work only while painting a visible focus outline, not an idle timer or overlay frame loop.

Version 0.20.0-shade.2 also reveals the newly focused control after Tab/Shift+Tab. The engine lays out the rebuilt tree, walks scrollable ancestors from inner to outer, accounts for horizontal and vertical offsets, and uses its normal captured-scroll path with a small outline margin. It resolves paths again after a scroll that may rebuild the tree and stops at top-layer boundaries. It does not continually force focus into view while a user scrolls manually.

Shade-owned project files retain the `CupriFace` assembly identity/version 0.20.0.0 and use informational version `0.20.0-shade.8`. The generator is built as an analyzer using its upstream Roslyn 4.14.0 dependency. Managed/native engine dependency versions are unchanged; the preserved native-assets props file controls runtime assets. No original engine package DLL is used by normal builds. Original packages remain pinned for regression comparisons.

## Verification, 2026-09-10

- Source build succeeds without warnings; 23 shared Windows checks pass.
- A raster regression checks horizontal/vertical scroll movement, partial clipping and a completely hidden focused control. Replacing only the engine DLL in a private test copy with the original release makes that same check fail because focus paint escapes the viewport.
- Published untrimmed Linux software UI passes the complete controls and broker entry/authentication/rejection/correction sequence. The Cupri debug screenshot was inspected: the outline now surrounds the password field at its scrolled position, fully within the content viewport.
- Published trimmed Linux default UI passes the same interaction sequence without fallback. Published Windows software controls pass slider/set/restore, Home Assistant form accessibility and normal close; its Cupri capture was inspected. These are specific checks, not the complete release oracle.
- The source patch applies cleanly to the exact upstream archive; restore checks all 90 source hashes.

These checks do not prove general CSS-transform focus behavior, all top-layer/rounded-clip combinations, complete screen-reader support, packaging parity or overlay/game performance. Those remain separate acceptance work.

## Keyboard scrolling follow-up, 2026-09-10

A new headless regression failed on the preceding engine because Tab left its target outside the viewport. With the correction, Tab, Shift+Tab and wraparound reveal controls on both axes; all 24 shared Windows checks pass. The published untrimmed Linux default host passes native Tab/Shift+Tab across broker fields and actions, with focus-state and visible-bounds assertions for each target. Native events go only to the owned window. The test focuses but does not activate Save and enable. It also retains the existing broker authentication/rejection/correction checks.

Native Windows Tab-navigation and unusual virtualized focus targets still need dedicated validation. Owner: Codex, continue those checks and Linux credential persistence.

Owner: Codex. Reviewed: 2026-09-10; next review: 2026-09-17. Before changing source, compare the upstream revision/license, review the complete diff, update the patch/provenance and rerun rendering plus Windows/Linux live controls. Before releasing, complete the packaging functional comparison with this engine build.

The trimmed Linux software host also passes the full native keyboard and broker sequence. Its Cupri debug screenshot was inspected: Save and enable is scrolled into view with a correctly placed outline and the password remains masked. This validates focus/navigation, not saved credentials or integration enablement.

## Accessible text replacement, 2026-09-10

Engine 0.20.0-shade.3 also synchronizes the focused edit buffer after AccessibilitySetText. This prevents the previous buffer from painting over the new value or restoring it on blur. The existing CupriDocument patch and provenance include this change. The focused-field regression checks model and rendered text, subsequent typing and blur. See the Windows accessibility follow-up in ../CupriFace-Shell-windows-password.md. Owner: Codex; published and broader accessibility verification remain open.

Engine 0.20.0-shade.4 additionally rejects accessibility text writes to disabled or read-only fields at the engine boundary, independently of host guards. A regression test first reproduced a disabled-field write, then verified rejection for `disabled`, `aria-disabled`, `readonly` and `aria-readonly`, unchanged model values after rejection, and successful editing after removing the attributes. This is document-level coverage; live assistive-technology state transitions remain separate acceptance work. Reviewed: 2026-09-10; owner: Codex.

## Exact single-line accessible values, 2026-09-10

Engine 0.20.0-shade.5 changes `AccessibilityTree.cs` to read ordinary single-line editor text from its caret anchor, preserving whitespace and Unicode. A placeholder anchor produces an empty value. Previously the accessibility tree collected rendered runs and trimmed the result, losing leading/trailing or whitespace-only content and potentially reporting a placeholder as the field value. Masked and multiline fields retain their existing path; no raw password value is added to the accessibility tree by this change.

The new regression fails on padded text before the fix and passes afterward for padded, whitespace-only, empty-with-placeholder and Unicode/supplementary-character values. All 25 shared Windows checks and 26 Linux checks pass. The live Windows state fixture also passes exact padded, blank and Unicode round trips in GPU/software hosts alongside restricted-write and re-enabled-editing checks. This does not establish multiline fidelity, full Text-pattern support, property-change events or screen-reader acceptance. Owner: Codex, continue those remaining accessibility gates and refresh published evidence when evaluating the final source state.

## Accessible activation follow-up, 2026-09-10

Version `0.20.0-shade.6` shares the keyboard scrolling path with accessibility activation. An Invoke/Toggle target is revealed through its scrollable ancestors, then resolved again after layout. A hit-test ancestry check rejects clipped or covered targets instead of clicking an unrelated control at the same coordinates. Activation uses CSS coordinates directly, avoiding a second zoom conversion. This runs only in response to an accessibility action; it introduces no timer or overlay rendering loop.

The regression reproduced an unrelated button click before the fix. It covers scrollable, hidden-overflow and occluded targets at 50%, 100% and 200% zoom. The live Windows smoke driver no longer uses Tab/Shift+Tab to reveal membership and restore controls before their accessibility actions. GPU and software verification evidence is stored locally under `artifacts/activation-reveal-*`; captures use Cupri's frame-dump feature. This does not establish full screen-reader, Linux desktop or gaming acceptance.

Verification: Release solution build completed with zero warnings/errors; all 26 shared Windows checks passed. Both live runs passed membership events in both directions, restore without changing global intent, broker text/password checks, native Tab/Shift+Tab, authenticated loopback broker setup and normal exit. Application stderr was empty in both modes. The Advanced Cupri capture was visually inspected. All 90 vendor hashes verify and the combined patch applies cleanly to the pinned source archive. One overlapping test rebuild initially encountered the still-running broker fixture's executable lock; after normal fixture exit, the complete suite passed. These are development-output checks, not a new published-package acceptance result.

Remaining / next action: Codex, verify the new activation behavior in self-contained output and continue accessibility/platform acceptance. Codex/User, complete physical topology, game frame-time and real Home Assistant checks using representative hardware and an authorized instance. Review: 2026-09-17.

## Published activation verification, 2026-09-10

Fresh self-contained Windows single-file baseline and opt-in trimmed builds of engine `0.20.0-shade.6` / host `0.20.0-shade.8` both pass GPU and software runs of `smoke-ui.ps1 -Configuration Release -PropertyEvents -BrokerSetup -KeyboardNavigation -ExecutablePath <published-Shade.exe>`. The driver activates membership and restore controls without pre-scrolling them through Tab. All four runs pass global independence, membership notifications, restoration, editable/protected fields, native keyboard traversal, authenticated loopback setup, encrypted credential saving, discovery removal and normal exit.

The baseline is 95,419,251 bytes and the trimmed candidate 32,491,474 bytes, each one executable. Publish logs contain no warnings/errors. Application stderr is empty in every run; stdout matches between baseline and trimmed for each renderer (empty GPU output and the expected explicit software selection). Exact commands, executable/source hashes and results are retained in ignored `artifacts/activation-published/`. This targeted comparison verifies the activation change in those builds; it does not repeat the complete packaging oracle or change release defaults.

Remaining / next action: Codex, continue full text accessibility interfaces and Linux desktop acceptance. Codex/User, complete real Home Assistant, physical topology and measured game frame-time acceptance. Clean-machine deployment and NativeAOT prerequisites remain open. Review: 2026-09-17.

## Programmatic focus reveal, 2026-09-10

Engine `0.20.0-shade.7` calls the existing keyboard reveal path after an accessibility focus request. Before this change, SetFocus could focus a clipped control without moving its scrollable ancestors. The new regression reproduced that failure; it now passes repeated direct focus changes between controls separated in both axes. All 27 shared Windows checks pass and the Release solution builds without warnings/errors. The older focus-paint regression resets scrolling after focus, then checks that subsequent manual scrolling can still partially and fully clip the outline; focus does not continuously override user scrolling.

This is an action-triggered layout/scroll change with no new timer or idle rendering. It does not implement UIA Text or AT-SPI Text/EditableText interfaces, which remain separate work. Owner: Codex, verify live focus behavior and continue those accessibility interfaces; physical/Linux/Home Assistant/game acceptance remains open. Review: 2026-09-17.

Live verification: Windows GPU and software runs of `smoke-ui.ps1 -Configuration Release -PropertyEvents -BrokerSetup -KeyboardNavigation` pass with an explicit viewport assertion after password SetFocus. Both complete membership/restore checks, field editing/password protection, native keyboard traversal, authenticated loopback setup and normal exit. The software run additionally used `-Capture`; its Cupri keyboard-focus frame was inspected and shows the focused action inside the scrolling viewport. Evidence is in ignored `artifacts/focus-reveal-gpu` and `artifacts/focus-reveal-software`. The four-check `--focus-rendering` entry point now includes direct focus and safe activation regressions and passes. Vendor hash verification and upstream patch applicability pass. These are development-build results, not updated packaged or Linux acceptance.

## Accessible editor state prerequisite, 2026-09-10

Engine `0.20.0-shade.8` adds `GetAccessibleTextState(path)` and `AccessibilitySelectText(path, anchor, caret)` for the planned text interfaces. The former returns an immutable ordinary bound-editor text snapshot with UTF-16 anchor/caret offsets and read-only state. Unfocused fields report no caret. Password controls return no snapshot, including during character peek; the separate IME API remains unchanged. Read these snapshots on the document thread before handing them to native accessibility threads.

Selection resolves the requested field, rejects passwords, disabled fields and invalid endpoints (including the interior of a UTF-16 surrogate pair), focuses/reveals the target, finishes active composition through normal editing, and sets the selection. Read-only text remains selectable. Rejected initial ranges do not steal focus. This is an engine API prerequisite, not advertised UIA TextPattern or AT-SPI Text/EditableText support yet. No host snapshot, native interface or recurring frame work was added.

All 28 shared Windows checks pass. The new test covers unfocused and focused snapshots, reverse emoji selection and typed replacement, immutable prior snapshots, preedit versus committed text, composition completion, read-only selection, disabled rejection and password exclusion across focus changes. Release build has zero warnings/errors; 90 vendor hashes and clean upstream patch applicability verify. Earlier live/packaged evidence remains version-specific and does not prove these new APIs on other platforms.

Design reference reviewed 2026-09-10: Microsoft's [Text and TextRange control patterns](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingtextandtextrange) and [ITextProvider contract](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.provider.itextprovider?view=windowsdesktop-10.0). Those interfaces require document/selection/visible ranges and point mapping; this prerequisite alone does not satisfy that contract.

Next action / owner: Codex, implement and verify host text ranges, selection actions and text-change events using these immutable snapshots, including geometry and Unicode navigation. Full screen-reader, Linux desktop, real Home Assistant, physical topology, gaming and release acceptance remain open. Review: 2026-09-17.
