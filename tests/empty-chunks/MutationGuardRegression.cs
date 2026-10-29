// Runs the shipped iterators and guard decisions. Only scene-bound lookup/destruction and
// network notification are spies; no Unity scene, live ZDO, world file, or socket is touched.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Fixture = ZoneSavior.Tests.EmptyChunkSelectionChecks.Fixture;
using static ZoneSavior.Tests.ProbeContext;

namespace ZoneSavior.Tests;

internal static class MutationGuardRegression
{
    private static readonly Harmony Spies = new("ZoneSavior.EmptyChunkTests.MutationBoundaries");
    private static Type Commands => Plugin.GetType("ZoneSavior.ZoneBundleCommands", true)!;
    private static Type Coroutines => Plugin.GetType("ZoneSavior.ZoneSaviorCoroutines", true)!;
    private static readonly List<ZDO> Objects = new();
    private static int removed, flushed;

    internal static void Run()
    {
        try
        {
            Test("reset admission retains existing eligibility and refuses an unknown save contract", () =>
            {
                var evaluated = 0;
                Func<Vector2i, bool> allowed = _ => { evaluated++; return true; };
                object?[] args = { new Vector2i(0, 0), allowed, null };
                Equal(true, Call(Commands, null, "CanContinueReset", args)); Equal(1, evaluated);
                args[1] = new Func<Vector2i, bool>(_ => false);
                Equal(false, Call(Commands, null, "CanContinueReset", args));
                Require(((string)args[2]!).Contains("eligible"), "Existing eligibility reason was lost.");
                InstallUpgrade(1); args[1] = allowed;
                Equal(false, Call(Commands, null, "CanContinueReset", args)); Equal(1, evaluated);
                Require(((string)args[2]!).Contains("Empty-chunk"), "Compatibility reason was lost.");
            });
            Test("initial load and clear refusal precedes model, scene and terrain-RPC access", () =>
            {
                InstallUpgrade(1);
                ExpectDenied(() => Iterator("ExecuteLoadRequestAsync", null, null, 0L).MoveNext());
                ExpectDenied(() => Iterator("ClearTargetZoneAsync", new Vector2i(0, 0), null, null).MoveNext());
                ExpectDenied(() => Iterator("PrepareAndApplyLoadWorkAsync", "fixture", null, false, 0f, 0L, null, false).MoveNext());
                ExpectDenied(() => Iterator("RequestClientTerrainApplyAsync", 0L, null, null, false, null).MoveNext());
            });
            Test("late incompatibility stops overwrite after its first batch and flushes prior removals", () =>
                WithClearSpies(() =>
                {
                    var completed = false;
                    var iterator = Iterator("ClearTargetZoneAsync", new Vector2i(0, 0), new Action<int>(_ => completed = true), null);
                    Require(iterator.MoveNext(), "Expected an overwrite batch yield.");
                    Equal(1000, removed); Equal(1, flushed);
                    InstallUpgrade(1);
                    ExpectDenied(() => iterator.MoveNext());
                    Equal(1000, removed); Equal(2, flushed); Require(!completed, "Interrupted overwrite reported success.");
                }));
            Test("disposing a suspended overwrite flushes notifications without completing the operation", () =>
                WithClearSpies(() =>
                {
                    var completed = false;
                    var iterator = Iterator("ClearTargetZoneAsync", new Vector2i(0, 0), new Action<int>(_ => completed = true), null);
                    Require(iterator.MoveNext(), "Expected an overwrite batch yield.");
                    ((IDisposable)iterator).Dispose();
                    Equal(1000, removed); Equal(2, flushed); Require(!completed, "Disposed overwrite reported success.");
                }));
            Test("safe coroutine runner unwinds nested finally blocks after a late guard failure", () =>
            {
                var cleanup = new List<string>(); Exception? failure = null;
                var safe = (IEnumerator)Call(Coroutines, null, "RunSafely", Outer(cleanup), new Action<Exception?>(e => failure = e))!;
                Require(safe.MoveNext(), "Expected nested work to suspend.");
                InstallUpgrade(1); Require(!safe.MoveNext(), "Failed nested work kept running.");
                Require(failure is InvalidOperationException, "Runner did not report the actual guard failure.");
                Equal("inner,outer", string.Join(",", cleanup));
            });
            Test("guarded load failure releases the real operation token and reports a failed result", () =>
            {
                InstallUpgrade(1);
                var loggerField = Commands.GetField("_logger", All)!;
                var originalLogger = loggerField.GetValue(null);
                loggerField.SetValue(null, Activator.CreateInstance(loggerField.FieldType, "MutationGuardRegression"));
                object?[] begin = { "fixture-load", 0, null };
                Equal(true, Call(Commands, null, "TryBeginOperation", begin));
                var token = (int)begin[1]!;
                try
                {
                    var requestType = Plugin.GetType("ZoneSavior.ZoneBundleCommandRequest", true)!;
                    var request = Activator.CreateInstance(requestType)!;
                    requestType.GetProperty("Operation")!.SetValue(request, "zs_loadzone");
                    var resultType = Plugin.GetType("ZoneSavior.ZoneBundleCommandResult", true)!;
                    object? result = null;
                    var callback = Method(typeof(MutationGuardRegression), nameof(Callback)).MakeGenericMethod(resultType)
                        .Invoke(null, new object[] { new Action<object>(value => result = value) })!;
                    var operation = Iterator("ExecuteRequestWithOperationAsync", request, callback, 0L, token);
                    Exception? failure = null;
                    var runner = (IEnumerator)Call(Coroutines, null, "RunSafely", operation, new Action<Exception?>(e => failure = e))!;
                    Require(!runner.MoveNext(), "Refused load should finish without scene work.");
                    Require(failure == null, "Command wrapper did not contain the guard failure.");
                    Require(result != null, "No failed command response was delivered.");
                    Equal(false, resultType.GetProperty("Success")!.GetValue(result));
                    Require(((string)resultType.GetProperty("Message")!.GetValue(result)!).Contains("Empty-chunk"), "Response omitted the refusal reason.");
                    object?[] next = { "fixture-next", 0, null };
                    Equal(true, Call(Commands, null, "TryBeginOperation", next));
                    Call(Commands, null, "EndOperation", next[1]);
                }
                finally { Call(Commands, null, "EndOperation", token); loggerField.SetValue(null, originalLogger); }
            });
        }
        finally { Spies.UnpatchSelf(); Objects.Clear(); }
    }

    private static Action<T> Callback<T>(Action<object> callback) => value => callback(value!);
    private static IEnumerator Iterator(string method, params object?[] args) => (IEnumerator)Call(Commands, null, method, args)!;
    private static void ExpectDenied(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error) when (error.Message.Contains("Empty-chunk")) { return; }
        throw new Exception("Mutation did not stop at the compatibility guard.");
    }
    private static IEnumerator Outer(List<string> cleanup)
    {
        try { yield return Inner(cleanup); }
        finally { cleanup.Add("outer"); }
    }
    private static IEnumerator Inner(List<string> cleanup)
    {
        try
        {
            yield return null;
            // Invoke directly so RunSafely sees the same exception as a production caller.
            var guard = (Action)Delegate.CreateDelegate(typeof(Action), Method(Compatibility, "RequireSafeMutation"));
            guard();
        }
        finally { cleanup.Add("inner"); }
    }
    private static void WithClearSpies(Action action)
    {
        var fixture = new Fixture();
        var singleton = typeof(ZDOMan).GetField("s_instance", All)!;
        var previousManager = singleton.GetValue(null);
        singleton.SetValue(null, fixture.Manager);
        var byId = new Dictionary<ZDOID, ZDO>(); Objects.Clear(); removed = flushed = 0;
        for (uint index = 1; index <= 1001; index++)
        {
            var zdo = (ZDO)FormatterServices.GetUninitializedObject(typeof(ZDO));
            zdo.m_uid = new ZDOID(99L, index); Objects.Add(zdo); byId.Add(zdo.m_uid, zdo);
        }
        fixture.Set("m_objectsByID", byId);
        try
        {
            Spies.Patch(Method(Plugin.GetType("ZoneSavior.ZoneSaviorZones", true)!, "FindObjects"),
                prefix: new HarmonyMethod(typeof(MutationGuardRegression), nameof(FindObjects)));
            Spies.Patch(Method(Commands, "TryDestroyOverwritableZdo"),
                prefix: new HarmonyMethod(typeof(MutationGuardRegression), nameof(Destroy)));
            Spies.Patch(Method(Plugin.GetType("ZoneSavior.ZoneBundleZdoHelper", true)!, "FlushDestroyed"),
                prefix: new HarmonyMethod(typeof(MutationGuardRegression), nameof(Flush)));
            action();
        }
        finally { Spies.UnpatchSelf(); Objects.Clear(); singleton.SetValue(null, previousManager); }
    }
    private static bool FindObjects(List<ZDO> objects) { objects.AddRange(Objects); return false; }
    private static bool Destroy(ref bool __result) { removed++; __result = true; return false; }
    private static bool Flush() { flushed++; return false; }
}
