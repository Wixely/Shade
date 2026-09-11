# CupriFace Shell DPI candidate

- Status: Integrated in the source-vendored host; current version 0.20.0-shade.5 also includes the [Linux accessibility fix](CupriFace-Shell-accessibility.md)
- Owner: Codex
- Reviewed: 2026-09-10; next review: 2026-09-17
- Upstream: CupriFace 0.20.0, commit `56a9850decbb3e07b1cbd5f409f3d988e406b94b`
- License: [MIT, Copyright (c) 2026 Wixely](CupriFace-LICENSE.txt)

The [patch](CupriFace-Shell-dpi.patch) changes only the non-Windows device-scale measurement in the [pinned SkiaWindow source](https://github.com/Wixely/CupriFace/blob/56a9850decbb3e07b1cbd5f409f3d988e406b94b/src/CupriFace.Shell/SkiaWindow.cs). A resize callback supplies a new framebuffer width while GLFW can still report its previous window width. Comparing these produces a false scale transition and another resize. Read both dimensions from GLFW's current state instead. Keep DPI awareness and monitor tracking enabled. Windows keeps its native DPI-query branch.

## Evidence

The published Linux default host repeatedly resized after the test stopped sending resize requests and missed its 40-second shutdown deadline. Cupri's DPI trace showed an ordinary 720-to-728-pixel resize being interpreted as scale 1.011, then 0.989, followed by increasingly large erroneous transitions. The repeated traced run sampled 3,420 CPU ms over two seconds; the earlier run sampled 4,900 ms.

The patched host passed the same 45-resize and normal-close test. Its trace stayed at scale 1 throughout the X11 resize sequence, and the short idle sample was 40 CPU ms over two seconds. A second candidate produced by the checked-in evaluation script also passed, sampling about 60 ms. The same candidate's explicit software run passed, sampling about 10 ms, with only the expected software-selection announcement. These samples diagnose the feedback loop; they are not game frame-time benchmarks. Application stdout/stderr were empty on the passing default-host runs, with no software-fallback announcement.

The comparison changes only `CupriFace.Shell.dll` in a private copy of the loose self-contained application output. The engine package stays at 0.20.0. All downloaded source, compiled files, traces and screenshots remain in ignored artifacts. The upstream source archive is pinned to SHA-256 `20EF2397697E2FCAF61071B00D33895DF113D97DBCADCDD60F473EEE96BBED88`; this is a downloaded-content pin, not a signed attestation.

## Reproduce

```powershell
.\scripts\evaluate-cupri-dpi-fix.ps1 -EvidenceDirectory artifacts/cupri-dpi-comparison
# Optionally reuse the same hash-checked archive with -SourceArchive <source.zip>.
# On Linux, using the existing self-contained test driver:
./artifacts/linux-probe/Shade.Tests --linux-ui-published <candidate/Shade> <new-evidence-directory>
# Repeat with --software. RuntimeIdentifier win-x64 creates a Windows comparison.
```

The script verifies the source archive, checks and applies the patch, builds only the host against the pinned engine, publishes the normal control and creates a separate candidate. It does not edit application package references, replace the package cache, publish a package or change release flags. Its PowerShell 5.1 execution and Linux candidate build/live default-host test were verified.

Integration follow-up, 2026-09-10: normal Shade builds now reference the [vendored host project](CupriFace.Shell/README.md). A manifest verifies that only two of its 20 upstream C# files differ. Source compilation exposed IL3000 in the probe launcher; the second patch replaces `Assembly.Location` with the command-line entry path used when launching through the dotnet host. The Windows-native DPI measurement branch is unchanged. The original host package is retained only for before/after comparison; the evaluation script now explicitly restores that original DLL into its private control output.

The Windows candidate passed GPU controls/form accessibility/tray hide/restore/exit and software controls/form checks with inspected capture evidence. Normal integrated builds pass all 22 shared checks. Both untrimmed and trimmed Linux single-file builds pass the strengthened live regression: final geometry remains exactly 720 by 740 throughout the idle sample, default startup does not fall back to SDL, and normal close returns zero. Both publish without warnings. Actual monitor-scale transitions, native Wayland UI, broader accessibility and full release acceptance remain unverified. Owner: Codex to complete those checks while continuing the remaining product requirements.

The normal integrated Windows single-file build also passed GPU controls/form/tray lifecycle. After hardening the X11 driver's error handling and independent watchdog, the integrated Linux default and software builds passed again; the software sample was 20 CPU ms over two seconds. The original control terminates as a failed geometry check under the watchdog. This confirms the regression check distinguishes the original failure from the integrated fix. Development locked restore and the source-provenance check pass after cross-platform publishes.
