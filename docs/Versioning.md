# Versioning

AetherFrame uses [semantic versioning](https://semver.org/). Before 1.0, versions are `0.MINOR.PATCH`.

## What changes the version

**MINOR** is a meaningful product milestone that has been accepted. Examples: a new Plate Library capability, a new editing subsystem, Trash and Restore, or a major sharing capability.

**PATCH** covers bug fixes, runtime safety improvements, build or CI improvements, and small UX corrections.

**No version bump** for documentation-only work, research or audit branches, or experiments that aren't merged.

## Where the version lives

The version is set in one place: [`Version.props`](../Version.props) at the repository root. Both projects import it, and everything else comes from it:

| Where | Value | Set by |
|---|---|---|
| `AssemblyVersion`, `FileVersion` | `0.1.3.0` | .NET SDK |
| `InformationalVersion` | `0.1.3+<commit>` | .NET SDK (Source Link appends the commit) |
| Dalamud manifest `AssemblyVersion` | `0.1.3.0` | DalamudPackager, read from the built assembly |

Don't write a version anywhere else. The tests fail if a project sets its own version.

## Seeing which build is loaded

- **`/aetherframe version`** or **`/af version`** prints the running build in chat, for example `AetherFrame 0.1.3 (build 1bf26e1)`.
- `dalamud.log` records the same text when the plugin loads.
- The Dalamud plugin installer shows the manifest version.

The build is the commit that was checked out when the plugin was built. A build with uncommitted changes shows the commit it started from.

## Milestone workflow

1. Every implementation milestone states its intended version before work begins.
2. The milestone branch sets that version in `Version.props` during implementation, so in-game testing can tell which build is loaded.
3. A release version is tagged only after the work is integrated into `master` and verified, including a green CI run on Windows and Ubuntu. Tags are annotated and named `v<version>`, for example `v0.1.0`.
