using System;
using BepInEx.Logging;

namespace ZoneSavior;

internal static class ZoneBundleCommandEndpoint
{
    private const string RequestRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleRequest";
    private const string ResultRpcName = ZoneSaviorPlugin.ModGUID + "_ZoneBundleResult";

    private static readonly ZoneRpcRegistrar RpcRegistrar = new();
    private static ManualLogSource _logger = null!;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        _ = new Terminal.ConsoleCommand(
            ZoneBundleCommands.SaveOperation,
            "(x,z) or (x~x,z~z) tag - Saves SupportFill zone bundles.",
            HandleSaveZoneCommand);
        _ = new Terminal.ConsoleCommand(
            ZoneBundleCommands.LoadOperation,
            "tag [restore|source (x,z)] [to (x,z)] [offset=Y] - Loads saved zone bundles.",
            HandleLoadZoneCommand);
    }

    public static void RegisterRpcs()
    {
        RpcRegistrar.EnsureRegistered(routedRpc =>
        {
            routedRpc.Register<ZPackage>(RequestRpcName, RPC_HandleRequest);
            routedRpc.Register<ZPackage>(ResultRpcName, RPC_HandleResult);
        });
    }

    private static void HandleSaveZoneCommand(Terminal.ConsoleEventArgs args)
    {
        ZoneBundleCommands.EnsureCommandAllowed();
        ZoneBundleCommandRequest request = ZoneBundleCommands.ParseRequest(
            args.ArgsAll,
            ZoneBundleCommands.SaveOperation,
            requireSingleZone: false,
            requireTarget: false);
        DispatchRequest(request, args.Context);
    }

    private static void HandleLoadZoneCommand(Terminal.ConsoleEventArgs args)
    {
        ZoneBundleCommands.EnsureCommandAllowed();
        ZoneBundleCommandRequest request = ZoneBundleCommands.ParseLoadRequest(args.ArgsAll);
        DispatchRequest(request, args.Context);
    }

    private static void DispatchRequest(ZoneBundleCommandRequest request, Terminal context)
    {
        if (ZNet.instance.IsServer())
        {
            ZoneBundleCommands.StartRequest(request, result => ZoneBundleCommands.ShowResult(result, context));
            return;
        }

        RegisterRpcs();

        ZPackage package = new();
        package.Write(ZoneBundleSerialization.Serialize(request));
        ZRoutedRpc.instance.InvokeRoutedRPC(ZRoutedRpc.instance.GetServerPeerID(), RequestRpcName, package);
        context.AddString($"{request.Operation} request sent to server.");
    }

    private static void RPC_HandleRequest(long sender, ZPackage package)
    {
        if (!ZNet.instance || !ZNet.instance.IsServer())
        {
            return;
        }

        try
        {
            if (!ZoneBundleCommands.IsAuthorizedSender(sender))
            {
                SendResult(sender, ZoneBundleCommandResult.Fail("Admin only."));
            }
            else
            {
                ZoneBundleCommandRequest request = ZoneBundleSerialization.Deserialize<ZoneBundleCommandRequest>(package.ReadString());
                ZoneBundleCommands.StartRequest(request, result => SendResult(sender, result), sender);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Zone bundle RPC failed: {ex}");
            SendResult(sender, ZoneBundleCommandResult.Fail(ex.Message));
        }
    }

    private static void SendResult(long target, ZoneBundleCommandResult result)
    {
        ZPackage response = new();
        response.Write(ZoneBundleSerialization.Serialize(result));
        ZRoutedRpc.instance.InvokeRoutedRPC(target, ResultRpcName, response);
    }

    private static void RPC_HandleResult(long sender, ZPackage package)
    {
        if (!ZoneRpcRegistrar.IsServerSender(sender))
        {
            return;
        }

        ZoneBundleCommandResult result = ZoneBundleSerialization.Deserialize<ZoneBundleCommandResult>(package.ReadString());
        ZoneBundleCommands.ShowResult(result, Console.instance);
    }
}
