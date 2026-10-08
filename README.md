# ZoneSavior
Archive inactive-player structures and tamed animals per zone, save/load/restore zone bundles, track player activity, configure archive exclusions, and enforce per-zone WearNTear limits.

![](https://i.ibb.co/ycMvZ9fj/Video-Project-26.gif) <br>
![](https://i.ibb.co/G3czYNtr/zonearchive.gif) <br>

ZoneSavior is a Valheim server maintenance mod for zone-based cleanup, archive, restore, and build-count control.

ZoneSavior **1.3.0** builds against original Valheim **1.0.16** game assemblies and requires BepInExPack Valheim **5.4.2351**. It includes a pinned ServerSync compatibility build. Existing ZoneSavior config keys, RPC formats and bundle payload format remain unchanged. New automatic archives use manifest version 3 to distinguish saved zones from reset zones; these manifests require ZoneSavior 1.3.0 or later. Manual archives keep manifest version 2, and existing manifest version 2/bundle version 3 archives remain readable. Update the server and clients running ZoneSavior together to satisfy the existing synchronized-version requirement.

It can:

- archive inactive player structures into zone bundle files
- restore saved bundles to the original place or a new zone
- optionally reset archived source zones
- enforce per-zone WearNTear limits from `zones.yml`
- track player activity for inactive-owner cleanup
- provide an optional client zone UI

The zone UI hotkey (F8 by default) displays the current zone boundary with eight 64m corner extensions. The lines follow the terrain, with height samples every 4m, to help estimate neighboring zones.

## Files

ZoneSavior uses one BepInEx config file and one data folder:

```text
BepInEx/config/
  sighsorry.ZoneSavior.cfg
  ZoneSavior/
    activity.yml
    zones.yml
    Diagnostics/
    ZoneBundles/
      tag_name/
        manifest.yml
        bundle001_<generation>.zonebundle.yml.gz
```

`activity.yml` stores player activity, scan state, and recent scan records. ZoneSavior reloads it conservatively at runtime: broken YAML, dirty runtime state, and active scans are ignored.

`zones.yml` stores zone limits and archive protection rules. Every rule must declare a non-negative `limit`. Steam IDs are the best long-term protection key; player names are convenient but can change.

`ZoneBundles/<tag>/manifest.yml` records the archive shape. Each `bundleNNN_<generation>.zonebundle.yml.gz` stores one source zone as a compact, gzip-compressed YAML bundle. ZoneSavior reads and writes these files directly; no manual extraction is required. New bundle files are committed by replacing the manifest only after every zone is saved successfully.

Manifest versions 2 and 3 and compressed bundle payload version 3 are supported. Older formats, including uncompressed legacy zone bundles, are not loaded or converted. If the original world data is still available, create a new archive from the live world with the current ZoneSavior version before restoring it.

`Diagnostics/` contains YAML reports written by `zs_debugzone`.

## Config

Config sections:

- `01 - General`
  - `Lock Configuration`: lets the server control synced settings.
- `02 - ZoneSavior`
  - `WearNTear Save Mode`: controls whether zone bundle saves include creatorless WearNTear.
  - `Zone WearNTear Limit`: enables zone limits from `zones.yml`.
  - `Zone UI Toggle Hotkey`: toggles the client zone overlay.
  - `Build Counter Visible Seconds`: controls how long the placement counter stays visible.
  - `Support Fill Contact Tolerance`: terrain contact capture tolerance. Source terrain must be loaded to capture exact contacts.
  - `Zone Bundle Support Fill Feather Width`: blend width around restored support terrain.
- `03 - Auto Archive`
  - `Dry Run`: report only.
  - `Reset After Save`: reset source zones whose creators are all eligible after saving. Connected mixed-owner zones are saved and kept.
  - `Minimum Pieces Per Cluster`: counts only eligible owners' structures. Small mixed-owner clusters are skipped; small clusters containing only eligible owners retain reset-without-save behavior during reset runs.
  - `Inactive Days`: owner inactivity threshold.
  - `Scan Interval Minutes`: automatic scan interval. `0` disables scheduled scans.
  - `Scanner Batch Size`: ZDOs inspected before yielding a frame.
  - `Max Zones Per Run`: maximum number of zones reserved for work in one automatic scan. Failed save/reset attempts still consume this budget.

## What Gets Archived

ZoneSavior saves player-build structures, not arbitrary world clutter.

Saved:

- player-build WearNTear objects with normal build recipes/resource costs
- tamed monsters with `MonsterAI` and `Tameable`
- creator metadata when present
- terrain support data for saved structures

Skipped:

- players, tombstones, loose item drops, projectiles, ragdolls, fish
- location objects and volatile world objects
- vanilla terrain comps and most raw terrain modifiers
- WearNTear prefabs without normal build recipes/resource costs

Auto archive candidate detection only starts from creator-linked WearNTear with `creator != 0`. `WearNTear Save Mode = IncludeCreatorless` can include nearby creatorless WearNTear during the save step, but creatorless structures alone do not make a zone eligible for inactive-owner cleanup.

Automatic archives keep structures together across owner boundaries. Adjacent zones (including diagonals) containing only eligible creators form the reset core. Mixed-owner neighbors join the same archive when they share an eligible creator with a connected zone. This can continue across several mixed zones, but an active or protected creator alone cannot connect additional zones. A normal scan requires at least one core zone, so retained mixed zones do not create a fresh archive after the core has been reset. There is no separate aggressive mode.

For adjacent zones `[A+B] [B+C] [C]`, with A/B eligible and C active, one tag saves both `[A+B]` and `[B+C]`. Only `[A+B]` is reset; `[B+C]` and `[C]` remain. The saved `[B+C]` snapshot contains all normally saved structures and tamed animals in that zone, including C's objects. Eligibility, recipe/save filters, minimum count and per-run limits still apply.

Before and during a saved reset, the server rechecks the saved state of creator-owned objects and tamed animals. Added/changed objects, changed container payloads or unexplained disappearances stop the affected reset. Normal inactivity scans also recheck eligibility and stop on a reconnect; an explicit Steam ID remains an administrator override of owner inactivity/protection. Spawned-object chains cannot delete outside the reset zone. The manifest records intent immediately before mutation and records each successfully reset zone separately. A failed preflight leaves the zone intact; an interrupted reset after mutation leaves a pending marker and blocks loading that tag until the partial world state has been reviewed. This is a conservative stop, not an automatic rollback.

## Terrain Restore

ZoneSavior uses SupportFill terrain restore.

When saving a loaded zone, it samples the lower footprint of saved structures and records terrain contacts where terrain is close enough to the structure bottom. When loading, those contacts can raise or cut terrain so structures regain support.

New saves also record one lowest valid contact height per touching piece and the lowest non-positive height difference from the original, unmodified terrain. For relocation, the most frequent piece contact height band (0.25m bands; ties prefer the lower band) selects the representative floor. The bundle is aligned at the first contact between that floor and the destination's unmodified terrain, then the saved negative terrain offset is applied. A large piece receives one vote, just like a small piece. Contacts in overlapping height bands are retained for alignment; support terrain still uses the lowest contact in each cell. All zones loaded together share one placement height; `offset=Y` adds a further adjustment.

Existing version 3 bundles remain readable. If any participating support zone lacks the new placement metadata, the entire load keeps its previous placement calculation; re-save the live structures to use the new rule. Original-location `restore` keeps the saved coordinates. The terrain's existing +/-8m limit still applies, so a different slope can leave some structures buried or unsupported.

If exact contacts are missing, ZoneSavior falls back to saved collider/footprint data and places terrain near the lowest reasonable support plane. The fallback is clamped to avoid extreme spikes.

## Terrain Editing and Blueprints

ZoneSavior no longer provides terrain proxy prefabs or replays terrain operations. Use Infinity Hammer to edit and save the final terrain snapshot in a blueprint, and Expand World Data to place that blueprint as a location. InfinityHammerAddon is a separate client-side addon for Infinity Hammer's existing tools; it does not depend on ZoneSavior or create saved terrain proxy objects.

This is a breaking removal, without legacy aliases, replay support, or automatic world cleanup. Preserve an external backup of existing worlds and blueprints before upgrading. While the previous ZoneSavior version is still installed, capture the final terrain with Infinity Hammer and remove the old ZoneSavior proxy entries from replacement blueprints. ZoneSavior does not migrate or clean up old proxy data; the game itself may discard unknown-prefab ZDOs when those areas load.

ZoneSavior also no longer increases the game's terrain height limit. Previously saved changes beyond the game's normal limit can look different unless every relevant client uses a compatible separate height-limit mod. Saving a terrain snapshot does not remove that requirement.

Zone bundle SupportFill terrain restoration remains part of ZoneSavior and is independent of blueprint terrain snapshots.

## Commands

### `zs_savezone`

Save one source zone or a rectangular source range.

```text
zs_savezone (x,z) tag
zs_savezone (x~x,z~z) tag
```

Examples:

```text
zs_savezone (-21,-4) test_base
zs_savezone (-21~-20,-4) old_base
```

A manual save command accepts at most 1,024 zones. `zs_savezone` does not accept a target or vertical offset.

### `zs_loadzone`

Load a saved tag.

```text
zs_loadzone tag [to (x,z)] [offset=Y]
zs_loadzone tag restore
zs_loadzone tag source (x,z) [to (x,z)] [offset=Y]
```

Examples:

```text
zs_loadzone auto_halla_c178 restore
zs_loadzone auto_halla_c178 to (-4,0)
zs_loadzone auto_halla_c178 source (-21,-4) to (10,3)
zs_loadzone test_base to (10,3) offset=2
```

Notes:

- Without `source`, ZoneSavior loads every bundle in the tag manifest and preserves the saved shape.
- `source (x,z)` loads only one saved source zone from the manifest.
- `restore` loads manual and older archives back to all saved source zones. For new automatic reset archives it restores only zones recorded as successfully reset, leaving saved-only mixed zones intact. For new automatic save-only archives it restores only the eligible core zones.
- `to (x,z)` is the target anchor.
- If `to (x,z)` is omitted, ZoneSavior uses the local player's current zone.
- `offset=Y` adds a vertical offset after the support anchor is calculated.
- `restore` does not accept `to (x,z)` or an offset.
- Relocating a new automatic archive still loads the whole saved shape, including mixed zones and their other owners' objects. It is a copy: the retained source zones remain. Choose the destination with that in mind, especially for containers and tamed animals. Loading onto a retained source zone in the same world is rejected, including single-source loads; world identity uses the world UID.
- New automatic archives reject clearing a target when its Spawned-object chain extends outside the complete load target set. Normal structural stability and terrain effects at a zone edge still require in-game verification.

### `zs_scan`

Run the inactive-player archive scanner manually.

```text
zs_scan [steamID] [dry|save|reset]
```

Examples:

```text
zs_scan dry
zs_scan save
zs_scan reset
zs_scan 76561198000000000 dry
zs_scan steam:76561198000000000 reset
```

Modes:

- `dry`: report candidates only.
- `save`: save matching archives but do not reset.
- `reset`: save matching archives and reset eligible source zones.

Without a Steam ID, inactive days, archive protection, minimum cluster size, and auto archive config apply. With a Steam ID, the scan is an admin override for that owner; mixed-owner zones are protected from targeted reset.

### `zs_status`

Write a YAML report with recent auto archive runs.

```text
zs_status
```

The console prints a short summary and the generated report path.

Cluster records distinguish `zones` (saved scope), `resetZones` (planned reset scope), `backupOnlyZones`, `resetCompletedZones` and `resetSkippedZones`. `zs_scan dry` reports the planned save/reset/keep counts without changing the world.

### `zs_debugzone`

Write a YAML diagnostic report explaining one zone's auto archive eligibility.

```text
zs_debugzone (x,z)
```

Example:

```text
zs_debugzone (-7,12)
```

Reports are written under `BepInEx/config/ZoneSavior/Diagnostics/`.

The zone report distinguishes a reset candidate (`wouldBeCandidateZone`) from a possible saved-only neighbor (`mayBeBackupOnlyZone`). A single-zone report cannot establish the connecting cluster; use `zs_scan dry` for that decision.

## Common Workflows

Test one save/load:

```text
zs_savezone (-21,-4) test_base
zs_loadzone test_base to (10,3)
```

Dry-run inactive cleanup:

```text
zs_scan dry
zs_status
```

Save inactive clusters without reset:

```text
zs_scan save
zs_status
```

Save and reset inactive clusters:

```text
zs_scan reset
```

Restore an archive elsewhere:

```text
zs_loadzone auto_halla_c178 to (20,-3)
```

Restore an archive to its original zones:

```text
zs_loadzone auto_halla_c178 restore
```
## Development verification

Build and update the local game plugin after merging:

```powershell
dotnet build ZoneSavior.csproj -c Debug -p:DeployToGame=true
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/Run-GameCompatibility.ps1
```

`Run-GameCompatibility.ps1` runs the existing 155 structure and 560 terrain-placement assertions, automatic-archive planning/manifest/restore and state-change regressions, plus original-API, Harmony-binding, cached-access and ZDO-codec checks in a disposable process using the installed Unity Mono runtime. Use `-GameManaged <original dedicated-server Managed directory>` for the server assembly set. Temporary test artifacts are retained and their path is printed. No Unity scene, real socket, world or native Harmony detour is started. Windows PowerShell's CLR cannot load every new game type; use this Mono runner for the complete suite rather than the older standalone terrain test host.

The separate ServerSync source/build provenance is in `Libs/ServerSync.Compatibility.md`. Actual client/host/dedicated gameplay still needs confirmation: startup, F8, build limits, save/reset/restore including portals and container contents, terrain placement on both slope directions, non-admin rejection, reconnect and settings synchronization.

For mixed-owner archives, verify the `[A+B] [B+C] [C]` case on a disposable world copy: compare the saved entries, retained container contents and tamed animals, reset exactly the first zone, then restore only that zone. Also check `source` and full relocation, a reconnect/content change during saving, cross-zone Spawned links, and interruption before/after the first deletion. A retained building may depend on support in the reset zone; automatic-archive membership does not identify structural dependencies or freeze the game's collapse simulation.

## Github
https://github.com/sighsorry1029/ZoneSavior
