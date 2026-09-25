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

- 8-bit RGBA (or RGB for fully opaque art), non-interlaced, at most 4096 px per side (what
  `BundledArtImage` decodes). Its exact size is stated in `BuiltInArtCatalog`, and it is always
  drawn at that aspect ratio.
- Real alpha transparency; no baked background or checkerboard (except an opaque Background).
- Tinted line art (Celestial Dream): square and power-of-two, so every level halves exactly;
  white/greyscale (`R = G = B`), since the Component color multiplies it at draw time; fully
  transparent texels stored white, so bilinear filtering never darkens a tint at line edges.
- Full-color art (Celestial Sakura): kept exactly as approved. When it loads, the invisible
  texels next to the drawing take its edge color (see `BundledArtImage.BleedIntoTransparentTexels`),
  so filtering never outlines strokes dark. The file itself is not changed.
- Corner Ornaments are drawn for the top-left corner. The catalog says whether the other corners
  rotate or mirror it.

`BuiltInArtTests` and `CelestialSakuraTests` check all of this against the embedded bytes.

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

## Celestial Sakura / 7 full-color pieces

Seven original pieces: champagne gold filigree, blush cherry blossoms, pearls and a crescent moon.
Each PNG is the approved file, byte for byte. Nothing was resampled, cropped, padded, recolored or
re-encoded. Only the two dividers were renamed from the staging names (`Divider_01` and
`Divider_02`). `CelestialSakuraTests` checks every SHA-256 below against the embedded resource.

| Runtime file | Pixels | Ratio | Alpha | Kind | SHA-256 |
|---|---|---|---|---|---|
| `CelestialSakura_Background.png` | 1672 x 941 | 1.7768 | none (RGB, opaque) | Background | `c7a939df…f7148` |
| `CelestialSakura_PlateFrame.png` | 1672 x 941 | 1.7768 | 77.3% clear | Plate Frame | `0c5c407d…6dad9` |
| `CelestialSakura_PortraitFrame.png` | 992 x 1586 | 0.6255 | 77.5% clear | Portrait Frame | `4278abbe…a4dde` |
| `CelestialSakura_Nameplate.png` | 2172 x 724 | 3.0000 | 58.6% clear | Name Backing | `57ef50f0…9af96` |
| `CelestialSakura_Divider_Ornate.png` (was `Divider_01`) | 2172 x 724 | 3.0000 | 83.4% clear | Divider | `099b678f…e442a` |
| `CelestialSakura_Divider_Slim.png` (was `Divider_02`) | 2172 x 724 | 3.0000 | 96.0% clear | Divider | `a1798774…f98f4` |
| `CelestialSakura_CornerOrnament.png` | 1254 x 1254 | 1.0000 | 75.9% clear | Corner Ornament | `e58f67f3…8899e` |

The staging copies and their generation notes are in the main checkout's
`Assets/Components/CelestialSakura` folder, which isn't committed.

**Plate-sized pieces against the canvas.** The Adventure Plate canvas is 1280 x 720 (16:9, 1.7778;
`ProfileDocument.DefaultCanvasWidth/Height` and `AdventurePlateClassicLayout.ReferenceWidth/Height`).
The Background and the Plate Frame are 1672 x 941 (1.7768). That is 0.053% off 16:9, because
exact 16:9 at this width would be 940.5 px. The difference is 0.4 px over the full 720 px height,
so both are drawn over the full canvas (`ComponentPaintPlan.ArtAspectTolerance` is 0.1%). The
frame's drawn border sits within 4 px of the top and 8 px of the bottom of its own 941 px canvas
(at most 6 logical px from any Plate edge at 1280 x 720), so it spans the full Plate height. It was
not normalized. Padding it to an exact 16:9 canvas (1680 x 945) would only add transparent margins
and slightly shrink the drawn frame. On a canvas of another shape (the Card presets are 3:2), the
art is fitted inside the canvas at its own ratio, never stretched.

**Portrait Frame against the portrait.** The Classic portrait is 400 x 640 (5:8, 0.625). The frame
is 992 x 1586 (0.6255), which is 0.076% off, so it is drawn exactly over the portrait.

**Default placements** on the Adventure Plate Classic, in logical px, before any Scale or Offset:

- Background and Plate Frame fill the canvas at (0, 0, 1280, 720). Art Plate Frames skip the
  14 px inset of the procedural borders, because the drawing carries its own margin.
- Portrait Frame covers the portrait.
- Nameplate: 1.5x the padded name box, fitted at 3:1 and centered on the name. With the starter's
  60 px name box, that is 324 x 108 at y 20.
- Dividers are centered on the procedural Divider line and fitted at 3:1. The Ornate band is 3x
  the Divider height (216 x 72), which keeps the crescent clear of the name. The Slim band is 4x
  (288 x 96).
- Corner Ornament: 120 px squares (3x the 40 px corner box), inset 22 px, mirrored into the other
  corners.

All of these are starting points. Scale, Offset, Rotation and Layer order work on them exactly as
on every other Component.

**Memory.** Each piece is about 1.57 megapixels. With its levels, each takes about 8.4 MB of GPU
memory and is decoded once, the first time a Plate draws it (about 59 MB if all seven are drawn).
If that ever matters, the Celestial Dream route is available: approved, reduced runtime copies
made from the full-size sources.
