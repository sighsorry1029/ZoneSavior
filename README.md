# ZoneSavior
Automatically archiving Inactive-player structure and tamed animals per zone, zone bundle save/load/restore, player activity tracking, archive protection rules, per-zone WearNTear limits and terrain proxies that you can save in blueprints.

![](https://i.ibb.co/ycMvZ9fj/Video-Project-26.gif) <br>
![](https://i.ibb.co/G3czYNtr/zonearchive.gif) <br>

ZoneSavior is a Valheim server maintenance mod for zone-based cleanup, archive, restore, and build-count control.

![](https://i.ibb.co/dwmrT2HF/proxies.png) <br>
Terrain proxies that you can save in blueprints.

![](https://i.ibb.co/k6364Vpw/blueprint1.gif) <br>
![](https://i.ibb.co/m5t9DZJG/blueprint2.gif) <br>
Put terrain proxies and paint proxies for your blueprints!


It can:

- archive inactive player structures into zone bundle files
- restore saved bundles to the original place or a new zone
- optionally reset archived source zones
- enforce per-zone WearNTear limits from `zones.yml`
- track player activity for inactive-owner cleanup
- provide admin terrain proxy tools that can be saved in blueprints
- provide an optional client zone UI

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
        bundle001_<generation>.zonebundle.yml
```

`activity.yml` stores player activity, scan state, and recent scan records. ZoneSavior reloads it conservatively at runtime: broken YAML, dirty runtime state, and active scans are ignored.

`zones.yml` stores zone limits and archive protection rules. Steam IDs are the best long-term protection key; player names are convenient but can change.

`ZoneBundles/<tag>/manifest.yml` records the archive shape. Each `bundleNNN_<generation>.zonebundle.yml` stores one source zone. New bundle files are committed by replacing the manifest only after every zone is saved successfully.

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
  - `Reset After Save`: reset eligible source zones after saving.
  - `Minimum Pieces Per Cluster`: small clusters are skipped, or reset without save during reset runs.
  - `Inactive Days`: owner inactivity threshold.
  - `Scan Interval Minutes`: automatic scan interval. `0` disables scheduled scans.
  - `Scanner Batch Size`: ZDOs inspected before yielding a frame.
  - `Max Zones Per Run`: zone limit for one automatic scan.
- `04 - Terrain Tool`
  - `Radius`: default circle radius.
  - `Slope Width`: default slope width.
  - `Terrain Edge Softness`: `0` is a hard edge, `1` is more rounded.
  - `Paint Type`: default paint proxy type.
  - `Terrain Tool Modifier Key`: modifier used with mouse wheel for terrain tool adjustments.

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

## Terrain Restore

ZoneSavior uses SupportFill terrain restore.

When saving a loaded zone, it samples the lower footprint of saved structures and records terrain contacts where terrain is close enough to the structure bottom. When loading, those contacts can raise or cut terrain so structures regain support.

If exact contacts are missing, ZoneSavior falls back to saved collider/footprint data and places terrain near the lowest reasonable support plane. The fallback is clamped to avoid extreme spikes.

## Terrain Proxy Tools

Admin terrain tools appear in the hammer Misc tab while debug mode is enabled and the local player is an admin.

Tools:

- `ZoneSavior Terrain Proxy`: fills terrain to the marker height in a circle.
- `ZoneSavior TerrainProxy Slope`: place a start marker and an end marker to create a saved slope.
- `ZoneSavior Paint Proxy`: stores terrain paint in a circle.
- `ZoneSavior Paint Reset`: resets paint in the radius and removes intersecting paint proxies.
- `ZoneSavior Terrain Reset`: resets terrain in the radius and removes intersecting terrain proxies.

Wheel controls:

- Terrain Proxy: wheel changes radius, modifier+wheel changes edge softness.
- TerrainProxy Slope: modifier+wheel changes width.
- Paint Proxy: wheel changes radius, modifier+wheel changes paint type.

The active radius, width, edge softness, paint type, and modifier hint are shown in the piece tooltip.

Terrain, paint, and slope proxies are persistent prefab objects. They can be captured by blueprint tools, then replay terrain changes when placed again.

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
- `restore` loads every saved bundle back to its original source zone.
- `to (x,z)` is the target anchor.
- If `to (x,z)` is omitted, ZoneSavior uses the local player's current zone.
- `offset=Y` adds a vertical offset after the support anchor is calculated.

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
## Github
https://github.com/sighsorry1029/ZoneSavior
