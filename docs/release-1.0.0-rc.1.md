# Shade 1.0.0-rc.1

Prepared 2026-09-12. Local Windows x64 release candidate; not yet published.

## Features

- Monitor-shaped controls match your desktop arrangement and toggle individual screens.
- Global dimming slider and five quick presets; disabled and independent screens stay unchanged.
- Advanced controls provide individual levels, global exclusions, and optional 100% shading.
- Automatic display-layout reconciliation and saved settings for identified displays, with manual identity assignment for ambiguous hardware.
- Home Assistant MQTT setup, numbered screen controls, and a computer-specific device name.
- Tray controls, restore-all action, and Ctrl+Alt+Shift+R recovery on Windows.
- Application icons and an About dialog with the build version and project link.

## Running and upgrading

Exit any existing Shade instance using **Exit Shade** in its tray menu, then run the candidate's `Shade.exe`. The Windows build includes its .NET runtime. Native UI libraries are extracted by that runtime on first launch.

Settings stay in your local application-data Shade directory. Back up that directory before upgrading. This version uses settings format 4; older builds may decline to load the newer format. Credentials remain protected for the Windows account that saved them.

For testing without reading or saving your normal settings, launch `Shade.exe --ephemeral --no-tray`. Closing that test window exits and removes its overlays.

## Known limitations and release gates

- Windows physical hotplug/docking, sleep/resume, mixed-DPI and monitor-identity continuity need the complete hardware acceptance matrix.
- Gaming frame-time impact, HDR/VRR and exclusive-fullscreen behavior have not completed acceptance testing.
- Real Home Assistant reconnect/discovery lifecycle and the trusted-TLS matrix remain incomplete.
- Linux X11/Wayland implementations exist, but supported desktop-session acceptance is unfinished. No Linux release binary is offered in this candidate.
- Native screen-reader text-range support remains incomplete.
- This candidate is unsigned. Clean-machine testing and the complete bundled native/runtime license-notice review remain required before binary distribution.
- Trimming and NativeAOT remain optional experiments. This candidate uses the untrimmed, self-contained single-file baseline.

## Maintainer follow-up

Verification on 2026-09-12: Release build, 32 shared checks and the isolated MQTT integration check pass. The About dialog was visually checked through Cupri. The Windows x64 single-file candidate is 95,464,317 bytes; executable product/version metadata and its icon are embedded. Live GPU UI Automation passed set/restore across seven sliders and observed 15.6 ms process CPU during one five-second idle sample. This sample is not a gaming-performance benchmark. The software-renderer candidate also passed controls, Home Assistant form accessibility, close-to-tray, reopen, tray Exit Shade and normal host exit. Both live runs had empty stderr logs.

Owner: Codex/User. Validate the candidate on a clean Windows machine and representative displays/games, complete the remaining Home Assistant checks, and review bundled notices. Codex must review the complete outgoing Git history and binaries for privacy before any upload. Publishing or tagging is a separate step. Review this checklist before release; next scheduled review 2026-09-17.

Single-instance follow-up, 2026-09-12: startup now explicitly acquires a nonblocking session mutex and releases it on normal exit; abandoned ownership is recovered after a crash. Duplicate launches exit before settings, MQTT or overlays initialize. All 33 shared checks pass, including process-based duplicate/exit/crash tests. The rebuilt candidate at `artifacts/releases/1.0.0-rc.1-win-x64-single-instance/` supersedes the earlier local binary. A live two-launch check confirmed that the second packaged process exits with code 3 while the original keeps running and subsequently exits cleanly. This verification was on Windows; Linux process behavior was not rerun.
