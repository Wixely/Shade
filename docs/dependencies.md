# Dependency provenance

- Verified: 2026-09-10; review: 2026-09-17 and before dependency updates
- Owner: Codex

## Direct dependencies

The CupriFace engine now builds from [pinned 0.20.0 source with focus, accessible editing and safe activation corrections](../third-party/CupriFace.Engine/README.md), informational version **0.20.0-shade.8**. The original [official release](https://github.com/Wixely/CupriFace/releases/tag/v0.20.0) is retained for regression comparison. Its desktop host now builds from the [source-vendored 0.20.0-shade.8 project](../third-party/CupriFace.Shell/README.md), incorporating the Linux resize-loop fix, a single-file probe-path fix and accessibility activation/focus of clipped controls plus Linux/Windows password semantics, protected Windows value reads, editable text and property-change notifications. The original engine and host package manifests identify repository commit `56a9850decbb3e07b1cbd5f409f3d988e406b94b`, author Wixely, and the MIT license. The license is preserved in [third-party/CupriFace-LICENSE.txt](../third-party/CupriFace-LICENSE.txt). Per-file provenance and all patches are recorded with the vendored source. No project reference points to a sibling checkout. The original packages remain available only for comparison; normal builds use the vendored engine and host. The binding generator is also built from the pinned source, with its original Roslyn 4.14.0 dependency.

| Release asset | SHA-256 |
| --- | --- |
| CupriFace.0.20.0.nupkg | `5BA702B29D9C2A0A4A34DFC1AA63709B4C65C0D08A3F9B09BFFA39A4E4CA5D34` |
| CupriFace.Shell.0.20.0.nupkg | `7A3A1C47277658F6C5997869A537B22E2041C1E1785EC23DF77C7B8C193439EF` |

The checksum pins were calculated from downloaded release assets, not independently signed attestations. `scripts/restore.ps1` verifies them on every invocation. `NuGet.Config` maps these exact package IDs to the local release feed. All other packages come from NuGet.org. All projects retain lockfiles with the resolved versions and content hashes; normal restore uses locked mode.

## Resolved dependency families and licenses

Verified against the restored `.nuspec` metadata and package license files. See the application [lockfile](../src/Shade/packages.lock.json) for every exact package ID and hash.

| Family | Resolved version | Package-declared license |
| --- | --- | --- |
| AngleSharp | 1.7.0 | MIT |
| HarfBuzzSharp and platform native assets | 8.3.0.1 | MIT |
| SkiaSharp, HarfBuzz integration and platform native assets | 3.116.1 | MIT |
| Silk.NET Core, GLFW, Input, Maths, OpenGL, SDL, Windowing and subpackages | 2.22.0 | MIT |
| Ultz.Native.GLFW | 3.4.0 | Zlib |
| Ultz.Native.SDL | 2.30.8 | Zlib |
| Tmds.DBus.Protocol | 0.94.2 | MIT |
| Microsoft.DotNet.PlatformAbstractions | 3.1.6 | MIT, verified in packaged LICENSE.TXT |
| Microsoft.Extensions.DependencyModel | 8.0.0 | MIT |

Native distributions can contain additional third-party notices. Owner: Codex, collect and verify the complete redistributed native/runtime notice set during I6 before packaging. Package-level license declarations alone are not a completed binary redistribution review.

Wayland recovery integration, 2026-09-10: Shade now directly references Tmds.DBus.Protocol **0.94.2**, the same version already supplied transitively by CupriFace. The restored package identifies repository commit `c75cbb3932b8f51959584357525d8550625d09c2`; its bundled XML documentation supplies the exact client API contract used. Both lockfiles retain the pinned package and content hash. This adds no version upgrade or code-generation tool. Desktop Global Shortcuts portal availability remains a runtime environment requirement.

## Assets and toolchain

Buildout dependencies added 2026-09-10: MQTTnet **5.2.0.1603** (runtime client) and MQTTnet.Server **5.2.0.1603** (isolated tests only), from NuGet.org. Package metadata identifies official repository commit `14463d1ef9f1f119514185c1832905b91bff827d`; MIT notice is vendored in `third-party/MQTTnet-LICENSE.txt`. System.Security.Cryptography.ProtectedData **10.0.0** supplies Windows current-user DPAPI credential protection, from the Microsoft .NET package family. Exact resolved content hashes are in both project lockfiles. No broker service is installed or exposed beyond the test process's loopback listener.

Shade markup/CSS, identity policy, tests and Win32 integration are original project code. CupriFace component styling is supplied by the pinned local package. No CDN, downloaded web font, reference-project asset, JavaScript engine, Python or Node.js build tooling is used. The prototype uses system fonts and .NET/PowerShell tooling. The transitive desktop native packages are prebuilt dependencies, not source-build pipelines executed here.

Updates: download a deliberately selected official release, inspect its manifests/licenses and changes, update SHA-256 pins and package versions, regenerate and review both lockfiles, then repeat headless, native and live host checks. Do not use an unreviewed floating dependency version.

Linux credential integration added 2026-09-10: original C# interop dynamically uses system `libsecret-1.so.0` (API requiring libsecret 0.19 or later), GLib, GObject and GIO. These libraries are not bundled or installed by Shade. Declarations and ownership were checked against official [password lookup](https://gnome.pages.gitlab.gnome.org/libsecret/func.password_lookupv_sync.html), [store](https://gnome.pages.gitlab.gnome.org/libsecret/func.password_storev_sync.html), [metadata search](https://gnome.pages.gitlab.gnome.org/libsecret/func.password_searchv_sync.html), and [cancellation](https://docs.gtk.org/gio/class.Cancellable.html) documentation. Libsecret's [license file](https://raw.githubusercontent.com/GNOME/libsecret/master/COPYING) contains LGPL 2.1; include the applicable native-library notices and review packaging obligations before redistribution. The available Linux test environment has GLib/GIO but no libsecret or Secret Service, so native keyring acceptance remains outstanding.
