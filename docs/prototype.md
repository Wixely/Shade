# First feasibility prototype

- Started: 2026-09-10; review: 2026-09-17
- Owner: Codex
- Scope: I2 and initial I3; not a release or platform-support claim

## Dependency and implementation decision

Pin CupriFace and CupriFace.Shell to the official v0.20.0 release packages, whose manifests identify source commit `56a9850decbb3e07b1cbd5f409f3d988e406b94b` and MIT licensing. Download into ignored `packages-local/`, verify SHA-256, and lock the complete NuGet graph. Do not reference the mutable sibling checkout. See [dependency provenance](dependencies.md).

Use original Win32 layered overlays on a dedicated message thread, behind a small backend interface. The UI reads levels from that backend so emergency restoration is reflected on refresh. Start undimmed, cap the prototype at 80%, and do not persist display IDs or levels. Require a registered Ctrl+Alt+Shift+R recovery hotkey before enabling dimming. Restore and disable dimming on display/power changes, pending I4's stable topology handling. Closing the application destroys its windows.

The 80% limit and recovery key are reversible prototype choices, not confirmed full-blackout product requirements. A normal .NET build is the functional baseline; NativeAOT, trimming and single-file publishing remain I6 work. No release pipeline is being selected.

## Test procedure defined before implementation

1. Restore exact dependency packages; build in Debug and Release. Run original .NET checks for level validation, independent state and real CupriFace slider/button input against a fake backend.
2. Run opt-in native smoke checks: enumerate physical monitor rectangles, create overlays, apply low independent levels briefly, check alpha/bounds/styles/no activation, restore and destroy. Keep identifying raw evidence in ignored artifacts.
3. Open the actual control window on GPU and software hosts. Check real keyboard input and UI Automation, independent displays, click/scroll/drag/type through overlays, hotkey restoration while another app has focus, and exit cleanup. Native assertions and headless rendering do not substitute for these interactive checks.
4. Test hotkey collision, mixed DPI, negative coordinates, topology and session changes, forced termination, HDR and full-screen behavior. Unsupported/unavailable environments stay explicitly unverified.
5. Verify VS Code launch with the C# debugger; configuration/build-path inspection alone is insufficient to claim a debugger session passed.

## Evidence

Implementation and validation in progress. Final results will be recorded here.
