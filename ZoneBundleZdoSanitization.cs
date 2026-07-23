using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DataEntry = ZoneSavior.ZoneBundleZdoData;

namespace ZoneSavior;

internal static partial class ZoneBundleCommands
{
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

    private static void SanitizeForSave(SaveEntryKind kind, DataEntry data, bool wearNTear)
    {
        if (wearNTear)
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

    private static void SanitizeForLoad(GameObject prefab, DataEntry data)
    {
        if (prefab.GetComponent<WearNTear>())
        {
            RemoveWearNTearVolatileKeys(data);
            return;
        }

        if (!prefab.GetComponent<Tameable>() || !prefab.GetComponent<MonsterAI>())
        {
            return;
        }

        RemoveMonsterVolatileKeys(data);
    }

    private static void RemoveStaleCharacterReferencesAfterLoad()
    {
        List<Character> characters = Character.GetAllCharacters();
        for (int i = characters.Count - 1; i >= 0; i--)
        {
            Character staleCharacter = characters[i];
            if (staleCharacter)
            {
                continue;
            }

            if (!ReferenceEquals(staleCharacter, null))
            {
                try
                {
                    if (EnemyHud.instance)
                    {
                        EnemyHud.instance.RemoveCharacterHud(staleCharacter);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug($"Failed to remove a stale Character HUD reference after zone load: {ex.Message}");
                }
            }

            characters.RemoveAt(i);
        }
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
        data.Floats.Clear();
        data.Longs.Clear();
        data.Vecs.Clear();
        data.Quats.Clear();
        data.ByteArrays.Clear();

        data.Ints[ZDOVars.s_tamed] = 1;
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
