# Changelog

## 1.3.0

- Automatic archives now include connected mixed-owner zones that share an eligible inactive creator, keeping structures together across zone boundaries. For adjacent `[A+B] [B+C] [C]` zones with A/B inactive and C active, one archive saves the first two zones and resets only `[A+B]`. This is the default policy; no separate aggressive setting is needed.
- Keep active/protected creators from expanding the archive into unrelated zones. Minimum cluster size counts only eligible owners' pieces; mixed-owner clusters below that minimum are skipped instead of reset without a backup.
- Record planned, completed and pending resets separately in automatic manifest version 3, together with the source world UID. Original-location restore of a reset archive only restores successfully reset zones; save-only archives restore their eligible core. Preserved mixed zones are not overwritten, including through single-zone and relocation commands aimed at their source coordinates.
- Recheck saved object contents before and during reset; object changes, container-content changes and unexplained movement/removal stop the affected reset. Normal inactivity scans also recheck eligibility and stop on reconnects; explicit Steam ID targeting retains its administrator override of owner inactivity/protection. Validate each captured object against the pre-save snapshot before allowing deletion.
- Stop reset and automatic-archive target clearing when a Spawned-object destruction chain crosses the allowed zone boundary. Write reset intent immediately before the first mutation; incomplete resets block loading until their partial world state has been reviewed.
- Add separate saved-only, planned reset, completed reset and skipped reset scopes to archive reports and mixed-zone eligibility diagnostics.
- Keep manual manifest version 2 and existing bundle version 3 files readable. New automatic manifest version 3 files require ZoneSavior 1.3.0 or later. Full relocation includes the saved mixed-zone objects and contents while leaving the retained source zone in place.
- Require BepInExPack Valheim 5.4.2351. Build against original Valheim 1.0.16 game assemblies with the pinned ServerSync compatibility build and the existing config/RPC contracts.
- Add automated archive planning, manifest, restore-target and snapshot-change regression coverage to the Unity Mono compatibility suite. Original client and dedicated-server DLL checks are separate from live gameplay validation; boundary support collapse and multiplayer world mutation still require in-game testing.

## 1.2.11

- Target Valheim 1.0.7 using original game assemblies and cached access to required private members.
- Update embedded ServerSync for the new RPC constant and login message ordering; require BepInExPack Valheim 5.4.2350.
- Preserve typed ZDO snapshots through the new GetData API, include the separate portal store in scans and zone queries, and prevent out-of-range coordinates from wrapping onto another zone.
- Adapt terrain save/rebuild calls and console command registration while preserving existing placement, config, bundle and RPC formats.

## 1.2.10

- New zone bundle saves record one valid bottom-contact height per piece and the deepest negative offset from the original base terrain. Relocation selects the most common 0.25m contact-height band, aligns its first contact with destination base terrain, then applies the saved negative offset. All loaded zones share one placement height; manual offsets remain additive.
- Preserved overlapping contact-height bands so stacked pieces do not hide the representative floor's first contact. Terrain support still uses the lowest contact in each cell and retains the existing +/-8m terrain limit.
- Kept existing version 3 bundles readable with their previous placement behavior when the new metadata is missing. Re-save live structures to use the new placement rule. Original-location restore keeps the saved coordinates.
- Extended the F8 zone outline with eight terrain-following, 64m corner extensions to make neighboring zone boundaries easier to estimate.
- Simplified archive merging and zone bundle command flow, reduced support-grace cleanup allocations, and avoided redundant zone HUD text updates.
- Added terrain-placement and serialization regression coverage, plus opt-in Debug deployment through `DeployToGame=true` with SHA-256 verification after DLL merging.

## 1.2.9

- Breaking: Removed all ZoneSavior terrain proxy tools and prefab registrations, with no legacy aliases, replay support, automatic conversion, or world cleanup. Back up existing worlds and replace proxy-based blueprints with final-terrain snapshots captured using Infinity Hammer before upgrading.
- Removed proxy ordering metadata, replay batches, executor assignment, terrain-commit RPCs, checkpoints, and proxy-specific Infinity Hammer, Expand World Data, TerrainMistile, and VeiledRecipes integration.
- Removed the terrain-tool config section and the global terrain height-limit extension. Existing terrain beyond the game's normal limit needs a compatible separate height-limit mod on every relevant client to retain its previous appearance.
- Terrain tool improvements are provided separately by the client-side InfinityHammerAddon, using Infinity Hammer's existing buttons instead of persistent proxy prefabs.
- Kept zone bundle SupportFill terrain restoration, archiving, and zone maintenance unchanged.

## 1.2.8

- Breaking: Terrain proxy data now requires the new ordered-replay format; proxies and blueprints saved by earlier ZoneSavior versions are intentionally not replayed. Re-place the proxies and re-save affected blueprints.
- Added deterministic terrain-proxy batches for direct placement, Infinity Hammer blueprint placement, and Expand World Data blueprint locations. Overlapping height, slope, and paint proxies are replayed in their original canonical application order after the complete batch is registered.
- Added a server-coordinated single-executor terrain controller for multiplayer and dedicated servers. It validates proxy and terrain manifests and revisions, commits canonical multi-zone `_TerrainCompiler` data with retry checkpoints, and prevents concurrent proxy resets or zone-bundle loads from racing the active batch.
- Reduced the initial visible placement delay by probing newly loaded replay terrain every frame during the first second, while retaining slower retries for terrain streaming and commit replication.
- Hardened Infinity Hammer and Expand World Data integration so failed or incomplete blueprint placement cannot partially replay ZoneSavior proxies; Expand World Data ghost locations remain deferred until a player streams their terrain.
- Terrain replay now rejects client-prepared results unless they exactly match the server-recomputed ZoneSavior proxy transition. Legacy or third-party terrain modifiers affecting the same heightmap can therefore make a batch fail closed.
- Terrain batches fail closed: if the assigned executor disconnects before completion, restart the server world session before retrying.

## 1.2.7

- Added an optional read-only admin RPC that resolves one creator player ID to its last known name from server activity data.
- Kept integrations soft through a versioned named RPC with no direct assembly dependency and no bulk activity-registry transfer.
- Bounded lookups to one ID per request and added server, admin, sender, world, payload, and rate-limit validation.

## 1.2.6

- Terrain Reset, Paint Proxy, and Paint Only Reset range outlines now follow the loaded terrain surface; Terrain Proxy retains its flat target-height outline.
- Reset preflight now detects known intersecting proxy ZDOs whose live objects are not loaded and aborts without changing terrain.
- Paint grid preview now falls back to the range outline while a required heightmap is still loading.

## 1.2.5

- Breaking: Removed the `ZoneSaviorPaintReset` prefab without a legacy alias; `ZoneSaviorTerrainReset` is now the single reset tool and defaults to resetting both terrain height and paint.
- Added a terrain-tool modifier-key toggle (Alt by default) for Paint Only Reset mode, including a current-mode tooltip, TopLeft notification, and exact affected-node grid preview.
- Reset mode returns to Terrain + Paint when the reset tool is deselected or the world is left.
- Reset now preflights loaded terrain coverage, known intersecting proxy footprints, and ownership before changing terrain or removing proxies, preventing partial mutation when a known requirement is unavailable.

## 1.2.4

- Added an exact affected-node grid preview and circular range outline for directly placed Paint Proxy tools, with an outline-only fallback for very large ranges.
- Fixed deferred placement ghosts and early location spawning so proxy ZDOs unregister safely and terrain application retries after heightmaps become ready.
- Fixed terrain-only proxy resets clearing unrelated terrain paint.

## 1.2.3

- Breaking: Previous manifest and bundle versions are no longer loaded or converted; every `zones.yml` rule now requires an explicit non-negative `limit`.
- Removed `zones.yml` version handling; the current schema is parsed directly and existing version fields are ignored.
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
