# Windows password accessibility

- Status: Password protection integrated in host 0.20.0-shade.5; editable values added in 0.20.0-shade.6
- Reviewed: 2026-09-10; next review: 2026-09-17
- Owner: Codex
- License/provenance: [vendored host](CupriFace.Shell/README.md)

The broker password was visually masked but did not expose UI Automation's IsPassword property. The [Microsoft edit-control contract](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-supporteditcontroltype) requires that flag for password fields and requires password Value queries to fail. The flag also informs screen-reader handling of typed characters.

The [patch](CupriFace-Shell-windows-password.patch) captures masked textbox paths from `data-mask` while building the UI-thread snapshot. The provider returns IsPassword for those paths and rejects IValueProvider.GetValue with an error. It checks protection and reads values from a single immutable snapshot, so a rebuild cannot mix an old protection decision with a new value. It does not inspect the live document from UIA client threads or infer password status from labels. Keyboard editing and non-password values retain their existing behavior.

The revised live test fails on the previous published host at IsPassword, then passes on the patched software host. It verifies the flag before/after typing and its absence on the hostname. Password-value checks query without default substitution: the .NET convenience getter can return an empty default when the provider rejects the value, whereas the no-default query reports it as unavailable. Ordinary text must remain readable through the same query. Tab/Shift+Tab and normal close still pass.

The initial password patch did not implement UIA Text or programmatic text SetValue. The follow-up below adds SetValue; full Text-pattern and Narrator acceptance remain open. Successful Linux credential persistence, full Home Assistant acceptance and the remaining overlay/hardware/performance gates also remain open.

The published trimmed Windows default GPU host also passes the password and native keyboard checks. All 24 shared checks pass. The source patch applies cleanly to the pinned upstream archive, the host source verifier passes and normal locked restore succeeds. The comparison script applies this Windows patch alongside the existing host patches; the complete comparison script was not rerun for this change.

The published untrimmed software broker setup workflow passes authenticated Test connection, encrypted Save and enable, discovery removal and password forgetting. Its old assertion that read masking characters was replaced with a no-default protected-read check; broker authentication during Save and enable proves the unsaved password survived testing. This uses synthetic credentials, a loopback broker and an isolated settings directory, not a real Home Assistant instance.

## Editable values, 2026-09-10

Host 0.20.0-shade.6 now exposes writable ValuePattern for bound, enabled, non-read-only textboxes. Writes use the existing UI-thread action queue and the engine's binding setter; live editability is checked again when the action runs. Password writes are supported while password reads continue to fail. Ordinary empty values return an empty string. The implementation follows the [Windows Value pattern contract](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcore/nf-uiautomationcore-ivalueprovider-setvalue).

Live verification exposed an engine defect: replacing a focused field updated its model but left its previous edit buffer visible, and blur could commit that old buffer back to the model. Engine 0.20.0-shade.3 synchronizes the active buffer, caret and selection after a successful accessibility write, ends composition, clears stale undo state and suppresses password character peeking for the replacement. A document-level regression test fails before this fix and passes afterward, checking the model, rendered accessibility value, subsequent typing and blur. Temporary tracing and an unsuccessful snapshot-publication experiment were removed.

The live GPU development build passes `smoke-ui.ps1 -Configuration Release -BrokerSetup -KeyboardNavigation -AccessibleText`: hostname replacement and clearing, password replacement from an initially incorrect value, protected reads, native keyboard navigation, authenticated test/save, encrypted persistence and cleanup. These checks use isolated synthetic credentials. The prior packaging matrices and physical acceptance folder contain the earlier engine/host versions and do not establish published equivalence for this change. Owner: Codex, complete software capture and published verification; full Text-pattern, screen-reader and disabled/read-only fixture coverage remain open.

The final software development build also passes the same workflow with `-Software -Capture`; the Cupri broker-form capture was inspected. Both hosts have empty application stderr; GPU stdout is empty and software stdout contains only the expected explicit software-selection message. Release build, 24 shared checks and locked restore pass. Owner: Codex, next verify published behavior and disabled/read-only cases, then continue full accessibility acceptance.

## Engine guards and packaged editing, 2026-09-10

Engine 0.20.0-shade.4 rejects text writes to disabled/read-only elements directly, so other callers cannot bypass the Windows host guard. The document-level test initially reproduced an unauthorized disabled-field change; after the fix it verifies `disabled`, `aria-disabled`, `readonly` and `aria-readonly`, unchanged model values after rejection, and editing after removal of each restriction. All shared checks pass on Windows and Linux. This does not yet test live UIA IsReadOnly notifications or disabled-state races.

Fresh Windows single-file baseline and trimmed candidates contain engine 0.20.0-shade.4 and host 0.20.0-shade.6. They are 95,416,691 and 32,491,474 bytes respectively. Publish output reports no warnings. Private build records preserve executable and source-manifest hashes; the UI run records identify the exact executable tested. These candidates extend the editable-text evaluation and do not replace the complete packaging oracle or the older physical acceptance folder.

All four published GPU/software runs pass the accessible-text, native keyboard and full broker setup workflow. Each result hash matches its candidate. Stderr is empty for every run and stdout is identical between variants for each renderer. The trimmed software Cupri keyboard-focus capture was inspected. Owner: Codex, next cover live disabled/read-only state transitions and remaining Text-pattern/screen-reader behavior; physical, Linux desktop and real Home Assistant acceptance remain open.

## Live restriction transitions, 2026-09-10

The isolated `AccessibilityStateApp` test fixture uses the normal Cupri desktop host without creating Shade overlays, reading settings or contacting a broker. `scripts/smoke-accessibility-state.ps1` changes its field through editable, read-only, editable, disabled and editable states. It checks live UIA IsReadOnly/IsEnabled, requires restricted SetValue calls to throw, verifies that the prior value survives after editing is restored, then successfully writes another value and closes normally. Read-only remains enabled; disabled reports disabled. Both GPU and software runs pass.

The initial driver failures were unrelated to field behavior: PowerShell 5.1 needed a retained process handle to report the exit code, and the software fixture required its initially hidden window to be shown before UIA discovery. The final driver shows only a window matching its own child PID and exact fixture title, without activating it or sending global input. Logs and result records remain ignored artifacts.

Repeat after a Release build with `powershell.exe -NoProfile -File scripts/smoke-accessibility-state.ps1 -EvidenceDirectory <fresh-folder>`, adding `-Software` for the software host. This verifies live property queries and rejected operations, not subscription to property-change events, concurrent queued-action races or full screen-reader operation. Owner: Codex, next address remaining text-accessibility fidelity and assistive-technology acceptance.

## Property notifications and provider identity, 2026-09-10

Host 0.20.0-shade.7 sends textbox value, IsReadOnly and IsEnabled changes through [UiaRaiseAutomationPropertyChangedEvent](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcoreapi/nf-uiautomationcoreapi-uiaraiseautomationpropertychangedevent). It compares successive UI-thread snapshots and emits only changed properties. Value events are excluded if either snapshot marks the field as a password. Temporary BSTR event payloads are freed after the call; no timer or overlay update loop is added.

The subscription test initially received no value event. After implementing emission, the native API returned success but delivery still failed. Investigation found the vendored `ServerSideProvider` constant was incorrectly `0x1`, which Windows defines as a client-side proxy. The [documented server-side value is `0x2`](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationcore/ne-uiautomationcore-provideroptions). Correcting that constant makes subscribed delivery pass. Temporary tracing was removed.

The live state driver now registers a managed UIA event handler on its own fixture field, checks the replacement-value payload and both boolean values for enabled/editability transitions, and removes its handler. A second subscription watches the fixture's password field: the model must confirm a successful synthetic password edit, and no Value event may arrive during the bounded observation. GPU and software hosts pass. This is event-delivery and payload-exclusion evidence, not full Narrator/Text-pattern acceptance or proof about concurrent masked/unmasked transitions. All 25 shared Windows checks also pass. Owner: Codex, verify the main app and published variants after this host change; remaining physical, Linux desktop and real Home Assistant gates stay open.

The main Release development app also passes the GPU broker setup workflow after this change: global exclusions, accessible text/password replacement, protected reads, native keyboard navigation, authenticated test/save, encrypted persistence, discovery removal and normal close. Application stdout/stderr are empty. This is the development executable; published event equivalence remains to be verified.

## Packaged application notification check, 2026-09-10

`smoke-ui.ps1 -PropertyEvents` now subscribes to the actual broker hostname field and requires each replacement, clearing and restoration to deliver its exact new value. It then subscribes to the actual password field, replaces the initially incorrect synthetic password and rejects any Value event during the observation. With `-BrokerSetup`, subsequent authenticated test/save proves the password replacement reached the integration model. Protected Value reads, native keyboard navigation, discovery removal and normal shutdown remain in the workflow. The subscriber is shared with the isolated state fixture through `scripts/uia-events.ps1`.

Fresh single-file baseline and trimmed builds use engine 0.20.0-shade.5 and host 0.20.0-shade.7. Their sizes are 95,418,227 and 32,491,474 bytes respectively. Private build records preserve binary and source-manifest hashes. These are focused notification checks; the full packaging oracle, Linux desktop and real Home Assistant acceptance remain separate requirements.

All four main-app GPU/software notification runs pass, including the full broker setup workflow. Result hashes match the actual candidate executables. Stderr is empty throughout; stdout matches the baseline exactly for each renderer. The trimmed software Cupri keyboard-focus capture was inspected and retains the scrolled, visible focus outline and masked password. Normal locked restore and both source-provenance verifiers pass. Owner: Codex, next address remaining full text/screen-reader acceptance and remaining platform/product gates; no release configuration was changed.

## Navigation Invoke recheck, 2026-09-10

The older GPU navigation discrepancy was rechecked against those same baseline/trimmed executables. `smoke-ui.ps1 -InvokeNavigation` performs ten Home Assistant/Back to screens cycles through InvokePattern alone. Every transition checks both the header action and presence/absence of the broker input in the accessibility tree. The final screen view must retain the independent global value and zero levels for disabled screens. Ordinary form editing and normal close run afterward.

Both variants pass in GPU and software modes: 80 direct navigation actions total, without native keyboard fallback during those actions. Exact executable hashes match the existing build records; application stderr is empty and stdout matches per renderer. Private evidence is recorded in `artifacts/navigation-invoke-results.json`. This establishes a passing repeatability check for these builds and this environment, not proof that all Invoke actions or all race conditions are resolved. The provider-type correction is a plausible contributor; these runs do not independently isolate it from other intervening fixes. Owner: Codex, retain this regression option and continue remaining accessibility and product acceptance.

## Shading-control notifications, 2026-09-10

Host 0.20.0-shade.8 extends snapshot comparison to slider RangeValue.Value, switch/checkbox ToggleState and screen-button Name changes. Enabled-state notifications now cover matching-role controls generally. Numeric payloads use native double VARIANTs; toggle states use integer payloads. Unchanged snapshots emit no repeated change events. Password-value exclusion remains in place.

The main-app property-event check first reproduced a missing global-slider notification. It now observes the exact numeric value, a screen button's changed enable-state label, and both directions of a per-screen Use global switch. The driver uses native Tab/Shift+Tab to reveal the membership control and return to the restore button. This is a notification test with a visible control; it does not claim that invoking a clipped control automatically scrolls it into view. Early driver failures distinguished inaccessible positioning from the correct integer toggle payload, which had initially been compared against a boxed enum.

The GPU development run passes these events, existing restore/global-exclusion checks, broker value notifications, password event exclusion and normal close. All 25 shared checks pass. Owner: Codex, finish software capture and published comparisons for this host revision; broader screen-reader, physical and real integration acceptance remain open.

The final software run also passes the shading and broker property checks. Its Cupri Advanced-panel capture was inspected. Both development-host stderr logs are empty; the expected forced-software message is the only software stdout. Locked restore and source verifiers pass. Published shading-event equivalence remains open for this revision.


## Packaged shading notifications, 2026-09-10

Fresh baseline and trimmed single-file Windows executables containing engine 0.20.0-shade.5 and host 0.20.0-shade.8 pass the full property-event and broker-setup workflow in GPU and software modes. This includes global RangeValue payloads, screen-button Name changes, Use global ToggleState changes in both directions, global/disabled-screen invariants, restore-all, broker text events, protected password reads/event exclusion, native keyboard navigation, encrypted save, discovery removal and normal exit.

All four result hashes match their candidate executables. Stderr is empty and stdout matches the baseline for each renderer. Baseline size is 95,419,251 bytes; trimmed size is 32,491,474 bytes. Build/result records are private under artifacts/shading-events-published. Locked restore and source verifiers pass. No new visual design or release configuration was introduced, and the earlier acceptance folder remains an older explicitly identified build. Owner: Codex, continue remaining full text/screen-reader and platform acceptance; real Home Assistant, physical monitor identity/topology and game frame-time checks remain open.
