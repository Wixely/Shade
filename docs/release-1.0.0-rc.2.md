# Shade 1.0.0-rc.2

Windows x64 prerelease, 2026-09-12.

## Changes

- Automated tag-based Windows builds, tests and GitHub Releases.
- Self-contained executable with application icon, version information, bundled license notices and SHA-256 checksums.
- Monitor-shaped controls, global presets, independent screen levels and optional 100% shading.
- Home Assistant MQTT integration with numbered screens and a computer-specific device name.
- About dialog, tray controls and single-instance protection with crash recovery.

## Install or upgrade

Download and extract the Windows x64 ZIP, then run `Shade.exe`. No separate .NET installation is required. Keep the included license notices with redistributed copies. The executable is unsigned.

Exit the previous instance from its tray menu before upgrading. Back up the Shade folder in your local application-data directory first. Settings use format 4; older builds may refuse newer settings. Windows MQTT credentials remain protected for the account that saved them.

Closing the window keeps Shade in the tray. **Exit Shade** quits. **Ctrl+Alt+Shift+R** restores all screens. Use `--ephemeral --no-tray` to test without loading or saving normal settings.

## Known limitations

This is a release candidate. Physical display hotplug/docking, sleep/resume, mixed-DPI identity continuity, gaming frame-time impact, HDR/VRR and exclusive-fullscreen behavior still need broader acceptance testing. Real Home Assistant reconnect/discovery and trusted-TLS acceptance are incomplete. Native screen-reader text-range support remains incomplete.

Linux backends are in development; this release provides only Windows x64. Trimming and NativeAOT remain experimental; the published candidate uses the untrimmed, self-contained single-file baseline. Clean-machine acceptance remains pending. Automated tests do not establish gaming performance or physical-monitor compatibility.

Owner: Codex/User for remaining acceptance checks. Review before a stable release; next review 2026-09-17.
