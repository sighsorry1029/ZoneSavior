using System.Collections.Generic;
using System.Linq;

namespace ZoneSavior;

internal sealed class AutoArchiveScanPolicy
{
    private AutoArchiveScanPolicy(AutoArchiveScanOptions options)
    {
        DryRun = options.DryRun;
        ResetAfterSave = options.ResetAfterSave;
        TargetPlayerIds = options.TargetPlayerIds.ToHashSet();
    }

    public bool DryRun { get; }
    public bool ResetAfterSave { get; }
    public HashSet<long> TargetPlayerIds { get; }
    public bool IsTargetOverride => TargetPlayerIds.Count > 0;
    public bool UsesMinimumClusterSize => !IsTargetOverride;
    public bool RequiresCreatorEligibility => !IsTargetOverride;
    public bool BlocksMixedOwnerReset => IsTargetOverride && ResetAfterSave;

    public static AutoArchiveScanPolicy FromOptions(AutoArchiveScanOptions options)
    {
        return new AutoArchiveScanPolicy(options);
    }

    public bool IncludesTargetCreator(IEnumerable<long> creators)
    {
        return creators.Any(TargetPlayerIds.Contains);
    }

    public bool IsSmallCluster(ArchiveClusterRecord record)
    {
        return UsesMinimumClusterSize && record.PieceCount < AutoArchiveConfig.MinimumPiecesPerCluster;
    }

    public string DryRunCandidateReason()
    {
        return IsTargetOverride
            ? "target override candidate; dry run is enabled"
            : "candidate only; dry run is enabled";
    }
}
