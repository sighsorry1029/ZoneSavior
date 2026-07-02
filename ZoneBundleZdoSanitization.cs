using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using DataEntry = ZoneSavior.ZoneBundleZdoData;
using DataHelper = ZoneSavior.ZoneBundleZdoHelper;

namespace ZoneSavior;

internal static partial class ZoneBundleCommands
{
    private static int _tamedAnimalOverwriteDestroyDepth;
    private static bool _loggedTamedAnimalDestroyException;

    private static bool TryClassify(ZDO zdo, out SaveEntryKind kind, out GameObject prefab)
    {
        prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
        kind = SaveEntryKind.Static;

        if (!prefab)
        {
            return false;
        }

        if (!prefab.GetComponent<ZNetView>() ||
            prefab.GetComponent<Player>() ||
            prefab.GetComponent<TombStone>() ||
            prefab.GetComponent<ItemDrop>() ||
            prefab.GetComponent<Projectile>() ||
            prefab.GetComponent<Ragdoll>() ||
            prefab.GetComponent<Fish>() ||
            prefab.GetComponent<TerrainComp>() ||
            prefab.GetComponent<TerrainModifier>() ||
            prefab.GetComponent<LocationProxy>())
        {
            return false;
        }

        Character character = prefab.GetComponent<Character>();
        if (character)
        {
            MonsterAI monsterAi = prefab.GetComponent<MonsterAI>();
            if (!monsterAi || character.IsBoss() || zdo.GetBool(ZDOVars.s_eventCreature, false) || !IsTamedMonster(zdo, prefab))
            {
                return false;
            }

            kind = SaveEntryKind.Monster;
        }

        return true;
    }

    private static bool ShouldDeleteForOverwrite(GameObject prefab, ZDO zdo)
    {
        if (prefab.GetComponent<TerrainModifier>())
        {
            return true;
        }

        return TryClassify(zdo, out _, out _);
    }

    private static bool IsTamedMonster(ZDO zdo, GameObject prefab)
    {
        return prefab.GetComponent<Tameable>() != null && zdo.GetBool(ZDOVars.s_tamed, false);
    }

    private static void DestroyOverwritableZdo(GameObject prefab, ZDO zdo)
    {
        if (!IsTamedMonster(zdo, prefab))
        {
            DataHelper.Destroy(zdo);
            return;
        }

        _tamedAnimalOverwriteDestroyDepth++;
        try
        {
            DataHelper.Destroy(zdo);
        }
        finally
        {
            _tamedAnimalOverwriteDestroyDepth--;
            RemoveStaleCharacterReferences();
        }
    }

    internal static Exception? FinalizeTamedAnimalCharacterDestroy(Character character, Exception? exception)
    {
        if (exception == null || _tamedAnimalOverwriteDestroyDepth <= 0)
        {
            return exception;
        }

        RemoveCharacterReference(character);
        if (!_loggedTamedAnimalDestroyException)
        {
            _loggedTamedAnimalDestroyException = true;
            _logger.LogWarning($"Suppressed Character.OnDestroy error while replacing a tamed animal: {exception.GetType().Name}: {exception.Message}");
        }

        return null;
    }

    private static void RemoveStaleCharacterReferences()
    {
        List<Character> characters = Character.GetAllCharacters();
        for (int i = characters.Count - 1; i >= 0; i--)
        {
            if (!characters[i])
            {
                characters.RemoveAt(i);
            }
        }
    }

    private static void RemoveCharacterReference(Character character)
    {
        if (ReferenceEquals(character, null))
        {
            return;
        }

        Character.GetAllCharacters().Remove(character);
        try
        {
            if (EnemyHud.instance)
            {
                EnemyHud.instance.RemoveCharacterHud(character);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"Failed to remove stale tamed animal HUD reference: {ex.Message}");
        }
    }

    private static void SanitizeForSave(SaveEntryKind kind, DataEntry data, string sanitize)
    {
        data.OriginalId = ZDOID.None;
        data.TargetConnectionId = ZDOID.None;
        data.ConnectionHash = 0;
        data.ConnectionType = ZDOExtraData.ConnectionType.None;

        if (string.Equals(sanitize, WearNTearSanitize, StringComparison.Ordinal))
        {
            RemoveWearNTearVolatileKeys(data);
            return;
        }

        if (kind == SaveEntryKind.Monster)
        {
            RemoveMonsterVolatileKeys(data);
            return;
        }
    }

    private static void SanitizeForLoad(ZoneBundleEntry entry, GameObject prefab, DataEntry data)
    {
        data.OriginalId = ZDOID.None;
        data.TargetConnectionId = ZDOID.None;
        data.ConnectionHash = 0;
        data.ConnectionType = ZDOExtraData.ConnectionType.None;

        if (string.Equals(entry.Sanitize, WearNTearSanitize, StringComparison.Ordinal) ||
            (string.IsNullOrEmpty(entry.Sanitize) && prefab.GetComponent<WearNTear>()))
        {
            RemoveWearNTearVolatileKeys(data);
            return;
        }

        if (!string.Equals(entry.Sanitize, MonsterSanitize, StringComparison.Ordinal) &&
            !string.Equals(entry.Sanitize, TamedMonsterSanitize, StringComparison.Ordinal))
        {
            return;
        }

        RemoveMonsterVolatileKeys(data);
    }

    private static void RemoveWearNTearVolatileKeys(DataEntry data)
    {
        RemoveCommonEntityVolatileKeys(data);
        RemoveKey(data, ZDOVars.s_support);
        RemoveKey(data, ZDOVars.s_inUse);
        RemoveKey(data, ZDOVars.s_user);
        RemoveKey(data, ZDOVars.s_zdoidUser.Key);
        RemoveKey(data, ZDOVars.s_zdoidUser.Value);
    }

    private static void RemoveMonsterVolatileKeys(DataEntry data)
    {
        KeepOnly(data.Strings, ZDOVars.s_tamedName, ZDOVars.s_tamedNameAuthor);
        KeepOnly(data.Ints, ZDOVars.s_level, ZDOVars.s_tamed, ZDOVars.s_haveSaddleHash);
        KeepOnly(data.Bools, ZDOVars.s_tamed, ZDOVars.s_haveSaddleHash);

        data.Floats.Clear();
        data.Hashes.Clear();
        data.Longs.Clear();
        data.Vecs.Clear();
        data.Quats.Clear();
        data.ByteArrays.Clear();

        data.Ints[ZDOVars.s_tamed] = 1;
        data.Bools[ZDOVars.s_tamed] = true;
    }

    private static void RemoveCommonEntityVolatileKeys(DataEntry data)
    {
        RemoveKey(data, ZDOVars.s_bodyAVelHash);
        RemoveKey(data, ZDOVars.s_bodyVelHash);
        RemoveKey(data, ZDOVars.s_bodyVelocity);
        RemoveKey(data, ZDOVars.s_velHash);
        RemoveKey(data, ZDOVars.s_initVel);
        RemoveKey(data, ZDOVars.s_forward);
        RemoveKey(data, ZDOVars.s_landed);
        RemoveKey(data, ZDOVars.s_inWater);
        RemoveKey(data, ZDOVars.s_hitDir);
        RemoveKey(data, ZDOVars.s_hitPoint);
        RemoveKey(data, ZDOVars.s_stamina);
        RemoveKey(data, ZDOVars.s_eitr);
        RemoveKey(data, ZDOVars.s_adrenaline);
        RemoveKey(data, ZDOVars.s_dodgeinv);
        RemoveKey(data, ZDOVars.s_startTime);
        RemoveKey(data, ZDOVars.s_lastTime);
        RemoveKey(data, ZDOVars.s_aliveTime);
        RemoveKey(data, ZDOVars.s_accTime);
        RemoveKey(data, ZDOVars.s_worldTimeHash);
    }

    private static void RemoveKey(DataEntry data, int hash)
    {
        data.Strings?.Remove(hash);
        data.Floats?.Remove(hash);
        data.Ints?.Remove(hash);
        data.Bools?.Remove(hash);
        data.Hashes?.Remove(hash);
        data.Longs?.Remove(hash);
        data.Vecs?.Remove(hash);
        data.Quats?.Remove(hash);
        data.ByteArrays?.Remove(hash);
    }

    private static void KeepOnly<T>(Dictionary<int, T> values, params int[] keys)
    {
        HashSet<int> keep = [..keys];
        foreach (int key in values.Keys.ToList())
        {
            if (!keep.Contains(key))
            {
                values.Remove(key);
            }
        }
    }

    private static Vector3 ReadScale(ZDO zdo, GameObject prefab)
    {
        return zdo.GetVec3(ZDOVars.s_scaleHash, prefab.transform.localScale);
    }

    private enum SaveEntryKind
    {
        Static,
        Monster
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.OnDestroy))]
internal static class ZoneBundleTamedAnimalCharacterDestroyPatch
{
    private static Exception? Finalizer(Character __instance, Exception? __exception)
    {
        return ZoneBundleCommands.FinalizeTamedAnimalCharacterDestroy(__instance, __exception);
    }
}
