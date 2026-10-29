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

For dedicated server coverage, select its original `valheim_server_Data/Managed` and Mono directories. `-BepInExCore` can select a different installed Harmony/BepInEx directory; the default is the local Valheim installation. Upgrade World and FreshWorld are required **test inputs**, not ZoneSavior runtime dependencies. The harness expects FreshWorld's reviewed empty-chunk implementation and compatibility checker.

The script compiles only the test host and probes into a unique Temp directory. It does not build/deploy the mod, create Release packages, start Unity, or invoke plugin `Awake`. It prints the loaded game and plugin SHA-256 values. Source reference DLLs are neither publicized nor overwritten. Logs and test binaries remain in the printed Temp directory; generated chunk fixtures are removed after verifying their paths.

Coverage:

- Empty dirty chunks at all four native sizes, far-edge occupancy, existing empty files, unsaved regions, existing replacements, parent/child overlap, retired and portal mappings, transient records, neighboring snapshots, world edges, and host checks.
- Failed-save dirty-state retry through the original `BeginSave`/`EndSave`/`UpdateSaveState`, without consuming live mapping metadata. This is **not disk failure injection**.
- Real Harmony discovery and private-field injection against the final ZoneSavior DLL.
- FreshWorld/ZoneSavior in both registration orders; all six Upgrade World/FreshWorld/ZoneSavior registration orders, with no duplicate file entries or counters.
- Late registration, partial hooks, mutation gate and normal save admission, runtime helper detours, related implementation/state changes, and irrelevant version/MVID changes.
- Two native chunk saves, mapping reload, and a backup copy for prior revisions 1, 2, and 7. A deliberately bypassed arbitration case reproduces the revision-one filename collision.
- Mutation admission and the shipped coroutine iterators: refusal before scene work, interruption after a completed deletion batch, pending-notification flush on failure/disposal, nested `finally` cleanup, and operation-token release with a failed response. Scene lookup, actual object destruction, and network flush use spies in these cases.

The original `ZDOMan` save selection, `ChunkSaveMapping`, and local chunk file I/O execute in the game's Mono runtime. Platform cloud checks and diagnostics are intercepted; Unity's persistent-data-path getter is redirected to Temp so `Utils` can initialize. No object/terrain/scene lifecycle or network transport is simulated as actual gameplay. Unity internal-call warnings may be printed while Harmony inspects methods whose original Unity-only body is suppressed.

Not covered: complete `ZNet.SaveWorldThread`, cloud storage, late cleanup I/O failures, power loss, simultaneous real clients, scene unload/reload, or real dedicated-server startup. The normal-save admission probe stops before entering Unity; successful chunk round trips do not establish full-world transaction atomicity.

The selection fixtures and external-compatibility cases were adapted from FreshWorld's `FreshWorld.Save.Tests` and `FreshWorld.Harmony.Tests`; they execute ZoneSavior's compiled implementation rather than a copied implementation or game stubs.

On 2026-10-29, 18 selection checks and 44 Harmony/compatibility/storage/mutation checks passed separately with original Valheim 1.0.21 client build 26249040 and dedicated-server build 26266951. Inputs were the merged ZoneSavior DLL (`B49AECDA9A81C4D886039ABBFB8690717DE98787F7642BCA581A7D39FB8A737B`), Upgrade World 1.87 (`7D2FEBA5E7222218FF4BAB809EBCBE00BC4F719DBB6DB644DB46CBF9CB5ADA77`), and the existing FreshWorld Debug DLL (`34D10E7B053261BEF50F484AECE97112FCD405EAA264EB469143D81ABCBF71A0`). These hashes identify the checked inputs; they are not runtime compatibility allowlists.

The 1.3.2 Release DLL (`DD2F5EEEE8AC53C11C630CC5F8ED0AAFA0175BC6F4B773BB62E37E12559C37D5`) passed the same 62 checks per role on 2026-10-29, using those original game builds and optional-mod inputs. The existing `Run-GameCompatibility.ps1` suite also passed against the Release DLL for both roles. This repeats the isolated coverage above, not live gameplay verification.
