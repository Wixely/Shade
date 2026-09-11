# Packaging evaluation and functional oracle

- Status: Baseline evaluation; no accepted release configuration
- Owner: Codex
- Reviewed: 2026-09-10; next review: 2026-09-17

No CI/release pipeline exists yet. The provisional Windows baseline uses Release, win-x64, self-contained deployment, no debug symbols, no trimming or compression. A single-file publish with native-library extraction is compared with the same publish using loose files. Commands and raw stdout/stderr are retained under ignored `artifacts/packaging`; output must not be treated as a release until the full oracle passes.

```powershell
dotnet publish src/Shade -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o artifacts/packaging/win-single
# Loose control: change PublishSingleFile to false and output to win-loose.
```

Initial single executable: 95,254,807 bytes. Loose output: 102,035,036 bytes. The largest loose components are CoreLib (16,013,096), Skia (9,605,176), Private.Xml (7,788,368), coreclr (4,609,320), and Linq.Expressions (3,639,080). The complete size inventory and verbatim build logs are private artifacts.

The initial single-file executable failed before showing its UI: CupriFace reported no suitable GLFW platform, attempted SDL fallback, and the application reported FileNotFoundException. This is an untrimmed packaging failure, not evidence against trimming. The loose control passed actual UI/native controls and normal exit in the same session. Full content extraction also passed GPU, software, native controls and actual tray exit, but is a compatibility mode that the current [.NET documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) discourages. It is not the selected remedy.

Opt-in `--diagnostics` now emits a full exception to stderr for private troubleshooting; ordinary error output stays concise. Raw process redirection identified the immediate failure in `Silk.NET.SDL.Sdl.CreateDefaultContext`, which could not find its native library. Shade now preloads only `glfw3.dll` and `SDL2.dll` from the .NET runtime's `NATIVE_DLL_SEARCH_DIRECTORIES` on Windows before selecting the host. Handles are released after host shutdown. A library that cannot load is left to the existing host selection/fallback path. The runtime supplies these search directories; Shade does not alter PATH or process-wide DLL search policy.

With that fix, ordinary native-extracting single-file deployment (no full managed extraction) passes GPU UI/native controls and clean exit without fallback/error output. Forced software UI/native controls and actual tray hide/restore/exit also pass. Two earlier tray-exit checks timed out; ordinary software close-to-exit passed. Giving the test thread explicit per-monitor DPI awareness while reading native menu geometry and positioning/restoring the cursor fixed the tray click. Those earlier failures remain in their evidence directories. This was a test coordinate issue; no application exit code was changed to force success. No trim, compression or AOT flags have been enabled.

The fixed executable remains 95,254,807 bytes. One cold-extraction run reached the first accessible slider in 977 ms; a second run using the same extraction directory took 930 ms. Both passed the actual GPU UI/native checks and normal exit. These are coarse startup samples including process launch, UIA discovery and its polling interval; cold extraction is not a cold filesystem/OS cache, and two samples are not a startup benchmark. Clean-machine validation without installed .NET remains pending.

## Required comparison oracle

Each candidate must be compared against a working untrimmed control in the same session. Existing tests are useful but testing a published test runner does not prove the actual application binary's equivalent behavior. `scripts/smoke-ui.ps1 -ExecutablePath <published Shade.exe> -EvidenceDirectory <ignored directory>` now exercises the actual application with ephemeral settings and ordinary close-to-exit.

| Axis | Required evidence / current limitation |
| --- | --- |
| GPU and software UI | Actual executable: seven sliders, independent global/exclusion behavior, monitor map, Advanced and scrolling, form editing/masking, CupriFace captures, normal exit |
| Windows native backend | Actual app plus native oracle: opacity/visibility/bounds, click-through/focus, global recovery, detached recovery, native window destruction on exit; physical routing/game acceptance remains |
| Tray and hidden operation | Actual executable tray hide/restore/exit; hidden live host MQTT/native test; combine physical overlays and actual tray exit before final acceptance |
| Identity and settings | Migration, restart, geometry changes, assignments/reassignment, unreadable backup recovery, write-failure recovery; published app recall plus real docking/reboot acceptance |
| Home Assistant | Discovery/state/commands, exclusions, TLS, ACLs, broker restart, saved credentials, forgetting, discovery removal; local transport tests plus real Home Assistant UI acceptance |
| Linux | Executable relaunches X11 worker under single-file deployment; native display/input/opacity/recovery, Wayland path, keyring, tray and complete UI; current Linux support is incomplete |
| Startup/runtime | Cold and warm extraction timing, loaded runtime/native paths, clean machine with no installed .NET, full output and log comparison |
| Accessibility/debug | Live keyboard/UIA, password semantics, tray menu, actual VS Code F5; current gaps remain |
| Distribution | One executable per supported platform when compatible, notice/license collection, no PDBs/secrets/logs/screenshots, exact size inventory |

## Fallback and caught-error inventory

Source scan: all `catch` and `fallback` occurrences in `src/Shade/*.cs` on the review date. Regenerate this inventory after code changes and inspect dependencies as well as application catches. No fallback should silently count as a successful supported backend.

| Source / caught paths | Oracle |
| --- | --- |
| Program startup/platform failure | Explicit nonzero exit and removal of owned overlays; distinguish UI backend failure from normal exit |
| BundledUiLibraries load failure/cleanup | Preserve GPU/software selection, release owned library handles after host exit, compare bundle and loose control |
| CupriFace host GPU-to-SDL fallback | Preserve exact logs and verify requested GPU path separately from forced software; a working fallback does not prove GPU compatibility |
| WindowsDisplayCatalog unreadable EDID | Untrusted identity, no accidental automatic recall, explicit assignment UI |
| WindowsDimmingBackend startup/native/topology/command/disposal errors | Fail safely, hide/retry inconsistent topology, keep recovery available, no stale overlays |
| SettingsStore and AutomationSettings load/save/recovery | Preserve unreadable/future files, rollback failed replacement, truthful credential/enable state, successful retry |
| ShadeApp assignment, binding, automation-command and recovery errors | Visible actionable status; no unintended changes to other screens; repeat through actual UI |
| HomeAssistantIntegration credentials/test/connect/subscribe/publish/disconnect/removal/cancellation | TLS and ACL rejection, retry, local-control independence, offline/last-will, bounded shutdown; real broker acceptance still needed |
| LinuxDimmingBackend child startup/read/timeout/exit/disposal | Worker crash recovery, bounded command failure, child cleanup, single-file relaunch |
| X11OverlayWorker startup/pipe/native/validation/topology errors | Hide safely, no input interception, compositor-focus guard, retry genuine transient failures |
| LinuxCredentialProtection missing API/keyring/abandoned key mutex | Clear error without plaintext fallback, preserve existing key, unlock/cancellation, native keyring round trip pending |

## Opt-in trimming and NativeAOT investigation

Reviewed 2026-09-10. Application-only `Trim=true` and `Aot=true` switches now permit experiments without changing default publishing. Reflection-based application JSON serialization was replaced with source-generated metadata for settings and X11 messages, and explicit JSON nodes for Home Assistant payloads. The application now explicitly registers the packaged Silk GLFW window/input platforms and disables automatic platform discovery. The software host still uses its direct SDL implementation.

The first trim attempt identified ten application IL2026 errors. After the JSON changes, two Silk.NET IL2072 warnings remain in `InputWindowExtensions.TryAdd(String)` and `Window.TryAdd(String)`: the reflected platform attribute types lack constructor-preservation annotations. Explicit registration avoids that runtime discovery path but does not remove it from linker analysis. These warnings are unresolved; they have not been suppressed or accepted as release-safe.

The diagnostic candidate uses the baseline single-file arguments plus `-p:Trim=true -p:TrimmerSingleWarn=false -p:ILLinkTreatWarningsAsErrors=false`. The latter permits investigation while preserving warning text in the private build log; normal warning policy remains unchanged. Compare against the same-code untrimmed control, not the earlier pre-JSON baseline.

| Check | Untrimmed control | Trimmed candidate |
| --- | --- | --- |
| Executable bytes | 95,300,720 | 32,463,439 (65.9% smaller) |
| Actual GPU controls, global exclusion/restoration, form editing/masking, clean exit | Pass | Pass |
| Actual forced-software controls, form and clean exit | Pass | Pass |
| Actual GPU tray hide, restore and native Exit Shade action | Pass | Pass |
| GPU/tray process stdout and stderr | Empty | Empty |
| Software process output | Explicit software selection; no stderr | Same output; no stderr |
| First accessible GPU slider, one sample | 1,726 ms | 1,278 ms |

These runs were performed back-to-back in the same Windows session with ephemeral settings and native overlays. Evidence directories are `artifacts/packaging/json-control-*-verified` and `trim-*-verified`; screenshots are private CupriFace software frame dumps. The monitor arrangement, Advanced controls and scrolled integration actions were visually inspected. The startup samples have uncontrolled extraction/cache state and are not a cold/warm benchmark. Five-second CPU samples varied between runs and do not establish gaming performance. Published settings recall, MQTT operation, topology transitions and the rest of the oracle remain unverified for trimming.

The first `Aot=true` Windows publish stopped with `Platform linker not found` in the SDK native targets. No C++ workload installation was returned by the installed Visual Studio discovery utility, and `link.exe`, `clang.exe`, and `lld-link.exe` were not found on PATH. This establishes a local prerequisite limitation, not dependency incompatibility with NativeAOT. No toolchain was installed. Verbatim publish output is retained under `artifacts/packaging/win-aot-initial.*.log`.

### Constructor annotation remedy

Follow-up on 2026-09-10: `src/Shade/Silk.LinkAttributes.xml` adds precise linker-only `DynamicallyAccessedMembers` annotations to the constructor parameter, backing field and getter return of the two platform attributes. The required members are public parameterless and nonpublic constructors, matching `Activator.CreateInstance(type, nonPublic: true)`. The attributes carry this requirement back to the statically known platform types. There are no warning suppressions, method substitutions or whole-assembly roots.

Source basis: the pinned Silk.NET [input attribute](https://raw.githubusercontent.com/dotnet/Silk.NET/v2.22.0/src/Input/Silk.NET.Input.Common/InputPlatformAttribute.cs) and [window attribute](https://raw.githubusercontent.com/dotnet/Silk.NET/v2.22.0/src/Windowing/Silk.NET.Windowing.Common/WindowPlatformAttribute.cs) store their constructor argument directly in an automatic property. The .NET 10 [ILLink annotation format](https://github.com/dotnet/runtime/blob/v10.0.8/docs/tools/illink/data-formats.md#custom-attributes-annotations-format) supports external dependency annotations via `--link-attributes`. The app passes that argument only for `Trim=true`, using the SDK's internal `_ExtraTrimmerArgs` hook, and includes the XML in `MSBuildAllProjects` to invalidate incremental linking after edits. This SDK hook and the exact dependency member names must be revalidated on upgrades; this does not establish NativeAOT annotation compatibility.

The annotated Windows publish succeeds with normal warnings-as-errors policy, without `ILLinkTreatWarningsAsErrors=false`. Verbatim stdout/stderr are `artifacts/packaging/win-trim-annotated.*.log`. The executable is 32,463,439 bytes and has the same SHA-256 as the preceding diagnostic trimmed executable. Thus this remedy lets the linker prove constructor requirements already satisfied in that candidate, with no binary behavior change. The earlier warnings remain recorded above as investigation history, not current publish failures.

After the annotation change, back-to-back untrimmed/annotated runs passed actual GPU controls, integration form editing/masking, tray hide/restore/native exit, forced-software controls and normal exit. GPU stdout/stderr remained empty; software selection output matched exactly with no stderr. Evidence is under `artifacts/packaging/annotations-*`. These are the same limited UI/lifecycle checks described above, not evidence for the still-pending published persistence or MQTT axes.

### Published settings and MQTT oracle

Added 2026-09-10: `dotnet run --project tests/Shade.Tests -c Release -- --published <Shade.exe>` drives the supplied executable as a child process against an authenticated loopback broker. It uses a unique temporary `--settings-directory`, retaining the application's normal session mutex and recovery key. This opt-in Windows check requires at least two trusted connected screens and briefly applies only 2–3% shading. It does not read or write ordinary user settings or stop an existing user instance.

The test seeds isolated encrypted broker settings and an undimmed screen profile, then verifies actual application credential loading, discovery IDs and control schema, global command isolation, per-screen enable, native window alpha, independent controls, retained-command rejection, debounced persistence and clean exit. A second launch must republish the same IDs, layout attributes and intent, restore native opacity, reject the retained command again and respond to restoration. It waits for publication of every trusted connected screen. This exercises the real CupriFace host command pump without reading a model revision in the test process. Seeded configuration is not UI setup acceptance or real Home Assistant acceptance.

The first harness close timed out because `Process.MainWindowHandle` selected a visible overlay. The test now finds the owned control window by title and sends its close message, with a bounded exit wait and cleanup limited to its own child. Earlier evidence is retained. Logs and a result manifest containing binary SHA-256, renderer selection and connected-screen count stay in ignored `artifacts/packaging/published-*` directories. Only known synthetic settings files are removed afterward.

Same-session untrimmed and trimmed executables passed this oracle with both GPU and forced-software hosts. Current sizes after adding the settings-directory option are 95,320,343 and 32,483,062 bytes respectively. Trim publishing still succeeds with warnings treated as errors. The combined Windows regression suite also passes all 23 checks. Published TLS/ACL failure paths, UI configuration/recovery actions, physical topology changes and other outstanding comparison axes are not proven by this new check.

### Published permission failure and recovery

Follow-up on 2026-09-10: the published oracle now disconnects only its owned Shade MQTT client, rejects two credential retries, then rejects subscriptions and discovery publications before restoring permissions. It checks the actual native overlay alpha after each failure, offline availability during subscription denial, fresh discovery/state publications after permission recovery, and renewed command handling without re-enabling disabled screens or changing the independent overlay. The broker keeps the test observer authorized. Result manifests retain counts of each rejection; all settings, broker traffic and logs remain isolated.

This investigation found an application retry defect: subscribing successfully reset backoff before discovery publication succeeded, so persistent PUBACK rejection retried every second. The reset now follows a complete successful publication cycle. A dedicated permission regression check observes three rejected publications and verifies increased retry spacing. All 23 Windows checks pass, as do Linux shared, permission, TLS-rejection and missing-keyring checks in the self-contained test publish. Linux desktop acceptance is not inferred from these results.

The updated untrimmed and trimmed Windows executables both pass the expanded oracle with GPU and forced-software hosts. Each run covers six connected screens, two rejected connection attempts, rejected subscriptions and a rejected discovery publication before automatic recovery. Sizes remain 95,320,343 and 32,483,062 bytes. Trim publishing has no warnings. The earlier candidate binaries remain separate from these `win-retry-control` and `win-retry-trim` artifacts, and result manifests identify the exact tested binaries by hash.

### Published untrusted TLS rejection

Added 2026-09-10: `dotnet run --project tests/Shade.Tests -c Release -- --published-tls <Shade.exe>` reuses the original loopback TLS probe. A test-only certificate-pinned client first proves that the generated certificate, TLS endpoint and MQTT CONNACK work. The actual application then loads isolated encrypted credentials and a saved 2% independent screen setting, using its ordinary certificate validation. No trust-store changes or application validation overrides are made.

The test waits for two completed application connection attempts, verifies saved native opacity, closes the owned control window and checks clean exit. The probe counts any application-data stream as well as complete MQTT CONNECT packets, so an incomplete packet cannot masquerade as rejection. For each run, three connections completed and only the pinned control sent application data or MQTT CONNECT. Neither initial application connection nor retry sent application data. The application continued shading at its saved level during the failures.

The same untrimmed and trimmed binaries from the retry comparison passed this check with GPU and forced-software hosts. Private `artifacts/packaging/published-tls-*` manifests identify their hashes, renderer and probe counts; stdout/stderr are retained alongside them. The existing TLS plus shared regression checks also pass after extracting the reusable probe path. This verifies untrusted-chain rejection, not successful system-trusted TLS, isolated hostname/expiry rejection, or configuring/disabling TLS through the published UI.

### Published UI recovery

Added 2026-09-10: `scripts/smoke-ui.ps1 -ExecutablePath <Shade.exe> -Recovery -EvidenceDirectory <ignored directory>` creates a unique isolated settings directory with deliberately unreadable screen and integration files. It exercises the normal controls first, checks that their activity has not overwritten the unreadable files, then scrolls to and activates each recovery button through live UI Automation focus and keyboard input.

Each action must create one backup with the original SHA-256 and valid replacement JSON. Screen recovery must retain the current global value and keep restored screens disabled. Integration recovery must stay disabled with no saved credential, even when synthetic password text has been entered in the form. Replacement-file checks allow the legitimate brief interval between backup rename and atomic replacement. The script rejects preview/tray combinations for this mode and retains only synthetic evidence beneath the ignored output directory.

Untrimmed and trimmed Windows executables pass recovery, the surrounding control/form checks and normal exit with GPU and software hosts. CupriFace debug captures `recovery-settings.png` and `recovery-automation.png` from the trimmed software run were inspected: both actions and their unreadable-file messages are visible at the appropriate scroll position. Evidence is under `artifacts/packaging/recovery-*`. No normal user settings were changed. These checks prove successful UI backup/recovery, not all disk-permission/rollback failures or full UI broker configuration acceptance.

### Published broker configuration through the UI

Added 2026-09-10: `scripts/smoke-ui.ps1 -ExecutablePath <Shade.exe> -BrokerSetup -EvidenceDirectory <ignored directory>` starts the test project's `--ui-broker` fixture and a published application with a fresh isolated settings directory. Build the Release test project first. The fixture binds only to loopback, accepts synthetic credentials and stops on the owning script's stop signal or a five-minute lifetime limit. Its status snapshots contain counters, not credentials or monitor identifiers. It retries brief Windows snapshot replacement conflicts with the PowerShell reader, while persistent failures still terminate the fixture.

The script enters hostname, port, username and password through the live UI, turns TLS off for the local plaintext fixture, tests authentication without discovery, saves/enables, verifies encrypted persistence, removes every discovery entity and forgets the saved password. Expected entity count comes from the current trusted monitor catalog: one global entity plus three per screen. Six screens produced all 19 entities, and removal cleared all 19. Port editing sends the scan codes required by the pinned GPU host, and TLS uses its exposed UIA TogglePattern.

This test found a product defect: testing a connection cleared the unsaved password, so subsequent Save and enable had no password to save. The model now preserves the masked field after testing and still clears it when saving. The UI oracle explicitly checks that the test preserves the masked entry. Earlier fixture failures (missing keyboard scan codes and a status-file sharing race) remain recorded in private artifacts and are distinct from this application fix.

The corrected `win-ui-setup-control` and `win-ui-setup-trim` executables pass the complete setup flow with GPU and software hosts; sizes remain 95,320,343 and 32,483,062 bytes. Trim publishing has no warnings. Shared/MQTT regression checks pass. Evidence is under `artifacts/packaging/ui-setup-*`; the inspected CupriFace `broker-connected.png` shows the entered synthetic connection details and saved-password hint with an empty password field. No real broker, Home Assistant installation or user configuration was touched. Actual Home Assistant acceptance and TLS setup acceptance remain separate gates.

### Compression comparison

Evaluated 2026-09-10 with SDK 10.0.300. Both candidates add only `-p:EnableCompressionInSingleFile=true` to the corresponding working single-file command; native-library extraction remains enabled. The trimmed candidate also uses the existing `-p:Trim=true -p:TrimmerSingleWarn=false` settings. Both publish with no warnings, and no default project or release configuration was changed.

| Same-code Windows variant | Executable bytes | Cold-extraction first slider (ms) | Warm-extraction first slider (ms) |
| --- | ---: | ---: | ---: |
| Untrimmed, uncompressed | 95,320,343 | 1,034 | 949 |
| Untrimmed, compressed | 45,551,693 | 1,068 | 1,007 |
| Trimmed, uncompressed | 32,483,062 | 1,194 | 1,216 |
| Trimmed, compressed | 19,306,709 | 1,293 | 1,267 |

Each variant uses a new, verified-absent `DOTNET_BUNDLE_EXTRACT_BASE_DIR` for its first run, then reuses that directory for its second run. Each run executes the live GPU control/form/normal-exit checks, not just startup. All eight runs passed, and all 16 process stdout/stderr files were empty. The samples include process launch, UI Automation discovery and polling; they are one cold/warm pair each, not repeated benchmarks or cold OS-cache measurements. The small timing differences do not prove a startup-performance ranking or gaming impact.

Compression alone reduces this baseline by 52.2%. Trimming plus compression reduces it by 79.7%, or 40.6% compared with trimming alone. Private `artifacts/packaging/compression-comparison.json` records binary hashes, sizes and startup samples. Per-run logs and extraction-directory provenance are in `comparison-<variant>-cold` and `-warm`; exact candidate publish logs are `win-compressed.*.log` and `win-trim-compressed.*.log`.

All four variants also passed the live forced-software controls and broker setup/removal/forgetting workflow in the same session. Software selection logs match exactly across the variants and there is no new software stderr. Evidence is under `comparison-<variant>-software`, including separate driver logs and the isolated broker's final counts. No compressed-only UI regression was detected in these checks.

All four variants then passed `--published` and `--published-tls` back-to-back in the same session. This covers native opacity, saved settings and identity/layout continuity over restart, global exclusions, retained-command rejection, rejected credentials/subscriptions/publications and automatic recovery, plus untrusted TLS rejection without application-data transmission. Transport driver stdout/stderr are in `comparison-<variant>-transport`; each test's detailed private result manifest identifies the tested executable hash. No candidate failed these checks.

| Functional axis in this comparison | All four Windows variants |
| --- | --- |
| GPU controls/form/normal exit, cold and warm extraction | Pass |
| Software controls, UI broker test/save/discovery/remove/forget | Pass |
| Published native opacity, persistence, MQTT permission recovery | Pass |
| Published untrusted TLS rejection and local shading isolation | Pass |
| GPU tray lifecycle and unreadable-settings UI recovery | Pass for all four variants |
| Real Home Assistant, trusted TLS, physical topology and game frame times | Still unverified |
| Clean machine without installed .NET; complete Linux functionality | Still unverified/incomplete |

The combined candidate is promising for size, but the full release oracle is incomplete and no configuration is accepted for release. No regressions have been accepted to obtain the reduction. Default publishing remains unchanged; compression was supplied only on experimental publish commands.

Follow-up on 2026-09-10: all four variants passed fresh GPU tray hide/restore/native Exit Shade checks and unreadable screen/integration settings recovery through the UI. The recovery tests also exercised controls, exact backup hashes, current global value, disabled-screen preservation and disabled automation after reset. Separate driver and application logs are retained under `artifacts/packaging/final-<variant>-Tray` and `-Recovery`. This closes those initial compressed-variant gaps on the GPU path; it does not establish Explorer restart, shell failure, clean-machine or Linux behavior.

### Linux single-file worker comparison after Wayland integration, 2026-09-10

Fresh self-contained Linux x64 controls and candidates use the same command shape as the Windows baseline, changing only RID/output directory. The candidate adds the existing application-scoped `-p:Trim=true` and `-p:TrimmerSingleWarn=false`; compression remains off. Both publish logs contain no warnings, and their separate stderr files are empty. Default publishing and release configuration remain unchanged.

| Linux x64 build | Executable bytes | Published Wayland worker | Published X11 worker |
| --- | ---: | --- | --- |
| Untrimmed single-file | 107,569,085 | Pass | Pass |
| Trimmed single-file | 46,899,224 | Pass | Pass |

The trimmed executable is 56.4% smaller. A fresh loose untrimmed publish totals 116,587,757 bytes. Its largest files include System.Private.CoreLib.dll (15,583,016), two Skia native filenames at 8,977,424 bytes each, System.Private.Xml.dll (7,901,992), libcoreclr.so (7,102,600), libclrjit.so (4,755,824), System.Linq.Expressions.dll (3,725,096) and Silk.NET.OpenGL.dll (2,820,816). No native file was removed based solely on matching size; native loader/SONAME requirements still need review before any deduplication proposal.

The driver can now launch the actual published executable as its supervised child rather than its own test assembly. `--wayland-published <executable>` ran each binary back-to-back against the isolated compositor/portal fixture. Both passed setup, assignment persistence, independent global controls, rotation, new-number replug, compositor reconnect without repeat authorization, recovery (including detached intent), recovery loss and worker restart. Both measured 0 CPU ms during the two-second shaded idle observation, with zero protocol requests/publications. These are protocol fixtures, not visible rendering or physical input tests.

`--linux-worker --linux-published <executable>` ran the existing X11 native-state/idle/crash-reconnect/cleanup checks against each actual published binary in the same session. Both passed. The two-second idle observations were 0 CPU ms for control and 10 CPU ms for trimmed, with zero publications in both; this single small sample does not establish a performance ranking. Other shared checks printed by that driver exercise the untrimmed test assembly, so they are not attributed to the published candidate.

Private evidence: `artifacts/packaging/linux-wayland-{control,trim,loose}.{stdout,stderr}.log`, `linux-wayland-{control,trim}.test.{stdout,stderr}.log`, `linux-{control,trim}-x11.{stdout,stderr}.log` and `linux-wayland-comparison.json` (binary sizes/hashes). Test-driver stderr files are empty. The supervisor drains child stderr internally; these driver logs do not constitute a verbatim child-stderr comparison. Native request/state assertions provide the worker-path evidence described above.

This comparison does not validate the published Linux control window, accessibility, native keyring, Home Assistant integration, visible shading, actual shortcut delivery, compression combinations or cold/warm UI startup. It does not accept trimming for release or supersede the remaining oracle gates.

Next: Codex to complete remaining release evidence and implementation while physical/performance and compatible Linux desktop access are unresolved. Trusted TLS and isolated hostname/expiry acceptance remain open. NativeAOT needs an available Windows native toolchain before compatibility can be determined. Release-pipeline changes require the user's decision after the full evidence report, per the canonical trimming preference.

### Published Linux UI library loading, 2026-09-10

A live run of the earlier single-file Linux control revealed a packaging defect: the executable contained the native UI assets but Silk could not find GLFW or SDL in the .NET extraction directory. The application failed before showing controls. `BundledUiLibraries` previously handled only Windows. It now registers a scoped Linux resolver using only runtime-provided native search directories and exact allowed library names. The resolver is removed on disposal. Windows retains its existing preload path; no new dependency or system package was installed.

SDL's shipped filename is `libSDL2-2.0.so`, while its ELF SONAME is `libSDL2-2.0.so.0`; merely preloading the file did not satisfy Silk's later filename lookup. Silk 2.22's GLFW name is `libglfw.so.3.3`, whereas the pinned Ultz GLFW 3.4 asset is `libglfw.so.3`. The resolver maps that exact request to the bundled asset rather than requiring filesystem aliases or machine loader configuration. The packaged libraries' direct ELF dependencies resolved in the test environment.

An original Linux-only driver (`--linux-ui-published <executable> <new-evidence-directory> --software`) launches the actual published app with ephemeral settings and an X11 UI session. It finds only the owned process's named control window, requests 45 resizes, checks Cupri's software frame readback, samples idle CPU for two seconds, then sends WM_DELETE_WINDOW and requires normal zero exit. It preserves separate application stdout/stderr, and cleans up the owned process on failure. It does not enable shading or synthesize global input.

The untrimmed and trimmed software builds passed on WSLg/Xwayland. Cupri's capture of the untrimmed UI was inspected: all six monitor shapes, the independent global slider, Advanced button and overflow scrollbar render correctly. Observed software CPU samples were 0–20 ms over two seconds; these short UI samples do not prove game performance. Software stderr is empty; stdout records the explicitly selected software host. Windows shared checks passed all 22 checks after the loader change.

The default path is **not accepted**. Before the GLFW filename correction it passed only by falling back to SDL. After the correction, the default-path live run created a window but failed its 40-second lifecycle deadline; its short CPU sample was 4,900 ms over two seconds. Its capture had unexpectedly enlarged dimensions, which requires investigation of rendering, DPI/resize handling and the test's window targeting. The cause is unresolved; neither a working GPU path nor a permanent software-only policy is inferred. The failed process was terminated by the owned test cleanup. No rendering defaults were changed to hide the failure.

Private evidence: `artifacts/packaging/linux-ui-control-software`, `linux-ui-trim-software-final`, `linux-ui-control-software-final` and the failed `linux-ui-control-default-fixed`. This is an additional published UI test, not full control interaction, accessibility, Home Assistant, keyring, native Wayland UI or active-overlay acceptance. These builds use the existing experimental baseline/Trim commands; no release pipeline change is accepted.

References checked 2026-09-10: [Silk 2.22 DefaultPathResolver](https://github.com/dotnet/Silk.NET/blob/v2.22.0/src/Core/Silk.NET.Core/Loader/DefaultPathResolver.cs), [SDL library names](https://github.com/dotnet/Silk.NET/blob/v2.22.0/src/Windowing/Silk.NET.SDL/SDLLibraryNameContainer.cs), [GLFW library names](https://github.com/dotnet/Silk.NET/blob/v2.22.0/src/Windowing/Silk.NET.GLFW/GlfwLibraryNameContainer.cs), and [Xlib window functions](https://xorg.freedesktop.org/archive/current/doc/libX11/libX11/libX11.html).

Next action / owner: Codex, diagnose the default Linux host's resize/CPU/shutdown failure, then extend live UI interaction checks. Windows trusted TLS, real Home Assistant, compatible Linux overlays, physical monitor changes, game frame times and full packaging/accessibility remain open. Review: 2026-09-17.

Follow-up, 2026-09-10: Cupri's DPI trace confirmed a resize feedback loop caused by comparing callback framebuffer dimensions with GLFW window dimensions from another moment. A [targeted host patch and reproducible evaluation](../third-party/CupriFace-Shell-dpi.md) now pass the Linux default and software host's repeated-resize and normal-close checks. The patch preserves DPI tracking and changes only the non-Windows measurement. It remains an isolated candidate: normal application dependency pins are unchanged, and Windows, genuine monitor-scale transitions and full release acceptance still require verification. Owner: Codex, complete regression coverage and dependency integration.

Integration follow-up, 2026-09-10: Shade now builds the patched host through a repository-local source project. The 20 upstream C# files have per-file provenance; only the DPI measurement and single-file probe path differ. The engine and existing third-party dependency versions remain pinned. Normal restores verify source hashes, and default locked restore/build/shared checks pass. Platform publishes now use generated RID-specific locks under `obj/publish-locks`, preserving the tracked development locks; locked development restore was verified after publishing.

The normal Windows single-file application passes live GPU controls, Home Assistant form accessibility, tray hide/restore/menu exit and normal shutdown. The earlier patched Windows candidate also passed software controls/form checks; its Cupri Advanced capture was inspected. The normal untrimmed and trimmed Linux single-file applications pass default-host startup without software fallback, repeated resize, exact stable final geometry and normal close; both short idle observations were 50 CPU ms over two seconds. Builds and publishes have no warnings. These samples do not establish gaming-performance acceptance.

The original host remains a failing control. Its resize storm also exposed a weakness in the native test deadline: managed cancellation alone cannot interrupt a blocked X11 request. The test now initializes Xlib threading, handles transient X errors and uses an independent cancellation callback to stop its owned renderer. The original control fails the geometry check and terminates under that watchdog; this is negative regression evidence, not a passing lifecycle result. Private evidence includes `linux-host-regression-watchdog`, `linux-host-integrated-default`, `linux-host-integrated-trim-default` and `win-host-integrated-tray`. The updated evaluation script successfully reconstructs original and patched loose-output controls after integration.

Next action / owner: Codex to extend Linux live UI interaction and accessibility coverage, then continue Home Assistant and remaining release checks. Physical monitor/DPI transitions, active overlays on compatible Linux desktops, real Home Assistant, Windows trusted TLS and gaming frame times remain open. No release-pipeline flags were changed or accepted. Review: 2026-09-17.

## Source-built engine follow-up, 2026-09-10

Normal builds now use the [pinned engine with focus-paint correction](../third-party/CupriFace.Engine/README.md), alongside the vendored host. Untrimmed Windows/Linux single-file and trimmed Linux single-file builds complete without warnings. Published Windows software controls and Linux software/default broker workflows pass as detailed in that record. These new engine binaries require the remaining packaging oracle before release; previous package-based evidence is historical. Locked development restore succeeds after cross-publishing, and source verifiers cover 20 host and 90 engine/generator/native-props files. Owner: Codex, complete remaining baseline/candidate equivalence checks.

## Current-source matrix, 2026-09-10

A fresh [Windows matrix](packaging-current-source-2026-09-10.md) records baseline/trim/compression/combined sizes, warning-free builds, cold/warm GPU/keyboard/tray checks and native/MQTT persistence/permission-recovery checks on every exact executable. All those checks pass. The combined candidate is 19,288,448 bytes versus 95,415,667 baseline bytes. Software setup, TLS, Linux and physical/real-HA gates remain open; no release defaults changed. Owner: Codex, complete those remaining comparisons next.
