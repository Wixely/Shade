# Supplemental release licenses

Verified 2026-09-12. These upstream license texts cover packages that do not carry a standalone license in their NuGet archive. Preserve copyright and contact information as required attribution.

| File | Version and original source |
| --- | --- |
| AngleSharp.txt | 1.7.0: https://raw.githubusercontent.com/AngleSharp/AngleSharp/1c2f3ab09d22cfb5e47f31d43212ea231917a726/LICENSE |
| Silk.NET.txt | 2.22.0: https://raw.githubusercontent.com/dotnet/Silk.NET/f9535d2900ac226ee6c52cf96a7110fc1115730c/LICENSE.md |
| Tmds.DBus.txt | 0.94.2: https://raw.githubusercontent.com/tmds/Tmds.DBus/c75cbb3932b8f51959584357525d8550625d09c2/COPYING |
| GLFW.txt | 3.4: https://raw.githubusercontent.com/glfw/glfw/3.4/LICENSE.md |
| SDL.txt | 2.30.8: https://raw.githubusercontent.com/libsdl-org/SDL/release-2.30.8/LICENSE.txt |

`scripts/collect-release-notices.ps1` also copies licenses and native third-party notices from the exact restored packages, plus the bundled .NET runtime license and notices. Linux/macOS native packages and build-only ILLink tooling are excluded from the Windows notice inventory. CupriFace's existing MIT notice is included. Review this inventory when dependencies change. Owner: Codex; review before the next dependency update.
