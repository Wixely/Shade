# Shade

A new multi-monitor screen dimmer using C#, .NET 10, and CupriFace.

Development is paused at the 2026-09-11 checkpoint. Start with the [resume checklist](docs/resume-2026-09-11.md) for current verification, unfinished work and the recommended next session.

- Status: Windows feasibility prototype implemented; validation and required features remain
- Owner: User (product decisions); Codex (implementation and validation)
- Created / last reviewed: 2026-09-10
- Next review: 2026-09-17, or before implementation
- Repository: Local Git repository; remote host, visibility, and license undecided

Shade will be independently implemented. ScreenDimmer-MultiScreen is a reference for documented behavior only; its code, assets, configuration files, and Git history must not be copied or translated into Shade.

Start with the [investigation plan](docs/investigation.md), [research findings](docs/research.md), and [initial decisions](docs/decisions/2026-09-10-project-foundation.md).

The [shared PLAN preferences applied to Shade](docs/preferences.md) form part of the plan, with canonical links, implementation checkpoints, and conditional requirements.

The UI engine and desktop host use pinned CupriFace 0.20.0 source with reviewed fixes: [engine focus rendering](third-party/CupriFace.Engine/README.md) and [host resize/accessibility behavior](third-party/CupriFace.Shell/README.md). Normal builds include those fixes, and restore verifies the vendored source hashes.

## Intended experience

A compact desktop interface showing the arrangement of connected monitors, independent variable shading controls, and an easy way to restore every display. Multi-screen shading and Home Assistant integration are required. All setup and operation must be available through the UI, including connection settings, discovery, status, and troubleshooting; manual configuration-file editing must not be required.

Windows and Linux support are required, with Windows the primary platform. Initial Windows checks have passed. The Linux X11 backend is implemented but has not passed desktop shading acceptance; the available WSLg compositor activates mapped overlays, so Shade disables shading there to preserve keyboard input. Native Wayland support remains unfinished. See the [Linux validation record](docs/linux-2026-09-10.md). Gaming performance, automatic display changes, and repeatable display identities are explicit requirements; see the [live-topology decision](docs/decisions/2026-09-10-live-topology-and-performance.md).

## Development status

The original .NET 10/CupriFace prototype provides per-display dimming from 0 to 80%, individual/global restoration, Ctrl+Alt+Shift+R recovery, automatic Windows topology reconciliation and hardware-identity-based settings recall. It uses static native overlays and requests no recurring UI redraw when state is unchanged. New displays start undimmed; missing/duplicate hardware serials are shown in the UI and excluded from automatic recall. Closing Shade removes all overlays; the next normal launch recalls remembered levels.

The [current controls](docs/controls-2026-09-10.md) put a global slider above monitor-shaped buttons matching your desktop arrangement. Click a screen to toggle its dimming; expand **Advanced** for individual sliders and a **Use global** switch per screen. Overflowing content scrolls. Global changes affect only enabled screens using global; disabled and independent screens stay unchanged. Global selection, membership, enabled state and individual levels now persist for trusted monitor identities.

The **Home Assistant** panel configures an MQTT 5 broker, TLS and credentials, tests the connection and enables/disables discovery. Windows encrypts saved passwords for the current account. Isolated MQTT transport checks pass; actual Home Assistant acceptance remains. See the [active buildout and verification record](docs/buildout-2026-09-10.md).

Linux now selects a native Wayland worker when the session advertises Wayland. It requires layer-shell, viewporter and the Global Shortcuts portal. Compatible sessions offer **Set up recovery shortcut** before shading can be enabled; authorization is initiated through that button. The worker is connected to settings and automatic reconnect, but active Wayland desktop acceptance remains unverified. The available WSLg compositor lacks layer-shell and reports that limitation without dimming. See the [Linux integration evidence](docs/linux-2026-09-10.md).

Screens with ambiguous hardware identities can now receive a named saved assignment through Advanced. Disconnected assignments can be moved to a replacement connection while retaining their settings and automation ID; reassignment starts undimmed. The UI explains that a connection assignment cannot identify which physical monitor occupies an indistinguishable connector. Settings recovery also has a UI action that preserves unreadable files before saving current settings.

Use Windows PowerShell 5.1 and .NET SDK 10.0.300 (or a compatible 10.0.3xx patch):

```powershell
.\scripts\restore.ps1
dotnet build Shade.slnx --no-restore
dotnet run --project src/Shade --no-build
```

Settings are stored under `%LOCALAPPDATA%\Shade\settings.json`, outside Git. `--ephemeral` starts without reading/writing settings for tests. Multiple copies in the same Windows session are rejected. A recovery-hotkey collision disables dimming and is displayed in the UI.

For isolated persistent testing, `--settings-directory <directory>` stores both screen and integration settings in that directory. It cannot be combined with `--ephemeral`; the session's single-instance restriction still applies. Ordinary setup remains available through the UI.

On Windows, closing the control window keeps Shade running in the notification area. Click its tray icon to reopen the controls; choose **Exit Shade** from its menu to quit and remove overlays. Ctrl+Alt+Shift+R remains the immediate restore-all shortcut. Use `--no-tray` for ordinary close-to-exit behavior. Linux currently closes normally because the pinned host's tray implementation is Windows-only.

```powershell
dotnet run --project tests/Shade.Tests --no-build
# These opt-in checks briefly apply low dimming levels to the local desktop:
dotnet run --project tests/Shade.Tests --no-build -- --native
.\scripts\smoke-ui.ps1
.\scripts\smoke-ui.ps1 -Software
```

VS Code: install/use Microsoft's C# extension, choose **Shade (Windows)** or **Shade (software renderer)**, then F5. The configurations restore pinned packages and build before launch. Their target paths and commands are verified; an actual F5 debugger session remains unverified.

See [prototype evidence and limitations](docs/prototype.md) and [dependency provenance](docs/dependencies.md). Development uses the normal .NET runtime. Initial self-contained Windows single-file checks pass after a native-library loading fix. An opt-in trimmed candidate passes live GPU/software controls and tray lifecycle checks. Precise dependency constructor annotations resolve its trim warnings; the full published functional comparison remains unfinished. NativeAOT evaluation requires a Windows C++ linker unavailable in the current toolchain. Linux acceptance, Home Assistant acceptance and remaining validation are tracked in the buildout record. No remote publication was performed.

## Next action

The local [Windows acceptance build and checklist](docs/windows-acceptance-2026-09-10.md) are ready for physical monitor and gaming tests, using isolated persistent settings. The baseline and three smaller candidates pass the current Windows GPU/software, tray, broker setup and untrusted-TLS checks; these do not establish gaming, physical topology or real Home Assistant acceptance.

Owner: Codex. Complete Linux desktop support and Home Assistant live/TLS acceptance, then packaging and accessibility checks. Codex/User: select representative hardware, games and Linux sessions for physical topology and frame-time validation.

The [packaging evaluation](docs/packaging-2026-09-10.md) records the first self-contained Windows baseline and its native-loader fix. A successful single-file UI/native smoke test is not full release acceptance; trimming, NativeAOT, runtime-free clean-machine validation and complete cross-platform functionality remain open. `--diagnostics` adds full exception details for local troubleshooting; its output can include private paths and should stay out of Git.
