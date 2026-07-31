using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private static readonly AdminTerrainPaintType[] PaintTypeValues = Enum.GetValues(typeof(AdminTerrainPaintType))
        .Cast<AdminTerrainPaintType>()
        .Distinct()
        .OrderBy(value => (int)value)
        .ToArray();

    private static bool TryGetProxyGhost(GameObject ghost, out GameObject proxyGhost)
    {
        proxyGhost = null!;
        if (!ghost)
        {
            return false;
        }

        if (IsProxyObject(ghost))
        {
            proxyGhost = ghost;
            return true;
        }

        ZoneSaviorTerrainProxy proxy = ghost.GetComponentInChildren<ZoneSaviorTerrainProxy>(includeInactive: true);
        if (proxy)
        {
            proxyGhost = proxy.gameObject;
            return true;
        }

        return false;
    }

    public static void PreparePlacementGhost(Player player)
    {
        if (!ShouldShowTo(player) || !player.m_placementGhost || !TryGetProxyGhost(player.m_placementGhost, out GameObject proxyGhost))
        {
            ClearPendingSlopeStart();
            HideAreaLine();
            return;
        }

        if (IsSlopeProxyObject(proxyGhost))
        {
            if (_hasPendingSlopeStart && TryCreateSlopePlacement(_pendingSlopeStart, proxyGhost.transform.position, out SlopePlacement slopePlacement))
            {
                DrawSlopeAreaLineIfChanged(slopePlacement);
            }
            else
            {
                DrawWidthLineIfChanged(proxyGhost.transform.position, proxyGhost.transform.rotation, CurrentSlopeWidth());
            }

            return;
        }

        ClearPendingSlopeStart();
        DrawCircleLineIfChanged(proxyGhost.transform.position, Quaternion.identity, CurrentCircleRadius());
    }

    private static void UpdatePlacementSizingInput()
    {
        Player player = Player.m_localPlayer;
        if (!player || !player.m_placementGhost || !TryGetProxyGhost(player.m_placementGhost, out GameObject proxyGhost))
        {
            return;
        }

        float wheel = Input.GetAxis("Mouse ScrollWheel");
        if (Mathf.Abs(wheel) < 0.001f)
        {
            return;
        }

        float direction = Mathf.Sign(wheel);
        if (IsSlopeProxyObject(proxyGhost))
        {
            if (IsTerrainToolModifierHeld())
            {
                float slopeWidth = Mathf.Clamp(CurrentSlopeWidth() + direction, MinSlopeSize, MaxSlopeSize);
                _runtimeSlopeWidth = slopeWidth;
                UpdatePieceDescriptions();
            }

            return;
        }

        if (IsTerrainProxyObject(proxyGhost) && IsTerrainToolModifierHeld())
        {
            float softness = Mathf.Clamp01(CurrentTerrainEdgeSoftness() + direction * TerrainEdgeSoftnessStep);
            _runtimeTerrainEdgeSoftness = softness;
            UpdatePieceDescriptions();
            return;
        }

        if (IsPaintProxyObject(proxyGhost) && IsTerrainToolModifierHeld())
        {
            _runtimePaintType = RotatePaintType(CurrentPaintType(), direction);
            UpdatePieceDescriptions();
            return;
        }

        float radius = Mathf.Clamp(CurrentCircleRadius() + direction, MinCircleRadius, MaxCircleRadius);
        _runtimeRadius = radius;
        UpdatePieceDescriptions();
    }

    internal static float GetPlacementMouseScrollWheel()
    {
        return ShouldSuppressVanillaPlacementWheelRotation()
            ? 0f
            : ZInput.GetMouseScrollWheel();
    }

    private static bool ShouldSuppressVanillaPlacementWheelRotation()
    {
        Player player = Player.m_localPlayer;
        if (!player || !player.m_placementGhost || !TryGetProxyGhost(player.m_placementGhost, out GameObject proxyGhost))
        {
            return false;
        }

        return (IsSlopeProxyObject(proxyGhost) || IsTerrainProxyObject(proxyGhost) || IsPaintProxyObject(proxyGhost)) &&
               IsTerrainToolModifierHeld();
    }

    private static float CurrentCircleRadius()
    {
        return CurrentRuntimeValue(ref _runtimeRadius, ref _lastConfigRadius, AdminTerrainToolConfig.Radius, MinCircleRadius, MaxCircleRadius);
    }

    private static float CurrentSlopeWidth()
    {
        return CurrentRuntimeValue(ref _runtimeSlopeWidth, ref _lastConfigSlopeWidth, AdminTerrainToolConfig.SlopeWidth, MinSlopeSize, MaxSlopeSize);
    }

    private static float CurrentTerrainEdgeSoftness()
    {
        return CurrentRuntimeValue(ref _runtimeTerrainEdgeSoftness, ref _lastConfigTerrainEdgeSoftness, AdminTerrainToolConfig.TerrainEdgeSoftness, 0f, 1f);
    }

    private static AdminTerrainPaintType CurrentPaintType()
    {
        AdminTerrainPaintType configValue = AdminTerrainToolConfig.PaintType;
        if (!_runtimePaintTypeInitialized || _lastConfigPaintType != configValue)
        {
            _runtimePaintType = configValue;
            _lastConfigPaintType = configValue;
            _runtimePaintTypeInitialized = true;
        }

        if (!PaintTypeValues.Contains(_runtimePaintType))
        {
            _runtimePaintType = configValue;
        }

        return _runtimePaintType;
    }

    private static AdminTerrainPaintType RotatePaintType(AdminTerrainPaintType current, float direction)
    {
        AdminTerrainPaintType[] values = PaintTypeValues;
        int index = Array.IndexOf(values, current);
        if (index < 0)
        {
            index = Array.IndexOf(values, AdminTerrainToolConfig.PaintType);
        }

        if (index < 0)
        {
            index = 0;
        }

        int offset = direction > 0f ? 1 : -1;
        int next = (index + offset + values.Length) % values.Length;
        return values[next];
    }

    private static bool IsTerrainProxyObject(GameObject obj)
    {
        return IsProxyObject(obj) &&
               !IsSlopeProxyObject(obj) &&
               !IsPaintProxyObject(obj) &&
               !IsResetProxyObject(obj) &&
               !IsPaintResetProxyObject(obj);
    }

    private static bool IsTerrainToolModifierHeld()
    {
        KeyboardShortcut shortcut = AdminTerrainToolConfig.TerrainToolModifierKey;
        return IsShortcutHeld(shortcut);
    }

    private static bool IsShortcutHeld(KeyboardShortcut shortcut)
    {
        if (shortcut.MainKey == KeyCode.None)
        {
            return shortcut.Modifiers.All(IsKeyHeld);
        }

        return IsKeyHeld(shortcut.MainKey) && shortcut.Modifiers.All(IsKeyHeld);
    }

    private static bool IsKeyHeld(KeyCode key)
    {
        return key switch
        {
            KeyCode.LeftShift or KeyCode.RightShift => Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift),
            KeyCode.LeftControl or KeyCode.RightControl => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl),
            KeyCode.LeftAlt or KeyCode.RightAlt => Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt),
            KeyCode.None => true,
            _ => Input.GetKey(key)
        };
    }

    private static float CurrentRuntimeValue(ref float runtimeValue, ref float lastConfigValue, float configValue, float min, float max)
    {
        configValue = Mathf.Clamp(configValue, min, max);
        if (float.IsNaN(runtimeValue) || float.IsNaN(lastConfigValue) || !Mathf.Approximately(lastConfigValue, configValue))
        {
            runtimeValue = configValue;
            lastConfigValue = configValue;
        }

        runtimeValue = Mathf.Clamp(runtimeValue, min, max);
        return runtimeValue;
    }

    private static bool ConfigureNewSlopeProxy(ZoneSaviorTerrainProxy proxy, ZDO zdo)
    {
        Vector3 marker = proxy.transform.position;
        if (!_hasPendingSlopeStart)
        {
            _pendingSlopeStart = marker;
            _hasPendingSlopeStart = true;
            _pendingSlopeStartFrame = Time.frameCount;
            ShowMessage("ZoneSavior slope start marker set.");
            proxy.QueueDestroy();
            return false;
        }

        Vector3 start = _pendingSlopeStart;
        _hasPendingSlopeStart = false;
        if (!TryCreateSlopePlacement(start, marker, out SlopePlacement placement))
        {
            _pendingSlopeStart = marker;
            _hasPendingSlopeStart = true;
            _pendingSlopeStartFrame = Time.frameCount;
            ShowMessage("ZoneSavior slope start marker reset.");
            proxy.QueueDestroy();
            return false;
        }

        proxy.transform.SetPositionAndRotation(placement.Center, placement.Rotation);
        zdo.SetPosition(placement.Center);
        zdo.SetRotation(placement.Rotation);
        WriteSettings(zdo, SlopeSettings(placement));
        return true;
    }

    private static bool TryCreateSlopePlacement(Vector3 start, Vector3 end, out SlopePlacement placement)
    {
        Vector3 flat = end - start;
        flat.y = 0f;
        if (flat.sqrMagnitude < 0.01f)
        {
            placement = default;
            return false;
        }

        Vector3 direction = flat.normalized;
        float length = Mathf.Clamp(flat.magnitude, MinSlopeSize, MaxSlopeSize);
        float width = CurrentSlopeWidth();
        Vector3 center = new(
            (start.x + end.x) * 0.5f,
            (start.y + end.y) * 0.5f,
            (start.z + end.z) * 0.5f);
        Quaternion rotation = Quaternion.LookRotation(direction, Vector3.up);

        placement = new SlopePlacement(center, rotation, width, length, end.y - start.y);
        return true;
    }

    private static void ClearPendingSlopeStart()
    {
        if (!_hasPendingSlopeStart || Time.frameCount <= _pendingSlopeStartFrame)
        {
            return;
        }

        _hasPendingSlopeStart = false;
        _pendingSlopeStartFrame = 0;
    }
}

[HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
internal static class AdminTerrainToolPlacementGhostPatch
{
    [HarmonyPriority(Priority.Last)]
    private static void Postfix(Player __instance)
    {
        AdminTerrainTool.PreparePlacementGhost(__instance);
    }
}

[HarmonyPatch(typeof(Player), nameof(Player.UpdatePlacement))]
internal static class AdminTerrainToolPlacementWheelPatch
{
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> code = instructions.ToList();
        MethodInfo original = AccessTools.Method(typeof(ZInput), nameof(ZInput.GetMouseScrollWheel));
        MethodInfo replacement = AccessTools.Method(typeof(AdminTerrainTool), nameof(AdminTerrainTool.GetPlacementMouseScrollWheel));
        if (original == null || replacement == null)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                "ZoneSavior placement wheel patch skipped because a required method could not be resolved.");
            return code;
        }

        const int expectedMatches = 1;
        int matches = code.Count(instruction => instruction.Calls(original));
        if (matches != expectedMatches)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning(
                $"ZoneSavior placement wheel patch expected {expectedMatches} mouse wheel call but found {matches}; leaving original IL unchanged.");
            return code;
        }

        code.Single(instruction => instruction.Calls(original)).operand = replacement;
        return code;
    }
}
