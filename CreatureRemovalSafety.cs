using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior;

internal static class CreatureRemovalTracker
{
    private const float PendingLifetimeSeconds = 15f;
    private const float PruneIntervalSeconds = 1f;

    private sealed class PendingRemoval
    {
        public PendingRemoval(Character character, float expiresAt)
        {
            Character = character;
            ExpiresAt = expiresAt;
        }

        public Character Character { get; }
        public float ExpiresAt { get; set; }
    }

    private static readonly Dictionary<int, PendingRemoval> PendingByInstanceId = [];
    private static float _nextPruneAt;

    internal static void Track(GameObject gameObject)
    {
        if (!gameObject)
        {
            return;
        }

        Character character = gameObject.GetComponent<Character>();
        if (!character)
        {
            return;
        }

        int instanceId = character.GetInstanceID();
        float expiresAt = Time.realtimeSinceStartup + PendingLifetimeSeconds;
        if (PendingByInstanceId.TryGetValue(instanceId, out PendingRemoval pending) &&
            ReferenceEquals(pending.Character, character))
        {
            pending.ExpiresAt = expiresAt;
            return;
        }

        PendingByInstanceId[instanceId] = new PendingRemoval(character, expiresAt);
    }

    internal static bool IsPending(Character character)
    {
        if (ReferenceEquals(character, null))
        {
            return false;
        }

        int instanceId = character.GetInstanceID();
        return PendingByInstanceId.TryGetValue(instanceId, out PendingRemoval pending) &&
               ReferenceEquals(pending.Character, character);
    }

    internal static void Complete(Character character)
    {
        if (ReferenceEquals(character, null))
        {
            return;
        }

        int instanceId = character.GetInstanceID();
        if (PendingByInstanceId.TryGetValue(instanceId, out PendingRemoval pending) &&
            ReferenceEquals(pending.Character, character))
        {
            PendingByInstanceId.Remove(instanceId);
        }
    }

    internal static void Update()
    {
        float now = Time.realtimeSinceStartup;
        if (PendingByInstanceId.Count == 0 || now < _nextPruneAt)
        {
            return;
        }

        _nextPruneAt = now + PruneIntervalSeconds;
        List<int> expired = [];
        foreach (KeyValuePair<int, PendingRemoval> pair in PendingByInstanceId)
        {
            if (now >= pair.Value.ExpiresAt)
            {
                expired.Add(pair.Key);
            }
        }

        foreach (int instanceId in expired)
        {
            PendingByInstanceId.Remove(instanceId);
        }
    }

    internal static void RepairCharacterReferences(Character character)
    {
        if (ReferenceEquals(character, null))
        {
            return;
        }

        List<Character> characters = Character.GetAllCharacters();
        for (int i = characters.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(characters[i], character))
            {
                characters.RemoveAt(i);
            }
        }

        TryRemoveCharacterHud(character);
    }

    internal static void SweepStaleCharacterReferences()
    {
        List<Character> characters = Character.GetAllCharacters();
        for (int i = characters.Count - 1; i >= 0; i--)
        {
            if (!characters[i])
            {
                Character staleCharacter = characters[i];
                if (!ReferenceEquals(staleCharacter, null))
                {
                    TryRemoveCharacterHud(staleCharacter);
                }

                characters.RemoveAt(i);
            }
        }
    }

    internal static void Clear()
    {
        PendingByInstanceId.Clear();
        _nextPruneAt = 0f;
    }

    private static void TryRemoveCharacterHud(Character character)
    {
        try
        {
            if (EnemyHud.instance)
            {
                EnemyHud.instance.RemoveCharacterHud(character);
            }
        }
        catch (Exception ex)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogDebug($"Failed to repair a removed Character HUD reference: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Destroy), typeof(GameObject))]
internal static class CreatureRemovalTrackerPatch
{
    [HarmonyPriority(Priority.First)]
    private static void Prefix(GameObject go)
    {
        CreatureRemovalTracker.Track(go);
    }
}

[HarmonyPatch(typeof(StatusEffect), nameof(StatusEffect.RemoveStartEffects))]
internal static class StatusEffectTeardownGuard
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(StatusEffect __instance)
    {
        if (!CreatureRemovalTracker.IsPending(__instance.m_character))
        {
            return true;
        }

        GameObject[] startEffectInstances = __instance.m_startEffectInstances;
        if (startEffectInstances == null || !ZNetScene.instance)
        {
            return false;
        }

        foreach (GameObject effectInstance in startEffectInstances)
        {
            if (!effectInstance)
            {
                continue;
            }

            ZNetView view = effectInstance.GetComponent<ZNetView>();
            if (!view)
            {
                UnityEngine.Object.Destroy(effectInstance);
                continue;
            }

            if (view.IsValid())
            {
                view.ClaimOwnership();
                view.Destroy();
                continue;
            }

            UnityEngine.Object.Destroy(effectInstance);
        }

        __instance.m_startEffectInstances = null;
        return false;
    }
}

[HarmonyPatch(typeof(Character), nameof(Character.OnDestroy))]
internal static class CharacterOnDestroyRepair
{
    [HarmonyPriority(Priority.Last)]
    private static bool Prefix(Character __instance)
    {
        if (__instance.m_seman != null)
        {
            return true;
        }

        try
        {
            CreatureRemovalTracker.RepairCharacterReferences(__instance);
        }
        catch (Exception cleanupException)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning($"Failed to repair an uninitialized Character.OnDestroy: {cleanupException}");
        }
        finally
        {
            CreatureRemovalTracker.Complete(__instance);
        }

        return false;
    }

    [HarmonyPriority(Priority.Last)]
    private static Exception? Finalizer(Character __instance, Exception? __exception)
    {
        bool pending = CreatureRemovalTracker.IsPending(__instance);
        try
        {
            if (pending || __exception != null)
            {
                CreatureRemovalTracker.RepairCharacterReferences(__instance);
            }
        }
        catch (Exception cleanupException)
        {
            ZoneSaviorPlugin.ZoneSaviorLogger.LogWarning($"Failed to repair Character.OnDestroy bookkeeping: {cleanupException}");
        }
        finally
        {
            CreatureRemovalTracker.Complete(__instance);
        }

        return __exception;
    }
}

[HarmonyPatch(typeof(ZNetScene), "OnDestroy")]
internal static class CreatureRemovalSceneCleanupPatch
{
    private static void Postfix()
    {
        CreatureRemovalTracker.Clear();
    }
}
