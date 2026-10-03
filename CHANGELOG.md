# Changelog

All notable changes to AetherFrame are listed here. Versions follow the [versioning policy](docs/Versioning.md), and each released version has an annotated `v<version>` tag.

## [Unreleased]

### Added

- **Open another Plate and New Plate... in the editors' Plate menu**, so switching Plates or starting a new one no longer means going back to My Plates. **Open another Plate** lists your Plates in My Plates' order, marks the one you're editing and your character's Active Plate, and opens the one you pick as a double-click on its card would; past ten Plates, the list scrolls under a search field. **New Plate...** opens the Create Plate chooser over the editor. With unsaved changes, both ask you to Save, Discard or Cancel first, and for a new Plate that happens before anything is made, so Cancel leaves nothing behind. In the chooser, wherever it opens, Use Template is greyed out for a Template that can't be used, such as one saved by a newer AetherFrame, and the chooser says why.
- **Unsaved changes are kept when AetherFrame closes.** If AetherFrame closes while an editor has unsaved changes (an update, turning it off, or closing the game), it keeps them beside your Plates, in its `Drafts` folder, and never in the Plate itself. The next time it loads, once a character is logged in, **Unsaved changes kept** offers them back. **Restore** opens the Plate with them in the editor you were using. Nothing is saved until you choose **Save**, and one Undo or Revert to Saved goes back to the saved Plate. **Discard** moves them to AetherFrame's Trash folder, and **Decide Later** keeps them: My Plates reminds you, with **Review**, and AetherFrame asks again next time. If the Plate was saved again since, was deleted, or can't be opened, they can be restored as a new Plate instead, named after the Plate followed by "(kept changes)", and the saved Plate stays as it is. A crash still loses unsaved changes, since nothing is written while you edit.
- **Click a Corner Ornament, frame or other decoration to select it**, in either editor, and find every one in a list. In the Advanced editor, the Layers list now lists your Components under its elements; clicking one there or on the canvas selects it, opens its controls under **Components** and outlines it on the canvas (every corner of a Corner Ornament). In the Basic editor, clicking one on the live view opens its category, brings its slot into view and outlines it; clicking the slot's name does the same. Selecting never changes your Plate. Clicks go to what is drawn on top: a frame only along its edge, so what is inside it is still clicked through it; an artwork Corner Ornament only where its artwork is drawn, so the portrait or text under the clear part of its box is still clicked through it; and a heading or name stays clickable through the Section Header or Divider drawn over it (one that lies entirely over its text is selected from the lists). The Background and Portrait Overlay cover the whole Plate or portrait, so they are selected from the lists only.

### Changed

- **Checking a character goes through your own internet connection** ("Checking a character through the player's own connection" in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)). When you check a character, AetherFrame opens one connection from your PC to the Lodestone, and AetherFrame's server reads the character's page through it. The page stays encrypted from the Lodestone to the server, so AetherFrame can't read or change it on the way, and the Lodestone sees your network address, as when you visit it. The page is read again the same way only while you use sharing: when you save a shared Plate, open **Sharing**, or view another player's Plate, the first time after AetherFrame starts and when the character's name or World changes. If the Lodestone turns your connection away, which some VPNs, proxies and hosting services cause, AetherFrame says so, and you can try again from another connection. Characters already sharing see a one-time notice in **Sharing**, and nothing is read through their connection until they have. The consent, **Sharing** and the Shared marker's hover text in My Plates say that a character whose page isn't read for 30 days stops showing its Plate to other players until you next use sharing with it.
- The Basic editor's **Reset Basic Layout** prompt lists the sections it resets as they are: **Favorite Jobs** by that name, and no retired Level line.

### Fixed

- **Escape closes only the menu or prompt in front.** Escape on a card's menu, the Help menu, a list, a color picker, the Create Plate chooser or a prompt went to the game, and the whole window behind it closed (or, in an editor with unsaved changes, asked you to save). Now it closes the menu, list or picker, or cancels the prompt, and the window stays. While a menu or prompt is open, your keys go to it and not to the game, so your character doesn't move until it closes. With nothing open, Escape closes the window as before. Help's **Esc** line says so.
- **Use Template from a Template's right-click menu in Create Plate closes the chooser**, as its **Use Template** button does. Before, the chooser stayed open over your new Plate.
- **The Basic editor fits the screen the first time it opens.** At a large UI scale it opened taller than the screen (at 150% on a 1080p screen, say); now it opens within the screen, as the Advanced editor and My Plates do. A Basic editor you have already opened keeps the size you left it at.

## [0.1.9] - 2026-10-02

Art Styles download from GitHub the first time you use them, so the plugin is about 75 MB smaller, and there are 20 more, for 40 in all. For a character that shares, making a Plate Active shares it at once, with a small window that shows how it is going. Also frames that fit what they frame, mirrored backgrounds that follow a Mirrored Plate, and a tutorial for sharing.

### Added

- **20 more Art Styles**, for 40 in all: Alchemist's Workshop, Corsair's Fortune, Dark Academia, Desert Oasis, Embroidered Tapestry, Enchanted Toybox, Frontier Silver, Industrial Salvage, Liquid Chrome, Memphis Playground, Mosaic Courtyard, Paper Theater, Porcelain Garden, Prehistoric Amber, Psychedelic Bloom, Racing Carbon, Retro Space Age, Sugarcraft Patisserie, Velvet Masquerade and Volcanic Forge. Each is a complete look of seven pieces, like the first twenty, with text colors that read on them, and downloads (2.7 to 5.5 MB) the first time you use it.
- **The tutorial covers sharing**: two new chapters in Help's tour, **Sharing online** and **Other players' Plates**. They show turning sharing on for a character, the Lodestone check, how its Active Plate is shared, pausing and turning sharing off, checking what a Plate would share, and viewing, hiding and reporting other players' Plates. The tour only points and explains: it never turns sharing on or off, shares, looks anyone up, hides or reports. Where a control isn't on screen (sharing not on yet, or already on), the step says so and moves on. Help marks the tour as updated for anyone who finished it.

### Changed

- **Art Styles download their artwork the first time you use them** ("Art on demand" in [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md)). The plugin no longer carries it, so it is about 75 MB smaller, and so is every update. Choosing an Art Style, or opening a Plate that uses one in an editor or the Plate Viewer, or viewing another player's Plate, downloads what isn't on your PC yet from GitHub, checked against a checksum built into the plugin, and keeps it in `artwork-cache` in AetherFrame's configuration folder. Until it arrives, the Plate shows the style's colors, and the editor or the Plate Viewer says how far the download has got; if it fails, they say why, with **Try again**. Hovering an Art Style in the Theme browser says how much it downloads. My Plates' cards show a style's artwork once it is on your PC; until then, open or **View** the Plate to fetch it. The style previews are still in the plugin, so the Theme browser shows every style at once. Nothing goes to AetherFrame's server for this, and GitHub sees your address, the plugin's version and which files you fetch.
- **Making a Plate Active shares it at once**, for a character that shares: nothing is shown first any more (before, a Plate never shared was shown before it was sent). Turning sharing on for a character shares its Active Plate as soon as the Lodestone check passes, too. Saving it shares the new version, as before. The consent when you turn sharing on says so, and **Check what would be shared (preview)**, in a Plate's menu in My Plates, still shows what a Plate would share. A small window at the side of the screen follows each share: what it is doing, then that the Plate is shared (closing by itself), or why it couldn't be, with **Try again**. It doesn't take keyboard focus when it appears, and **Hide** puts it away while it sends. A share now waits as long as the sharing server may take with its images (about 7 minutes for one image, longer for more), and **Stop sending** stops it at any time. If your Active Plate isn't shared yet (you logged in with one that wasn't, say), the Sharing window offers **Share it now**. A send for another of your characters, still going after you switch characters or log out, can be stopped from the Sharing window too, and a Plate that is no longer your Active Plate is never sent: a send of it stops, and the Sharing window says so.
- **Frames fit what they frame**. A Plate Frame drawn from artwork runs along the Plate's edges: its rails lie on them, and its corners and crests reach a little past, where before the whole frame sat inside the Plate. A Portrait Frame's rails lie on the edges of the picture: of the whole portrait box in Fill and Stretch, and of the picture itself in Fit, whatever its shape, so no picture shows outside its rails. Like the name plaques, a frame stretches only along its plain rails: its corners and ornaments keep their shape on a Plate or picture of any shape. Celestial Sakura's frames, ornamented all along, stretch instead at a few short, hand-picked places, so a vine or a pendant there grows a little longer. Portrait Overlays lie on the picture too. Other players need 0.1.9 to see them: an earlier version draws them as grey boxes, which cover most of a Plate with an Art Style, and the sharing server asks it to update before it can share. A Plate you already share shows its new frames to others once you save it again.
- **A Mirrored Plate mirrors its Art Style's background**, so the details sit over the art's calm side as they do in Normal, and read as well. Most Art Styles' backgrounds are drawn busy behind the portrait and calm behind the details; before, a Mirrored Plate put the details over the busy side. The Plate Frame and Portrait Frame stay as they are, since every one is drawn symmetric. A background moved or turned in the Advanced editor is mirrored with its Offset and Rotation. Other players see it mirrored once the Plate is shared again: a Plate you already share updates for them the next time you save it.
- **The Basic editor's Style shows Pattern and Customize Background only when they can change something**: while no background artwork covers the Plate. While an Art Style's background artwork covers the whole Plate, they would change nothing, so they are hidden, and **Remove the Artwork** in their place brings them back (as does setting **Background** to **None** under **Frame & Decorations**, or choosing a Simple Theme after an Art Style, which takes that style's background away). Your background's settings are kept either way, and the Advanced editor still has its colors, gradient and image, and an existing Pattern's tuning.

### Fixed

- Sharing a Plate with images works again. A fault on AetherFrame's sharing server, fixed on October 2, made such a share wait and then fail.

## [0.1.8] - 2026-10-01

Sharing (alpha): your Plate on your character, for other players who share to see, the way the game's Adventure Plates work. Also a font library, Art Styles, and one Plate view everywhere. Sharing is off until you turn it on for a character, and until then AetherFrame sends nothing and looks nothing up.

### Added

- **Sharing (alpha).** Turn it on for a character in My Plates' **Sharing** window. It shows what other players will see and what the server keeps, and asks you to prove the character is yours with a one-time code on your Lodestone profile. That character's Active Plate is then shared, exactly as it visibly draws and nothing more, and saving it updates what other players see. Turning sharing off for the character removes its Plate from the server. The installer's description says what sharing sends: the character's name, World, Lodestone id and Active Plate, and, when you view a Plate, the name and World you look up.
- **Viewing other players' Plates.** Once one of your characters shares, right-click another player's character in game (in the world, the party list, the friend list or chat) and choose **View AetherFrame Plate**, or search for them by full name and World in the **AetherFrame Plates** window. Their Active Plate opens read-only in the Plate Viewer, as it draws for them, artwork past its edges included, and nothing is added to your Library. Its right-click menu refreshes it, hides that player on this PC (nothing is sent), or reports the Plate with a reason. Each image is checked before it is shown, and everything received is kept in memory only.
- **A font library** of 101 families from Google Fonts, beside AetherFrame Sans, Serif and Mono: fantasy and medieval faces (Cinzel, MedievalSharp, Uncial Antiqua and more), script and handwriting (Great Vibes, Dancing Script, Caveat and more), elegant serifs (Cormorant Garamond, Playfair Display, EB Garamond and more), modern sans-serifs (Montserrat, Raleway, Lato, Inter and more), display faces (Bebas Neue, Orbitron, Press Start 2P and more) and three monospaced. Both editors' **Font** control lists them by category, with a search box. Bold and Italic are offered where a family has the real face, and a character a family lacks is drawn in AetherFrame Sans instead of "?". Each family is under the SIL Open Font License or the Apache License; **Help**, then **Font licences**, shows them all. The fonts add about 29 MB to the plugin, and a font uses memory only once something draws it. Each library font's capitals sit where AetherFrame Sans's do, so a name lines up with its backing whichever font it uses.
- **Art Styles**, 20 complete looks for a Plate: Allagan Tech, Ancient Amaurot, Anime Pop, Art Nouveau, Botanical Cottage, Celestial Sakura, Crystarium Crystal, Cute Kawaii, Cyberpunk Neon, Dark Fantasy, High Fantasy Royal, Ishgardian Gothic, Minimalist Modern, Oceanic Siren, Retro RPG, Steampunk Machinist, Tarot Arcana, Ukiyo-e Fantasy, Void Cosmic Horror and Watercolor Fantasy. In the Basic editor's Theme browser, each shows a preview card. Choosing one sets its background, Plate Frame, Portrait Frame, Corner Ornaments, a name plaque, a divider and section header labels, with text colors that read on them, as one undo step. Every piece stays yours to change under **Frame & Decorations**. The themes from before are under **Simple Themes** and set colors only. The art adds about 75 MB to the download.
- Name backings, dividers and section headers drawn from artwork stretch to fit their text: a short name gets a short plaque, a long one a long plaque, and only their plain middle stretches.
- **Height** for every text, in both editors (the Basic editor's text styling, under **Size**, and the Advanced editor's text properties): it moves the text up or down, up to 40 px either way, for a font that sits a little high or low. The text's box and its Name Backing stay where they are, and other players see the text where you put it.

### Changed

- **Preview**, in both editors, opens the Plate in the Plate Viewer, the same floating view as My Plates' **View**. It shows the Plate as you edit it, artwork past its edges included; you can move it, resize it with Ctrl+scroll, and keep it open while you edit.
- The Basic editor puts a title only on the name's line: **Identity Layout** offers **Inline Before** and **Inline After**, and **Use game placement** follows the game's order. A Plate whose title already shows above or below the name looks exactly as it did until you press **Put on One Line**, which is one undo step.
- A Plate Frame drawn from artwork (Celestial Sakura's and the Art Styles') sits over every picture and under every text, so its ornaments never cover the text and a picture never cuts off its corners. Line, Double Line and Notched frames stay on top.
- Under the hood, for sharing: a signed protocol, still a draft until version 1 is frozen; keys that Windows protects for your user; the snapshot of exactly what your Plate shows; and image preparation that shares only the visible part of each image, re-encoded with no metadata. [docs/networking](docs/networking/) describes each part, and the [decision register](docs/networking/DecisionRegister.md) records why.

### Fixed

- The Help button reads **Help** instead of a "?", which sat off centre. In My Plates it is in the window's top right corner.

## [0.1.7] - 2026-09-30

A new look and a first-time tutorial, a Plate menu in both editors, and more Plate Library reliability fixes. This version has no networking and needs no account. Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged.

### Added

- A Plate menu in both editors (interface task 1, [docs/InterfaceAudit.md](docs/InterfaceAudit.md)). Click the Plate's name in the editor's top bar for View, Set Active, Save as New Plate, Save as Template, Export and Rename, without going back to My Plates:
  - View shows the Plate over the game in the movable Plate Viewer, unsaved changes included.
  - Save as New Plate saves the Plate as it is, unsaved changes included, as a new Plate right after it, with the same character links but never Active, and the editor continues on it. The original stays as it was last saved.
  - Set Active, Save as Template and Export use the last saved version, as they do from My Plates, and the menu says so while there are unsaved changes. Set Active says why when it's unavailable.
  - Results and errors show under the save state. The menu's control keeps its icon at any window width; the name shows when there's room.
  - When the editor window is too narrow for the whole top bar, the save state and its buttons (Preview, Revert, Save, Help) move to a second row instead of being cut off.
  - The tutorial points at it, and its steps no longer send you to My Plates for these actions. Its version goes to 2, so Help says it was updated.
- A networking preview flavour of the plugin, for testers and CI (`dotnet build -p:AetherFrameNetworkPreview=true`; [docs/networking/DecisionRegister.md](docs/networking/DecisionRegister.md), D9b and P2): it compiles the remote protocol and persona foundation sources into `AetherFrame.dll`, says `[network preview]` in `/aetherframe version`, and still opens no connection and creates no key. Player builds compile none of it and are unchanged; boundary tests keep them free of any protocol, persona or networking code, and the release tooling's package check now refuses a DLL that holds any protocol or persona type, so a preview build can reach neither a release nor a test build. AetherFrame's log now hides persona, profile, revision and asset identifiers, should one ever appear in it.
- A visual identity and design system for AetherFrame's own windows ([docs/DesignGuide.md](docs/DesignGuide.md)): midnight surfaces, one aetherial accent, a restrained glow for focus and the tutorial spotlight, gold only for the Active Plate, headings and section labels in the game's Axis face, and a corner-bracket-and-spark mark drawn in code. Every color and measurement comes from one token layer (`AetherPalette`, `AetherMetrics`, with contrast tests), and the ImGui style is pushed around AetherFrame's windows only and always popped again, so every window shares the same surfaces, rounding and spacing. My Plates gains a brand row, one primary Create Plate button, an empty state that invites the first Plate, and cards with a raised frame, the accent selection ring with the frame corners and the gold Active badge; the Basic editor's category title and group labels use the section-label style; the unsaved-changes, open-another-Plate, revert, rename and delete prompts use the shared button row (red for the destructive choice, Cancel as a quiet ghost). Saved Plates render exactly as before: the redesign touches the windows' chrome, never the Plate.
- An interactive first-time tutorial: twelve short chapters that dim AetherFrame's windows, spotlight the real control, explain it on a card beside it, and let the player use it where the step asks for that. Steps whose control isn't on screen say so and let the interface be used; steps that need an editor open explain how to get there and offer to open it; nothing in the tour creates, saves, imports, exports, overwrites or deletes anything for the player. A new install (no configuration, no Plates, no Templates) is offered the tour once the Library has loaded, with Start Tutorial, Maybe Later and Do Not Show Again; an install upgrading from any earlier version is never offered it unasked. The Help menu in My Plates and both editors starts, resumes or jumps into the tour, and lists the keyboard shortcuts and chat commands. The tutorial's state is kept in the plugin configuration beside the guidance flag and never in a Plate; the configuration's version is unchanged.
- A Help button in My Plates and at the right end of both editors' action bar, and tooltips on My Plates' Create Plate and search field. The controls' names, the editors' collapsible sections and their existing tooltips are unchanged.
- Manual acceptance steps for the interface and the tutorial ([docs/ManualAcceptance-UI-Onboarding.md](docs/ManualAcceptance-UI-Onboarding.md)); nothing about appearance or input in game is claimed as verified until they have been run.
- Release tooling for a public custom Dalamud repository ([docs/CustomRepository.md](docs/CustomRepository.md)): `tools/AetherFrame.ReleaseTools` checks a release package against the full rule set (x64, built from the released commit, one version and one Dalamud API level everywhere, only the three plugin files, safe entry names), generates and checks the repository metadata Dalamud reads (`pluginmaster.json`, stable and testing channels), and writes and verifies SHA-256 checksum files. The Build and Release workflows run it; the Release dry run keeps the generated metadata as an artifact. Nothing is published to a repository yet, and the plugin itself is unchanged.
- A manual **Publish custom repository** workflow ([docs/CustomRepository.md](docs/CustomRepository.md#publishing)) that puts a published GitHub Release into the custom repository's testing or stable channel after the owner's approval, or rolls a channel back. It verifies every release it describes from scratch, never publishes a draft, never moves a channel to an older version by accident, and writes only `pluginmaster.json` and a README to its own branch. It has not been used yet.

### Changed

- My Plates' card menu and Manage Templates call the Plate Viewer View, as the tutorial already did, so Preview means only the editors' Preview. Set Active on a card also says why when it's unavailable. My Plates' Plate actions and their prompts now come from one component shared with the editors' Plate menu, and behave as before.
- The tutorial card, after the owner's first run through it:
  - The close button no longer covers long chapter names; the chapter line wraps short of it.
  - The footer is two rows (the step count and Chapters; Skip tour, Back and Next), so Skip tour and Back no longer overlap. Back is hidden on the first step.
  - Creating your first Plate can't be skipped with Next any more: Next on "Start a new Plate" and "Choose a Template" says what to do instead. Back, Chapters and Skip tour still work.
  - The window a step explains comes in front of AetherFrame's other windows, and the dim and the card stay in front of it, so My Plates no longer hides the control being explained and the Create Plate chooser no longer fades the card.
  - Beside a tall, narrow control, such as the Basic Editor's section list, the card now sits to its right (or left), instead of below it.
  - A step that asks you to click the highlighted control (Create Plate, Choose a Template, + Text) can't be skipped with Next: pressing Next flashes the control and says what to do.
  - The same holds for a step that first needs you somewhere else and highlights the way there, such as switching to the Advanced Editor for its tools.
- The project moved to [QuietFoxLabs/AetherFrame](https://github.com/QuietFoxLabs/AetherFrame). The plugin's repository link and release downloads use that address, and so does the custom repository address for new installs ([docs/CustomRepository.md](docs/CustomRepository.md)). The old `richhiiee/AetherFrame` addresses still redirect, so a custom repository URL added earlier keeps working.
- The release tooling accepts the address from before the move only as history: in the custom repository file published before it, and in packages up to 0.1.6. Everything it publishes uses the new address ([docs/CustomRepository.md](docs/CustomRepository.md#repository-move)).

### Fixed

- The plugin installer shows AetherFrame's icon again. Its address now points at [`AetherFrame/images/icon.png`](AetherFrame/images/icon.png) in this repository; the separate repository it pointed at no longer exists.

Plate Library reliability and data preservation ([docs/reliability/PlateLibraryReliability.md](docs/reliability/PlateLibraryReliability.md)). Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged.

- A damaged Plate, Template, character or Plate order file that was read from Dalamud's backup copy is never written over until its damaged bytes are kept under `Recovery`: if that copy failed at load (a full disk), the next save or change now retries it, and is refused with a plain message while it still fails, instead of replacing bytes that may be newer than the backup. Set Active on a character whose file can't be kept gives the same message, Create, Use Template and Duplicate for that character add it to their "couldn't be linked" message, and the log no longer claims a copy it couldn't make.
- A Plate, Template, character or Plate order file holding bytes that aren't valid text still loads from the file itself, exactly as in 0.1.6 (the bytes read as replacement characters), but its original bytes are now kept under `Recovery` before anything writes over it, and a write is refused with a plain message while that copy fails. A Plate or Template read this way says so when its card is hovered. Validity is checked on the bytes, in the file's own encoding (UTF-8, or UTF-16 or UTF-32 behind a byte order mark), so a correctly encoded replacement character is ordinary text, and invalid bytes alone never make a file load from an older backup, become unreadable, lose a newer version's protection, or have its binding or Plate order rebuilt. A Plate or Template card shows this note together with its unsupported-elements warning when both apply.
- Recovery and pre-migration backup copies are flushed to disk and only then given their name, so a copy that fails partway (a full disk) never leaves a partial file that looks like a kept copy.
- Every Plate and Template write is checked to read back as exactly the same text, and to load again, before anything is written.
- Duplicate no longer reports a failure (inviting a second copy) when only the Plate order couldn't be saved, and a reorder whose save fails or is refused puts the previous order back, so the same move can simply be made again.
- Exporting a Plate that holds a value a Plate file can't carry (for example a font size over 1024 typed in 0.1.5) says which value, instead of "The Plate in this file is damaged." Data the editor doesn't show (from a hand edit or a newer build) gets a plain refusal without advice to change it in the editor.
- A package whose image is only mentioned in a text field, or used only where the import doesn't re-point it, or whose text isn't valid UTF-8, is refused as such; a package can't be imported twice after an import that wrote its Plate but reported a failure.
- Add Image names the stored file after the stored content, and a Plate or Template file that couldn't be opened (in use by another program) is no longer described as damaged.
- Recovery and trash file names are stamped in the Gregorian calendar whatever the Windows culture, and Save as Template of a Plate holding data nested too deep for a Template is refused with a plain message instead of the serializer's.

## [0.1.6] - 2026-09-27

Reliability, data safety and import security, the v0.1.6 milestone. Saved Plates, Templates, `.aetherframe` packages and the configuration format are unchanged: a file written by 0.1.0 through 0.1.5 loads and re-saves byte for byte, and the schema versions did not move.

### Fixed

- A `.aetherframe` file with an explicit `null` where a list or text belongs no longer passes validation and then throws inside the editor; a local Plate with the same shape opens with those fields empty.
- A package whose ZIP end record disagrees with the one .NET reads is refused before the archive's directory is parsed, so a hostile file can no longer make the plugin allocate hundreds of megabytes on import.
- A package whose `profile.json` is made of millions of tiny values is refused before it is parsed (new limits on JSON value count and property-name length), instead of costing over a gigabyte and several seconds to check.
- Very large text sizes, from a package or from zooming far in, no longer make the plugin rasterize hundreds of megapixels of glyphs: each font family builds tiers only up to a bounded size (Sans 280 px, Serif 240 px, Mono 140 px; the Dalamud default family, whose glyph set depends on the game's language, 96 px) and text is slightly upscaled above it. The bundled fonts keep every glyph they rendered in 0.1.5; only the size of a tier is bounded.
- Typing a value such as `1e39` into an editor slider, or an overflowing number in a hand-edited file, no longer leaves a Plate that can never be saved. A value typed into a slider still goes past the slider's range, as in 0.1.5, but only as far as a Plate can hold and still export: font size and Auto Fit minimum 1 to 1024 px, letter and line spacing ±10,000, and angles and rotations wrap into 0-360°. Opacity, Pattern intensity and outline thickness stay within their slider, which is all the renderer draws. A typed `1e39` takes the limit for font size, Auto Fit minimum and spacing; it leaves a background's gradient angle or Pattern rotation as it was before the entry, and resets an image's rotation to 0°. Values that aren't numbers are repaired in memory, and the save error names the offending value if one ever gets through.
- Create Plate and Use Template now report a Plate that was created but could not be linked to the character, instead of a total failure that invited a duplicate.
- A character binding whose file could not be read at startup is treated as unavailable (never replaced) rather than as damaged; the player is told to restart the game.
- A failed index or migration write at startup, or a failed Recovery copy of a damaged index, no longer marks the whole Plate Library as unloadable.
- Plate, Template, character and index files are read from disk by the plugin itself, and Dalamud's backup copy is asked for only when the file's content is unusable: a file that is merely locked or missing is reported unavailable instead of being silently replaced by an older backup. A file served from the backup is logged, and the damaged on-disk copy is kept under `Recovery` before anything writes over it.
- A Basic editor action that fails partway (for example revealing a section when the Plate is already at its element limit) now rolls back completely instead of leaving a half-applied, un-undoable change.
- Undoing a delete puts the element back where it was, so overlapping elements keep their paint order.
- Saving while dragging commits the drag first; a layout refinement can no longer land while a save is being written; Discard is unavailable while a save is in flight, and a Discard that is refused keeps the question open and says why.
- A write that fails inside Dalamud's reliable storage (a full disk, for example) no longer blocks every later save of that Plate until the game restarts: the plugin writes the file directly while Dalamud's temporary file for it is stuck open, and says so once in the log.
- 16-bit PNGs are counted at 8 bytes per pixel against the decoded-memory limit, matching what the game's decoder allocates.
- Importing a package commits its images on a background thread instead of inside a frame; the Import Preview can't be closed until the import finishes; checking a package counts as running work during unload, and an import already copying its images when the plugin unloads finishes as one operation instead of being rolled back; a package check never leaves a stray staging folder.
- An import whose Plate file was written although the import then failed, and which couldn't move that file away (or was stopped by unloading first), keeps its images: the Plate appears whole the next time AetherFrame starts instead of pointing at images that were rolled back.
- A canceled plugin load now tears the plugin down itself, since Dalamud does not dispose a plugin whose load was canceled.
- A newer build's configuration settings and asset metadata are preserved instead of being overwritten by this build.
- AetherFrame's log no longer shows a character's Content ID: a character's settings file, which is named after it, appears as "character binding file", including inside a logged error.
- Plate names made only of invisible characters are refused, and a name's zero-width joiners and other format characters become spaces; a package an earlier version exported with such a name still imports, under the folded name. A Template file named with a built-in Template's id is ignored instead of listed twice; Use Template always instantiates from the saved file.
- Stale temporary files from an interrupted image import or thumbnail generation are removed at load.

### Changed

- The Plate and Template Libraries read and parse their files on a background thread while loading, instead of inside one framework tick, and their threading contract is documented.
- Loading a Plate whose values had to be repaired in memory logs one line per file; the file itself is unchanged until it is saved.
- Card previews no longer draw a bar for an affix on empty text.
- The test suite grew from 2071 to over 2600 tests, including real fixture sets written by every tagged build since 0.1.0, failure injection for every multi-step file operation, and a threading contract test against a queued dispatcher.

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

[Unreleased]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.9...HEAD
[0.1.9]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.8...v0.1.9
[0.1.8]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.7...v0.1.8
[0.1.7]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.6...v0.1.7
[0.1.6]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.5...v0.1.6
[0.1.5]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.4...v0.1.5
[0.1.4]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.3...v0.1.4
[0.1.3]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.2...v0.1.3
[0.1.2]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/QuietFoxLabs/AetherFrame/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/QuietFoxLabs/AetherFrame/releases/tag/v0.1.0
