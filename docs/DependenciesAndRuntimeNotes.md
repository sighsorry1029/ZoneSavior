# Homestead and ZoneSavior dependency and runtime notes

Last checked: 2026-05-02

## Runtime shape

Homestead is the player-facing construction mod. ZoneSavior is the server zone maintenance mod. ZoneSavior is safest on a dedicated server because the long-inactive archive scanner needs authoritative access to the world ZDO list, server-side save/reset, and scheduled execution. A listen server can run the same code when the host is the server, but it is less ideal because scans only run while the host client is online and world ownership/loading can be affected by the host session.

Homestead writes player blueprint data under:

```text
BepInEx/config/Homestead/Blueprints/
BepInEx/config/Homestead/ServerBlueprints/PlanGhosts/
BepInEx/config/Homestead/ServerBlueprints/Store/
```

ZoneSavior writes zone maintenance data under:

```text
BepInEx/config/ZoneSavior/ZoneBundles/
BepInEx/config/ZoneSavior/activity.yml
BepInEx/config/ZoneSavior/zones.yml
```

Archive tags and blueprint names are shared within the active BepInEx profile. Neither mod creates per-world subfolders.

Clients can install Homestead too. Client-side behavior is intentionally light:

- native WearNTear blueprints through the Jotunn-backed hammer `Homestead` tab
- Area Save includes WearNTear allowed by the Area Save creator policy, then carries a lifted preview at the aim point until it is saved or cleared
- blueprint placement keeps its yaw independent from player movement and rotates in 15 degree steps with the mouse wheel
- blueprint placement uses a temporary `piece_chest_wood_blueprint` plan chest: the chest is 8x4, absorbs matching materials into plan progress, previews ready pieces with normal visuals, and crouch-use confirms the final WearNTear spawn
- ZoneSavior clients can additionally show the current zone number and draw its 64m boundary on the ground

Normal players do not run archive/reset logic. Archive and restore commands are server/admin workflows.

## Return-player restore workflow

1. Returning player toggles Zone UI at the desired empty site and sends the zone number to an admin.
2. Admin checks recent archive tags with `zs_status`, or inspects `ZoneSavior/ZoneBundles`.
3. Admin restores a whole archived manifest shape with RCON/server console:

```text
zs_loadzone auto_p123456789_s76561198000000000_c001_x-10_z5 to (20,-3)
```

The target zone is treated as the lower-left/min X/min Z anchor of the saved manifest. Non-rectangular clusters keep their original shape because `zs_loadzone` loads the manifest's explicit zone list.

Zone bundle terrain handling is SupportFill-only. `zs_savezone` and `zs_loadzone` do not accept a `--terrain` mode override.

SupportFill saves terrain contact samples instead of treating every WearNTear footprint as needing terrain support. During zone save, ZoneSavior samples each 1m x/z footprint cell, finds the lowest WearNTear bottom there, and records the cell only when the current terrain height is within the relevant contact tolerance. During load, only those saved contact cells can raise/cut terrain, with `Zone Bundle Support Fill Feather Width` blending zone bundle/archive terrain.

## Auto archive naming

Automatic archive tags use the representative creator name or Steam ID, cluster order, and a collision suffix when needed. The save timestamp is left to the filesystem metadata and the activity run record:

```text
auto_{owner}_cNNN
```

For example, a connected cluster owned by `halla` as the first processed cluster becomes:

```text
auto_halla_c001
```

The files are stored under:

```text
BepInEx/config/ZoneSavior/ZoneBundles/auto_halla_c001/
```

with a manifest plus one bundle file per saved source zone:

```text
manifest.yml
bundle001_<generation>.zonebundle.yml.gz
bundle002_<generation>.zonebundle.yml.gz
```

Each bundle is compact YAML compressed independently with gzip. ZoneSavior opens it in memory, so RCON and in-game commands use the same syntax without manual extraction.

If a connected inactive cluster has multiple creators, ZoneSavior uses the representative owner plus a compact multi-owner suffix, for example `auto_Snack_plus1_b7a5018f_c103`. The manifest and each zone bundle also list creator playerIDs so admins can inspect or split mixed-owner archives later. If the same auto archive tag already exists, ZoneSavior appends a collision suffix like `_n002` instead of overwriting the old bundle.

## Hard dependencies

Homestead has a hard runtime dependency on Jotunn. Jotunn owns the custom hammer `Homestead` tab/category plumbing and is used to render generated snapshot icons for saved native blueprints.

ZoneSavior no longer directly depends on Server Devcommands, World Edit Commands, Infinity Hammer, or Upgrade World. The zone bundle system uses ZoneSavior's own `zs-zdo-v1` ZDO data format for new saves.

`ServerSync.dll` and `YamlDotNet.dll` are bundled into both mod DLLs by ILRepack, so they are not Thunderstore runtime dependencies.

## Optional / operational companion

| Mod | Local BepInPlugin version observed | Role |
| --- | --- | --- |
| Upgrade World | 1.79 in the local DLL | Not directly referenced by ZoneSavior. It is conceptually related because it has zone reset/generate/restore commands and is useful as an admin comparison tool. |

## Thunderstore manifest

Thunderstore dependencies use the `{team}-{package}-{version}` format documented by Thunderstore. The current manifest lists:

```json
[
  "denikson-BepInExPack_Valheim-5.4.2202",
  "ValheimModding-Jotunn-2.29.0"
]
```

All previous manifest and bundle versions are intentionally not loaded or converted, including uncompressed bundles and bundles saved with the old World Edit Commands `DataEntry` payload. If the original world data still exists, create a new archive from that live world with the current ZoneSavior version to get the current compressed format and `zs-zdo-v1` payloads.

## Referenced mod summaries

Server Devcommands enables dev/admin commands on servers and clients, improves autocomplete, supports aliases/binds, and lets server admins execute/permission commands more comfortably. ZoneSavior no longer calls it directly.

World Edit Commands adds advanced world editing commands, object spawning/editing, data/ZDO editing helpers, terrain commands, undo helpers, and serialization helpers. ZoneSavior no longer calls it directly.

Infinity Hammer is an advanced building/admin tool: unrestricted building, selecting/copying objects, blueprints, placement manipulation, scaling, repairing/removing, and terrain/tool helpers. ZoneSavior no longer calls it directly; old Infinity Hammer-backed terrain payloads must be re-saved with the current ZoneSavior terrain format.

Upgrade World is a world maintenance tool for already explored areas: zone reset/generation/restore, object count/remove/edit, location reset/add/remove, chest reset, vegetation operations, and world upgrade workflows. ZoneSavior does not call it directly.

Jotunn is a Valheim modding library that provides managers and helpers for custom pieces, piece-table categories, prefabs, localization, assets, GUI hooks, and snapshot rendering. Homestead now uses it directly for the hammer blueprint UI instead of manually expanding the vanilla build menu.

## Sources checked

- Local DLLs in the `secondaryattacks` Gale profile, using BepInPlugin attributes and class lists.
- Thunderstore dependency format: https://wiki.thunderstore.io/mods/creating-a-package
- Server Devcommands: https://thunderstore.io/c/valheim/p/JereKuusela/Server_devcommands/
- World Edit Commands: https://thunderstore.io/c/valheim/p/JereKuusela/World_Edit_Commands/ and https://github.com/JereKuusela/valheim-world_edit_commands
- Infinity Hammer: https://thunderstore.io/c/valheim/p/JereKuusela/Infinity_Hammer/
- Upgrade World: https://thunderstore.io/c/valheim/p/JereKuusela/Upgrade_World/
- Jotunn: https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/
