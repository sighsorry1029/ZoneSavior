using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private const float TooltipFontSizeMultiplier = 1.15f;

    private static readonly List<PieceTable> PieceTableScratch = [];
    private static float _defaultTooltipFontSize = -1f;
    private static Hud? _tooltipHud;

    public static void AddToAvailablePieces(PieceTable table, Player player)
    {
        if (!table)
        {
            return;
        }

        if (!ShouldShowTo(player))
        {
            RemoveFromPieceTable(table);
            return;
        }

        EnsureRegistered();
        if (!_prefab || !_slopePrefab || !_paintPrefab || !_resetPrefab)
        {
            return;
        }

        SanitizePieceTable(table);
        AddPieceToTable(table, _prefab);
        AddPieceToTable(table, _slopePrefab);
        AddPieceToTable(table, _paintPrefab);
        AddPieceToTable(table, _resetPrefab);
    }

    private static void EnsureRegistered()
    {
        ZNetScene scene = ZNetScene.instance;
        if (scene == null)
        {
            return;
        }

        if (!_prefab)
        {
            _prefab = CreatePrefab(PrefabName, slope: false);
        }

        if (!_slopePrefab)
        {
            _slopePrefab = CreatePrefab(SlopePrefabName, slope: true);
        }

        if (!_paintPrefab)
        {
            _paintPrefab = CreatePrefab(PaintPrefabName, slope: false, paint: true);
        }

        if (!_resetPrefab)
        {
            _resetPrefab = CreatePrefab(ResetPrefabName, slope: false, reset: true);
        }

        if (_registeredScene == scene &&
            IsRegistered(scene, PrefabHash) &&
            IsRegistered(scene, SlopePrefabHash) &&
            IsRegistered(scene, PaintPrefabHash) &&
            IsRegistered(scene, ResetPrefabHash))
        {
            return;
        }

        RegisterPrefab(scene, PrefabHash, PrefabName, _prefab);
        RegisterPrefab(scene, SlopePrefabHash, SlopePrefabName, _slopePrefab);
        RegisterPrefab(scene, PaintPrefabHash, PaintPrefabName, _paintPrefab);
        RegisterPrefab(scene, ResetPrefabHash, ResetPrefabName, _resetPrefab);
        _registeredScene = scene;
    }

    private static bool IsRegistered(ZNetScene scene, int prefabHash)
    {
        return scene.m_namedPrefabs.TryGetValue(prefabHash, out GameObject prefab) && prefab;
    }

    private static GameObject? GetRegisteredPrefab(int prefabHash)
    {
        EnsureRegistered();
        return prefabHash == PrefabHash ? _prefab :
            prefabHash == SlopePrefabHash ? _slopePrefab :
            prefabHash == PaintPrefabHash ? _paintPrefab :
            prefabHash == ResetPrefabHash ? _resetPrefab :
            null;
    }

    private static void RegisterPrefab(ZNetScene scene, int prefabHash, string prefabName, GameObject prefab)
    {
        if (!scene.m_namedPrefabs.ContainsKey(prefabHash))
        {
            scene.m_namedPrefabs[prefabHash] = prefab;
        }

        if (!scene.m_prefabs.Any(registered => registered && string.Equals(Utils.GetPrefabName(registered), prefabName, StringComparison.Ordinal)))
        {
            scene.m_prefabs.Add(prefab);
        }
    }

    private static void AddPieceToTable(PieceTable table, GameObject prefab)
    {
        string prefabName = Utils.GetPrefabName(prefab);
        if (!table.m_pieces.Any(piece => piece && string.Equals(Utils.GetPrefabName(piece), prefabName, StringComparison.Ordinal)))
        {
            table.m_pieces.Add(prefab);
        }

        Piece pieceComponent = prefab.GetComponent<Piece>();
        pieceComponent.m_category = Piece.PieceCategory.Misc;
        pieceComponent.m_clipEverything = true;
        pieceComponent.m_icon = GetIcon(
            IsSlopeProxyObject(prefab),
            IsResetProxyObject(prefab),
            IsPaintProxyObject(prefab));
        pieceComponent.m_description = GetPieceDescription(prefab);
        int category = Mathf.Clamp((int)pieceComponent.m_category, 0, table.m_availablePieces.Count - 1);
        if (category < 0)
        {
            return;
        }

        List<Piece> pieces = table.m_availablePieces[category];
        if (!pieces.Any(piece => piece && string.Equals(Utils.GetPrefabName(piece.gameObject), prefabName, StringComparison.Ordinal)))
        {
            pieces.Add(pieceComponent);
        }
    }

    internal static void SanitizeKnownRecipePieceTables(Player player)
    {
        if (!player)
        {
            return;
        }

        bool removeAdminTools = !ShouldShowTo(player);
        SanitizePieceTable(player.m_buildPieces);
        if (removeAdminTools)
        {
            RemoveFromPieceTable(player.m_buildPieces);
        }

        PieceTableScratch.Clear();
        player.m_inventory?.GetAllPieceTables(PieceTableScratch);
        foreach (PieceTable table in PieceTableScratch)
        {
            SanitizePieceTable(table);
            if (removeAdminTools)
            {
                RemoveFromPieceTable(table);
            }
        }

        PieceTableScratch.Clear();
    }

    private static void RemoveFromPieceTable(PieceTable? table)
    {
        if (!table || table.m_pieces == null)
        {
            return;
        }

        table.m_pieces.RemoveAll(IsProxyPrefab);
        if (table.m_availablePieces == null)
        {
            return;
        }

        foreach (List<Piece> pieces in table.m_availablePieces)
        {
            pieces?.RemoveAll(IsProxyPiece);
        }
    }

    private static bool IsProxyPrefab(GameObject prefab)
    {
        return prefab && IsProxyPrefabName(Utils.GetPrefabName(prefab));
    }

    private static void SanitizePieceTable(PieceTable? table)
    {
        if (!table || table.m_pieces == null)
        {
            return;
        }

        table.m_pieces.RemoveAll(piece => !piece);
        foreach (List<Piece> pieces in table.m_availablePieces)
        {
            pieces?.RemoveAll(piece => !piece);
        }
    }

    private static void UpdatePieceDescriptions()
    {
        UpdatePieceDescription(_prefab);
        UpdatePieceDescription(_slopePrefab);
        UpdatePieceDescription(_paintPrefab);
        UpdatePieceDescription(_resetPrefab);
    }

    private static void UpdatePieceDescription(GameObject? prefab)
    {
        if (!prefab)
        {
            return;
        }

        Piece piece = prefab.GetComponent<Piece>();
        if (piece)
        {
            piece.m_description = GetPieceDescription(prefab);
        }
    }

    private static string GetPieceDescription(GameObject prefab)
    {
        if (IsSlopeProxyObject(prefab))
        {
            return string.Join("\n",
                "Places start and end markers to store and replay a terrain slope.",
                "",
                $"Width: {FormatMeters(CurrentSlopeWidth())}",
                $"Start marker: {(_hasPendingSlopeStart ? "set" : "not set")}",
                FormatTerrainToolWheelAction("width"));
        }

        if (IsPaintProxyObject(prefab))
        {
            return string.Join("\n",
                "Stores and replays terrain paint in this circle.",
                "",
                $"Current radius: {FormatMeters(CurrentCircleRadius())}",
                $"Paint: {CurrentPaintType()}",
                FormatTerrainToolWheelAction("paint type"));
        }

        if (IsResetProxyObject(prefab))
        {
            bool paintOnly = CurrentTerrainResetScope == TerrainResetScope.PaintOnly;
            return string.Join("\n",
                paintOnly
                    ? "Resets only terrain paint in this radius and removes intersecting Paint Proxy objects."
                    : "Resets terrain height and paint in this radius and removes intersecting terrain, slope, and paint proxies.",
                "",
                $"Current radius: {FormatMeters(CurrentCircleRadius())}",
                $"Reset mode: {TerrainResetScopeLabel()}",
                FormatTerrainResetScopeAction());
        }

        return string.Join("\n",
            "Stores and replays terrain filled to this marker height.",
            "",
            $"Current radius: {FormatMeters(CurrentCircleRadius())}",
            $"Edge Softness: {CurrentTerrainEdgeSoftness():0.##}",
            FormatTerrainToolWheelAction("edge softness"));
    }

    private static string FormatMeters(float value)
    {
        return $"{value:0.#}m";
    }

    private static string FormatTerrainToolWheelAction(string action)
    {
        string modifier = FormatShortcut(AdminTerrainToolConfig.TerrainToolModifierKey);
        return string.IsNullOrWhiteSpace(modifier)
            ? $"Set Terrain Tool Modifier Key to adjust {action}."
            : $"{modifier} + wheel: {action}";
    }

    private static string FormatTerrainResetScopeAction()
    {
        string modifier = FormatShortcut(AdminTerrainToolConfig.TerrainToolModifierKey);
        return string.IsNullOrWhiteSpace(modifier)
            ? "Set Terrain Tool Modifier Key to switch reset mode."
            : $"Press {modifier}: switch reset mode";
    }

    private static string TerrainResetScopeLabel()
    {
        return CurrentTerrainResetScope == TerrainResetScope.PaintOnly
            ? "Paint Only"
            : "Terrain + Paint";
    }

    private static string FormatShortcut(KeyboardShortcut shortcut)
    {
        List<string> keys = shortcut.Modifiers
            .Select(FormatKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToList();

        string mainKey = FormatKey(shortcut.MainKey);
        if (!string.IsNullOrWhiteSpace(mainKey))
        {
            keys.Add(mainKey);
        }

        return keys.Count > 0 ? string.Join(" + ", keys) : "";
    }

    private static string FormatKey(KeyCode key)
    {
        return key switch
        {
            KeyCode.None => "",
            KeyCode.LeftAlt or KeyCode.RightAlt => "Alt",
            KeyCode.LeftControl or KeyCode.RightControl => "Ctrl",
            KeyCode.LeftShift or KeyCode.RightShift => "Shift",
            _ => key.ToString()
        };
    }

    private static GameObject CreatePrefab(
        string prefabName,
        bool slope = false,
        bool reset = false,
        bool paint = false)
    {
        GameObject prefab = new(prefabName);
        prefab.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(prefab);

        int pieceLayer = LayerMask.NameToLayer("piece");
        if (pieceLayer >= 0)
        {
            prefab.layer = pieceLayer;
        }

        ZNetView nview = prefab.AddComponent<ZNetView>();
        nview.m_persistent = !reset;
        nview.m_distant = false;
        nview.m_type = ZDO.ObjectType.Solid;
        nview.m_syncInitialScale = true;

        Piece piece = prefab.AddComponent<Piece>();
        piece.m_name = paint
            ? "ZoneSavior Paint Proxy"
            : reset
            ? "ZoneSavior Terrain Reset"
            : slope
                ? "ZoneSavior TerrainProxy Slope"
                : "ZoneSavior Terrain Proxy";
        piece.m_enabled = true;
        piece.m_category = Piece.PieceCategory.Misc;
        piece.m_groundOnly = false;
        piece.m_groundPiece = false;
        piece.m_clipGround = false;
        piece.m_clipEverything = true;
        piece.m_allowAltGroundPlacement = true;
        piece.m_canBeRemoved = true;
        piece.m_noClipping = true;
        piece.m_resources = Array.Empty<Piece.Requirement>();

        prefab.AddComponent<ZoneSaviorTerrainProxy>();
        piece.m_icon = GetIcon(slope, reset, paint);
        piece.m_description = GetPieceDescription(prefab);
        return prefab;
    }

    internal static void PreparePieceTooltip(Hud hud, Piece piece)
    {
        if (!hud || !hud.m_pieceDescription)
        {
            return;
        }

        CaptureTooltipFontSize(hud);

        if (!IsProxyPiece(piece))
        {
            hud.m_pieceDescription.fontSize = _defaultTooltipFontSize;
            return;
        }

        piece.m_description = GetPieceDescription(piece.gameObject);
    }

    internal static void ApplyPieceTooltipStyle(Hud hud, Piece piece)
    {
        if (!hud || !hud.m_pieceDescription || !IsProxyPiece(piece))
        {
            return;
        }

        CaptureTooltipFontSize(hud);
        hud.m_pieceDescription.fontSize = Mathf.Max(
            _defaultTooltipFontSize * TooltipFontSizeMultiplier,
            _defaultTooltipFontSize + 1f);
    }

    private static void CaptureTooltipFontSize(Hud hud)
    {
        if (_tooltipHud == hud && _defaultTooltipFontSize >= 0f)
        {
            return;
        }

        _tooltipHud = hud;
        _defaultTooltipFontSize = hud.m_pieceDescription.fontSize;
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
internal static class AdminTerrainToolScenePatch
{
    private static void Postfix()
    {
        AdminTerrainTool.Update();
    }
}

[HarmonyPatch(typeof(ZNetScene), "CreateObject")]
internal static class AdminTerrainToolCreateObjectPatch
{
    private static bool Prefix(ZDO zdo, ref GameObject __result)
    {
        return !AdminTerrainTool.TryCreateLoadedProxy(zdo, ref __result);
    }
}

[HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
internal static class AdminTerrainToolPieceTablePatch
{
    private static void Postfix(PieceTable __instance, Player player)
    {
        AdminTerrainTool.AddToAvailablePieces(__instance, player);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.UpdateKnownRecipesList))]
internal static class AdminTerrainToolKnownRecipesPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(Player __instance)
    {
        AdminTerrainTool.SanitizeKnownRecipePieceTables(__instance);
    }
}

[HarmonyPatch(typeof(Hud), "SetupPieceInfo")]
internal static class AdminTerrainToolTooltipPatch
{
    private static void Prefix(Hud __instance, Piece piece)
    {
        AdminTerrainTool.PreparePieceTooltip(__instance, piece);
    }

    private static void Postfix(Hud __instance, Piece piece)
    {
        AdminTerrainTool.ApplyPieceTooltipStyle(__instance, piece);
    }
}
