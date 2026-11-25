# Empty chunk save regressions

Run `tests/Run-EmptyChunkChecks.ps1` against a **built, merged** ZoneSavior DLL and original game DLLs:

```powershell
./tests/Run-EmptyChunkChecks.ps1 `
  -GameManaged '<original snapshot>/original/valheim_Data/Managed' `
  -MonoRuntimeDirectory '<original snapshot>/original/MonoBleedingEdge/EmbedRuntime' `
  -ModDll './bin/ZoneSavior/Debug/ZoneSavior.dll' `
  -UpgradeWorldDll '<UpgradeWorld.dll>' `
  -FreshWorldDll '<FreshWorld.dll>'
```

For dedicated server coverage, select its original `valheim_server_Data/Managed` and Mono directories. Without `-GameManaged`, both this runner and `Run-GameCompatibility.ps1` resolve the project's `CorlibPath`; the Mono runtime defaults to the same snapshot. This keeps the checks on the build's original game references even when the locally installed game is older. `-BepInExCore` can select a different installed Harmony/BepInEx directory; the default is the local Valheim installation. Upgrade World and FreshWorld are required **test inputs**, not ZoneSavior runtime dependencies. The harness expects FreshWorld's reviewed empty-chunk implementation and compatibility checker.

The script compiles only the test host and probes into a unique Temp directory. It does not build/deploy the mod, create Release packages, start Unity, or invoke plugin `Awake`. It prints the loaded game and plugin SHA-256 values. Source reference DLLs are neither publicized nor overwritten. Logs and test binaries remain in the printed Temp directory; generated chunk fixtures are removed after verifying their paths.

Coverage:

- Empty dirty chunks at all four native sizes, far-edge occupancy, existing empty files, unsaved regions, existing replacements, parent/child overlap, retired and portal mappings, transient records, neighboring snapshots, world edges, and host checks.
- Failed-save dirty-state retry through the original `BeginSave`/`EndSave`/`UpdateSaveState`, without consuming live mapping metadata. This is **not disk failure injection**.
- Real Harmony discovery and private-field injection against the final ZoneSavior DLL.
- FreshWorld/ZoneSavior in both registration orders; all six Upgrade World/FreshWorld/ZoneSavior registration orders, with no duplicate file entries or counters.
- Late registration, partial hooks, mutation gate and normal save admission, runtime helper detours, related implementation/state changes, and irrelevant version/MVID changes.
- Two native chunk saves, mapping reload, and a backup copy for prior revisions 1, 2, and 7. A deliberately bypassed arbitration case reproduces the revision-one filename collision.
- Mutation admission and the shipped coroutine iterators: refusal before scene work, interruption after a completed deletion batch, pending-notification flush on failure/disposal, nested `finally` cleanup, and operation-token release with a failed response. Scene lookup, actual object destruction, and network flush use spies in these cases.

The original `ZDOMan` save selection, `ChunkSaveMapping`, and local chunk file I/O execute in the game's Mono runtime. Platform cloud checks and diagnostics are intercepted; Unity's persistent-data-path getter is redirected to Temp so `Utils` can initialize. In 1.0.30, `ManagedGameBoundary.InitializeZdo` temporarily handles exactly one identity-quaternion conversion in the original ZDO static initializer, then removes its own patch and checks the cached result. It does not replace the initializer, serializers, or arbitrary rotation math. No object/terrain/scene lifecycle or network transport is simulated as actual gameplay. Unity internal-call warnings may be printed while Harmony inspects methods whose original Unity-only body is suppressed.

Not covered: complete `ZNet.SaveWorldThread`, cloud storage, late cleanup I/O failures, power loss, simultaneous real clients, scene unload/reload, or real dedicated-server startup. The normal-save admission probe stops before entering Unity; successful chunk round trips do not establish full-world transaction atomicity.

The selection fixtures and external-compatibility cases were adapted from FreshWorld's `FreshWorld.Save.Tests` and `FreshWorld.Harmony.Tests`; they execute ZoneSavior's compiled implementation rather than a copied implementation or game stubs.

On 2026-10-29, 18 selection checks and 44 Harmony/compatibility/storage/mutation checks passed separately with original Valheim 1.0.21 client build 26249040 and dedicated-server build 26266951. Inputs were the merged ZoneSavior DLL (`B49AECDA9A81C4D886039ABBFB8690717DE98787F7642BCA581A7D39FB8A737B`), Upgrade World 1.87 (`7D2FEBA5E7222218FF4BAB809EBCBE00BC4F719DBB6DB644DB46CBF9CB5ADA77`), and the existing FreshWorld Debug DLL (`34D10E7B053261BEF50F484AECE97112FCD405EAA264EB469143D81ABCBF71A0`). These hashes identify the checked inputs; they are not runtime compatibility allowlists.

The 1.3.2 Release DLL (`DD2F5EEEE8AC53C11C630CC5F8ED0AAFA0175BC6F4B773BB62E37E12559C37D5`) passed the same 62 checks per role on 2026-10-29, using those original game builds and optional-mod inputs. The existing `Run-GameCompatibility.ps1` suite also passed against the Release DLL for both roles. This repeats the isolated coverage above, not live gameplay verification.

On 2026-11-25, the 1.0.30 compatibility Debug DLL (`C1563395D504DBF69C750446A401313F2A5D765FCE1A36E2CFC216D4F446B006`, mod version still 1.3.3) passed all 62 checks in each of four combinations: original client build 26870846 / dedicated-server build 26870897, each with Upgrade World 1.87 / 1.88. Each run also used FreshWorld 1.2.2. Upgrade World inputs were `7D2FEBA5E7222218FF4BAB809EBCBE00BC4F719DBB6DB644DB46CBF9CB5ADA77` (1.87) and `F21F5DE8585454FCAA94341A127A52AB444A859302A63D015831F3E275088998` (1.88); FreshWorld was `51C6B91A38A8DD65BCFFEFCC21440D7C43087F13710FB7DE22ED06BEF641CDF0`. The runtime compatibility policy remains based on the relevant behavior contract, not these hashes or version numbers.

`Run-GameCompatibility.ps1` also passed separately for both 1.0.30 roles: 2,556 compatibility assertions (including resolution of 1,976 game/Unity IL references and 25 Harmony target contracts), 125 build-rule/localization, 247 structure/identity, 560 terrain-placement, 59 auto-archive, and 16 native terrain-save assertions. Added coverage exercises the indexed ZDO data views, independent byte buffers, existing v1 bundle payloads, ownership/revision preservation, and native TerrainComp v2 save/load with snow state. Native terrain Save/Load execute through the mod's cached delegate and original game code, with only the platform path supplied by the harness. Actual Unity terrain geometry, host/client transport, dedicated-server startup, and live world save/reconnect still require gameplay checks.

On 2026-11-26, the final 1.3.4 Release DLL (`B3EAF51C76AE090012B61D1E27C18A826D48D900D7E0C0031B2DDC4C6546AF0E`) passed the same general suite for both 1.0.30 roles and all 62 empty-chunk checks per role with Upgrade World 1.88 and FreshWorld 1.2.2. Both Release ZIPs were checked against their source files, including the merged DLL and English/Korean translations. This remains isolated validation, not actual game/server execution.
