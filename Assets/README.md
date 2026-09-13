# Launcher icon

Generated with the built-in image-generation tool. The mint terminal mark and cyan/mint stack sit on a navy tile with transparent outside corners.

- `launcher-icon.png`: original generated source artwork with alpha.
- `launcher.ico`: embedded application/window icon, with 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixel frames.
- `create-icon.ps1`: repackages the PNG into ICO frames using Windows System.Drawing; preserves alpha.

The project embeds the ICO as both its executable icon and a WPF resource. All three windows reference that resource, so it works in source builds and the single-file portable app without a loose icon file.

Regenerate the ICO after changing the source artwork:

```powershell
./Assets/create-icon.ps1
```

## Generation prompt

Use case: logo-brand. Asset type: Windows desktop application icon for Full Stack Launcher, a developer dashboard for services and database tools. Create one polished square app icon, optimized for a tiny taskbar size. A bold mint-green terminal chevron and underscore on a deep navy rounded-square terminal tile, with two short cyan/mint stacked layer edges beneath the terminal suggesting a full software stack. Flat, crisp geometry with very restrained depth; strong simple silhouette, thick strokes, balanced generous spacing inside the tile. Palette matches a dark developer app: navy #10151E / #1B2432, mint #69E2C0, subtle cyan accent. Center icon nearly filling the square with only a small margin. Transparent background outside the rounded tile, true alpha, no drop shadow extending outside it. No lettering, no words, no watermark, no mockup, no surrounding scene. Produce the finished icon artwork, not a grid of alternatives.
