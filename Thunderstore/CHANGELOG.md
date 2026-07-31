# Changelog

## 1.2.3

- Breaking: Previous manifest and bundle versions are no longer loaded or converted; `zones.yml` now requires `version: 1` and an explicit non-negative `limit` for every rule.
- Added strict save/load/restore limits, complete preflight validation, and safer coroutine, RPC, and archive failure cleanup.
- Simplified auto-archive, terrain-tool, and bundle internals by removing redundant wrappers and co-locating code that changes together.

## 1.2.2

- Improved SupportFill contact capture with actual collider and mesh support points, preventing invalid lower overlaps from hiding valid terrain contacts.
- Moved the generic creature-removal tracker, status-effect teardown guard, and `Character.OnDestroy` repair to the standalone InteropFixes mod.
- Kept stale Character and HUD cleanup local to completed zone bundle loads.

## 1.2.1

- Added destructive-load preflight validation and generation-based atomic zone bundle commits.
- Made activity reloads fully validated and atomic.
- Reduced terrain witness payloads, repeated bundle scans, and stale recipe-index risk.

## 1.2.0

- Added strict zone bundle preflight validation, safe tag paths, and atomic activity/archive commits.
- Reduced terrain witness RPC payloads and hardened RPC sender/session handling.
- Simplified terrain and archive internals; older zone bundle files must be re-saved.

## 1.1.0

- Added runtime support grace for zones loaded with `zs_loadzone`, with Zone UI countdown display.
- Improved zone bundle restore stability for tamed animals and missing prefab entries.

## 1.0.10

- Allowed RCON and dedicated server zone loads/restores to use nearby ZoneSavior clients as terrain witnesses when the server has not loaded the target terrain.

## 1.0.9

- Removed admin terrain tool pieces from build tables when the player is not in the allowed admin/debug state.

## 1.0.8

- Added safer Expand World Data and TerrainMistile compatibility for ZoneSavior terrain proxies.
- Cleaned watcher reload handling, terrain reset placement, and zone bundle capture/load internals.

## 1.0.7

- Kept admin terrain proxy prefabs alive across scene/world reloads.
- Sanitized dead piece table references before recipe refreshes to prevent Jotunn/Unity null-reference errors.

## 1.0.6

- Added a safe text-input visibility fallback so the zone boundary overlay no longer logs repeated errors when another mod patches `TextInput.IsVisible` before its UI is ready.

## 1.0.5

- Fixed admin terrain tools on dedicated servers with ServerDevcommands.
- Reduced optional Infinity Hammer and WorldEditCommands compatibility noise.
- Registered terrain tool pieces with VeiledRecipes when available.

## 1.0.4

- Cleaned release packaging and synced package version from the DLL assembly version.

## 1.0.3

- Added terrain proxies that can be saved in blueprints.
- Refactored internals and cleaned up configuration.

## 1.0.2

- Fixed `zs_loadarchive` not working on dedicated servers.

## 1.0.1

- Fixed ZoneSavior commands not working for admins on dedicated servers.

## 1.0.0

- Initial release.
