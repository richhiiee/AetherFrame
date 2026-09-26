# Changelog

All notable changes to AetherFrame are listed here. Versions follow the [versioning policy](docs/Versioning.md), and each released version has an annotated `v<version>` tag.

## [Unreleased]

## [0.1.5] - 2026-09-26

Distribution and submission readiness: the first version meant to reach testers. Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged, and so is how the plugin behaves in game.

### Added

- This changelog.
- Issue templates for bug reports and feature requests, with guidance on keeping personal details out of public reports.
- A guide for testers ([docs/Testing.md](docs/Testing.md)): installing through Dalamud's testing builds or a GitHub Release ZIP, what to test, and how to report problems.
- A tag-based release workflow that builds, tests and checks the plugin package and prepares a **draft** GitHub Release for review ([docs/Releasing.md](docs/Releasing.md)).
- Preparation notes and a draft `manifest.toml` for submitting AetherFrame to the official Dalamud plugin repository's testing track.

### Changed

- The plugin description in the Dalamud plugin installer now says that the plugin icon is AI-generated and that the bundled Celestial Dream and Celestial Sakura Components use AI-assisted artwork.
- The README describes the installation plan, where to get support, and AetherFrame's AI use at the Dalamud policy's *Copilot* level.

## [0.1.4] - 2026-09-25

Runtime readiness fixes before broader testing. Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged.

### Fixed

- The Advanced Editor, My Plates, Templates and Import windows now follow Dalamud's global UI scale, so they stay usable at 150% and 200%.
- My Plates and the Advanced Editor open at a sensible size the first time, held to the screen.
- If saved Templates fail to load, Create Plate still works with the built-in Templates, and the Template windows say what happened instead of waiting forever.
- Editor errors (image import, save, edits) show a plain description instead of raw exception text that could include local file paths.
- A damaged configuration file no longer stops the plugin from loading. It is logged and replaced by the defaults.
- If startup fails part-way, AetherFrame undoes what it had already hooked up.

## [0.1.3] - 2026-09-25

### Changed

- The Basic Editor no longer shows the logged-in character's name anywhere, including the Character Name placeholder and hints. Typing a name, saved names and the Active Plate for each character are unchanged.
- The plugin installer text describes character Plates, the Basic Editor and the Advanced Editor.
- The README has screenshots and a What's coming section.

## [0.1.2] - 2026-09-25

### Added

- **Celestial Sakura**: a full-color set of seven bundled Components (Background, Plate Frame, Portrait Frame, Name Backing, two Dividers and a Corner Ornament). This is original artwork created with AI assistance. The files are shipped exactly as approved and keep their C2PA Content Credentials.
- A Background Component kind that paints over the whole canvas, under everything else.
- In the Advanced Editor, Name Backings and Dividers can stop following the name ("Follows the name") and be placed independently.

### Fixed

- A Portrait Frame now paints over the picture when the Plate has no portrait element, instead of being hidden underneath it.
- Bundled artwork loads in the background instead of stalling the frame the first time it is drawn.

## [0.1.1] - 2026-09-25

### Fixed

- Unloading AetherFrame while a file operation is still running no longer risks writing after Dalamud has released the plugin's storage, and window teardown runs on the game's framework thread.
- Editor keyboard shortcuts no longer take keys from the game while Dalamud hides plugin UI (cutscenes, gpose, or hiding the UI).

### Changed

- My Plates no longer shows the character's name and World.

## [0.1.0] - 2026-09-25

The first versioned alpha.

### Added

- **My Plates**: a local library of saved Plates with previews, search, rename, duplicate and delete, and an Active Plate for each character.
- **Basic Editor**: an Adventure Plate-style editor for identity and details, portrait, playstyle, active hours, message, Themes and background patterns.
- **Advanced Editor**: a freeform canvas with text and image elements, layers, snapping, rotation, undo and redo, bundled fonts and Clean Preview.
- **Components**: procedural frames, backings, dividers, section headers and corner ornaments, plus the bundled Celestial Dream *Astrolabe Pivot* corner ornament (original artwork created with AI assistance).
- **Templates**: the built-in *Adventure Plate Classic* and *Blank Canvas*, and saving your own.
- **Import and export**: `.aetherframe` package files, validated on a staging copy before anything is imported.
- **Plate Viewer** and the commands `/aetherframe` (`/af`), `/af view` and `/af version`.
- Builds and tests on Windows and Linux in CI.

[Unreleased]: https://github.com/richhiiee/AetherFrame/compare/v0.1.5...HEAD
[0.1.5]: https://github.com/richhiiee/AetherFrame/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/richhiiee/AetherFrame/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/richhiiee/AetherFrame/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/richhiiee/AetherFrame/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/richhiiee/AetherFrame/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/richhiiee/AetherFrame/releases/tag/v0.1.0
