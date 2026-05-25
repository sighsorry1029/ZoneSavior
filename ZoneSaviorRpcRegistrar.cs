using System;

namespace ZoneSavior;

internal sealed class ZoneRpcRegistrar
{
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
}
