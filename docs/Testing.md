# Testing AetherFrame

Thanks for helping test AetherFrame. This page covers how to install a test build, what to look at, and how to report what you find.

> [!IMPORTANT]
> **Current status:** AetherFrame is not in the Dalamud plugin installer yet. Test builds are published as [GitHub Releases](https://github.com/richhiiee/AetherFrame/releases), starting with 0.1.5, the first tester build. Install one as a dev plugin: see [From a GitHub Release ZIP](#from-a-github-release-zip-dev-plugin). These builds **do not update themselves**.

AetherFrame is an early alpha. Expect rough edges, and keep a backup of anything you care about (see [Before you start](#before-you-start)).

## Installing a test build

There are two ways a test build can reach you. Right now only the GitHub Release ZIP is available. Use only one at a time.

### From a GitHub Release ZIP (dev plugin)

This is the current route. Test builds are attached to [GitHub Releases](https://github.com/richhiiee/AetherFrame/releases) and load through Dalamud's dev plugin loader. They **do not update themselves**: check the Releases page for newer builds.

1. Download `AetherFrame-<version>.zip` from the release.
2. Optional: check it against the SHA-256 in the release notes. In PowerShell: `Get-FileHash .\AetherFrame-<version>.zip`.
3. Extract it to a folder you'll keep, for example `C:\FFXIV\AetherFrame\`. It contains `AetherFrame.dll`, `AetherFrame.json` and `AetherFrame.deps.json`.
4. Type `/xlsettings`, open **Experimental**, and add the full path to `AetherFrame.dll` under **Dev Plugin Locations**. Save.
5. Type `/xlplugins`, open **Dev Tools → Installed Dev Plugins**, and enable AetherFrame.

To update, disable AetherFrame, replace the three files with the new ones, and enable it again.

### From the Dalamud plugin installer (testing builds)

**Not available yet.** This will become the main route once AetherFrame has been accepted into the official Dalamud repository's testing track. Updates will then arrive automatically.

1. Type `/xlsettings` in game and open the **Experimental** tab.
2. Tick **Get plugin testing builds**, then **Save and Close**.
3. Type `/xlplugins`, search for **AetherFrame**, and install it.

### Switching between the two

Both routes use the same plugin name and the same data folder, so your Plates carry over. Dalamud can't load both at once, though: before installing from the plugin installer, remove the dev plugin location from `/xlsettings`, and the other way round.

## Before you start

AetherFrame keeps everything in its Dalamud configuration folder:

```
%AppData%\XIVLauncher\pluginConfigs\AetherFrame\
```

Copy that folder somewhere safe before testing a new build. It holds your Plates, Templates and imported images. Deleted Plates are kept in a `Trash` folder inside it, but AetherFrame can't restore them yet.

## What to test

Everything is useful, but these areas matter most right now:

- **My Plates** (`/af`): creating, duplicating, renaming, deleting and choosing the Active Plate, on more than one character if you can.
- **Basic Editor**: filling in every section, and whether it feels familiar if you know Adventure Plates.
- **Advanced Editor**: moving, resizing, rotating, layering, undo and redo, and Clean Preview.
- **Plate Viewer** (`/af view`): does it show the Plate you chose as Active?
- **Import and export**: sharing a `.aetherframe` file with another tester and importing theirs.
- **UI scale**: everything at Dalamud's 100%, 150% and 200% global scale.
- **Stability**: reloading the plugin, logging out and in, cutscenes and gpose while an editor is open.

## Reporting a problem

Open a [bug report](https://github.com/richhiiee/AetherFrame/issues/new?template=bug_report.yml). The form asks for:

- **Your version.** Type `/af version` in game and copy what it prints, for example `AetherFrame 0.1.5 (build 1a2b3c4)`.
- **What happened, and how to make it happen again.**
- **Log lines**, if anything went wrong. Type `/xllog` to open Dalamud's log, or find `dalamud.log` in `%AppData%\XIVLauncher\`. Lines mentioning AetherFrame are the useful ones.

Issues are public. Before posting, remove anything you don't want to share: character and Free Company names in screenshots, and your Windows user name, which can appear in file paths in the log. A `.aetherframe` export helps with rendering bugs, but it contains that Plate's text and images, so only attach one you're happy to make public.

Ideas are welcome too, as a [feature request](https://github.com/richhiiee/AetherFrame/issues/new?template=feature_request.yml).

## Known limitations

- Deleted Plates can't be restored from inside AetherFrame yet.
- Imported images that are no longer used aren't cleaned up automatically.
- Plates are local only. There's no sharing between players yet.

The [changelog](../CHANGELOG.md) lists what changed in each version.
