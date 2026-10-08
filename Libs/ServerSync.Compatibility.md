# ServerSync vendored baseline

- Selected baseline: `valheim-1.0.7-r1`, assembly identity `ServerSync, Version=1.0.0.0`, file version `1.0.0.1`.
- DLL SHA-256: `b4dd786997f4e90d770f09ef3e9d64154754fe7e8edfb4841795751895b35846`.
- Source/build/manifest: `C:/Users/blizz/.codex/references/valheim/integrations/serversync/versions/valheim-1.0.7-r1/`.
- Upstream: https://github.com/blaxxun-boop/ServerSync/tree/c57c2aa54e07cdcc7630d6068699ea781622323e (MIT-0; see `ServerSync.LICENSE.txt`).
- Previous ZoneSavior DLL SHA-256: `166956302a294e224474b26f4c7d58409084ad3f48bd0af1feb7551f229c8f60`, identical to the central baseline input; no independent ZoneSavior changes were present.

The source build uses original Valheim 1.0.7 DLLs, emits constants instead of old `ZRoutedRpc.Everybody` storage reads, uses the equivalent public administrator API, and buffers login-dependent player/history/admin lists in order. The central comparison preserves all 73 public/protected API entries and the synchronization wire format. ZoneSavior's GUID, keys, custom value IDs, version requirements and event handling are unchanged.

Both compile reference and ILRepack input use this repository's pinned DLL. Builds do not fetch a global latest binary, rewrite game DLLs, or install ServerSync separately. No per-build binary patch is needed.

The central library's static and isolated socket tests are evidence about the library, not proof of a live ZoneSavior multiplayer session. Run `tests/Run-GameCompatibility.ps1` on the final merged ZoneSavior DLL; real host/dedicated/Steam/PlayFab reconnect, settings and authorization still need game testing.
