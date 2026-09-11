# Current-source Linux packaging comparison

- Reviewed: 2026-09-10; next review: 2026-09-17
- Owner: Codex
- Status: Evaluation; release defaults unchanged; desktop acceptance incomplete

The existing packaging evaluator now has an exercised Linux target:

```powershell
powershell.exe -NoProfile -File scripts/evaluate-packaging.ps1 -RuntimeIdentifier linux-x64 -EvidenceDirectory artifacts/packaging/current-source-linux-matrix
```

All five variants build without warning/error output using the same source engine 0.20.0-shade.2 and host 0.20.0-shade.5 as the current Windows comparison. The private manifest records exact publish arguments and SHA-256 for every output file. No symbols are included. These are self-contained .NET builds, not NativeAOT.

| Variant | Bytes | Files |
| --- | ---: | ---: |
| Baseline single-file | 107,571,854 | 1 |
| Loose untrimmed control | 116,590,494 | 217 |
| Trimmed | 46,881,860 | 1 |
| Compressed | 52,546,988 | 1 |
| Trimmed and compressed | 26,731,367 | 1 |

Largest loose components include CoreLib (15,583,016 bytes), two Skia library filenames (8,977,424 bytes each), Private.Xml (7,901,992), coreclr (7,102,600), and clrjit (4,755,824). This inventory does not establish that either Skia filename can safely be removed.

All four single-file variants pass the published Wayland worker fixture under the existing WSL Linux environment. The fixture starts the actual candidate executable in worker mode and uses isolated compositor and portal services. It verifies recovery setup, persistent named assignments, global changes without enabling screens, independent exclusions, geometry changes, disconnect/reconnect, restore while disconnected, loss of recovery authorization and restart. During each two-second shaded idle observation, there are zero state publications and compositor protocol requests; observed worker CPU is 0 or 10 ms. These are short fixture observations, not gaming measurements.

Passing worker records in private `verified-oracles.json` include the exact executable SHA-256. The parent test coordinator is a separate self-contained test publish; this worker fixture does not exercise the candidate's full application UI, real portal authorization or a real layer-shell compositor. WSLg still does not provide layer-shell.

All eight published default/software UI comparisons pass against these exact binaries, in the same WSLg/Xwayland session. Each exercises repeated resize and stable idle geometry, global changes without enabling screens, Advanced sliders, global opt-out, native broker field entry, authenticated connection testing, rejection/correction of a wrong password, password accessibility role, Tab/Shift+Tab focus reveal and normal close. Each also invokes Save and enable while libsecret is absent: the visible failure must not connect, and the seeded disabled broker settings must remain byte-for-byte unchanged after exit. That negative test does not establish successful credential storage.

For every variant, default-host application stdout/stderr are empty. Software stdout exactly matches the baseline's explicit forced-software selection message, with empty stderr. The fixture rejects unintended default-to-software fallback. Raw application streams and driver output remain separate private artifacts. Result records link the checks to executable hashes. Cupri software screenshots of the baseline and combined candidate were inspected: the Save button's focus outline is visible within the scrolled viewport, and the global control remains at the top. The synthetic saved-password hint reflects the deliberately seeded prior settings, not a successful keyring save.

The locked development restore passes after the cross-publishes. Successful Linux keyring storage, real Home Assistant, physical X11/Wayland overlays, cold/warm extraction comparisons and clean-machine testing remain open. Owner: Codex, continue the remaining packaging and accessibility gates; User/TBD, supply a representative desktop for physical and keyring acceptance. The full [packaging oracle](packaging-2026-09-10.md) still applies.
