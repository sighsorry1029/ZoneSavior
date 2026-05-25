using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static partial class AdminTerrainTool
{
    private const float TooltipFontSizeMultiplier = 1.15f;
    private static float _defaultTooltipFontSize = -1f;

    internal static void PreparePieceTooltip(Hud hud, Piece piece)
    {
        if (!hud || !hud.m_pieceDescription)
        {
            return;
        }

        if (_defaultTooltipFontSize < 0f)
        {
            _defaultTooltipFontSize = hud.m_pieceDescription.fontSize;
        }

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

        if (_defaultTooltipFontSize < 0f)
        {
            _defaultTooltipFontSize = hud.m_pieceDescription.fontSize;
        }

        hud.m_pieceDescription.fontSize = Mathf.Max(
            _defaultTooltipFontSize * TooltipFontSizeMultiplier,
            _defaultTooltipFontSize + 1f);
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
