using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior.Tests;

internal static class ManagedGameBoundary
{
    private static int identityConversions;

    // Valheim 1.0.30 initializes ZDO.c_quaternionIdentity with
    // Quaternion.identity.eulerAngles before assigning its managed serializer delegates.
    // Unity's native conversion is unavailable in the isolated Mono harness. This
    // boundary covers only that exact identity conversion, not general rotation math.
    // Patch the static managed wrapper so Mono does not detour a struct-instance getter.
    internal static void InitializeZdo()
    {
        var conversion = AccessTools.DeclaredMethod(typeof(Quaternion), "Internal_ToEulerRad", new[] { typeof(Quaternion) });
        var identityField = typeof(ZDO).GetField("c_quaternionIdentity", BindingFlags.Static | BindingFlags.NonPublic);
        if (conversion == null || !conversion.IsStatic || conversion.ReturnType != typeof(Vector3) ||
            identityField == null || identityField.FieldType != typeof(Vector3) || !identityField.IsInitOnly)
            throw new InvalidOperationException("The ZDO identity initialization boundary no longer matches the original game contract.");

        var harmony = new Harmony("ZoneSavior.Tests.ManagedGameBoundary");
        var prefix = AccessTools.Method(typeof(ManagedGameBoundary), nameof(IdentityEulerRadians));
        identityConversions = 0;
        try
        {
            harmony.Patch(conversion, prefix: new HarmonyMethod(prefix));
            RuntimeHelpers.RunClassConstructor(typeof(ZDO).TypeHandle);
        }
        finally
        {
            harmony.Unpatch(conversion, prefix);
        }

        var identity = (Vector3)identityField.GetValue(null)!;
        if (identityConversions != 1 || identity.x != 0f || identity.y != 0f || identity.z != 0f)
            throw new InvalidOperationException("The ZDO initializer must perform exactly one identity-to-zero Euler conversion.");

        var patches = Harmony.GetPatchInfo(conversion);
        if (patches != null)
            foreach (var patch in patches.Prefixes)
                if (patch.PatchMethod == prefix)
                    throw new InvalidOperationException("The temporary ZDO initialization boundary was not removed.");
    }

    private static bool IdentityEulerRadians(Quaternion rotation, ref Vector3 __result)
    {
        if (rotation.x != 0f || rotation.y != 0f || rotation.z != 0f || rotation.w != 1f)
            throw new InvalidOperationException($"The ZDO initialization fixture does not support nonidentity rotations: ({rotation.x}, {rotation.y}, {rotation.z}, {rotation.w}).");

        identityConversions++;
        __result = new Vector3(0f, 0f, 0f);
        return false;
    }
}
