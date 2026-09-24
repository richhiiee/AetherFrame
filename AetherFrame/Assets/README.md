# Bundled Component artwork

Runtime copies of built-in graphical Components, embedded into `AetherFrame.dll` as manifest
resources (`AetherFrame.Assets.<path with dots>`, see `AetherFrame.csproj`). Nothing here is
copied to the output folder, and nothing here is ever persisted: Plates, Templates and
`.aetherframe` packages store only the stable definition id (`af.corner-ornament.astrolabe-pivot`),
which the compile-time catalog maps to the logical asset id
(`af.asset.celestial-dream.corner-ornament.astrolabe-pivot`) and from there to the resource.

The full-size approved sources live in the separate AetherFrameAssets repository and are never
needed at runtime or build time.

## Requirements for every runtime PNG

- Square, power-of-two size, 8-bit RGBA, non-interlaced (the only format `BundledArtImage` decodes).
- Real alpha transparency; no baked background or checkerboard.
- White/greyscale when tintable (`R = G = B`); the Component color multiplies it at draw time.
- Fully transparent texels stored white, so bilinear filtering never darkens a tint at line edges.
- Drawn for the top-left corner (Corner Ornaments); the other corners are rotations of it.

`BuiltInArtTests` checks all of this against the embedded bytes.

## Celestial Dream / Corner Ornaments / AstrolabePivot.png — 512 x 512

Source: `AetherFrameAssets/source/CelestialDream/AstrolabePivot.png`, 1254 x 1254 RGBA
(sha256 `a0d9d8a4…ea6f1`), unchanged.

**Why 512 is enough.** A Corner Ornament's box is `CornerSize (40) x SizeFactor (2) x canvasHeight/720`
logical pixels, times the Component's Size (25–400%), times the surface's fit scale. The Profile
View fits the Plate to its window, so on screen the default box is about `80 x windowHeight / 720`:

| View height | Size 100% | Size 200% | Size 400% |
|---|---|---|---|
| 720 px | 80 px | 160 px | 320 px |
| 1350 px (1440p full height) | 150 px | 300 px | 600 px |
| 2100 px (4K full height) | 233 px | 467 px | 933 px |

512 px covers every realistic setting without magnification — up to Size 200% on a full-height 4K
view, and up to ~340% at 1440p. Only extreme combinations magnify: Size 400% on a 4K full-height
view (~1.8x), or inspecting it with the Advanced editor zoomed in past fit. 1024 px would quadruple GPU memory
and file size for those edge cases only; 256 px would already magnify at Size 200% on 1440p.

**Why levels.** Dalamud textures have no mipmaps, so a 512 px texture drawn at its common 60–150 px
would skip texels and break the thin arcs. `BuiltInArtTextureCache` decodes the PNG once and builds
alpha-weighted halved levels (512, 256, 128, 64, 32 — about 1.4 MB of GPU memory in total) and
draws the smallest level at least as large as the on-screen box.

**How it was made.** Lanczos resample of the premultiplied source in float precision, luminance
(`0.2126 R + 0.7152 G + 0.0722 B`) un-premultiplied and normalized so the brightest line core is
white, alpha kept as resampled, transparent texels set to white. Regenerate the same way if the
approved source ever changes; never edit the source.
