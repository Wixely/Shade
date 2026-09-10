# Shade

A new multi-monitor screen dimmer using C#, .NET 10, and CupriFace.

- Status: Exploring; investigation and planning only
- Owner: User (product decisions); Codex (investigation)
- Created / last reviewed: 2026-09-10
- Next review: 2026-09-17, or before implementation
- Repository: Local Git repository; remote host, visibility, and license undecided

Shade will be independently implemented. ScreenDimmer-MultiScreen is a reference for documented behavior only; its code, assets, configuration files, and Git history must not be copied or translated into Shade.

Start with the [investigation plan](docs/investigation.md), [research findings](docs/research.md), and [initial decisions](docs/decisions/2026-09-10-project-foundation.md).

The [shared PLAN preferences applied to Shade](docs/preferences.md) form part of the plan, with canonical links, implementation checkpoints, and conditional requirements.

## Intended experience

A compact desktop interface showing the arrangement of connected monitors, independent variable shading controls, and an easy way to restore every display. Multi-screen shading and Home Assistant integration are required. All setup and operation must be available through the UI, including connection settings, discovery, status, and troubleshooting; manual configuration-file editing must not be required.

Windows and Linux support are required, with Windows the primary platform. Investigate Linux desktop/session compatibility explicitly; no platform behavior has been tested yet.

## Development status

There is no application code or runnable build yet. The local machine has .NET SDK 10.0.300. The first implementation milestone must add a working VS Code build/debug configuration, top-level C# entry point, and reproducible dependency restore. Use .NET and Windows PowerShell 5.1; Python and Node.js are not authorized.

## Next action

Owner: Codex. Build a small original feasibility prototype combining a CupriFace control window with independently managed Windows dimming overlays, after recording the dependency pin and test procedure in the investigation plan.
