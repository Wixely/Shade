# Shade 1.1.0

Windows x64 release, 2026-09-25.

Shade dims individual monitors from a compact control window that mirrors your desktop layout, and
exposes the same controls to Home Assistant over MQTT and to the command line.

## Changes since 1.0.0

- **The command line now sets most of what the interface sets**: the global level, per-screen levels,
  enabling and disabling screens, following or leaving the global level, 100% shading, restore-all,
  the saved Home Assistant integration, the Advanced section, and showing, hiding or quitting the
  window. Run `Shade --help` for the list, or see the [command-line reference](command-line.md).
- **A second launch updates the instance already running instead of doing nothing.** It hands its
  command line to that instance, which applies it immediately, and the existing window comes forward.
  Previously a duplicate launch printed a message and exited without bringing the window back.
- `Shade --status` prints the running instance's state for scripts, and `Shade --exit` quits it and
  removes its overlays.
- Exit codes now distinguish success, an unreachable instance, an invalid command line and a rejected
  request. They are listed in `--help` and in the reference.
- The release workflow marks a stable version as the repository's latest release; this was added for
  1.0.0 and is unchanged here.

## Using the command line

```powershell
Shade --global 40                            # set the global level
Shade --screen 2 --level 60 --screen 3 --off # per-screen, in the order given
Shade --screen all --on
Shade --restore                              # clear every screen
Shade --status                               # print the current state
Shade --exit                                 # quit the running instance
```

`--screen` takes the number shown beside each monitor, or `all`. A screen number that is not
connected refuses the whole command line rather than applying part of it. Startup options
(`--settings-directory`, `--ephemeral`, `--no-tray`) apply only when Shade is not already running and
are refused, not ignored, when it is.

**Broker credentials are never accepted on a command line.** Other programs running as your account
can read a process command line, so an MQTT username and password belong only in the Home Assistant
section. `--home-assistant on` re-enables the broker already saved, and refuses when none is saved.

Forwarded command lines travel over a named pipe restricted to your own account and desktop session,
carrying a fixed, length-capped vocabulary that is validated before anything is applied. The
reference describes the access control, the input limits and the trust boundary in full.

## Install or upgrade

Download and extract `Shade-1.1.0-win-x64.zip`, then run `Shade.exe`. No separate .NET installation
is required. Verify the download against `SHA256SUMS.txt`. Keep the included license notices with
redistributed copies. The executable is unsigned, so Windows SmartScreen may warn on first launch.

Upgrading from 1.0.0 needs no settings migration; the settings format is still 4. Exit the previous
instance from its tray menu, or run `Shade --exit`, before replacing the executable. Back up the
Shade folder in your local application-data directory (`%LOCALAPPDATA%\Shade`) first. Windows MQTT
credentials remain protected for the account that saved them.

## Verified for this release

The tagged build runs the shared checks and the isolated MQTT integration check on Windows, verifies
the vendored CupriFace sources, restores from lock files, and packages a self-contained single-file
executable with bundled license notices and SHA-256 checksums.

The 36 shared checks also pass on a self-contained `linux-x64` build executed under WSL Ubuntu 24.04,
which exercises the forwarded-command-line channel over Unix domain sockets. A trimmed Windows
publish completes without warnings, though trimming remains off for the release as before.

New automated coverage: command-line parsing and its refusals, every operation's forwarded form,
29 malformed protocol lines, a live model driven through a real pipe, untrusted channel input refused
without changing state, and a quit request whose reply reaches the caller before shutdown.

## Known limitations

Unchanged from 1.0.0 and not closed by this release:

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
- Trimming and NativeAOT remain experimental; this release uses the untrimmed, self-contained
  single-file baseline.
- The executable is unsigned, and clean-machine acceptance remains pending.

New in this release and not yet closed:

- Raising or hiding the control window on request is implemented only on Windows. On Linux a
  forwarded command line still applies its controls, and `--show` and `--hide` report that the
  window cannot be moved.
- The live path of a second launch raising a real window has not been exercised by an automated test;
  the channel and the applied controls are covered on both platforms, but window activation against
  a running interface has only been reasoned about from the Windows foreground rules.
- The control channel's boundary is the user account. Another program running as the same account can
  send control commands, or claim the channel name while Shade is not running. This crosses no
  boundary that account does not already have, and the channel carries no credential.

Owner: Codex/User for the remaining acceptance items. Next review 2026-10-02.
