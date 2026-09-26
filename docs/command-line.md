# Command line

- Reviewed: 2026-09-25; next review: 2026-10-02
- Owner: Codex
- Status: Implemented; Windows verified by automated tests, Linux control channel verified under WSL, physical desktop acceptance still open

Shade's command line sets the same controls the window does. Because only one Shade runs per desktop
session, a second launch does not open a second window: it hands its command line to the instance
already running, which applies it immediately and comes forward.

Run `Shade --help` for the same list this document expands on.

## Options

Control options apply to the running instance, or to this one when Shade is not yet running.

| Option | Effect |
| --- | --- |
| `--global <0-100>` | Global dimming percentage. |
| `--screen <number\|all>` | Select the screen that the following options act on. |
| `--level <0-100>` | Set the selected screen's own level, which also unlinks it from global. |
| `--on` / `--off` | Enable or disable dimming on the selected screen. |
| `--link` / `--unlink` | Make the selected screen follow, or stop following, the global level. |
| `--allow-full-shade <on\|off>` | Permit levels above 80%. |
| `--restore` | Remove dimming from every screen. |
| `--home-assistant <on\|off>` | Enable or disable the saved MQTT integration. |
| `--advanced <on\|off>` | Show or hide the Advanced section. |
| `--show` / `--hide` | Show or hide the control window. |
| `--status` | Print the running instance's state. |
| `--exit` | Quit the running instance and remove its overlays. |

Startup options apply only when Shade is not already running. Passing one while an instance exists
is refused rather than ignored, because a running instance cannot adopt it.

| Option | Effect |
| --- | --- |
| `--settings-directory <path>` | Use this settings directory instead of the default. |
| `--ephemeral` | Do not load or save settings. |
| `--no-tray` | Closing the window exits instead of staying in the tray. |

| Option | Effect |
| --- | --- |
| `--no-activate` | Do not raise the running window. |
| `--diagnostics` | Print detailed local failure information. |
| `--help`, `--version` | Print usage or the version and exit. |

## Screens

`--screen` takes the number shown beside each monitor in Shade, or `all`. Selection applies to the
options that follow it, so order matters and one command line can address several screens:

```powershell
Shade --global 40                          # every linked, enabled screen follows 40%
Shade --screen 2 --level 60 --screen 3 --off
Shade --screen all --on --allow-full-shade on
Shade --restore                            # clear everything
```

A screen number that is not connected refuses the whole command line without applying any part of
it, so a request either happens completely or not at all. Screens are renumbered as displays change,
so a script that must survive a topology change should read `--status` first.

## Reading state

`--status` prints one `name value` line per setting and one line per screen:

```
version 1.2.0
global 40
maximum 80
full-shade off
home-assistant off
advanced off
screen 1 on level 40 global DELL U2720Q
screen 2 off level 0 global Generic PnP Monitor
```

The screen line is `screen <number> <on|off> level <percent> <global|independent> <label>`. The label
is last because it may contain spaces. Labels come from display hardware and are flattened to one
printable line before they are printed.

## When Shade is not running

`--status` and `--exit` do not start Shade; they print `Shade is not running.` and succeed, because
that is already the state they describe or ask for. Any other control option starts Shade normally
and applies itself on the first interface tick.

`--show` and `--hide` are ignored on a cold start, since the window opens anyway.

## Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Success. |
| 1 | Unexpected failure. |
| 2 | Unsupported platform. |
| 3 | Another instance is running but did not answer on its control channel. |
| 4 | Invalid command line. |
| 5 | The running instance rejected the request. |

## How a forwarded command line reaches the running instance

The running instance owns a named pipe scoped to the current account and desktop session. Both ends
pass `PipeOptions.CurrentUserOnly`: on Windows that restricts the pipe's security descriptor to the
account that created it and makes a client refuse a pipe owned by anyone else; on Linux the backing
socket is created owner-only and its ownership is verified before use. Nothing binds a network
address, so the channel cannot be reached from another machine.

A request is ASCII, capped at 4 KB and 64 operations, read under a deadline, and parsed into a fixed
vocabulary before anything is applied. Any unparsable line rejects the entire request. The channel
applies nothing itself: it hands parsed operations to the interface thread that owns the controls and
waits for that thread's result, which is why a forwarded change behaves exactly as a click does.
Shade never impersonates a caller, and the client does not permit the server to impersonate it.

Windows only lets the process the user just launched pass the foreground right along, so the launched
duplicate calls `AllowSetForegroundWindow` for the running instance before forwarding; the running
instance then raises its own window. On Linux the controls still apply, but raising the window is
compositor-dependent and is not implemented, and `--show` and `--hide` report that.

One instance of the channel name exists at a time, so while Shade holds it no other program can
create it. If the name is already taken, Shade runs normally without accepting forwarded command
lines rather than trusting a channel it does not own, and says so on standard error.

**Broker credentials are never accepted on a command line or over this channel.** Other programs
running as the same account can read a process command line, so an MQTT username and password belong
only in the Home Assistant section, where Windows protects them for the account that saved them.
`--home-assistant on` re-enables the broker already saved; it refuses when none is saved.

### Trust boundary

The channel's boundary is the user account. Another account cannot reach it. A different program
running as the *same* account can send it control commands, and could claim the channel name while
Shade is not running. That is not a new exposure: a process running as the same account can already
read Shade's memory, send input to its window, or replace its executable. The commands it could send
change dimming levels; they carry no credential and cause nothing to be executed.

## Verification

Automated, 2026-09-25, on Windows and on Linux:

- Command-line parsing accepts the documented forms, refuses unknown options, missing values,
  out-of-range percentages, screen-scoped options without a screen, a settings directory combined
  with `--ephemeral`, and control characters in arguments. Usage errors stay on one line.
- Every operation survives the forwarded form unchanged, and 29 malformed protocol lines are
  refused, including padded, mis-cased, over-long and unknown forms.
- A live model behind a real pipe applies global, per-screen, link, full-shade, restore and Advanced
  changes, and reports them back through `--status`. An unknown screen number and a level above the
  maximum are refused without changing any state.
- Untrusted channel input is refused without changing state, and the channel keeps serving
  afterwards: over-long lines, unknown verbs, a request whose second line is invalid, an oversized
  request, too many operations, and non-ASCII bytes.
- A quit request's reply reaches the caller before the instance shuts down, and the shutdown runs
  once.

The 36 shared checks pass on both platforms. The Linux run used a self-contained `linux-x64` publish
executed under WSL Ubuntu 24.04, which confirms the control channel over Unix domain sockets. The
isolated MQTT check does not pass in that environment because Linux credential storage needs
libsecret and a desktop Secret Service, neither of which headless WSL provides; that gap is recorded
in the [current-source Linux comparison](packaging-linux-current-source-2026-09-10.md) and is
unrelated to the command line.

Not established: raising a window on a real Linux desktop, physical multi-monitor behavior through
forwarded command lines, and cross-account refusal, which needs a second account to observe rather
than a documented guarantee. Owner: Codex for the remaining automated gates; User/TBD for a
representative desktop.
