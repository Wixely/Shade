# Windows physical acceptance

- Prepared: 2026-09-10; review: 2026-09-17
- Build preparation and investigation: Codex; physical actions and game selection: User
- Status: Ready to run; the physical checks below have not passed yet

The local acceptance folder is `artifacts/windows-acceptance-2026-09-10`. It contains the tested self-contained baseline `Shade.exe`, a hash manifest, this checklist and `Start-Shade.ps1`. This is a local test build, not a release. NativeAOT and clean-machine acceptance remain outstanding. The smaller packaging candidates remain under evaluation.

Run `powershell.exe -NoProfile -File .\Start-Shade.ps1` from that folder. The launcher verifies the executable hash and keeps persistent test settings in the folder's `settings` subdirectory. Use the same launcher after restarting Windows to test recall. Exit any existing Shade instance using its tray menu first; only one instance can run per session. Closing the controls leaves Shade in the tray. **Ctrl+Alt+Shift+R** restores all displays; **Exit Shade** removes overlays and stops the process.

## Controls and recovery

1. Start with all screens off. Move global to 15%. Every screen must remain off.
2. Enable two monitor buttons. Both should dim to 15%; other screens must remain off.
3. In Advanced, turn off **Use global** for one enabled screen and set its individual level to 8%. Move global to 25%. The independent screen must stay at 8%, the linked screen must reach 25%, and disabled screens must stay off.
4. Disable the linked screen, move global again, and re-enable it. It should adopt the current global level only when re-enabled.
5. Resize the controls and scroll Advanced. All fields and buttons must remain reachable; the global control should remain at the top. Tab and Shift+Tab should reveal focused controls.
6. Exit and relaunch after allowing two seconds for settings to save. Enabled state, global selection, exclusions and independent levels should return on recognized screens.
7. Press the recovery shortcut. Every screen should clear immediately, including after reconnecting a previously disconnected display. Recovery intentionally resets saved dimming; set distinct levels again before the topology checks.

## Monitor identity and topology

Record each physical display using private labels such as A/B/C, its UI identity status and its chosen level/global membership. Keep the record outside Git. Test one transition at a time, and record the result before the next.

| Transition | Expected result |
| --- | --- |
| Unplug/reconnect a recognized screen | Remaining overlays stay on their screens; returning screen recalls its own settings automatically |
| Change resolution, rotation or desktop position | Map and overlay bounds update automatically; no uncovered strip, displaced shading or settings transfer |
| Change Windows scaling; move controls between mixed-DPI screens | Controls remain usable; physical overlay coverage remains correct |
| Sleep/resume and Windows reboot | Recognized identities retain the correct settings after recovery/relaunch |
| Dock/undock or change ports, where available | Recognized hardware keeps its settings; ambiguous identity is explained rather than silently assigned |
| Add a previously unseen screen | New screen starts undimmed; existing screens keep their settings |
| Assign an ambiguous screen in Advanced | Named assignment persists on its connection; deliberate reassignment retains the assignment but starts undimmed |
| Mirror/unmirror, where available | A mirrored desktop is one shading surface; a new group starts undimmed unless that exact trusted group was saved |

A named connection assignment cannot distinguish physically identical monitors lacking unique serial information. Record this as a limitation, not a successful physical-identity match. Also check pointer clicks and keyboard focus through the overlay during each transition.

## Gaming performance

Use a repeatable built-in benchmark or recorded route in a game you actually use. Record game/version, GPU/driver, display resolution/refresh, frame cap, graphics settings, HDR/VRR and fullscreen mode. Choose the acceptable regression and hitch threshold with the user before judging results; no threshold is agreed yet.

Warm up the game first. Capture at least three matched runs per condition using an existing game benchmark or already available frame-time recorder. Keep each run the same length and rotate condition order to reduce temperature/cache bias. Do not run Cupri frame capture, diagnostics or UI automation during measurements.

| Condition | Shade state |
| --- | --- |
| A | Fully exited using Exit Shade |
| B | Running in tray, all screens off |
| C | Running in tray, gaming display dimmed at 15% |
| D | Running in tray, gaming display dimmed at 40% |
| E | Same as C, controls visible on another screen if available |

Repeat for the fullscreen/borderless and SDR/HDR/VRR combinations actually used. Record median, p95 and p99 frame time, count of frames over the agreed hitch threshold, CPU/GPU/compositor activity where measurable, and visible flicker, focus loss or brightness issues. Average FPS alone cannot establish absence of stuttering. Preserve raw exports privately for Codex to analyze; mark unavailable measurements as unavailable.

## Record and next action

Use `results.csv` in the acceptance folder for each check, with `not-run`, `pass`, `fail` or `unavailable`, reproduction details and an evidence filename. Never put passwords in it. Screenshots, machine details and game captures remain local ignored artifacts.

Owner User: run the controls and physical transitions, then select a game and measurement threshold. Owner Codex: investigate failures, analyze matched frame-time exports and repeat affected checks. Real Home Assistant, Windows trusted TLS, full accessibility and representative Linux desktop acceptance remain separate open gates.
