using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private const int MaxReplayOperations = 4096;
    private const int MaxReplayTerrainCompilers = 64;
    private const int MaxReplayTerrainPayloadBytes = 2 * 1024 * 1024;
    private const int MaxReplayCommitPackageBytes = 16 * 1024 * 1024;
    private const int ReplayRpcVersion = 2;
    private const float ReplayFastReadinessProbeSeconds = 1f;
    private const float ReplayRequestRetrySeconds = 1f;
    private const float ReplayCommitRetrySeconds = 2f;
    private const float ReplayCompletionReleaseTimeoutSeconds = 30f;
    private const float ReplayStateBroadcastSeconds = 5f;
    private const string ReplayRequestRpcName = ZoneSaviorPlugin.ModGUID + "_TerrainReplayRequest";
    private const string ReplayResponseRpcName = ZoneSaviorPlugin.ModGUID + "_TerrainReplayResponse";

    private static readonly int ReplayBatchHash = Hash("replay_batch");
    private static readonly int ReplayBatchCountHash = Hash("replay_batch_count");
    private static readonly int ReplayBatchIndexHash = Hash("replay_batch_index");
    private static readonly int TerrainCompilerPrefabHash =
        StringExtensionMethods.GetStableHashCode("_TerrainCompiler");
    private static readonly ZoneRpcRegistrar ReplayRpcRegistrar = new();

    private static long _lastIssuedApplyOrder;
    private static long _lastReplayRequestId;
    private static int _blueprintReplayDepth;
    private static int _nextBlueprintReplaySequence;
    private static bool _blueprintReplayFailed;
    private static ZDOMan? _replayControllerWorld;
    private static long _announcedActiveReplayBatch;
    private static bool _announcedReplayControllerLocked;
    private static float _nextReplayStateBroadcastTime;
    private static ServerReplayLease? _serverReplayLease;
    private static ServerReplayCommitReceipt? _lastServerReplayReceipt;
    private static readonly Dictionary<ZDOID, BlueprintReplayEntry> BlueprintReplayEntries = [];
    private static readonly Dictionary<long, ClientReplayState> ClientReplayStates = [];

    internal static bool IsTerrainMutationControllerBusy =>
        _announcedActiveReplayBatch > 0L || _serverReplayLease != null;

    private static void ResetBlueprintReplayState()
    {
        _lastIssuedApplyOrder = 0L;
        _lastReplayRequestId = 0L;
        _blueprintReplayDepth = 0;
        _nextBlueprintReplaySequence = 0;
        _blueprintReplayFailed = false;
        BlueprintReplayEntries.Clear();
        ResetReplayControllerRuntime(null);
    }

    internal static void UpdateTerrainReplayController()
    {
        bool registered = ReplayRpcRegistrar.EnsureRegistered(routedRpc =>
        {
            routedRpc.Register<ZPackage>(ReplayRequestRpcName, RPC_HandleReplayRequest);
            routedRpc.Register<ZPackage>(ReplayResponseRpcName, RPC_HandleReplayResponse);
        });

        ZDOMan? world = ZDOMan.instance;
        if (registered || !ReferenceEquals(_replayControllerWorld, world))
        {
            ResetReplayControllerRuntime(world);
        }

        foreach (ClientReplayState state in ClientReplayStates.Values.ToList())
        {
            TryFinishReplicatedReplayCommit(state);
            DriveLoadedReplayOperation(state);
        }

        if (world == null || ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        ServerReplayLease? lease = _serverReplayLease;
        if (lease == null)
        {
            return;
        }

        if (Time.realtimeSinceStartup >= _nextReplayStateBroadcastTime)
        {
            _nextReplayStateBroadcastTime = Time.realtimeSinceStartup + ReplayStateBroadcastSeconds;
            BroadcastReplayControllerState(
                lease.Locked
                    ? ReplayResponseStatus.ControllerLocked
                    : ReplayResponseStatus.ControllerActive,
                lease.BatchId,
                lease.ExecutorPeer);
        }

        if (lease.Locked)
        {
            return;
        }

        if (lease.Completed)
        {
            // All canonical writes and completion markers are already durable. Release normally
            // arrives after the executor observes replication; disconnect or a lost release RPC
            // must not strand a finished global batch forever.
            if (!IsReplayExecutorConnected(lease.ExecutorPeer) ||
                Time.realtimeSinceStartup >= lease.CompletedAt + ReplayCompletionReleaseTimeoutSeconds)
            {
                ReleaseReplayController();
            }

            return;
        }

        if (IsReplayExecutorConnected(lease.ExecutorPeer))
        {
            return;
        }

        lease.Locked = true;
        _announcedReplayControllerLocked = true;
        _logger?.LogWarning(
            $"ZoneSavior terrain batch {lease.BatchId} was locked after its executor disconnected; " +
            "automatic handoff is disabled until the server world session restarts.");

        BroadcastReplayControllerState(ReplayResponseStatus.ControllerLocked, lease.BatchId, lease.ExecutorPeer);
    }

    private static void ResetReplayControllerRuntime(ZDOMan? world)
    {
        _replayControllerWorld = world;
        _announcedActiveReplayBatch = 0L;
        _announcedReplayControllerLocked = false;
        _nextReplayStateBroadcastTime = 0f;
        _serverReplayLease = null;
        _lastServerReplayReceipt = null;
        ClientReplayStates.Clear();
    }

    internal static void BeginBlueprintReplayBatch()
    {
        if (_blueprintReplayDepth++ == 0)
        {
            BlueprintReplayEntries.Clear();
            _nextBlueprintReplaySequence = 0;
            _blueprintReplayFailed = false;
        }
    }

    private static bool TryDeferBlueprintReplayProxy(ZoneSaviorTerrainProxy proxy)
    {
        if (_blueprintReplayDepth <= 0)
        {
            return false;
        }

        ZNetView nview = proxy.GetComponent<ZNetView>();
        ZDO zdo = nview ? nview.GetZDO() : null!;
        if (zdo != null)
        {
            RegisterBlueprintReplayProxy(
                proxy,
                zdo,
                liveProxy: !ZNetView.m_ghostInit,
                requiresCompletedSource: true);
        }

        // PostProcessPlaced/CleanGhostInit registers the final ZDO state again.
        return true;
    }

    internal static void CompleteBlueprintReplayBatch(bool succeeded)
    {
        if (_blueprintReplayDepth <= 0)
        {
            _blueprintReplayDepth = 0;
            BlueprintReplayEntries.Clear();
            return;
        }

        if (!succeeded)
        {
            _blueprintReplayFailed = true;
        }

        _blueprintReplayDepth--;
        if (_blueprintReplayDepth > 0)
        {
            return;
        }

        try
        {
            if (_blueprintReplayFailed)
            {
                InvalidateBlueprintReplayBatch();
                _logger?.LogWarning(
                    "ZoneSavior disabled an incomplete terrain replay batch after the placement operation failed.");
            }
            else
            {
                FinalizeBlueprintReplayBatch();
            }
        }
        catch (Exception ex)
        {
            InvalidateBlueprintReplayBatch();
            _logger?.LogWarning($"ZoneSavior terrain replay batch finalization failed: {ex}");
        }
        finally
        {
            BlueprintReplayEntries.Clear();
            _nextBlueprintReplaySequence = 0;
            _blueprintReplayFailed = false;
        }
    }

    internal static bool FinalizeBlueprintReplayProxyRegistration(
        ZoneSaviorTerrainProxy? proxy,
        ZDO zdo,
        bool liveProxy,
        bool requiresCompletedSource = true)
    {
        if (_blueprintReplayDepth <= 0)
        {
            return false;
        }

        if (!RegisterBlueprintReplayProxy(proxy, zdo, liveProxy, requiresCompletedSource))
        {
            _blueprintReplayFailed = true;
            InvalidateBlueprintReplayProxy(zdo);
            _logger?.LogWarning(
                $"ZoneSavior terrain proxy {zdo.m_uid} has incomplete or unsupported replay data; " +
                "the terrain replay batch was disabled.");
        }

        return true;
    }

    internal static void FailBlueprintReplayBatch()
    {
        if (_blueprintReplayDepth > 0)
        {
            _blueprintReplayFailed = true;
        }
    }

    private static bool RegisterBlueprintReplayProxy(
        ZoneSaviorTerrainProxy? proxy,
        ZDO zdo,
        bool liveProxy,
        bool requiresCompletedSource)
    {
        if (_blueprintReplayDepth <= 0 ||
            zdo == null ||
            !zdo.IsValid() ||
            !HasStoredSettings(zdo))
        {
            return false;
        }

        long sourceApplyOrder = zdo.GetLong(ApplyOrderHash, 0L);
        if (!IsValidApplyOrder(sourceApplyOrder))
        {
            _logger?.LogWarning(
                $"ZoneSavior terrain proxy {zdo.m_uid} has no valid apply order; refusing an unordered blueprint replay.");
            return false;
        }

        ObserveApplyOrder(sourceApplyOrder);
        if (BlueprintReplayEntries.TryGetValue(zdo.m_uid, out BlueprintReplayEntry existing))
        {
            existing.SourceApplyOrder = sourceApplyOrder;
            existing.RequiresCompletedSource &= requiresCompletedSource;
            // A later ghost registration must discard the temporary ghost GameObject reference.
            existing.Proxy = liveProxy && proxy ? proxy : null;
            return true;
        }

        BlueprintReplayEntries.Add(
            zdo.m_uid,
            new BlueprintReplayEntry(
                zdo,
                liveProxy && proxy ? proxy : null,
                sourceApplyOrder,
                _nextBlueprintReplaySequence++,
                requiresCompletedSource));
        return true;
    }

    private static void FinalizeBlueprintReplayBatch()
    {
        List<BlueprintReplayEntry> entries = BlueprintReplayEntries.Values.ToList();
        if (entries.Count == 0)
        {
            return;
        }

        if (entries.Any(entry => !entry.Zdo.IsValid()))
        {
            throw new InvalidOperationException("a registered terrain replay operation became unavailable");
        }

        if (entries.Count > MaxReplayOperations)
        {
            throw new InvalidOperationException(
                $"terrain replay batch has {entries.Count} operations; " +
                $"the supported maximum is {MaxReplayOperations}");
        }

        // Repeated copies of one source proxy legitimately share an order. Registration sequence
        // supplies the stable occurrence order within this placement batch.
        entries.Sort(CompareBlueprintSourceOrder);
        if (entries.Any(entry =>
                entry.RequiresCompletedSource &&
                (entry.Zdo.GetBool(ApplyOrderPendingHash, false) ||
                 !HasCompletedReplaySource(entry.Zdo))))
        {
            throw new InvalidOperationException(
                "terrain replay source contains an operation that has not finished applying");
        }

        long replayBatch = NextReplayNonce();
        for (int index = 0; index < entries.Count; index++)
        {
            BlueprintReplayEntry entry = entries[index];
            entry.Zdo.Set(ApplyOrderHash, NextApplyOrder());
            // The provisional value preserves source ordering while the batch is assembled.
            // Each server commit replaces it with the actual canonical execution order.
            entry.Zdo.Set(ApplyOrderPendingHash, true);
            entry.Zdo.Set(AppliedHash, false);
            SetReplayBatch(entry.Zdo, replayBatch, entries.Count, index);
        }

        _logger?.LogDebug(
            $"ZoneSavior collected {entries.Count} terrain proxy operation(s) into ordered batch {replayBatch}.");

        // The complete manifest is committed before any live operation asks the server controller
        // for a lease. Ghost batches remain inert until a player streams the generated location.
        foreach (BlueprintReplayEntry entry in entries)
        {
            if (entry.Proxy)
            {
                ApplyLoadedProxy(entry.Proxy);
            }
        }
    }

    private static int CompareBlueprintSourceOrder(BlueprintReplayEntry left, BlueprintReplayEntry right)
    {
        int order = left.SourceApplyOrder.CompareTo(right.SourceApplyOrder);
        return order != 0 ? order : left.RegistrationSequence.CompareTo(right.RegistrationSequence);
    }

    private static void AssignStandaloneReplayBatch(ZDO zdo)
    {
        SetReplayBatch(zdo, NextReplayNonce(), 1, 0);
    }

    private static void SetReplayBatch(ZDO zdo, long batchId, int count, int index)
    {
        ClearReplayBatch(zdo);
        zdo.Set(ReplayBatchHash, batchId);
        zdo.Set(ReplayBatchCountHash, count);
        zdo.Set(ReplayBatchIndexHash, index);
    }

    private static bool TryApplyReplayOperation(
        ZoneSaviorTerrainProxy proxy,
        ZDO zdo,
        long batchId,
        int batchCount,
        int batchIndex)
    {
        if (!IsReplayOperationMetadataValid(zdo, batchId, batchCount, batchIndex))
        {
            LogInvalidReplayMetadata(zdo, "invalid batch metadata");
            return true;
        }

        if (!Player.m_localPlayer ||
            ZNet.instance == null ||
            ZDOMan.instance == null ||
            ZRoutedRpc.instance == null)
        {
            return false;
        }

        ClientReplayState state = GetClientReplayState(batchId, batchCount);
        bool newlyLoaded =
            !state.LoadedOperations.TryGetValue(batchIndex, out ZoneSaviorTerrainProxy previousProxy) ||
            !previousProxy ||
            !ReferenceEquals(previousProxy, proxy);
        state.LoadedOperations[batchIndex] = proxy;
        if (newlyLoaded)
        {
            state.FastReadinessProbeUntil =
                Time.realtimeSinceStartup + ReplayFastReadinessProbeSeconds;
        }

        if (state.Invalid)
        {
            return true;
        }

        if (TryFinishReplicatedReplayCommit(state) && HasReplayOperationApplied(zdo))
        {
            return true;
        }

        if (state.Granted && ReplayTerrainManifestNeedsRefresh(state))
        {
            state.Granted = false;
            state.ExpectedTerrainCompilers.Clear();
            state.NextRequestTime = 0f;
        }

        if (!state.Granted)
        {
            if (Time.realtimeSinceStartup < state.NextRequestTime)
            {
                return false;
            }

            if ((state.ServerNextIndex < 0 || batchIndex == state.ServerNextIndex) &&
                IsReplayTerrainFootprintLoaded(proxy, zdo))
            {
                RequestReplayLease(state, batchIndex);
            }

            return false;
        }

        if (_announcedReplayControllerLocked)
        {
            return false;
        }

        if (state.PendingCommit != null)
        {
            RetryReplayCommitIfNeeded(state);
            return false;
        }

        if (batchIndex != state.ServerNextIndex)
        {
            return false;
        }

        if (!IsLocalReplaySourceSynchronized(zdo))
        {
            return false;
        }

        if (!TryPrepareReplayCommit(
                proxy,
                zdo,
                batchId,
                batchCount,
                batchIndex,
                state.ExpectedTerrainCompilers,
                out ReplayCommitProposal proposal,
                out string failureReason))
        {
            if (!string.IsNullOrEmpty(failureReason))
            {
                LogInvalidReplayMetadata(zdo, failureReason);
                InvalidateClientReplayState(state);
                return true;
            }

            return false;
        }

        proposal.RequestId = NextReplayRequestId();
        proposal.Token = state.Token;
        state.PendingCommit = proposal;
        SendReplayCommit(proposal);
        return false;
    }

    private static ClientReplayState GetClientReplayState(long batchId, int batchCount)
    {
        if (!ClientReplayStates.TryGetValue(batchId, out ClientReplayState state))
        {
            state = new ClientReplayState(batchId, batchCount);
            ClientReplayStates.Add(batchId, state);
        }
        else if (state.BatchCount != batchCount)
        {
            InvalidateClientReplayState(state);
        }

        return state;
    }

    private static void DriveLoadedReplayOperation(ClientReplayState state)
    {
        if (state.Invalid ||
            state.PendingCommit != null ||
            state.ServerNextIndex >= state.BatchCount ||
            state.LoadedOperations.Count == 0)
        {
            return;
        }

        // Before the first server response there is no canonical next index yet. Recheck the
        // earliest loaded operation every frame so a footprint that becomes ready just after
        // placement does not wait for the proxy coroutine's 0.5 second fallback tick.
        int candidateIndex = state.ServerNextIndex >= 0
            ? state.ServerNextIndex
            : state.LoadedOperations.Keys.Min();
        if (!state.LoadedOperations.TryGetValue(candidateIndex, out ZoneSaviorTerrainProxy proxy))
        {
            return;
        }

        if (!proxy)
        {
            state.LoadedOperations.Remove(candidateIndex);
            return;
        }

        if (Time.realtimeSinceStartup > state.FastReadinessProbeUntil)
        {
            return;
        }

        if (TryApplyLoadedProxy(proxy))
        {
            state.LoadedOperations.Remove(candidateIndex);
        }
    }

    private static void RequestReplayLease(ClientReplayState state, int candidateIndex)
    {
        if (state.Invalid ||
            Time.realtimeSinceStartup < state.NextRequestTime ||
            ZRoutedRpc.instance == null)
        {
            return;
        }

        state.PendingAcquireRequestId = NextReplayRequestId();
        state.NextRequestTime = Time.realtimeSinceStartup + ReplayRequestRetrySeconds;
        ZPackage package = new();
        package.Write(ReplayRpcVersion);
        package.Write((int)ReplayRequestKind.Acquire);
        package.Write(state.PendingAcquireRequestId);
        package.Write(state.BatchId);
        package.Write(state.BatchCount);
        package.Write(candidateIndex);
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            ReplayRequestRpcName,
            package);
    }

    private static void SendReplayCommit(ReplayCommitProposal proposal)
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        ZPackage package = new();
        package.Write(ReplayRpcVersion);
        package.Write((int)ReplayRequestKind.Commit);
        package.Write(proposal.RequestId);
        package.Write(proposal.BatchId);
        package.Write(proposal.BatchCount);
        package.Write(proposal.BatchIndex);
        package.Write(proposal.Token);
        package.Write(proposal.ProxyId);
        package.Write(proposal.ProxyDataRevision);
        package.Write(proposal.Terrain.Count);
        foreach (ReplayTerrainCommitEntry entry in proposal.Terrain)
        {
            package.Write(entry.ZdoId);
            package.Write(entry.BaseDataRevision);
            package.Write(entry.Data);
        }

        if (package.Size() > MaxReplayCommitPackageBytes)
        {
            InvalidateClientReplayState(ClientReplayStates[proposal.BatchId]);
            _logger?.LogWarning(
                $"ZoneSavior terrain batch {proposal.BatchId} operation {proposal.BatchIndex} " +
                "exceeded the safe commit package limit.");
            return;
        }

        proposal.NextSendTime = Time.realtimeSinceStartup + ReplayCommitRetrySeconds;
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            ReplayRequestRpcName,
            package);
    }

    private static void RetryReplayCommitIfNeeded(ClientReplayState state)
    {
        ReplayCommitProposal proposal = state.PendingCommit!;
        if (!proposal.Accepted && Time.realtimeSinceStartup >= proposal.NextSendTime)
        {
            SendReplayCommit(proposal);
        }
    }

    private static bool TryFinishReplicatedReplayCommit(ClientReplayState state)
    {
        ReplayCommitProposal? proposal = state.PendingCommit;
        ZDOMan? world = ZDOMan.instance;
        if (proposal == null || !proposal.Accepted || world == null)
        {
            return false;
        }

        ZDO source = world.GetZDO(proposal.ProxyId);
        if (source == null ||
            source.DataRevision < proposal.ExpectedProxyDataRevision ||
            !HasReplayOperationApplied(source))
        {
            return false;
        }

        foreach (ReplayExpectedRevision expected in proposal.ExpectedTerrainRevisions)
        {
            ZDO terrain = world.GetZDO(expected.ZdoId);
            ZNetView? terrainView = terrain != null ? ZNetScene.instance?.FindInstance(terrain) : null;
            TerrainComp? compiler = terrainView ? terrainView.GetComponent<TerrainComp>() : null;
            if (terrain == null ||
                terrain.DataRevision < expected.DataRevision ||
                !compiler ||
                compiler.m_lastDataRevision != terrain.DataRevision ||
                !TryReadReplayTerrainCheckpoint(
                    terrain.GetByteArray(ZDOVars.s_TCData),
                    out long checkpointBatch,
                    out int checkpointIndex) ||
                checkpointBatch != proposal.BatchId ||
                checkpointIndex < proposal.BatchIndex)
            {
                return false;
            }
        }

        state.PendingCommit = null;
        state.LoadedOperations.Remove(proposal.BatchIndex);
        if (proposal.BatchIndex + 1 >= state.BatchCount)
        {
            SendReplayRelease(state);
            ClientReplayStates.Remove(state.BatchId);
        }
        else
        {
            state.Granted = false;
            state.ExpectedTerrainCompilers.Clear();
            state.NextRequestTime = 0f;
            state.FastReadinessProbeUntil =
                Time.realtimeSinceStartup + ReplayFastReadinessProbeSeconds;
        }

        return true;
    }

    private static bool ReplayTerrainManifestNeedsRefresh(ClientReplayState state)
    {
        if (state.ExpectedTerrainCompilers.Count == 0)
        {
            return true;
        }

        ZDOMan? world = ZDOMan.instance;
        if (world == null)
        {
            return false;
        }

        foreach (ReplayExpectedRevision expected in state.ExpectedTerrainCompilers)
        {
            ZDO terrain = world.GetZDO(expected.ZdoId);
            if (terrain != null && terrain.DataRevision > expected.DataRevision)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLocalReplaySourceSynchronized(ZDO zdo)
    {
        ZDOMan? world = ZDOMan.instance;
        return world != null &&
               (ZNet.instance == null ||
                ZNet.instance.IsServer() ||
                !world.m_clientChangeQueue.Contains(zdo.m_uid));
    }

    private static void RPC_HandleReplayRequest(long sender, ZPackage package)
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZDOMan.instance == null ||
            ZRoutedRpc.instance == null ||
            package.Size() > MaxReplayCommitPackageBytes)
        {
            return;
        }

        try
        {
            int version = package.ReadInt();
            ReplayRequestKind kind = (ReplayRequestKind)package.ReadInt();
            long requestId = package.ReadLong();
            if (version != ReplayRpcVersion || requestId <= 0L)
            {
                return;
            }

            switch (kind)
            {
                case ReplayRequestKind.Acquire:
                    HandleReplayAcquireRequest(
                        sender,
                        requestId,
                        package.ReadLong(),
                        package.ReadInt(),
                        package.ReadInt(),
                        package);
                    break;
                case ReplayRequestKind.Commit:
                    HandleReplayCommitRequest(sender, requestId, package);
                    break;
                case ReplayRequestKind.Abort:
                    HandleReplayAbortRequest(sender, requestId, package);
                    break;
                case ReplayRequestKind.Release:
                    HandleReplayReleaseRequest(sender, requestId, package);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"ZoneSavior terrain controller rejected a malformed request: {ex.Message}");
        }
    }

    private static void HandleReplayAcquireRequest(
        long sender,
        long requestId,
        long batchId,
        int batchCount,
        int candidateIndex,
        ZPackage package)
    {
        if (package.GetPos() != package.Size() ||
            !IsEligibleReplayExecutor(sender) ||
            batchId <= 0L ||
            batchCount <= 0 ||
            batchCount > MaxReplayOperations ||
            candidateIndex < 0 ||
            candidateIndex >= batchCount)
        {
            SendReplayResponse(
                sender,
                ReplayRequestKind.Acquire,
                requestId,
                batchId,
                ReplayResponseStatus.Invalid,
                0L,
                0L,
                0,
                0u,
                []);
            return;
        }

        if (ZoneBundleCommands.IsTerrainMutationActive)
        {
            SendReplayResponse(
                sender,
                ReplayRequestKind.Acquire,
                requestId,
                batchId,
                ReplayResponseStatus.Busy,
                0L,
                0L,
                candidateIndex,
                0u,
                []);
            return;
        }

        ServerReplayLease? active = _serverReplayLease;
        if (active != null)
        {
            if (active.Locked)
            {
                SendReplayResponse(
                    sender,
                    ReplayRequestKind.Acquire,
                    requestId,
                    batchId,
                    ReplayResponseStatus.ControllerLocked,
                    0L,
                    active.ExecutorPeer,
                    active.NextIndex,
                    0u,
                    []);
                return;
            }

            if (active.BatchId != batchId || active.ExecutorPeer != sender)
            {
                SendReplayResponse(
                    sender,
                    ReplayRequestKind.State,
                    0L,
                    active.BatchId,
                    ReplayResponseStatus.ControllerActive,
                    0L,
                    active.ExecutorPeer,
                    active.NextIndex,
                    0u,
                    []);
                SendReplayResponse(
                    sender,
                    ReplayRequestKind.Acquire,
                    requestId,
                    batchId,
                    ReplayResponseStatus.Busy,
                    0L,
                    active.ExecutorPeer,
                    active.NextIndex,
                    0u,
                    []);
                return;
            }

            if (active.Completed)
            {
                SendReplayResponse(
                    sender,
                    ReplayRequestKind.Acquire,
                    requestId,
                    batchId,
                    ReplayResponseStatus.Busy,
                    0L,
                    active.ExecutorPeer,
                    active.BatchCount,
                    0u,
                    []);
                return;
            }

            if (candidateIndex != active.NextIndex)
            {
                SendReplayResponse(
                    sender,
                    ReplayRequestKind.Acquire,
                    requestId,
                    batchId,
                    ReplayResponseStatus.Waiting,
                    0L,
                    active.ExecutorPeer,
                    active.NextIndex,
                    0u,
                    []);
                return;
            }

            if (!TryRefreshServerReplayTerrainManifest(active, out string refreshFailure))
            {
                active.Locked = true;
                _announcedReplayControllerLocked = true;
                BroadcastReplayControllerState(
                    ReplayResponseStatus.ControllerLocked,
                    active.BatchId,
                    active.ExecutorPeer);
                SendReplayResponse(
                    sender,
                    ReplayRequestKind.Acquire,
                    requestId,
                    batchId,
                    ReplayResponseStatus.ControllerLocked,
                    0L,
                    active.ExecutorPeer,
                    active.NextIndex,
                    0u,
                    []);
                _logger?.LogWarning(
                    $"ZoneSavior terrain batch {batchId} could not prepare operation {active.NextIndex}: " +
                    refreshFailure);
                return;
            }

            SendReplayResponse(
                sender,
                ReplayRequestKind.Acquire,
                requestId,
                batchId,
                ReplayResponseStatus.Granted,
                active.Token,
                active.ExecutorPeer,
                active.NextIndex,
                0u,
                active.ExpectedTerrainCompilers);
            return;
        }

        ReplayManifestResult manifestResult = TryBuildServerReplayManifest(
            batchId,
            batchCount,
            out ServerReplayManifest manifest,
            out string failureReason);
        if (manifestResult != ReplayManifestResult.Ready)
        {
            SendReplayResponse(
                sender,
                ReplayRequestKind.Acquire,
                requestId,
                batchId,
                manifestResult == ReplayManifestResult.Waiting
                    ? ReplayResponseStatus.Waiting
                    : ReplayResponseStatus.Invalid,
                0L,
                0L,
                0,
                0u,
                []);
            if (manifestResult == ReplayManifestResult.Invalid)
            {
                _logger?.LogWarning(
                    $"ZoneSavior terrain batch {batchId} was rejected by the server controller: {failureReason}");
            }

            return;
        }

        if (manifest.NextIndex >= batchCount)
        {
            CleanupCompletedReplayManifest(manifest);
            SendReplayResponse(
                sender,
                ReplayRequestKind.Acquire,
                requestId,
                batchId,
                ReplayResponseStatus.Completed,
                0L,
                sender,
                batchCount,
                0u,
                []);
            return;
        }

        if (candidateIndex != manifest.NextIndex)
        {
            SendReplayResponse(
                sender,
                ReplayRequestKind.Acquire,
                requestId,
                batchId,
                ReplayResponseStatus.Waiting,
                0L,
                0L,
                manifest.NextIndex,
                0u,
                []);
            return;
        }

        long token = NextReplayNonce();
        ServerReplayLease lease = new(manifest, sender, token);
        if (!TryRefreshServerReplayTerrainManifest(lease, out string terrainFailure))
        {
            SendReplayResponse(
                sender,
                ReplayRequestKind.Acquire,
                requestId,
                batchId,
                ReplayResponseStatus.Invalid,
                0L,
                0L,
                manifest.NextIndex,
                0u,
                []);
            _logger?.LogWarning(
                $"ZoneSavior terrain batch {batchId} could not prepare its compiler manifest: {terrainFailure}");
            return;
        }

        _serverReplayLease = lease;
        _announcedActiveReplayBatch = batchId;
        _announcedReplayControllerLocked = false;
        _nextReplayStateBroadcastTime = Time.realtimeSinceStartup + ReplayStateBroadcastSeconds;
        BroadcastReplayControllerState(ReplayResponseStatus.ControllerActive, batchId, sender);
        SendReplayResponse(
            sender,
            ReplayRequestKind.Acquire,
            requestId,
            batchId,
            ReplayResponseStatus.Granted,
            token,
            sender,
            manifest.NextIndex,
            0u,
            lease.ExpectedTerrainCompilers);
        _logger?.LogDebug(
            $"ZoneSavior server assigned terrain batch {batchId} ({batchCount} operation(s)) to peer {sender}.");
    }

    private static void HandleReplayCommitRequest(long sender, long requestId, ZPackage package)
    {
        long batchId = package.ReadLong();
        int batchCount = package.ReadInt();
        int batchIndex = package.ReadInt();
        long token = package.ReadLong();
        ZDOID proxyId = package.ReadZDOID();
        uint proxyDataRevision = package.ReadUInt();
        int terrainCount = package.ReadInt();
        if (terrainCount <= 0 || terrainCount > MaxReplayTerrainCompilers)
        {
            SendInvalidReplayCommit(sender, requestId, batchId);
            return;
        }

        List<ReplayTerrainCommitEntry> terrain = new(terrainCount);
        for (int index = 0; index < terrainCount; index++)
        {
            ZDOID terrainId = package.ReadZDOID();
            uint baseRevision = package.ReadUInt();
            int dataLength = package.ReadInt();
            int remaining = package.Size() - package.GetPos();
            if (dataLength <= 0 ||
                dataLength > MaxReplayTerrainPayloadBytes ||
                dataLength > remaining)
            {
                SendInvalidReplayCommit(sender, requestId, batchId);
                return;
            }

            byte[] data = package.ReadByteArray(dataLength);
            terrain.Add(new ReplayTerrainCommitEntry(terrainId, baseRevision, data));
        }

        if (package.GetPos() != package.Size())
        {
            SendInvalidReplayCommit(sender, requestId, batchId);
            return;
        }

        ServerReplayCommitReceipt? receipt = _lastServerReplayReceipt;
        if (receipt != null && receipt.Matches(sender, batchId, batchIndex, token, requestId))
        {
            SendReplayCommitReceipt(sender, receipt);
            return;
        }

        ServerReplayLease? lease = _serverReplayLease;
        if (lease == null ||
            lease.Locked ||
            lease.ExecutorPeer != sender ||
            lease.BatchId != batchId ||
            lease.BatchCount != batchCount ||
            lease.Token != token ||
            lease.NextIndex != batchIndex ||
            batchIndex < 0 ||
            batchIndex >= batchCount ||
            !IsEligibleReplayExecutor(sender))
        {
            SendReplayResponse(
                sender,
                ReplayRequestKind.Commit,
                requestId,
                batchId,
                lease?.Locked == true
                    ? ReplayResponseStatus.ControllerLocked
                    : ReplayResponseStatus.Invalid,
                0L,
                lease?.ExecutorPeer ?? 0L,
                lease?.NextIndex ?? 0,
                0u,
                []);
            return;
        }

        if (!TryValidateReplayCommit(
                lease,
                proxyId,
                proxyDataRevision,
                terrain,
                out ZDO proxy,
                out List<ZDO> terrainZdos,
                out bool revisionConflict,
                out string failureReason))
        {
            if (revisionConflict)
            {
                lease.ExpectedTerrainCompilers.Clear();
            }

            foreach (ZDO terrainZdo in terrainZdos)
            {
                ZDOMan.instance.ForceSendZDO(sender, terrainZdo.m_uid);
            }

            if (proxy != null)
            {
                ZDOMan.instance.ForceSendZDO(sender, proxy.m_uid);
            }

            SendReplayResponse(
                sender,
                ReplayRequestKind.Commit,
                requestId,
                batchId,
                revisionConflict ? ReplayResponseStatus.Conflict : ReplayResponseStatus.Invalid,
                token,
                sender,
                lease.NextIndex,
                proxy?.DataRevision ?? 0u,
                []);
            if (!revisionConflict)
            {
                _logger?.LogWarning(
                    $"ZoneSavior terrain batch {batchId} operation {batchIndex} was rejected: {failureReason}");
            }

            return;
        }

        List<ReplayExpectedRevision> committedTerrain = new(terrain.Count);
        for (int index = 0; index < terrain.Count; index++)
        {
            ZDO terrainZdo = terrainZdos[index];
            terrainZdo.Set(ZDOVars.s_TCData, terrain[index].Data);
            // Keep the live executor as owner. A dedicated server has no TerrainComp instance at
            // this position, so routing vanilla TerrainOp RPCs to server ownership would drop them.
            terrainZdo.SetOwner(sender);
            committedTerrain.Add(new ReplayExpectedRevision(terrainZdo.m_uid, terrainZdo.DataRevision));
            ZDOMan.instance.ForceSendZDO(sender, terrainZdo.m_uid);
        }

        proxy.SetOwner(ZDOMan.GetSessionID());
        MarkReplayOperationApplied(proxy);
        lease.ExpectedTerrainCompilers.Clear();
        lease.NextIndex++;
        bool completed = lease.NextIndex >= lease.BatchCount;
        if (completed)
        {
            CleanupCompletedReplayManifest(lease.Manifest);
        }

        ZDOMan.instance.ForceSendZDO(sender, proxy.m_uid);
        ServerReplayCommitReceipt committedReceipt = new(
            sender,
            requestId,
            batchId,
            batchIndex,
            token,
            lease.NextIndex,
            proxy.DataRevision,
            committedTerrain,
            completed);
        _lastServerReplayReceipt = committedReceipt;

        if (completed)
        {
            lease.Completed = true;
            lease.CompletedAt = Time.realtimeSinceStartup;
        }

        SendReplayCommitReceipt(sender, committedReceipt);
    }

    private static void HandleReplayAbortRequest(long sender, long requestId, ZPackage package)
    {
        long batchId = package.ReadLong();
        long token = package.ReadLong();
        if (requestId <= 0L || package.GetPos() != package.Size())
        {
            return;
        }

        ServerReplayLease? lease = _serverReplayLease;
        if (lease == null ||
            lease.ExecutorPeer != sender ||
            lease.BatchId != batchId ||
            lease.Token != token)
        {
            return;
        }

        lease.Locked = true;
        _announcedReplayControllerLocked = true;
        BroadcastReplayControllerState(ReplayResponseStatus.ControllerLocked, lease.BatchId, lease.ExecutorPeer);
        _logger?.LogWarning(
            $"ZoneSavior terrain batch {batchId} was locked after its executor reported a terminal replay failure; " +
            "restart the server world session after correcting the source data.");
    }

    private static void HandleReplayReleaseRequest(long sender, long requestId, ZPackage package)
    {
        long batchId = package.ReadLong();
        long token = package.ReadLong();
        if (requestId <= 0L || package.GetPos() != package.Size())
        {
            return;
        }

        ServerReplayLease? lease = _serverReplayLease;
        if (lease == null ||
            !lease.Completed ||
            lease.Locked ||
            lease.ExecutorPeer != sender ||
            lease.BatchId != batchId ||
            lease.Token != token)
        {
            return;
        }

        ReleaseReplayController();
    }

    private static void ReleaseReplayController()
    {
        _serverReplayLease = null;
        _announcedActiveReplayBatch = 0L;
        _announcedReplayControllerLocked = false;
        BroadcastReplayControllerState(ReplayResponseStatus.ControllerIdle, 0L, 0L);
    }

    private static bool TryValidateReplayCommit(
        ServerReplayLease lease,
        ZDOID proxyId,
        uint proxyDataRevision,
        IReadOnlyList<ReplayTerrainCommitEntry> terrain,
        out ZDO proxy,
        out List<ZDO> terrainZdos,
        out bool revisionConflict,
        out string failureReason)
    {
        proxy = null!;
        terrainZdos = [];
        revisionConflict = false;
        failureReason = string.Empty;
        ZDOMan world = ZDOMan.instance;
        proxy = world.GetZDO(proxyId);
        ZDOID expectedProxyId = lease.Manifest.OperationIds[lease.NextIndex];
        if (proxy == null ||
            proxyId != expectedProxyId ||
            !IsReplayOperationMetadataValid(
                proxy,
                lease.BatchId,
                lease.BatchCount,
                lease.NextIndex))
        {
            failureReason = "the source proxy does not match the active manifest";
            return false;
        }

        if (proxy.DataRevision != proxyDataRevision)
        {
            revisionConflict = true;
            failureReason = "the source proxy revision changed during preparation";
            return false;
        }

        if (terrain.Count != lease.ExpectedTerrainCompilers.Count)
        {
            failureReason = "the commit does not match the server terrain compiler manifest";
            return false;
        }

        if (!TryBuildServerTerrainCompilerManifest(
                proxy,
                createMissing: false,
                out List<ReplayExpectedRevision> currentManifest,
                out string manifestFailure) ||
            currentManifest.Count != lease.ExpectedTerrainCompilers.Count)
        {
            failureReason = string.IsNullOrEmpty(manifestFailure)
                ? "the commit does not match the server terrain compiler manifest"
                : manifestFailure;
            return false;
        }

        Dictionary<ZDOID, uint> expectedRevisions = [];
        for (int index = 0; index < lease.ExpectedTerrainCompilers.Count; index++)
        {
            ReplayExpectedRevision expected = lease.ExpectedTerrainCompilers[index];
            ReplayExpectedRevision current = currentManifest[index];
            if (expected.ZdoId != current.ZdoId ||
                expectedRevisions.ContainsKey(expected.ZdoId))
            {
                failureReason = "the terrain compiler manifest changed during preparation";
                return false;
            }

            expectedRevisions.Add(expected.ZdoId, expected.DataRevision);

            if (expected.DataRevision != current.DataRevision)
            {
                revisionConflict = true;
                failureReason = "a terrain compiler revision changed during preparation";
                return false;
            }
        }

        HashSet<ZDOID> seen = [];
        if (!TryGetServerTerrainNodeCount(out int expectedTerrainNodes))
        {
            failureReason = "the server terrain dimensions are unavailable";
            return false;
        }

        foreach (ReplayTerrainCommitEntry entry in terrain)
        {
            ZDO terrainZdo = world.GetZDO(entry.ZdoId);
            if (terrainZdo == null ||
                !terrainZdo.IsValid() ||
                terrainZdo.GetPrefab() != TerrainCompilerPrefabHash ||
                !terrainZdo.Persistent ||
                !seen.Add(entry.ZdoId))
            {
                failureReason = "the commit contains an invalid or duplicate terrain compiler";
                return false;
            }

            terrainZdos.Add(terrainZdo);
            if (!expectedRevisions.TryGetValue(entry.ZdoId, out uint expectedRevision))
            {
                failureReason = "the commit contains a terrain compiler outside the active manifest";
                return false;
            }

            if (entry.BaseDataRevision != expectedRevision ||
                terrainZdo.DataRevision != entry.BaseDataRevision)
            {
                revisionConflict = true;
                failureReason = "a terrain compiler revision changed during preparation";
                return false;
            }

            if (!TryValidateReplayTerrainTransition(
                    proxy,
                    terrainZdo,
                    entry.Data,
                    lease.BatchId,
                    lease.NextIndex,
                    expectedTerrainNodes,
                    out string transitionFailure))
            {
                failureReason = transitionFailure;
                return false;
            }
        }

        return true;
    }

    private static bool TryGetServerTerrainNodeCount(out int terrainNodes)
    {
        terrainNodes = 0;
        GameObject zonePrefab = ZoneSystem.instance ? ZoneSystem.instance.m_zonePrefab : null!;
        Heightmap template = zonePrefab ? zonePrefab.GetComponentInChildren<Heightmap>() : null!;
        if (!template || template.m_width <= 0)
        {
            return false;
        }

        int width = template.m_width + 1;
        terrainNodes = width * width;
        return terrainNodes > 0 && terrainNodes <= MaxReplayTerrainNodes;
    }

    private static ReplayManifestResult TryBuildServerReplayManifest(
        long batchId,
        int batchCount,
        out ServerReplayManifest manifest,
        out string failureReason)
    {
        manifest = null!;
        failureReason = string.Empty;
        ZDOMan world = ZDOMan.instance;
        ZDO?[] operations = new ZDO?[batchCount];
        int found = 0;
        foreach (ZDOID id in ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Long, ReplayBatchHash))
        {
            ZDO candidate = world.GetZDO(id);
            if (candidate == null || candidate.GetLong(ReplayBatchHash, 0L) != batchId)
            {
                continue;
            }

            int candidateCount = candidate.GetInt(ReplayBatchCountHash, 0);
            int candidateIndex = candidate.GetInt(ReplayBatchIndexHash, -1);
            if (!IsReplayOperationMetadataValid(candidate, batchId, candidateCount, candidateIndex) ||
                candidateCount != batchCount)
            {
                failureReason = $"member {candidate.m_uid} has inconsistent metadata";
                return ReplayManifestResult.Invalid;
            }

            if (operations[candidateIndex] != null)
            {
                failureReason = $"batch index {candidateIndex} is duplicated";
                return ReplayManifestResult.Invalid;
            }

            ObserveApplyOrder(candidate.GetLong(ApplyOrderHash, 0L));
            operations[candidateIndex] = candidate;
            found++;
        }

        if (found < batchCount)
        {
            return ReplayManifestResult.Waiting;
        }

        bool foundIncomplete = false;
        int nextIndex = batchCount;
        for (int index = 0; index < operations.Length; index++)
        {
            ZDO operation = operations[index]!;
            bool appliedFlag = operation.GetBool(AppliedHash, false);
            bool applied = HasReplayOperationApplied(operation);
            if (appliedFlag && !applied)
            {
                failureReason = $"batch index {index} has an invalid completion marker";
                return ReplayManifestResult.Invalid;
            }

            if (!applied)
            {
                if (!foundIncomplete)
                {
                    nextIndex = index;
                    foundIncomplete = true;
                }
            }
            else if (foundIncomplete)
            {
                failureReason = "completed operations do not form a contiguous prefix";
                return ReplayManifestResult.Invalid;
            }
        }

        manifest = new ServerReplayManifest(
            batchId,
            operations.Select(operation => operation!).ToArray(),
            nextIndex);
        return ReplayManifestResult.Ready;
    }

    private static bool TryRefreshServerReplayTerrainManifest(
        ServerReplayLease lease,
        out string failureReason)
    {
        failureReason = string.Empty;
        ZDOMan world = ZDOMan.instance;
        if (lease.NextIndex < 0 || lease.NextIndex >= lease.BatchCount)
        {
            failureReason = "the active operation index is outside the batch";
            return false;
        }

        ZDO source = world.GetZDO(lease.Manifest.OperationIds[lease.NextIndex]);
        if (source == null ||
            !IsReplayOperationMetadataValid(
                source,
                lease.BatchId,
                lease.BatchCount,
                lease.NextIndex) ||
            !TryBuildServerTerrainCompilerManifest(
                source,
                createMissing: true,
                out List<ReplayExpectedRevision> expected,
                out failureReason))
        {
            if (string.IsNullOrEmpty(failureReason))
            {
                failureReason = "the source proxy is no longer valid";
            }

            return false;
        }

        lease.ExpectedTerrainCompilers.Clear();
        lease.ExpectedTerrainCompilers.AddRange(expected);
        foreach (ReplayExpectedRevision compiler in expected)
        {
            world.ForceSendZDO(lease.ExecutorPeer, compiler.ZdoId);
        }

        return true;
    }

    private static bool TryBuildServerTerrainCompilerManifest(
        ZDO source,
        bool createMissing,
        out List<ReplayExpectedRevision> manifest,
        out string failureReason)
    {
        manifest = [];
        failureReason = string.Empty;
        ZDOMan? world = ZDOMan.instance;
        ZNetScene? scene = ZNetScene.instance;
        ZoneSystem? zones = ZoneSystem.instance;
        if (world == null || scene == null || zones == null)
        {
            failureReason = "the server world is not ready";
            return false;
        }

        const float tileSize = ZoneSystem.c_ZoneSize;

        TerrainProxySettings settings = ReadSettings(source);
        Vector3 position = source.GetPosition();
        Quaternion rotation = source.GetRotation();
        if (!IsFinite(position) || !IsFinite(rotation))
        {
            failureReason = "the source proxy transform is invalid";
            return false;
        }
        float halfTile = tileSize * 0.5f;
        float radius = settings.SearchRadius;
        int minTileX = Mathf.CeilToInt((position.x - radius - halfTile) / tileSize - 0.001f);
        int maxTileX = Mathf.FloorToInt((position.x + radius + halfTile) / tileSize + 0.001f);
        int minTileZ = Mathf.CeilToInt((position.z - radius - halfTile) / tileSize - 0.001f);
        int maxTileZ = Mathf.FloorToInt((position.z + radius + halfTile) / tileSize + 0.001f);
        List<Vector2i> terrainZones = [];
        for (int tileZ = minTileZ; tileZ <= maxTileZ; tileZ++)
        {
            for (int tileX = minTileX; tileX <= maxTileX; tileX++)
            {
                Vector2i zone = new(tileX, tileZ);
                if (FootprintIntersectsTile(
                        position,
                        rotation,
                        settings,
                        ZoneSystem.GetZonePos(zone),
                        tileSize))
                {
                    terrainZones.Add(zone);
                }
            }
        }

        if (terrainZones.Count == 0 || terrainZones.Count > MaxReplayTerrainCompilers)
        {
            failureReason =
                $"the footprint requires {terrainZones.Count} terrain compilers; " +
                $"the supported range is 1-{MaxReplayTerrainCompilers}";
            return false;
        }

        GameObject terrainPrefab = scene.GetPrefab(TerrainCompilerPrefabHash);
        ZNetView template = terrainPrefab ? terrainPrefab.GetComponent<ZNetView>() : null!;
        if (createMissing && (!terrainPrefab || !template))
        {
            failureReason = "the _TerrainCompiler prefab is unavailable";
            return false;
        }

        List<ZDO> sectorObjects = [];
        foreach (Vector2i zone in terrainZones)
        {
            Vector3 center = ZoneSystem.GetZonePos(zone);
            sectorObjects.Clear();
            world.FindSectorObjects(zone, 0, 0, sectorObjects);
            ZDO terrain = null!;
            foreach (ZDO candidate in sectorObjects)
            {
                if (candidate == null ||
                    !candidate.IsValid() ||
                    candidate.GetPrefab() != TerrainCompilerPrefabHash)
                {
                    continue;
                }

                if (Utils.DistanceXZ(candidate.GetPosition(), center) >= 0.1f)
                {
                    failureReason = $"zone {zone} contains a misaligned _TerrainCompiler ZDO";
                    return false;
                }

                if (terrain != null)
                {
                    failureReason = $"zone {zone} contains duplicate _TerrainCompiler ZDOs";
                    return false;
                }

                terrain = candidate;
            }

            if (terrain == null)
            {
                if (!createMissing)
                {
                    failureReason = $"zone {zone} is missing its _TerrainCompiler ZDO";
                    return false;
                }

                terrain = world.CreateNewZDO(center, TerrainCompilerPrefabHash);
                terrain.Persistent = template.m_persistent;
                terrain.Type = template.m_type;
                terrain.Distant = template.m_distant;
                terrain.SetPrefab(TerrainCompilerPrefabHash);
                terrain.SetRotation(Quaternion.identity);
            }

            if (!terrain.Persistent)
            {
                failureReason = $"zone {zone} has a non-persistent _TerrainCompiler ZDO";
                return false;
            }

            manifest.Add(new ReplayExpectedRevision(terrain.m_uid, terrain.DataRevision));
        }

        return true;
    }

    private static bool IsReplayOperationMetadataValid(
        ZDO zdo,
        long batchId,
        int batchCount,
        int batchIndex)
    {
        return zdo != null &&
               zdo.IsValid() &&
               zdo.Persistent &&
               IsPersistentProxyPrefabHash(zdo.GetPrefab()) &&
               HasStoredSettings(zdo) &&
               batchId > 0L &&
               batchCount > 0 &&
               batchCount <= MaxReplayOperations &&
               batchIndex >= 0 &&
               batchIndex < batchCount &&
               zdo.GetLong(ReplayBatchHash, 0L) == batchId &&
               zdo.GetInt(ReplayBatchCountHash, 0) == batchCount &&
               zdo.GetInt(ReplayBatchIndexHash, -1) == batchIndex &&
               IsValidApplyOrder(zdo.GetLong(ApplyOrderHash, 0L));
    }

    private static bool HasReplayOperationApplied(ZDO zdo)
    {
        if (!zdo.GetBool(AppliedHash, false) ||
            zdo.GetBool(ApplyOrderPendingHash, false))
        {
            return false;
        }

        TerrainProxySettings settings = ReadSettings(zdo);
        if (!HasAppliedAtPosition(zdo, zdo.GetPosition()) ||
            zdo.GetInt(AppliedSettingsHash, int.MinValue) != GetSettingsFingerprint(settings))
        {
            return false;
        }

        if (settings.Mode != TerrainProxyMode.Slope)
        {
            return true;
        }

        float appliedYaw = zdo.GetFloat(AppliedYawHash, float.NaN);
        return !float.IsNaN(appliedYaw) &&
               Mathf.Abs(Mathf.DeltaAngle(appliedYaw, zdo.GetRotation().eulerAngles.y)) < 0.1f;
    }

    private static bool HasCompletedReplaySource(ZDO zdo)
    {
        if (!zdo.GetBool(AppliedHash, false) ||
            !zdo.GetVec3(AppliedPositionHash, out Vector3 appliedPosition) ||
            !IsFinite(appliedPosition))
        {
            return false;
        }

        TerrainProxySettings settings = ReadSettings(zdo);
        if (zdo.GetInt(AppliedSettingsHash, int.MinValue) != GetSettingsFingerprint(settings))
        {
            return false;
        }

        return settings.Mode != TerrainProxyMode.Slope ||
               !float.IsNaN(zdo.GetFloat(AppliedYawHash, float.NaN));
    }

    private static void MarkReplayOperationApplied(ZDO zdo)
    {
        TerrainProxySettings settings = ReadSettings(zdo);
        Vector3 position = zdo.GetPosition();
        zdo.Set(AppliedPositionHash, position);
        zdo.Set(AppliedYawHash, zdo.GetRotation().eulerAngles.y);
        zdo.Set(AppliedSettingsHash, GetSettingsFingerprint(settings));
        CommitPendingApplyOrder(zdo);
        // Publish the boolean completion flag last so readers never accept a partial marker set.
        zdo.Set(AppliedHash, true);
    }

    private static void CleanupCompletedReplayManifest(ServerReplayManifest manifest)
    {
        ZDOMan world = ZDOMan.instance;
        for (int index = 0; index < manifest.OperationIds.Length; index++)
        {
            ZDO operation = world.GetZDO(manifest.OperationIds[index]);
            if (operation == null ||
                !IsReplayOperationMetadataValid(
                    operation,
                    manifest.BatchId,
                    manifest.BatchCount,
                    index))
            {
                continue;
            }

            ClearReplayBatch(operation);
            ZDOMan.instance.ForceSendZDO(operation.m_uid);
        }
    }

    private static void LogInvalidReplayMetadata(ZDO zdo, string reason)
    {
        _logger?.LogWarning(
            $"ZoneSavior terrain proxy {zdo.m_uid} has {reason}; refusing an unsafe ordered replay.");
    }

    private static void InvalidateBlueprintReplayBatch()
    {
        foreach (BlueprintReplayEntry entry in BlueprintReplayEntries.Values)
        {
            InvalidateBlueprintReplayProxy(entry.Zdo);
        }
    }

    private static void InvalidateBlueprintReplayProxy(ZDO zdo)
    {
        if (zdo == null || !zdo.IsValid())
        {
            return;
        }

        zdo.Set(AppliedHash, false);
        zdo.Set(ApplyOrderHash, 0L);
        zdo.Set(ApplyOrderPendingHash, false);
        ClearReplayBatch(zdo);
    }

    private static void ClearReplayBatch(ZDO zdo)
    {
        zdo.Set(ReplayBatchHash, 0L);
        zdo.Set(ReplayBatchCountHash, 0);
        zdo.Set(ReplayBatchIndexHash, -1);
        zdo.RemoveLong(ReplayBatchHash);
        zdo.RemoveInt(ReplayBatchCountHash);
        zdo.RemoveInt(ReplayBatchIndexHash);
    }

    private static bool IsEligibleReplayExecutor(long sender)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
        {
            return false;
        }

        if (sender == ZDOMan.GetSessionID())
        {
            return Player.m_localPlayer != null;
        }

        ZNetPeer peer = ZNet.instance.GetPeer(sender);
        return peer != null && peer.IsReady();
    }

    private static bool IsReplayExecutorConnected(long executor)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
        {
            return false;
        }

        return executor == ZDOMan.GetSessionID()
            ? true
            : ZNet.instance.GetPeer(executor)?.IsReady() == true;
    }

    private static bool IsReplayServerSender(long sender)
    {
        return (ZNet.instance != null &&
                ZNet.instance.IsServer() &&
                ZDOMan.instance != null &&
                sender == ZDOMan.GetSessionID()) ||
               ZoneRpcRegistrar.IsServerSender(sender);
    }

    private static void RPC_HandleReplayResponse(long sender, ZPackage package)
    {
        if (!IsReplayServerSender(sender))
        {
            return;
        }

        try
        {
            int version = package.ReadInt();
            ReplayRequestKind kind = (ReplayRequestKind)package.ReadInt();
            long requestId = package.ReadLong();
            long batchId = package.ReadLong();
            ReplayResponseStatus status = (ReplayResponseStatus)package.ReadInt();
            long token = package.ReadLong();
            long executor = package.ReadLong();
            int nextIndex = package.ReadInt();
            uint proxyRevision = package.ReadUInt();
            int revisionCount = package.ReadInt();
            if (version != ReplayRpcVersion ||
                revisionCount < 0 ||
                revisionCount > MaxReplayTerrainCompilers)
            {
                return;
            }

            List<ReplayExpectedRevision> revisions = new(revisionCount);
            for (int index = 0; index < revisionCount; index++)
            {
                revisions.Add(new ReplayExpectedRevision(package.ReadZDOID(), package.ReadUInt()));
            }

            if (package.GetPos() != package.Size())
            {
                return;
            }

            if (kind == ReplayRequestKind.State)
            {
                ApplyReplayControllerState(status, batchId);
                return;
            }

            if (!ClientReplayStates.TryGetValue(batchId, out ClientReplayState state))
            {
                return;
            }

            if (kind == ReplayRequestKind.Acquire)
            {
                if (requestId != state.PendingAcquireRequestId)
                {
                    return;
                }

                state.PendingAcquireRequestId = 0L;
                switch (status)
                {
                    case ReplayResponseStatus.Granted:
                        state.Granted = true;
                        state.Token = token;
                        state.ServerNextIndex = nextIndex;
                        state.NextRequestTime = 0f;
                        state.FastReadinessProbeUntil =
                            Time.realtimeSinceStartup + ReplayFastReadinessProbeSeconds;
                        state.ExpectedTerrainCompilers.Clear();
                        state.ExpectedTerrainCompilers.AddRange(revisions);
                        _announcedActiveReplayBatch = batchId;
                        _announcedReplayControllerLocked = false;
                        break;
                    case ReplayResponseStatus.Completed:
                        state.Granted = false;
                        state.ServerNextIndex = state.BatchCount;
                        ClientReplayStates.Remove(batchId);
                        break;
                    case ReplayResponseStatus.ControllerLocked:
                        _announcedActiveReplayBatch = batchId;
                        _announcedReplayControllerLocked = true;
                        break;
                    case ReplayResponseStatus.Invalid:
                        InvalidateClientReplayState(state);
                        break;
                    case ReplayResponseStatus.Waiting:
                        state.ServerNextIndex = nextIndex;
                        state.NextRequestTime = Time.realtimeSinceStartup + ReplayRequestRetrySeconds;
                        break;
                    default:
                        state.NextRequestTime = Time.realtimeSinceStartup + ReplayRequestRetrySeconds;
                        break;
                }

                return;
            }

            ReplayCommitProposal? proposal = state.PendingCommit;
            if (kind != ReplayRequestKind.Commit ||
                proposal == null ||
                requestId != proposal.RequestId)
            {
                return;
            }

            switch (status)
            {
                case ReplayResponseStatus.CommitAccepted:
                case ReplayResponseStatus.Completed:
                    proposal.Accepted = true;
                    proposal.ExpectedProxyDataRevision = proxyRevision;
                    proposal.ExpectedTerrainRevisions.Clear();
                    proposal.ExpectedTerrainRevisions.AddRange(revisions);
                    state.ServerNextIndex = nextIndex;
                    break;
                case ReplayResponseStatus.Conflict:
                    state.PendingCommit = null;
                    state.Granted = false;
                    state.ExpectedTerrainCompilers.Clear();
                    state.NextRequestTime = Time.realtimeSinceStartup + ReplayRequestRetrySeconds;
                    break;
                case ReplayResponseStatus.ControllerLocked:
                    _announcedReplayControllerLocked = true;
                    break;
                default:
                    InvalidateClientReplayState(state);
                    state.PendingCommit = null;
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning($"ZoneSavior terrain controller rejected a malformed response: {ex.Message}");
        }
    }

    private static void ApplyReplayControllerState(ReplayResponseStatus status, long batchId)
    {
        switch (status)
        {
            case ReplayResponseStatus.ControllerActive:
                _announcedActiveReplayBatch = batchId;
                _announcedReplayControllerLocked = false;
                break;
            case ReplayResponseStatus.ControllerLocked:
                _announcedActiveReplayBatch = batchId;
                _announcedReplayControllerLocked = true;
                break;
            case ReplayResponseStatus.ControllerIdle:
                _announcedActiveReplayBatch = 0L;
                _announcedReplayControllerLocked = false;
                // A completed server commit is already canonical even if this client unloaded the
                // terrain before observing the final checkpoint. The server keeps the controller
                // active while waiting for that observation and broadcasts Idle when it releases;
                // discard only final, accepted proposals so an unloaded area cannot leave a dead
                // client replay state behind indefinitely.
                foreach (long completedBatch in ClientReplayStates
                             .Where(entry =>
                                 entry.Value.PendingCommit?.Accepted == true &&
                                 entry.Value.ServerNextIndex >= entry.Value.BatchCount)
                             .Select(entry => entry.Key)
                             .ToList())
                {
                    ClientReplayStates.Remove(completedBatch);
                }
                break;
        }
    }

    private static void BroadcastReplayControllerState(
        ReplayResponseStatus status,
        long batchId,
        long executor)
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        SendReplayResponse(
            ZRoutedRpc.Everybody,
            ReplayRequestKind.State,
            0L,
            batchId,
            status,
            0L,
            executor,
            0,
            0u,
            []);
    }

    private static void SendReplayCommitReceipt(long target, ServerReplayCommitReceipt receipt)
    {
        SendReplayResponse(
            target,
            ReplayRequestKind.Commit,
            receipt.RequestId,
            receipt.BatchId,
            receipt.Completed
                ? ReplayResponseStatus.Completed
                : ReplayResponseStatus.CommitAccepted,
            receipt.Token,
            receipt.ExecutorPeer,
            receipt.NextIndex,
            receipt.ProxyDataRevision,
            receipt.TerrainRevisions);
    }

    private static void SendInvalidReplayCommit(long sender, long requestId, long batchId)
    {
        SendReplayResponse(
            sender,
            ReplayRequestKind.Commit,
            requestId,
            batchId,
            ReplayResponseStatus.Invalid,
            0L,
            0L,
            0,
            0u,
            []);
    }

    private static void SendReplayResponse(
        long target,
        ReplayRequestKind kind,
        long requestId,
        long batchId,
        ReplayResponseStatus status,
        long token,
        long executor,
        int nextIndex,
        uint proxyRevision,
        IReadOnlyList<ReplayExpectedRevision> terrainRevisions)
    {
        if (ZRoutedRpc.instance == null)
        {
            return;
        }

        ZPackage package = new();
        package.Write(ReplayRpcVersion);
        package.Write((int)kind);
        package.Write(requestId);
        package.Write(batchId);
        package.Write((int)status);
        package.Write(token);
        package.Write(executor);
        package.Write(nextIndex);
        package.Write(proxyRevision);
        package.Write(terrainRevisions.Count);
        foreach (ReplayExpectedRevision revision in terrainRevisions)
        {
            package.Write(revision.ZdoId);
            package.Write(revision.DataRevision);
        }

        ZRoutedRpc.instance.InvokeRoutedRPC(target, ReplayResponseRpcName, package);
    }

    private static long NextReplayRequestId()
    {
        long current = DateTime.UtcNow.Ticks;
        if (current <= _lastReplayRequestId)
        {
            current = _lastReplayRequestId + 1L;
        }

        _lastReplayRequestId = current;
        return current;
    }

    private static void InvalidateClientReplayState(ClientReplayState state)
    {
        if (state.Invalid)
        {
            return;
        }

        state.Invalid = true;
        if (!state.Granted || state.AbortSent || ZRoutedRpc.instance == null)
        {
            return;
        }

        state.AbortSent = true;
        ZPackage package = new();
        package.Write(ReplayRpcVersion);
        package.Write((int)ReplayRequestKind.Abort);
        package.Write(NextReplayRequestId());
        package.Write(state.BatchId);
        package.Write(state.Token);
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            ReplayRequestRpcName,
            package);
    }

    private static void SendReplayRelease(ClientReplayState state)
    {
        if (!state.Granted || state.Token == 0L || ZRoutedRpc.instance == null)
        {
            return;
        }

        ZPackage package = new();
        package.Write(ReplayRpcVersion);
        package.Write((int)ReplayRequestKind.Release);
        package.Write(NextReplayRequestId());
        package.Write(state.BatchId);
        package.Write(state.Token);
        ZRoutedRpc.instance.InvokeRoutedRPC(
            ZRoutedRpc.instance.GetServerPeerID(),
            ReplayRequestRpcName,
            package);
    }

    private static long NextReplayNonce()
    {
        long nonce;
        do
        {
            nonce = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & long.MaxValue;
        }
        while (nonce == 0L || ClientReplayStates.ContainsKey(nonce));

        return nonce;
    }

    private static long NextApplyOrder()
    {
        long current = ZNet.instance != null ? ZNet.instance.GetTime().Ticks : DateTime.UtcNow.Ticks;
        if (current <= _lastIssuedApplyOrder)
        {
            if (_lastIssuedApplyOrder >= long.MaxValue - 1L)
            {
                throw new InvalidOperationException("terrain apply order is exhausted");
            }

            current = _lastIssuedApplyOrder + 1L;
        }

        _lastIssuedApplyOrder = current;
        return current;
    }

    private static bool IsValidApplyOrder(long applyOrder)
    {
        return applyOrder > 0L && applyOrder < long.MaxValue;
    }

    private static void CommitPendingApplyOrder(ZDO zdo)
    {
        if (!zdo.GetBool(ApplyOrderPendingHash, false))
        {
            return;
        }

        // Persist the actual canonical application order. This is normally the placement order,
        // but if terrain streaming delays one proxy it must reflect the order that produced the
        // terrain snapshot the blueprint is expected to reproduce.
        zdo.Set(ApplyOrderHash, NextApplyOrder());
        zdo.Set(ApplyOrderPendingHash, false);
    }

    private static void ObserveApplyOrder(long applyOrder)
    {
        if (applyOrder > _lastIssuedApplyOrder)
        {
            _lastIssuedApplyOrder = applyOrder;
        }
    }

    private sealed class BlueprintReplayEntry
    {
        public BlueprintReplayEntry(
            ZDO zdo,
            ZoneSaviorTerrainProxy? proxy,
            long sourceApplyOrder,
            int registrationSequence,
            bool requiresCompletedSource)
        {
            Zdo = zdo;
            Proxy = proxy;
            SourceApplyOrder = sourceApplyOrder;
            RegistrationSequence = registrationSequence;
            RequiresCompletedSource = requiresCompletedSource;
        }

        public ZDO Zdo { get; }
        public ZoneSaviorTerrainProxy? Proxy { get; set; }
        public long SourceApplyOrder { get; set; }
        public int RegistrationSequence { get; }
        public bool RequiresCompletedSource { get; set; }
    }

    private sealed class ClientReplayState
    {
        public ClientReplayState(long batchId, int batchCount)
        {
            BatchId = batchId;
            BatchCount = batchCount;
            ServerNextIndex = -1;
        }

        public long BatchId { get; }
        public int BatchCount { get; }
        public long PendingAcquireRequestId { get; set; }
        public long Token { get; set; }
        public int ServerNextIndex { get; set; }
        public float NextRequestTime { get; set; }
        public float FastReadinessProbeUntil { get; set; }
        public bool Granted { get; set; }
        public bool Invalid { get; set; }
        public bool AbortSent { get; set; }
        public ReplayCommitProposal? PendingCommit { get; set; }
        public Dictionary<int, ZoneSaviorTerrainProxy> LoadedOperations { get; } = [];
        public List<ReplayExpectedRevision> ExpectedTerrainCompilers { get; } = [];
    }

    private sealed class ReplayCommitProposal
    {
        public ReplayCommitProposal(
            long batchId,
            int batchCount,
            int batchIndex,
            ZDOID proxyId,
            uint proxyDataRevision,
            List<ReplayTerrainCommitEntry> terrain)
        {
            BatchId = batchId;
            BatchCount = batchCount;
            BatchIndex = batchIndex;
            ProxyId = proxyId;
            ProxyDataRevision = proxyDataRevision;
            Terrain = terrain;
        }

        public long BatchId { get; }
        public int BatchCount { get; }
        public int BatchIndex { get; }
        public ZDOID ProxyId { get; }
        public uint ProxyDataRevision { get; }
        public List<ReplayTerrainCommitEntry> Terrain { get; }
        public List<ReplayExpectedRevision> ExpectedTerrainRevisions { get; } = [];
        public long RequestId { get; set; }
        public long Token { get; set; }
        public float NextSendTime { get; set; }
        public uint ExpectedProxyDataRevision { get; set; }
        public bool Accepted { get; set; }
    }

    private sealed class ReplayTerrainCommitEntry
    {
        public ReplayTerrainCommitEntry(ZDOID zdoId, uint baseDataRevision, byte[] data)
        {
            ZdoId = zdoId;
            BaseDataRevision = baseDataRevision;
            Data = data;
        }

        public ZDOID ZdoId { get; }
        public uint BaseDataRevision { get; }
        public byte[] Data { get; }
    }

    private readonly struct ReplayExpectedRevision
    {
        public ReplayExpectedRevision(ZDOID zdoId, uint dataRevision)
        {
            ZdoId = zdoId;
            DataRevision = dataRevision;
        }

        public ZDOID ZdoId { get; }
        public uint DataRevision { get; }
    }

    private sealed class ServerReplayManifest
    {
        public ServerReplayManifest(long batchId, ZDO[] operations, int nextIndex)
        {
            BatchId = batchId;
            OperationIds = operations.Select(operation => operation.m_uid).ToArray();
            NextIndex = nextIndex;
        }

        public long BatchId { get; }
        public ZDOID[] OperationIds { get; }
        public int BatchCount => OperationIds.Length;
        public int NextIndex { get; }
    }

    private sealed class ServerReplayLease
    {
        public ServerReplayLease(ServerReplayManifest manifest, long executorPeer, long token)
        {
            Manifest = manifest;
            ExecutorPeer = executorPeer;
            Token = token;
            NextIndex = manifest.NextIndex;
        }

        public ServerReplayManifest Manifest { get; }
        public long BatchId => Manifest.BatchId;
        public int BatchCount => Manifest.BatchCount;
        public long ExecutorPeer { get; }
        public long Token { get; }
        public int NextIndex { get; set; }
        public bool Locked { get; set; }
        public bool Completed { get; set; }
        public float CompletedAt { get; set; }
        public List<ReplayExpectedRevision> ExpectedTerrainCompilers { get; } = [];
    }

    private sealed class ServerReplayCommitReceipt
    {
        public ServerReplayCommitReceipt(
            long executorPeer,
            long requestId,
            long batchId,
            int batchIndex,
            long token,
            int nextIndex,
            uint proxyDataRevision,
            List<ReplayExpectedRevision> terrainRevisions,
            bool completed)
        {
            ExecutorPeer = executorPeer;
            RequestId = requestId;
            BatchId = batchId;
            BatchIndex = batchIndex;
            Token = token;
            NextIndex = nextIndex;
            ProxyDataRevision = proxyDataRevision;
            TerrainRevisions = terrainRevisions;
            Completed = completed;
        }

        public long ExecutorPeer { get; }
        public long RequestId { get; }
        public long BatchId { get; }
        public int BatchIndex { get; }
        public long Token { get; }
        public int NextIndex { get; }
        public uint ProxyDataRevision { get; }
        public List<ReplayExpectedRevision> TerrainRevisions { get; }
        public bool Completed { get; }

        public bool Matches(long executor, long batchId, int batchIndex, long token, long requestId)
        {
            return ExecutorPeer == executor &&
                   BatchId == batchId &&
                   BatchIndex == batchIndex &&
                   Token == token &&
                   RequestId == requestId;
        }
    }

    private enum ReplayRequestKind
    {
        Acquire = 1,
        Commit = 2,
        State = 3,
        Abort = 4,
        Release = 5
    }

    private enum ReplayResponseStatus
    {
        Granted = 1,
        Waiting = 2,
        Busy = 3,
        Conflict = 4,
        CommitAccepted = 5,
        Completed = 6,
        Invalid = 7,
        ControllerActive = 8,
        ControllerIdle = 9,
        ControllerLocked = 10
    }

    private enum ReplayManifestResult
    {
        Ready,
        Waiting,
        Invalid
    }
}
