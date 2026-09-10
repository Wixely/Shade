# Initial investigation findings

- Verified: 2026-09-10
- Review by: 2026-09-17, or before dependency selection
- Boundary: Documentation, public repository/release metadata, and read-only inspection of the existing local CupriFace checkout. No legacy implementation source was read or copied. No legacy binary was run and no Shade runtime tests were performed.

## Reference behavior

The [ScreenDimmer-MultiScreen README](https://github.com/Wixely/ScreenDimmer-MultiScreen/blob/master/README.md) documents a monitor arrangement view, MQTT-based Home Assistant discovery for the computer and its displays, .NET 8, and JSON configuration. The repository is a fork of datbnh/ScreenDimmer. These are documented claims, not independently tested behavior.

The README does not establish detailed dimming semantics, retained-message behavior, hotkeys, recovery behavior, monitor identity rules, or configuration compatibility. Do not assume full feature parity from its short description. Do not copy example credentials or configurations. Define any compatibility requirement through a separate behavior specification.

## CupriFace

The [CupriFace documentation](https://github.com/Wixely/CupriFace) describes C# model binding, HTML/CSS rendering without a browser or JavaScript engine, desktop hosting, sliders, switches, and accessibility support. Its CSS implementation is a subset; verify the intended controls and layout instead of assuming browser equivalence.

The GitHub [latest-release API](https://api.github.com/repos/Wixely/CupriFace/releases/latest) returned [v0.20.0](https://github.com/Wixely/CupriFace/releases/tag/v0.20.0), published 2026-09-10. Release assets include CupriFace.0.20.0.nupkg and CupriFace.Shell.0.20.0.nupkg. This is a candidate dependency pin, not an installed or tested dependency. Its release notes describe per-monitor DPI awareness and logical window dimensions; account for the distinction between UI logical pixels and overlay physical bounds.

Read-only local source inspection at commit `42f011d6ef73d19a86738cbdf0eef5f68289e1b1` found:

- `src/CupriFace/CupriApp.cs`: transparency, topmost, and close-to-tray options.
- `src/CupriFace.Shell/DesktopHost.cs`: GPU/software desktop paths and Windows tray attachment.
- `src/CupriFace.Shell/CupriFace.Shell.csproj`: .NET 10 and native rendering/windowing dependencies.

The local checkout contains unrelated pending edits and was left unchanged. These observations are specific to that checkout; they do not prove that the v0.20.0 package exposes identical APIs. The targeted search did not establish a ready-made multi-monitor, click-through overlay manager. Inspect the pinned package/source before choosing a host extension.

## Windows mechanisms to evaluate

[Microsoft's window documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features) describes layered windows and their hit-testing behavior. This supports investigating dedicated native overlay windows; it does not demonstrate correct Shade behavior in games, HDR, capture software, or secure desktops.

[QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig) supplies active display paths and modes. Investigate it with monitor enumeration and device information for identity and topology. Enumeration order alone should not be treated as persistent identity. Remote sessions, cloned displays, docking, and display replacement need explicit tests.

## Local tooling

`dotnet --list-sdks` returned SDK 10.0.300 alongside an older SDK. No package restore, compiler/toolchain check for NativeAOT, build, UI launch, monitor changes, or MQTT connection was performed. Dependency availability and publish compatibility remain unverified.
