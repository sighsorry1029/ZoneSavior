# ZoneSavior Commands

ZoneSavior owns zone bundle, inactive-player auto archive, zone restore, and zone-limit administration.

Commands can be run from a dedicated server console, RCON, or an admin client with ZoneSavior installed. Supported admin-client commands are routed to the server by ZoneSavior RPC.

## Zone Bundle Commands

Zone bundle archives are written under:

```text
BepInEx/config/ZoneSavior/ZoneBundles/{tag}/
```

Each archive folder contains:

- `manifest.yml`
- one `bundleNNN_<generation>.zonebundle.yml.gz` file per saved source zone

ZoneSavior compresses and opens bundle files automatically. Console, RCON, and admin-client commands do not require manual extraction.
Previous manifest and bundle versions, including uncompressed legacy bundle files, are not loaded or converted. If the original world data is still available, create a new archive from the live world with the current version.

### `zs_savezone`

Saves one source zone or a rectangular source range.

```text
zs_savezone (x,z) tag
zs_savezone (x~x,z~z) tag
```

Examples:

```text
zs_savezone (-21,-4) test_base
zs_savezone (-21~-20,-4) old_base
```

A manual save command accepts at most 1,024 zones. Targets and vertical offsets are not valid save options.

### `zs_loadzone`

Loads a saved tag into the world.

```text
zs_loadzone tag [to (x,z)] [offset=Y]
zs_loadzone tag restore
zs_loadzone tag source (x,z) [to (x,z)] [offset=Y]
```

Examples:

```text
zs_loadzone auto_halla_c178 restore
zs_loadzone auto_halla_c178 to (-4,0)
zs_loadzone auto_Snack_plus1_b7a5018f_c103 to (20,-3) offset=1.5
zs_loadzone auto_halla_c178 source (-21,-4) to (10,3)
```

Notes:

- Without `source`, ZoneSavior loads every bundle listed in the tag manifest and preserves the saved shape.
- `source (x,z)` loads only the bundle whose manifest source zone matches that zone.
- `restore` loads every saved bundle back to its original source zone.
- `to (x,z)` is the target anchor.
- If `to (x,z)` is omitted, ZoneSavior uses the local player's current zone. A dedicated server console has no local player, so provide `to (x,z)` there.
- `offset=Y`, `yoffset=Y`, `y-offset=Y`, `--offset Y`, `--yoffset Y`, and `--y-offset Y` are accepted for vertical offset.
- `restore` does not accept `to (x,z)` or any vertical offset.
- ZoneSavior maps the saved archive's minimum source X/Z zone to this target zone and preserves every other manifest zone's relative offset.

## Auto Archive Commands

Auto archive commands inspect inactive creators, write connected candidate zones into archive bundles, optionally reset source zones, and report activity state. Supplying a Steam ID makes the scan an admin override for that owner.

Activity and zone rule files:

```text
BepInEx/config/ZoneSavior/activity.yml
BepInEx/config/ZoneSavior/zones.yml
```

`zones.yml` must declare `version: 1`, and every zone rule must include an explicit non-negative `limit`.

Mode arguments:

- `dry` or `dry-run`: scan and report only.
- `save`: save matching archives but do not reset.
- `reset`: save matching archives and reset eligible source zones.

If a mode is omitted, the command uses current server config values.

### `zs_scan`

Runs the inactive-player archive scanner manually, optionally filtered to one Steam owner.

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

Notes:

- Steam ID is the intended target format.
- If a mode is omitted, the command uses current server config values.
- Without a Steam ID, inactive-player eligibility, archive protection, and small-cluster rules apply.
- With a Steam ID, inactive days, archive protection, and minimum cluster size are bypassed for that owner.
- Targeted reset skips mixed-owner zones instead of resetting them.

### `zs_status`

Writes a YAML report with recent archive scan runs. The console only prints a short summary and the generated report path.

```text
zs_status
```

### `zs_debugzone`

Writes a YAML diagnostic report explaining one zone's auto archive eligibility.

```text
zs_debugzone (x,z)
```

Example:

```text
zs_debugzone (-7,12)
```
