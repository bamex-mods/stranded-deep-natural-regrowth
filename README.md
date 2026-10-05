# Stranded Deep Natural Regrowth

A BepInEx mod for Stranded Deep that maintains persistent palm reproduction, palm growth and coconut regrowth ecology.

Current stable version: **0.2.2**

## Ecology and persistence

Natural Regrowth keeps the existing production palm and coconut ecology behavior. Palm reproduction, staged growth, coconut scheduling and coconut materialization remain owned by the mod.

Per-world ecology state is stored in the authoritative sidecar directory:

`BepInEx\config\StrandedDeepNaturalRegrowth\Worlds\`

Ecology state is committed together with a normal in-game save. The mod uses atomic sidecar writes with temporary and backup files where possible and does not replace the native Stranded Deep save format.

## Optional Mod Settings integration

When Stranded Deep Mod Settings 0.3.x is installed, Natural Regrowth exposes eight controls in `Settings -> MODS` with Russian and English labels:

- Palm Reproduction
- Palm Regrowth Speed
- Palm Growth to Maturity
- Coconut Regrowth
- Minimum Coconut Regrowth Delay
- Maximum Coconut Regrowth Delay
- Coconut Crop Chance
- Reset Nature Settings

The Natural Regrowth `ConfigEntry` values remain the source of truth for configuration. Stranded Deep Mod Settings is an optional soft dependency; if it is absent, the ecology mod continues to load, run and persist normally using its BepInEx configuration.

Changing minimum or maximum coconut delay affects future scheduling only. Existing scheduled coconut timers are not rewritten by the settings UI.

## Installation

1. Install BepInEx for Stranded Deep.
2. Install the Natural Regrowth plugin DLL under `BepInEx\plugins\StrandedDeepNaturalRegrowth\`.
3. Fully restart Stranded Deep.

## Version history

### 0.2.2

- Restored the production v0.2.1 Mod Settings integration into canonical source.
- Added bilingual Russian/English Mod Settings registration through the current vendored SDK.
- Preserved ConfigEntry keys, defaults, ecology behavior and sidecar format.
- English/Russian Mod Settings and ecology behavior were field-tested before publication.

### 0.2.0

- Canonical persistent palm and coconut ecology baseline.

## Important

Because this mod maintains persistent ecology data, keep backups of both Stranded Deep saves and `BepInEx\config\StrandedDeepNaturalRegrowth\Worlds\` before updating or removing the mod.

Requires BepInEx. Stranded Deep Mod Settings is optional.
