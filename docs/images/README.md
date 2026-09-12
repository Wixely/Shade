# README screenshots

Captured and visually reviewed on 2026-09-12 using Shade's real CupriFace renderer with synthetic monitor data. These documentation images are intentionally versioned; routine test screenshots remain in ignored `artifacts/`.

Regenerate from the repository root:

```powershell
dotnet run --project tests/Shade.Tests -c Release -- --readme-screenshots
```

Inspect the images under `artifacts/readme/` before copying the three PNGs here. The capture uses a fake dimming backend and an in-memory, disabled Home Assistant integration. It does not read the physical display layout, saved settings, or credentials, and does not connect to a broker. The hostname is a reserved example domain. Images are direct renderer output without desktop content or post-processing.

Owner: Codex. Review when the interface changes; keep README captions and screenshots aligned.
