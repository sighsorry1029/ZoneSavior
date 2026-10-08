using System;

namespace ZoneSavior;

internal sealed class ZoneRpcRegistrar
{
    internal static long ServerPeerId => ZNet.instance?.GetServerPeer()?.m_uid ?? 0L;
    private ZRoutedRpc? _registeredRoutedRpc;

    public bool EnsureRegistered(Action<ZRoutedRpc> register)
    {
        ZRoutedRpc? routedRpc = ZRoutedRpc.instance;
        if (routedRpc == null || _registeredRoutedRpc == routedRpc)
        {
            return false;
        }

        register(routedRpc);
        _registeredRoutedRpc = routedRpc;
        return true;
    }

    public void Reset()
    {
        _registeredRoutedRpc = null;
    }

    public static bool IsServerSender(long sender)
    {
        return ZNet.instance != null &&
               !ZNet.instance.IsServer() &&
               ZRoutedRpc.instance != null &&
               sender != 0L && sender == ServerPeerId;
    }
}
