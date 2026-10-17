using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

// Player placement only. Loading network objects and administrative bundle restores
// must not turn into destructive enforcement of a newly edited ban list.
[HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece), new[] { typeof(Piece) })]
internal static class ZoneBuildRestrictions
{
    private static readonly AccessTools.FieldRef<Player, GameObject> PlacementGhost =
        AccessTools.FieldRefAccess<Player, GameObject>("m_placementGhost");

    internal static bool CanPlace(Player player, Piece piece)
    {
        if (!ZoneLimitConfiguration.HasBlockedPieces || player != Player.m_localPlayer || !piece)
        {
            return true;
        }

        GameObject ghost = PlacementGhost(player);
        if (!ghost)
        {
            return true; // Preserve the game's invalid-placement handling.
        }

        Vector2i zone = ZoneSaviorZones.GetZone(ghost.transform.position);
        if (!ZoneLimitConfiguration.TryGetRule(zone, out ZoneLimitRule rule) ||
            !rule.IsPieceBlocked(Utils.GetPrefabName(piece.gameObject)))
        {
            return true;
        }

        string name = string.IsNullOrWhiteSpace(piece.m_name)
            ? Utils.GetPrefabName(piece.gameObject)
            : ZoneSaviorLocalization.MaybeLocalize(piece.m_name);
        player.Message(MessageHud.MessageType.Center, ZoneSaviorLocalization.Format("zs_build_piece_blocked", name));
        return false;
    }

    // The original method refreshes the final ghost position before checking placement.
    // A false return here also prevents its caller from consuming materials/stamina.
    // Skipping Player.PlacePiece (void) instead would still report a successful build.
    [HarmonyPriority(Priority.Last)]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        List<CodeInstruction> codes = new(instructions);
        var updateGhost = AccessTools.DeclaredMethod(typeof(Player), "UpdatePlacementGhost", new[] { typeof(bool) })
            ?? throw new MissingMethodException(typeof(Player).FullName, "UpdatePlacementGhost(bool)");
        int found = -1;
        for (int i = 0; i < codes.Count; i++)
        {
            if (!codes[i].Calls(updateGhost)) continue;
            if (found >= 0) throw new InvalidOperationException("ZoneSavior: multiple ghost updates in Player.TryPlacePiece; cannot safely install piece restrictions.");
            found = i;
        }

        if (found < 0 || found + 1 >= codes.Count)
        {
            throw new InvalidOperationException("ZoneSavior: missing ghost update in Player.TryPlacePiece; cannot safely install piece restrictions.");
        }

        Label allowed = generator.DefineLabel();
        codes[found + 1].labels.Add(allowed);
        codes.InsertRange(found + 1, new[]
        {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldarg_1),
            new CodeInstruction(OpCodes.Call, AccessTools.DeclaredMethod(typeof(ZoneBuildRestrictions), nameof(CanPlace))),
            new CodeInstruction(OpCodes.Brtrue, allowed),
            new CodeInstruction(OpCodes.Ldc_I4_0),
            new CodeInstruction(OpCodes.Ret)
        });
        return codes;
    }
}
