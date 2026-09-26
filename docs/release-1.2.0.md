# Shade 1.2.0

Windows x64 release, 2026-09-26.

Shade dims individual monitors from a compact control window that mirrors your desktop layout, and
exposes the same controls to Home Assistant over MQTT and to the command line.

## Changes since 1.1.0

- **No console window.** Shade was built as a console application, so starting it from Explorer, a
  shortcut or the tray opened a console window that stayed behind the control window for as long as
  Shade ran. It is now a windows-subsystem application and no console is created. This affected every
  earlier release, including 1.0.0 and 1.1.0.
- **A second, much smaller download.** `Shade-1.2.0-win-x64-aot.zip` is compiled ahead of time with
  NativeAOT: one 26 MB executable instead of 91 MB, and it starts sooner. The original build is still
  published as `Shade-1.2.0-win-x64.zip` and remains the recommended download.
- The command line is unchanged from 1.1.0.

## Which download

| Download | Size | Notes |
| --- | ---: | --- |
| `Shade-1.2.0-win-x64.zip` | ~91 MB executable | The build every previous release shipped. Choose this one unless you have a reason not to. |
| `Shade-1.2.0-win-x64-aot.zip` | ~26 MB executable | Compiled ahead of time. Smaller and quicker to start. Newer, and tested on fewer machines. |

Both are a single self-contained `Shade.exe` that needs no .NET installation, both are unsigned, and
both read the same settings, so you can swap between them without losing anything. Verify either
against `SHA256SUMS.txt`.

The ahead-of-time build restores four bundled libraries to
`%LOCALAPPDATA%\Shade\native\<content-hash>\` on first start, because ahead-of-time compilation
cannot hold unmanaged libraries inside the executable. They are verified by SHA-256 on every start
and replaced if altered, and the directory is per user.

## Install or upgrade

Download and extract either ZIP, then run `Shade.exe`. Keep the included license notices with
redistributed copies. Windows SmartScreen may warn on first launch because neither build is signed.

Upgrading from 1.0.0 or 1.1.0 needs no settings migration; the settings format is still 4. Exit the
previous instance from its tray menu, or run `Shade --exit`, before replacing the executable. Back up
the Shade folder in your local application-data directory (`%LOCALAPPDATA%\Shade`) first. Windows MQTT
credentials remain protected for the account that saved them.

## One behaviour change worth knowing

Because Shade is no longer a console application, a shell does not wait for it. Typing
`Shade --status` at a prompt still prints — Shade attaches to the terminal it was started from — but
the prompt returns first, and `$LASTEXITCODE` is not filled in.

Scripts that need Shade's output or exit code should wait explicitly:

```powershell
$shade = Start-Process Shade.exe -ArgumentList '--status' -RedirectStandardOutput status.txt -Wait -PassThru
$shade.ExitCode
Get-Content status.txt
```

Output already redirected to a file or a pipe is untouched and behaves as before.

## Verified for this release

The tagged build runs the shared checks and the isolated MQTT integration check, verifies the vendored
CupriFace sources, restores from lock files, and packages both Windows archives with bundled license
notices and SHA-256 checksums. The ahead-of-time archive is rejected if it contains more than one
executable, which is what proves its libraries were embedded rather than left beside it.

The ahead-of-time build was additionally checked by hand on two Windows machines: the control window,
dimming, Home Assistant with saved credentials surviving a restart, and forwarded command lines. Its
accessibility and renderer were verified with Shade's live UI Automation smoke — seven sliders
discovered, set and restored, accessible text and password editing, the Home Assistant form, and
native Tab/Shift+Tab — reporting `software=False`, so hardware OpenGL rather than the software
renderer. Startup to an accessible slider measured 1,369 ms against 1,090 ms for the untrimmed
baseline, so it is smaller and quicker to start as a process without being quicker to reach.

Ahead-of-time compilation reports five warnings from Silk.NET's native-library resolver
(`IL3000`/`IL3002`) about APIs that return nothing in a single-file application. They are left visible
in the build log rather than suppressed: they are the standing reason to keep checking that this build
finds its libraries, which the UI Automation smoke above is what confirms.

## Known limitations

Unchanged from 1.1.0:

- Physical display hotplug/docking, sleep/resume and mixed-DPI identity continuity have not completed
  the full hardware acceptance matrix. Identical monitors without unique serial numbers cannot always
  be distinguished after moving connections.
- Gaming frame-time impact, HDR/VRR and exclusive-fullscreen behavior have not completed acceptance
  testing. The automated tests do not establish gaming performance or physical-monitor compatibility.
- Real Home Assistant reconnect/discovery lifecycle and the trusted-TLS matrix remain incomplete.
- Native screen-reader text-range support remains incomplete.
- Windows x64 only. The Linux backends are implemented and the shared checks pass there, but desktop
  acceptance is unfinished, so no Linux binary is published. Linux credential storage additionally
  needs libsecret and a desktop Secret Service.
- Raising or hiding the control window on request is implemented only on Windows.
- The executables are unsigned, and clean-machine acceptance remains pending.
- The control channel's boundary is the user account. Another program running as the same account can
  send control commands, or claim the channel name while Shade is not running. This crosses no
  boundary that account does not already have, and the channel carries no credential.

New in this release and not yet closed:

- The ahead-of-time build has been exercised on two machines, not on a clean-machine matrix, and not
  with a screen reader attached beyond the automated UI Automation checks. The untrimmed build remains
  the recommended download for that reason.
- A Linux ahead-of-time build has not been attempted. Ahead-of-time compilation cannot cross-compile
  between operating systems, so it would have to be built on Linux, and the embedded-library restore
  that makes the Windows build one file is Windows-only.

Owner: Codex/User for the remaining acceptance items. Next review 2026-10-03.
