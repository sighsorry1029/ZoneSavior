using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using UnityEngine;

namespace ZoneSavior.Tests;

// Exercise the mod's cached native save delegate, not a copied terrain codec. No scene,
// Heightmap, snow raycasts, terrain ownership transfer, or plugin Awake is started.
internal static class Terrain1030Regression
{
    private static int checks;
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static void Run(Assembly mod)
    {
        checks = 0;
        var boundary = new Harmony("ZoneSavior.Tests.Terrain1030");
        var pathGetter = typeof(Application).GetProperty("persistentDataPath", All).GetMethod;
        var managerField = AccessTools.Field(typeof(ZDOMan), "s_instance");
        var netField = AccessTools.Field(typeof(ZNet), "m_instance");
        var serverField = AccessTools.Field(typeof(ZNet), "m_isServer");
        object oldManager = managerField.GetValue(null), oldNet = netField.GetValue(null), oldServer = serverField.GetValue(null);
        var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
        zdo.m_uid = new ZDOID(103030L, 1);
        zdo.Init();
        try
        {
            // Utils' static initializer queries a Unity platform path before its managed
            // GZip functions can run. Only that getter is redirected; Save/Load stay original.
            boundary.Patch(pathGetter, prefix: new HarmonyMethod(typeof(Terrain1030Regression), nameof(TemporaryDataPath)));
            managerField.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZDOMan)));
            netField.SetValue(null, FormatterServices.GetUninitializedObject(typeof(ZNet)));
            serverField.SetValue(null, true);
            var owner = AccessTools.Property(typeof(ZDO), "Owner");
            owner.SetValue(zdo, true);

            var view = (ZNetView)FormatterServices.GetUninitializedObject(typeof(ZNetView));
            AccessTools.Field(typeof(ZNetView), "m_zdo").SetValue(view, zdo);
            var source = CreateCompiler(view);
            Set(source, "m_operations", 17);
            Set(source, "m_lastOpPoint", new Vector3(1, 2, 3));
            Set(source, "m_lastOpRadius", 4.5f);
            Set(source, "m_modifiedHeight", new[] { true, false, true, false });
            Set(source, "m_levelDelta", new[] { 2f, 0f, -3f, 0f });
            Set(source, "m_smoothDelta", new[] { 0.5f, 0f, -0.25f, 0f });
            Set(source, "m_modifiedPaint", new[] { true, false, false, true });
            var paint = new[] { new Color(.1f, .2f, .3f, .4f), default(Color), default(Color), new Color(.5f, .6f, .7f, .8f) };
            Set(source, "m_paintMask", paint);
            Set(source, "m_lastSnowTimerIncrement", 182345.25d);
            Set(source, "m_snowSettled", new[] { true, false, true, false });

            var terrain = mod.GetType("ZoneSavior.ZoneBundleTerrain", true);
            var save = (Action<TerrainComp, bool>)terrain.GetField("SaveCompiler", All).GetValue(null);
            save(source, false);
            byte[] payload = zdo.GetByteArray(ZDOVars.s_TCData);
            Check(payload != null && payload.Length > 0, "native terrain save writes its ZDO payload");
            var package = new ZPackage(Utils.Decompress(payload));
            Check(package.ReadInt() == 2, "cached delegate writes native terrain format 2");
            Check(Get<double>(source, "m_lastSnowTimerIncrement") == 182345.25d, "save preserves the owner's snow timer");
            Check(Get<bool[]>(source, "m_snowSettled").SequenceEqual(new[] { true, false, true, false }), "height save does not rewrite settled-snow state");
            Check(Get<Color[]>(source, "m_paintMask")[3].g == .6f, "height save retains the existing snow/paint mask");

            var restored = CreateCompiler(view);
            Set(restored, "m_snowSettled", new[] { true, true, true, true });
            var load = AccessTools.DeclaredMethod(typeof(TerrainComp), "Load", Type.EmptyTypes);
            Check((bool)load.Invoke(restored, null), "original terrain Load accepts the saved payload");
            Check(Get<int>(restored, "m_operations") == 17 && Get<float>(restored, "m_lastOpRadius") == 4.5f, "operation metadata round-trips");
            Check(Get<Vector3>(restored, "m_lastOpPoint").Equals(new Vector3(1, 2, 3)), "operation position round-trips");
            Check(Get<float[]>(restored, "m_levelDelta").SequenceEqual(new[] { 2f, 0f, -3f, 0f }), "level heights round-trip");
            Check(Get<float[]>(restored, "m_smoothDelta").SequenceEqual(new[] { .5f, 0f, -.25f, 0f }), "smooth heights round-trip");
            Check(Get<bool[]>(restored, "m_modifiedPaint").SequenceEqual(new[] { true, false, false, true }), "paint flags round-trip");
            Check(Get<Color[]>(restored, "m_paintMask")[0].Equals(paint[0]) && Get<Color[]>(restored, "m_paintMask")[3].Equals(paint[3]), "paint and snow channels round-trip");
            Check(Get<double>(restored, "m_lastSnowTimerIncrement") == 182345.25d, "native format 2 preserves the snow timer on reload");
            Check(Get<bool[]>(restored, "m_snowSettled").All(value => !value), "native reload invalidates settled-snow cache");

            uint revision = zdo.DataRevision;
            owner.SetValue(zdo, false);
            Set(source, "m_lastSnowTimerIncrement", 0d);
            save(source, false);
            Check(zdo.DataRevision == revision && zdo.GetByteArray(ZDOVars.s_TCData).SequenceEqual(payload), "non-owner delegate call cannot overwrite terrain");
            owner.SetValue(zdo, true);
            Set(source, "m_initialized", false);
            save(source, false);
            Check(zdo.DataRevision == revision && zdo.GetByteArray(ZDOVars.s_TCData).SequenceEqual(payload), "uninitialized compiler cannot overwrite terrain");
            System.Console.WriteLine("PASS " + checks + " terrain 1.0.30 native-save/ownership/snow round-trip checks; platform path only is stubbed.");
        }
        finally
        {
            zdo.Reset();
            managerField.SetValue(null, oldManager);
            netField.SetValue(null, oldNet);
            serverField.SetValue(null, oldServer);
            boundary.Unpatch(pathGetter, AccessTools.DeclaredMethod(typeof(Terrain1030Regression), nameof(TemporaryDataPath)));
        }
    }

    private static TerrainComp CreateCompiler(ZNetView view)
    {
        var compiler = (TerrainComp)FormatterServices.GetUninitializedObject(typeof(TerrainComp));
        Set(compiler, "m_nview", view);
        Set(compiler, "m_initialized", true);
        Set(compiler, "m_width", 1);
        Set(compiler, "m_pitch", 2);
        Set(compiler, "m_modifiedHeight", new bool[4]);
        Set(compiler, "m_levelDelta", new float[4]);
        Set(compiler, "m_smoothDelta", new float[4]);
        Set(compiler, "m_modifiedPaint", new bool[4]);
        Set(compiler, "m_paintMask", new Color[4]);
        Set(compiler, "m_snowSettled", new bool[4]);
        return compiler;
    }

    private static void Set(TerrainComp compiler, string field, object value) => AccessTools.Field(typeof(TerrainComp), field).SetValue(compiler, value);
    private static T Get<T>(TerrainComp compiler, string field) => (T)AccessTools.Field(typeof(TerrainComp), field).GetValue(compiler);
    private static bool TemporaryDataPath(ref string __result) { __result = Path.GetTempPath(); return false; }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); checks++; }
}
