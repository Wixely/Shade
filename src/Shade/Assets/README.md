# Shade icon

Original project artwork: a teal monitor with a diagonal shaded screen. No third-party artwork, fonts, or assets are used.

- `shade.svg`: scalable artwork.
- `shade.png`: 256-pixel image embedded for the CupriFace window, tray, and form header.
- `shade.ico`: Windows executable icon containing 16, 24, 32, 48, 64, 128, and 256-pixel images.

Regenerate these assets on Windows with Windows PowerShell 5.1:

```powershell
./scripts/generate-icons.ps1
```

The generator uses Windows System.Drawing and requires no additional packages.
