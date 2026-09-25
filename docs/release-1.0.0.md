# Shade 1.0.0

Windows x64 stable release, 2026-09-25.

Shade dims individual monitors from a compact control window that mirrors your desktop layout, and exposes the same controls to Home Assistant over MQTT.

## Changes since 1.0.0-rc.2

- First stable release. The application code is unchanged from `1.0.0-rc.2`; this release promotes that candidate to a stable version after the candidate period passed without new defect reports.
- Version metadata moved from `1.0.0-rc.2` to `1.0.0`, so the release is published as a normal release rather than a prerelease.
- The release workflow now marks stable versions as the repository's latest release and continues to leave prereleases unmarked.
- README and release documentation updated for the stable version.

## Features

- Monitor-shaped controls that match your desktop arrangement; click a monitor to enable or disable its shading.
- Global slider with **0 / 20 / 40 / 60 / 80%** quick presets. Global changes preserve disabled screens and independent levels.
- **Advanced** controls for individual screen levels, **Use global**, and **Allow 100% shading**, which switches the presets to **0 / 25 / 50 / 75 / 100%**.
- Automatic display-layout reconciliation, with saved settings for trusted display identities and manual identity assignment for ambiguous hardware.
- Home Assistant MQTT 5 integration: a device named **PCNAME Shade**, per-screen controls using the numbers shown in Shade, TLS through the system certificate trust store, and credentials protected for the Windows account that saved them.
- Tray controls, **Restore all displays**, and **Ctrl+Alt+Shift+R** recovery.
- Single-instance protection with recovery after an abnormal exit, an application icon, and an About dialog.

## Install or upgrade

Download and extract `Shade-1.0.0-win-x64.zip`, then run `Shade.exe`. No separate .NET installation is required. Verify the download against `SHA256SUMS.txt`. Keep the included license notices with redistributed copies. The executable is unsigned, so Windows SmartScreen may warn on first launch.

Upgrading from a release candidate needs no settings migration. Exit the previous instance from its tray menu first, and back up the Shade folder in your local application-data directory (`%LOCALAPPDATA%\Shade`). Settings use format 4, the same format as `1.0.0-rc.1` and `1.0.0-rc.2`; older development builds may refuse newer settings. Windows MQTT credentials remain protected for the account that saved them and are not portable between accounts or machines.

Closing the window keeps Shade in the tray. **Exit Shade** quits and removes all overlays. Use `--ephemeral --no-tray` to test without loading or saving your normal settings.

## Verified for this release

The tagged build runs the 33 shared checks and the isolated MQTT integration check on Windows, verifies the vendored CupriFace sources, restores from lock files, and packages a self-contained single-file executable with bundled license notices and SHA-256 checksums. Earlier candidate verification, including live UI Automation runs on the GPU and software renderers, is recorded in [the 1.0.0-rc.1 notes](release-1.0.0-rc.1.md) and [validation records](validation-2026-09-10.md).

## Known limitations

These items are unchanged from `1.0.0-rc.2` and are not closed by this release:

- Physical display hotplug/docking, sleep/resume and mixed-DPI identity continuity have not completed the full hardware acceptance matrix. Identical monitors without unique serial numbers cannot always be distinguished after moving connections.
- Gaming frame-time impact, HDR/VRR and exclusive-fullscreen behavior have not completed acceptance testing. The automated tests do not establish gaming performance or physical-monitor compatibility.
- Real Home Assistant reconnect/discovery lifecycle and the trusted-TLS matrix remain incomplete.
- Native screen-reader text-range support remains incomplete.
- Windows x64 only. The Linux X11 and Wayland backends are implemented but desktop-session acceptance is unfinished, so no Linux binary is published.
- Trimming and NativeAOT remain experimental; this release uses the untrimmed, self-contained single-file baseline.
- The executable is unsigned, and clean-machine acceptance remains pending.

Owner: Codex/User for the remaining acceptance items, which are tracked for a later release. Next review 2026-10-02.
