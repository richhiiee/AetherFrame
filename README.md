# AetherFrame

**Enhanced character Plates for Final Fantasy XIV.**

AetherFrame is a [Dalamud](https://github.com/goatcorp/Dalamud) plugin for designing character Plates: profile cards that start from the familiar shape of the in-game Adventure Plate and can grow into fully freeform layouts.

> **Basic mode feels like FFXIV. Advanced mode removes the restrictions.**

> [!NOTE]
> AetherFrame is in **early development**. It is not an official Dalamud repository plugin, and its features, file formats and internal APIs may still change.

---

## Screenshots

_Screenshots will be added as the interface settles._

<!--
  To add screenshots: place PNG files in a `docs/screenshots/` folder in this repository and
  reference them with relative paths, for example:

  ![Plate Library](docs/screenshots/plate-library.png)

  Suggested set: Plate Library, Basic editor, Advanced editor, Plate Viewer, Clean Preview.
-->

| Plate Library | Basic editor | Advanced editor | Plate Viewer |
|---|---|---|---|
| _coming soon_ | _coming soon_ | _coming soon_ | _coming soon_ |

---

## Features

### Plates and the Plate Library

- **Multiple saved Plates.** Each Plate is a complete, independent design. Keep as many as you like.
- **Plate Library ("My Plates").** Browse, search, preview, rename, duplicate and delete Plates, and choose which Plate is Active for each character. Duplicating a Plate is an easy way to keep several variations of a look side by side.
- **Plate previews.** Library cards and a preview pane show each Plate at a glance.
- **Plate Viewer.** A dedicated window that shows a Plate fitted to its size.
- **Clean Preview.** Hide the editor UI and see the Plate exactly as it will look.

### Basic editor

A structured editor modelled on FFXIV's Adventure Plates. You fill in sections, and AetherFrame handles the layout.

- **Identity**: name, title, world, job and level, drawn from your character where available.
- **Portrait**: import your own image, with Fill / Fit / Stretch framing and a mirrored layout option.
- **Playstyle, active hours and a free-form message.**
- **Themes, backgrounds and patterns**: built-in colour themes plus procedural background patterns.

### Advanced editor

A freeform canvas for when the Basic layout isn't enough.

- Place, move, resize and rotate text and image elements anywhere on the Plate.
- Layers, snapping, undo/redo and per-element styling.
- Custom text with bundled fonts, so a Plate renders the same on every machine.
- Custom images from your own files.

### Components

Reusable decorative pieces you add to a Plate and restyle without redrawing anything.

- **Procedural Components**: Plate frames, portrait frames and overlays, name backings, dividers, section headers and corner ornaments, all drawn in code and tintable.
- **Bundled graphical Components**: hand-made artwork shipped inside the plugin (for example the Celestial Dream *Astrolabe Pivot* corner ornament).
- **Corner-specific placement**: choose which corners a corner ornament appears on.
- **Overflow**: Components can deliberately extend past the Plate's edges, and previews account for it.

### Templates

- Start a new Plate from a built-in Template (*Adventure Plate Classic* or *Blank Canvas*).
- Save any Plate as your own Template and reuse it later.

### Import and export

- Share a Plate as a single **`.aetherframe`** file that includes the images it uses.
- Imports are validated before anything is written. Size limits, path checks, image checks and document validation all run on a staging copy first.

### Local assets and data safety

- Imported images are stored and tracked locally, and unused ones are cleaned up.
- **Forward compatibility.** Plate data is versioned and migrated. Content from a newer version of AetherFrame that this version doesn't understand is preserved rather than discarded.

---

## Local first

Everything AetherFrame does today happens on your own machine.

- No account is needed.
- Editing is entirely local.
- Plates, Templates and images are stored in the plugin's local configuration folder.
- Import and export are file-based: you choose what to export and who you give it to.
- No online service is required for any of the core experience.

## Privacy direction

Sharing Plates with other players is a possible future direction, not a current feature. If it arrives, the intent is:

- **Intentional sharing** rather than passive discovery.
- No silent telemetry.
- No automatic scraping of nearby players.
- No public Content IDs, no alt correlation, and no public location history.

---

## Installation

AetherFrame is not yet available from the official Dalamud plugin repository, and there is no public custom repository for players yet. **Public installation instructions will be added later.**

Developers can build it from source and load it as a dev plugin (see below).

## Building from source

### Requirements

- Windows with FFXIV, XIVLauncher and Dalamud installed, and the game run with Dalamud at least once
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Dalamud API 15 (the project uses `Dalamud.NET.Sdk` 15)
- **x64** platform. The plugin project only builds for x64.

### Build

From the repository root:

```bash
dotnet build AetherFrame/AetherFrame.csproj --configuration Debug -p:Platform=x64
```

```bash
dotnet build AetherFrame/AetherFrame.csproj --configuration Release -p:Platform=x64
```

The output is written to `AetherFrame/bin/x64/<Configuration>/`.

### Tests

`AetherFrame.Tests` covers the Dalamud-independent logic (documents, persistence, packages, editor sessions and layout) and runs without the game:

```bash
dotnet test AetherFrame.Tests/AetherFrame.Tests.csproj
```

### Loading in game

1. Open `/xlsettings` → **Experimental** and add the full path to the built `AetherFrame.dll` under **Dev Plugin Locations**.
2. Open `/xlplugins` → **Dev Tools → Installed Dev Plugins** and enable AetherFrame.
3. Use **`/aetherframe`** to open My Plates, or **`/aetherframe view`** to view your character's Active Plate.

---

## Project structure

```
AetherFrame/            the plugin
  Domain/               Plate documents, Basic layout rules, Components, Templates (no Dalamud dependencies)
  Persistence/          versioned JSON storage, schema migrations, unknown-data preservation
  Services/             Plate & Template libraries, assets, fonts, .aetherframe packages, thumbnails
  UI/Editor/            editor sessions: history, selection, snapping, Basic/Advanced coordination
  UI/Rendering/         Plate renderer, backgrounds, text, Components, previews
  Windows/              Dalamud/ImGui windows: My Plates, Basic editor, Advanced editor, Viewer, import
  Hosting/              thin adapters over Dalamud services
  Assets/               bundled Component artwork, embedded in the DLL
  Fonts/                bundled fonts (SIL Open Font License), embedded in the DLL
AetherFrame.Tests/      pure-logic tests that build without Dalamud
```

### Key concepts

- **Plate / Profile document.** A Plate's content is a single versioned document (`ProfileDocument`) holding the canvas, background, elements (text and images), Components and Basic-mode settings. Both editors work on the same document, so a Plate can move from Basic to Advanced.
- **Plate Library.** Stores every saved Plate plus per-character bindings (which Plates belong to a character and which one is Active).
- **Active Plate.** The Plate AetherFrame presents for a character when nothing more specific is asked for (e.g. `/aetherframe view`). Resolved in one place (`ActivePlateResolver`); a character with no Active Plate gets an explicit empty state, never a substitute.
- **Basic editor.** Structured input (identity, portrait, playstyle, message, theme) mapped onto an Adventure Plate-style layout.
- **Advanced editor.** Direct manipulation of every element on the canvas, with layers, snapping and undo.
- **Templates.** Starting points for new Plates: built-in ones compiled into the plugin, plus user Templates saved locally.
- **Components.** Decorations described by a stable definition id and per-instance settings. Procedural ones are drawn in code, graphical ones use embedded artwork. Plates store only ids, never the art itself.
- **Rendering.** One renderer draws a Plate for the editors, the Viewer, Clean Preview and library previews, so all of them match.
- **Assets.** User images are copied into a local asset store, checked on import and tracked by reference so unused files can be removed.
- **Packages.** `.aetherframe` files are ZIP-based packages containing a manifest, the Plate document and its images. They are validated in a staging area before anything is imported.

Full-size source artwork for bundled Components lives in the separate [AetherFrameAssets](https://github.com/richhiiee/AetherFrameAssets) repository. The plugin only needs the optimized copies in `AetherFrame/Assets/`.

---

## Development note

AetherFrame is built with AI-assisted development. Product and architecture decisions, testing, in-game validation, and review and iteration are directed by a human developer.

## Contributing

AetherFrame is still taking shape. Issues, bug reports and feedback are welcome on the [issue tracker](https://github.com/richhiiee/AetherFrame/issues).

## License

AetherFrame is licensed under the [GNU Affero General Public License v3.0](LICENSE.md).

Bundled fonts are licensed separately under the SIL Open Font License 1.1. See `AetherFrame/Fonts/THIRD-PARTY-FONT-LICENSES.txt`.

## Links

- Repository: <https://github.com/richhiiee/AetherFrame>
- Assets: <https://github.com/richhiiee/AetherFrameAssets>

---

<sub>AetherFrame is a fan-made plugin and is not affiliated with or endorsed by Square Enix. FINAL FANTASY XIV © SQUARE ENIX CO., LTD.</sub>
