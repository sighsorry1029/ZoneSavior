# Commands and configuration

Install the same ZoneSavior version on the server and clients. Put the DLL under `BepInEx/plugins`; settings and archives are stored under `BepInEx/config`.

Management commands are available to the host, dedicated server console and server administrators. Admin-client requests are checked and executed by the server. F8 is available to all players and does not require administrator access.

**Back up the world before resetting or loading zones. Loading first removes eligible existing objects and terrain modifiers from destination zones, then places the archive. It is a replacement operation, not a merge.**

## Files

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

`activity.yml` holds activity, character names and scan records. `zones.yml` holds building rules and archive exclusions. Archives belong to the server or host running the world. Keep each tag's manifest and bundle files together; ZoneSavior handles gzip compression without manual extraction. A new manifest replaces the old one only after all its zones have been saved successfully.

## Save and load commands

### `zs_savezone`

Save one zone or a rectangular range, using **zone coordinates** rather than world meters:

```text
zs_savezone (x,z) tag
zs_savezone (x~x,z~z) tag
```

```text
zs_savezone (-21,-4) test_base
zs_savezone (-21~-20,-4) old_base
```

The maximum is 1,024 zones per command. Targets and vertical offsets are not save options. Use F8 to see your current zone.

### `zs_loadzone`

```text
zs_loadzone tag [to (x,z)] [offset=Y]
zs_loadzone tag restore
zs_loadzone tag source (x,z) [to (x,z)] [offset=Y]
```

```text
zs_loadzone test_base to (10,3)
zs_loadzone test_base to (10,3) offset=2
zs_loadzone old_base source (-21,-4) to (10,3)
zs_loadzone old_base restore
```

- A normal load uses every bundle in the manifest and preserves their relative arrangement. `source (x,z)` selects one saved source zone.
- `to (x,z)` is the destination anchor: the archive's minimum source X/Z maps there. Without `to`, the command uses the local player's current zone. **A dedicated server console must provide a destination** unless using `restore`.
- `offset=Y` adds a vertical adjustment in meters after terrain alignment. The aliases `yoffset=Y`, `y-offset=Y`, `--offset Y`, `--yoffset Y` and `--y-offset Y` are also accepted.
- `restore` keeps the original coordinates and height. It cannot be combined with `source`, `to` or an offset.

The zones selected by `restore` depend on the archive:

| Archive | Original-location restore |
| --- | --- |
| Manual or older supported manifest | All saved source zones |
| Automatic reset archive with manifest 3 | Only zones recorded as successfully reset |
| Automatic save-only archive with manifest 3 | Only the eligible core zones |

Relocating an automatic archive loads its whole saved shape, including retained mixed-owner zones. Those source zones remain in the world, so relocation **copies their structures, container contents and tamed animals**. Loading onto a retained source zone in the same world is rejected, including single-source loads. World identity is checked by world UID.

New automatic archives also reject target clearing when a linked Spawned-object chain would extend outside the complete load target set. This does not guarantee that neighboring structures remain supported after a terrain change.

On a dedicated server, terrain work may need a nearby ZoneSavior client. If the target is not loaded for terrain overwrite, move a client into that area and retry.

## Scan and diagnostic commands

### `zs_scan`

```text
zs_scan [steamID] [dry|save|reset]
```

| Mode | Action |
| --- | --- |
| `dry` or `dry-run` | Report candidates without saving or changing the world |
| `save` | Save matching archives and keep source zones |
| `reset` | Archive matching clusters and reset eligible zones; see the small-cluster exception below |
| Omitted | Use the current server configuration |

Start with a preview:

```text
zs_scan dry
zs_status
```

Then choose `zs_scan save` or `zs_scan reset` as appropriate. An explicit Steam ID is an administrator override:

```text
zs_scan 76561198000000000 dry
zs_scan steam:76561198000000000 reset
```

Without an ID, inactivity, archive protection and minimum cluster size apply. With an ID, those checks are bypassed for that owner's known player IDs. Targeted reset skips mixed-owner zones. Prefer Steam IDs to character names for long-term identification.

### `zs_status`

Writes a YAML report of recent scans and prints its path:

```text
zs_status
```

Cluster records distinguish saved `zones`, planned `resetZones`, `backupOnlyZones`, `resetCompletedZones` and `resetSkippedZones`. Dry-run output includes the planned save/reset/keep counts.

### `zs_debugzone`

Explains the eligibility of one zone:

```text
zs_debugzone (-7,12)
```

Reports go to `BepInEx/config/ZoneSavior/Diagnostics/`. `wouldBeCandidateZone` identifies a reset candidate; `mayBeBackupOnlyZone` identifies a possible retained neighbor. A single-zone report cannot establish the connecting cluster; use `zs_scan dry` for that decision.

## Automatic archive policy

Archives include player-built WearNTear structures with normal build recipes/resource costs, supported tamed animals, creator metadata and terrain support data. Players, tombstones, loose items, projectiles, ragdolls, fish, location objects and raw terrain components are not archived. Tamed animals must use `MonsterAI` and `Tameable`; bosses and event creatures are excluded.

Candidate detection starts from structures with a nonzero creator ID. `WearNTear Save Mode = IncludeCreatorless` can include creatorless structures in the save, but those structures alone do not trigger inactivity cleanup.

Adjacent zones, including diagonals, whose creators are all eligible form the reset core. Mixed-owner neighbors join the archive when they share an eligible creator with a connected zone. Active or protected creators do not extend the archive by themselves. Normal scans require at least one core zone; there is no separate aggressive mode.

For `[A+B] [B+C] [C]`, where A/B are eligible and C is active, the first two zones share one archive. Only `[A+B]` is reset. `[B+C]` is saved and retained, including C's normally archived objects; `[C]` is left alone.

**Small-cluster exception:** during reset runs, a cluster containing only eligible owners and fewer than `Minimum Pieces Per Cluster` structures is reset **without being saved**. Small mixed-owner clusters are skipped. The minimum counts only eligible owners' structures. Explicit Steam ID scans bypass this threshold.

Before and during a saved reset, changed objects, changed container contents or unexplained disappearances stop the affected reset. Normal scans also recheck eligibility and stop when an eligible owner reconnects. Destruction chains cannot leave the reset zone.

A failed preflight leaves the zone intact. Once mutation has started, interruption can leave a partially reset world and a pending marker in the manifest. Loading that tag remains blocked until the partial state has been reviewed. Completed changes are not automatically rolled back.

## Configuration

Edit `BepInEx/config/sighsorry.ZoneSavior.cfg`. Synced settings are controlled by the server when `Lock Configuration` is On. The generated file describes every option.

### General and client settings

| Setting | Default | Purpose |
| --- | --- | --- |
| `Lock Configuration` | `On` | Let the server control synced settings |
| `Zone WearNTear Limit` | `Off` | Enable the quantity limits in `zones.yml` |
| `Zone UI Toggle Hotkey` | `F8` | Show zone coordinates and terrain-following boundaries; client only |
| `Build Counter Visible Seconds` | `2.5` | Placement-counter duration; client only |
| `WearNTear Save Mode` | `CreatorOnly` | Include player-created structures; use `IncludeCreatorless` to include otherwise eligible structures without creators |

F8 starts hidden after login. Its outline has eight 64m corner extensions, with terrain height sampled every 4m, to help estimate neighboring zones.

### Auto Archive settings

| Setting | Default | Purpose |
| --- | --- | --- |
| `Dry Run` | `On` | Report without saving or resetting |
| `Reset After Save` | `Off` | Reset eligible source zones; retain mixed-owner neighbors |
| `Minimum Pieces Per Cluster` | `5` | Minimum eligible-owner structure count for an archive; see the reset-without-save exception above |
| `Inactive Days` | `30` | Required absence before normal cleanup |
| `Scan Interval Minutes` | `0` | Automatic scans are disabled at 0 |
| `Scanner Batch Size` | `1000` | Objects inspected before yielding a frame |
| `Max Zones Per Run` | `50` | Work budget; failed save/reset attempts also consume it |

Owners first discovered in an existing world start their inactivity period at discovery, not at an inferred historical logout time.

## Building rules and archive protection

Edit `BepInEx/config/ZoneSavior/zones.yml` on the server. Rules reload when the file changes; invalid YAML retains the previous valid rules.

```yaml
archive_protection:
  steam_ids: ["76561198000000000"]
  player_ids: []
  player_names: []
defaults:
  count_creatorless: false
rules:
  - name: center_keep
    x: [-1, 1]
    z: [-1, 1]
    limit: 50
    count_creatorless: true
    blocked_pieces:
      - portal_wood
      - piece_workbench
  - name: outer_ring
    x: [-2, 2]
    z: [-2, 2]
    limit: 100
```

Replace the example Steam ID with the account you want to protect, or use `[]`. Protection affects normal inactivity cleanup, not explicit administrator targeting. Steam IDs are more stable than player names.

Rule behavior:

- Coordinates are inclusive. The first matching rule supplies the complete policy; overlapping limits and ban lists are not combined.
- A limit applies to **each zone**, not the whole rectangle. Unmatched zones have no rule-based limit or bans.
- Every rule needs a `limit` of 0 or greater. A limit of 50 allows 50 counted objects and rejects the next one. Enable `Zone WearNTear Limit` to use quantity restrictions.
- `count_creatorless` changes only which structures contribute to the quantity count.
- `blocked_pieces` uses exact, case-sensitive prefab names, not display names or wildcards. Omitted, null or empty lists mean no bans. Blank entries are invalid; repeated names are deduplicated.
- Piece bans work independently of the quantity-limit toggle, including pieces without WearNTear. Normal administrator placement follows the same rules. Existing objects and administrative bundle restoration are unaffected.

The placing client rejects a banned piece before creating it or spending materials, stamina or durability. All participating clients need ZoneSavior. This is not server-only anti-bypass protection: mods that directly create objects need separate integration. Quantity limits still count loaded local structures and remove excess placements afterward; they do not reserve capacity for simultaneous builders.

## Terrain restoration

SupportFill restores the terrain needed to support saved structures. Loaded source terrain lets the save record exact bottom contacts; otherwise it falls back to collider/footprint data. Fallback heights are clamped to avoid extreme spikes.

For relocation, new saves select the most common piece-contact height band as a representative floor. Bands are 0.25m high, ties prefer the lower band, and each touching piece receives one vote. The floor meets the destination's unmodified terrain, then the saved deepest non-positive terrain offset is applied. Overlapping contact bands remain available for alignment; support terrain uses the lowest contact in each cell. All zones loaded together share one placement height. `offset=Y` adds an adjustment afterward.

If any participating support zone lacks the newer placement metadata, the whole load uses the previous placement calculation. Re-save the live structures to use the newer rule. `restore` always keeps the original saved coordinates. The game's normal +/-8m terrain limit remains in effect; different slopes can leave structures buried or unsupported.

| Setting | Default | Purpose |
| --- | --- | --- |
| `Support Fill Contact Tolerance` | `0.5m` | Maximum distance between loaded source terrain and a structure bottom for contact capture |
| `Zone Bundle Support Fill Feather Width` | `6m` | Blend support terrain into surrounding terrain; 0 changes only exact footprint cells |
| `Zone Bundle Support Grace Minutes` | `60` | Temporary support grace after loading; 0 disables it |

Support grace is runtime-only and does not survive a server restart. Inspect restored structures before it expires.

## Localization

Building-limit and blocked-piece messages, the F8 HUD and support-grace HUD follow the selected game language. English and Korean are embedded, with English fallback for untranslated keys. Commands, diagnostics, logs and config descriptions retain their existing text.

Packages include `ZoneSavior.English.yml` and `ZoneSavior.Korean.yml` beside the DLL. To add or override a language, place `ZoneSavior.<Language>.yml` or `.yaml` anywhere under `BepInEx/plugins`, using the game's language name. Keep one external file per language and preserve placeholders such as `{0}` and `{1}`. Restart after editing files. Changing the in-game language updates the registered translations and F8 HUD.

## Activity and character-name lookup

`activity.yml` stores account activity separately from its `character_names` map. Each character name is the last name actually observed for that player ID. Unknown offline IDs stay unknown until the server sees the character connect or observes it during an activity check; account aliases and building creator-name fields are not treated as verified character names. New archive labels use this mapping, while existing bundles and world data remain unchanged.

Runtime reload is conservative: invalid YAML, unsaved runtime changes or an active scan prevent replacement of the current activity state.

The optional administrator-only creator-name RPC supports protocol 2 for exact character names and protocol 1 clients, echoing the requested version. Both use the exact mapping and return not found for unobserved IDs. Integrations requiring exact-name semantics should request version 2 and reject version 1 responses from older servers. Server and clients should be updated together.

## Optional mods and interrupted work

ZoneSavior includes previously saved chunks that become completely empty in the next normal world save. This prevents old objects from returning after reload. A save chunk spans 8x8 to 64x64 zones, so resetting one zone may not empty it. The correction does not force an immediate save or repair existing duplicates.

Upgrade World and FreshWorld are optional. With a complete recognized Upgrade World empty-chunk implementation, ZoneSavior delegates that save cleanup to Upgrade World. ZoneSavior keeps its own archive and reset policy. FreshWorld can coexist without scheduling or counting the same empty replacement twice. Recognition checks relevant active behavior, not a fixed mod version or whole-DLL hash.

An unknown or incomplete Upgrade World save contract stops reset scans and bundle loading/target replacement. Normal world saves, F8, building rules, reports, dry runs and save-only archives remain available. Resolve the mod combination and restart before retrying; replacing DLLs while the game runs is unsupported.

If compatibility changes during an operation, later mutation batches stop and cleanup runs. Completed changes are not rolled back, pending reset records remain where applicable, and an already sent client terrain request may finish. Existing support grace remains active for partially restored structures.

## Older archives and terrain tools

Supported archives use manifest version 2 or 3 and gzip-compressed bundle payload version 3. Earlier formats, including uncompressed legacy bundles, are not loaded or converted. If the original world still exists, save a fresh archive from that world with the current version.

ZoneSavior no longer supplies terrain proxy prefabs, replays terrain operations or raises the game's terrain-height limit. Before updating a world or blueprint that uses old proxies, keep an external backup. While the previous ZoneSavior version is still installed, capture the final terrain with Infinity Hammer and remove old proxy entries from replacement blueprints. There is no automatic migration or cleanup; the game may discard unknown-prefab objects when their areas load.

Terrain saved beyond the normal height limit can look different unless every relevant client has a compatible separate height-limit mod. A terrain snapshot does not remove that requirement.

Infinity Hammer can save final terrain in a blueprint, and Expand World Data can place that blueprint as a location. InfinityHammerAddon is a separate client-side addon for Infinity Hammer's tools. ZoneSavior's SupportFill bundle restoration remains independent of those blueprint snapshots.
