# ZoneSavior

Archive inactive players' buildings and tamed animals, then restore them in place or move them to a new zone. Set local building limits, block selected pieces, and let every player see zone boundaries.


![A saved base relocated to different terrain](https://i.ibb.co/ycMvZ9fj/Video-Project-26.gif)

*Move a saved base to a new zone.*

![An archived settlement restored inside zone boundaries](https://i.ibb.co/G3czYNtr/zonearchive.gif)

*Bring an archived settlement back into the world.*

## Getting started

Install the same ZoneSavior version on the **server or host and all clients**, with BepInExPack Valheim. For manual installation, place `ZoneSavior.dll` in `BepInEx/plugins`.

Start the game or server once to create:

- `BepInEx/config/sighsorry.ZoneSavior.cfg` — settings and automatic archive options.
- `BepInEx/config/ZoneSavior/zones.yml` — building rules and archive protection.
- `BepInEx/config/ZoneSavior/ZoneBundles/` — saved archives.

Press **F8** to show your zone number and boundary. Anyone can use it; admin access is not required. Eight 64m extensions help you judge neighboring zones.

Building messages and the zone HUD support **English and Korean**, with English fallback. Editable translation files are included with the mod.

## Archive and restore

Run management commands from the server console, RCON, or a server admin client. **Back up your world before resetting or loading: loading replaces existing objects in the destination zones.**

| Task | Command |
| --- | --- |
| Preview inactive-player cleanup | `zs_scan dry` |
| View the latest scan report | `zs_status` |
| Archive without resetting | `zs_scan save` |
| Archive and reset eligible zones | `zs_scan reset` |
| Save one zone | `zs_savezone (-21,-4) my_base` |
| Load at a new destination | `zs_loadzone my_base to (10,3)` |
| Restore at the original location | `zs_loadzone my_base restore` |

Automatic scans are **off by default**. To schedule them, set `Scan Interval Minutes` above `0`. Start with `Dry Run = On`; review the report before changing `Dry Run` or enabling `Reset After Save`. The default inactivity threshold is 30 days. Use `zones.yml` to protect players or zones from normal cleanup.

Connected mixed-owner zones are saved so buildings crossing a boundary stay together, but active players' zones are kept. For `[A+B] [B+C] [C]`, with A/B inactive and C active, the first two zones are archived and only `[A+B]` is reset.

- Original-location restore of an automatic reset archive restores only zones recorded as reset.
- If a supported building or tamed animal is missing from the bundle, `WarnAndContinue` (default) allows the reset. That bundle cannot restore the missing object or its contents. Choose `Missing Bundle Entry Policy = BlockReset` to keep the entire cluster. In both modes, the server log points to a per-scan YAML report in `ZoneSavior/Diagnostics/` with the affected prefabs and counts.
- Relocating loads the whole saved archive, including mixed-zone containers and animals. Retained source objects remain, so relocation creates copies.
- During reset runs, fully eligible clusters below `Minimum Pieces Per Cluster` are **reset without an archive**. Small mixed-owner clusters are skipped.
- An interrupted reset can block loading that archive until its partial state is reviewed. There is no automatic rollback.

## Building rules

Edit `zones.yml` using zone coordinates, not world meters. Enable **`Zone WearNTear Limit`** in the config to enforce quantity limits; it is off by default. Piece bans work independently of this toggle.

```yaml
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

Ranges include both endpoints. **The first matching rule wins**, and each limit applies to one zone. Every rule needs a non-negative `limit`. `blocked_pieces` uses exact, case-sensitive prefab names; omitted lists allow all piece types. Zones with no matching rule are unrestricted.

Banned pieces are rejected before normal placement spends resources. Quantity limits count loaded structures and remove over-limit placements afterward; they do not reserve capacity for simultaneous builders. These restrictions need the mod on clients and do not cover other mods that bypass normal placement. Existing buildings and admin bundle restores are unaffected by piece bans.

## Terrain support

Bundles capture ground contacts to restore support under buildings. Keep source terrain loaded for accurate capture; relocation adjusts the archive's shared height, and `offset=Y` adds a manual adjustment. Different slopes can still leave buildings buried or unsupported because the game's normal ±8m terrain limit applies.

Restored structures receive temporary support grace, configurable with `Zone Bundle Support Grace Minutes`; this protection ends on server restart. On a dedicated server, if destination terrain is unavailable, move a player with ZoneSavior near it and retry.

## Compatibility

- **Upgrade World and FreshWorld are optional.** Empty-chunk save handling is coordinated to avoid duplicate processing. This prevents deleted objects returning from omitted empty chunks; it does not repair existing duplicates.
- An unrecognized Upgrade World save setup stops resets and bundle loading. Normal world saves, F8, building rules and save-only archives remain available. Resolve the mod combination and restart before retrying.
- Manifest versions **2/3** and compressed bundle version **3** are supported. Older formats are not converted; re-save from the original live world when available.
- ZoneSavior does not raise the game's terrain height limit. Old worlds with changes beyond that limit need a separate compatible height-limit mod on relevant clients.

## Detailed guide

See [Commands and configuration](https://github.com/sighsorry1029/ZoneSavior/blob/main/docs/ZoneSaviorCommands.md) for ranges, targeted scans, protection rules, restore behavior, terrain placement and translation overrides.
