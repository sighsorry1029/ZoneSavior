using System;
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
        RemoveCommonEntityVolatileKeys(data);
        RemoveKey(data, ZDOVars.s_alert);
        RemoveKey(data, ZDOVars.s_aggravated);
        RemoveKey(data, ZDOVars.s_follow);
        RemoveKey(data, ZDOVars.s_haveTargetHash);
        RemoveKey(data, ZDOVars.s_huntPlayer);
        RemoveKey(data, ZDOVars.s_patrol);
        RemoveKey(data, ZDOVars.s_patrolPoint);
        RemoveKey(data, ZDOVars.s_spawnPoint);
        RemoveKey(data, ZDOVars.s_targets);
        RemoveKey(data, ZDOVars.s_shownAlertMessage);
        RemoveKey(data, ZDOVars.s_sleeping);
        RemoveKey(data, ZDOVars.s_worldTimeHash);
        RemoveKey(data, ZDOVars.s_spawnTime);
        RemoveKey(data, ZDOVars.s_spawn_time__DontUse);
        RemoveKey(data, ZDOVars.s_SpawnTime__DontUse);
        RemoveKey(data, ZDOVars.s_tameLastFeeding);
        RemoveKey(data, ZDOVars.s_tameTimeLeft);
        RemoveKey(data, ZDOVars.s_lovePoints);
        RemoveKey(data, ZDOVars.s_pregnant);
        RemoveKey(data, ZDOVars.s_seAttrib);
        RemoveKey(data, ZDOVars.s_lastAttack);
        RemoveKey(data, ZDOVars.s_noise);
        RemoveKey(data, ZDOVars.s_tiltrot);
        RemoveKey(data, ZDOVars.s_toRemoveTarget.Key);
        RemoveKey(data, ZDOVars.s_toRemoveTarget.Value);
        RemoveKey(data, ZDOVars.s_toRemoveSpawnID.Key);
        RemoveKey(data, ZDOVars.s_toRemoveSpawnID.Value);
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
