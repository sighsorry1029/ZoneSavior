using System.Collections.Generic;

namespace ZoneSavior;

internal sealed class ZoneBundleManifest
{
    public int Version { get; set; } = 1;
    public string Tag { get; set; } = "";
    public string World { get; set; } = "";
    public string SavedAt { get; set; } = "";
    public ZoneBundleRange SourceRange { get; set; } = new();
    public List<ZoneBundleCreatorPlayer> SourceZoneCreators { get; set; } = new();
    public List<ZoneBundleManifestEntry> Bundles { get; set; } = new();
}

internal sealed class ZoneBundleManifestEntry
{
    public ZoneBundleZone Zone { get; set; } = new();
    public string File { get; set; } = "";
    public List<ZoneBundleCreatorPlayer> SourceZoneCreators { get; set; } = new();
}

internal sealed class ZoneBundleCreatorPlayer
{
    public long PlayerId { get; set; }
    public string? Name { get; set; }
    public string? PlatformId { get; set; }
}

internal sealed class ZoneBundleFile
{
    public int Version { get; set; } = 1;
    public string Tag { get; set; } = "";
    public ZoneBundleZone SourceZone { get; set; } = new();
    public string TerrainMode { get; set; } = "";
    public float SourceBaseY { get; set; }
    public ZoneBundleTerrainCaptureState TerrainCaptureState { get; set; } = ZoneBundleTerrainCaptureState.NotLoaded;
    public bool TerrainContactsCaptured { get; set; }
    public List<ZoneBundleTerrainContact> TerrainContacts { get; set; } = [];
    public List<ZoneBundleCreatorPlayer> SourceZoneCreators { get; set; } = new();
    public List<ZoneBundleEntry> Entries { get; set; } = new();
}

internal enum ZoneBundleTerrainCaptureState
{
    NotLoaded = 0,
    LoadedNoContacts = 1,
    Contacts = 2
}

internal sealed class ZoneBundleTerrainContact
{
    public float LocalX { get; set; }
    public float LocalZ { get; set; }
    public float RelativeY { get; set; }
}

internal sealed class ZoneBundleEntry
{
    public string SaveId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Prefab { get; set; } = "";
    public float[] LocalPos { get; set; } = new float[3];
    public float[] Rot { get; set; } = new float[4];
    public float[] Scale { get; set; } = new float[3];
    public long CreatorPlayerId { get; set; }
    public string? CreatorName { get; set; }
    public string Data { get; set; } = "";
    public string Sanitize { get; set; } = "";
}

internal sealed class ZoneBundleRange
{
    public int MinX { get; set; }
    public int MaxX { get; set; }
    public int MinZ { get; set; }
    public int MaxZ { get; set; }
}

internal sealed class ZoneBundleZone
{
    public int X { get; set; }
    public int Z { get; set; }
}

internal sealed class ZoneBundleCommandRequest
{
    public string Operation { get; set; } = "";
    public ZoneBundleRange SourceRange { get; set; } = new();
    public string Tag { get; set; } = "";
    public ZoneBundleZone? TargetZone { get; set; }
    public float YOffset { get; set; }
    public bool RestoreOriginal { get; set; }
    public bool LoadSourceZone { get; set; }
}

internal sealed class ZoneBundleCommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";

    public static ZoneBundleCommandResult Ok(string message)
    {
        return new ZoneBundleCommandResult
        {
            Success = true,
            Message = message
        };
    }

    public static ZoneBundleCommandResult Fail(string message)
    {
        return new ZoneBundleCommandResult
        {
            Success = false,
            Message = message
        };
    }
}

internal sealed class TerrainSupportTarget
{
    public Vector2i Zone { get; set; }
    public float SourceBaseY { get; set; }
    public List<ZoneBundleEntry> Entries { get; set; } = [];
    public bool ContactsCaptured { get; set; }
    public List<ZoneBundleTerrainContact> Contacts { get; set; } = [];
}

internal sealed class TerrainPlacementContext
{
    public float BaseWorldY { get; set; }
    public Dictionary<long, float> SupportRelativeHeights { get; set; } = new();
}

internal sealed class ZoneBundleClientTerrainApplyRequest
{
    public string RequestId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Tag { get; set; } = "";
    public TerrainPlacementContext? Context { get; set; }
    public List<ZoneBundleZone> TargetZones { get; set; } = [];
    public List<ZoneBundleClientTerrainApplyTarget> Targets { get; set; } = [];
}

internal sealed class ZoneBundleClientTerrainApplyTarget
{
    public ZoneBundleZone Zone { get; set; } = new();
    public List<ZoneBundleEntry> Entries { get; set; } = [];
    public bool ContactsCaptured { get; set; }
    public List<ZoneBundleTerrainContact> Contacts { get; set; } = [];
}

internal sealed class ZoneBundleClientTerrainApplyResponse
{
    public string RequestId { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public int TargetZones { get; set; }
    public int ChangedZones { get; set; }
}

internal sealed class ZoneBundleClientTerrainCaptureRequest
{
    public string RequestId { get; set; } = "";
    public string Tag { get; set; } = "";
    public ZoneBundleZone Zone { get; set; } = new();
    public float SourceBaseY { get; set; }
    public List<ZoneBundleEntry> Entries { get; set; } = [];
}

internal sealed class ZoneBundleClientTerrainCaptureResponse
{
    public string RequestId { get; set; } = "";
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public bool ContactsCaptured { get; set; }
    public List<ZoneBundleTerrainContact> Contacts { get; set; } = [];
}

