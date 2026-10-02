# Stranded Deep Natural Regrowth

A BepInEx mod for Stranded Deep that adds persistent natural palm and coconut regrowth.

Current version: **0.2.0**

## Persistence

Natural Regrowth maintains its own per-world ecology data.

The sidecar data is stored under:

`BepInEx\config\StrandedDeepNaturalRegrowth\Worlds\`

Ecology state is committed together with a normal in-game save.

The mod uses atomic sidecar writes with temporary and backup files where possible.

## Installation

1. Install BepInEx for Stranded Deep.
2. Download `StrandedDeepNaturalRegrowth-0.2.0.zip` from the GitHub Release.
3. Extract the archive into the Stranded Deep game directory.
4. Allow the included `BepInEx` folder to merge with the existing `BepInEx` folder.
5. Fully restart Stranded Deep.

## Important

Because this mod maintains persistent ecology data, keep backups of your Stranded Deep saves before updating or removing the mod.

Requires BepInEx.
