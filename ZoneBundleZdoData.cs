using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace ZoneSavior;

internal sealed class ZoneBundleZdoData
{
    private const string Prefix = "zs-zdo-v1:";
    private const int Version = 1;
    private const int MaxDictionaryEntries = 100000;

    public Dictionary<int, string> Strings { get; private set; } = [];
    public Dictionary<int, float> Floats { get; private set; } = [];
    public Dictionary<int, int> Ints { get; private set; } = [];
    public Dictionary<int, long> Longs { get; private set; } = [];
    public Dictionary<int, Vector3> Vecs { get; private set; } = [];
    public Dictionary<int, Quaternion> Quats { get; private set; } = [];
    public Dictionary<int, byte[]> ByteArrays { get; private set; } = [];

    public bool Persistent { get; private set; }
    public bool Distant { get; private set; }
    public ZDO.ObjectType ObjectType { get; private set; }

    public ZoneBundleZdoData()
    {
    }

    public ZoneBundleZdoData(ZDO zdo)
    {
        Load(zdo);
    }

    public ZoneBundleZdoData(string payload)
    {
        Load(payload);
    }

    public string GetBase64()
    {
        ZPackage package = new();
        package.Write(Version);
        package.Write(Persistent);
        package.Write(Distant);
        package.Write((int)ObjectType);

        WriteDictionary(package, Strings, package.Write);
        WriteDictionary(package, Floats, package.Write);
        WriteDictionary(package, Ints, package.Write);
        WriteDictionary(package, Longs, package.Write);
        WriteDictionary(package, Vecs, package.Write);
        WriteDictionary(package, Quats, package.Write);
        WriteDictionary(package, ByteArrays, bytes => package.Write(bytes ?? []));

        return Prefix + package.GetBase64();
    }

    public void ApplyTo(ZDO zdo)
    {
        zdo.Persistent = Persistent;
        zdo.Distant = Distant;
        zdo.Type = ObjectType;

        foreach (KeyValuePair<int, string> item in Strings)
        {
            zdo.Set(item.Key, item.Value);
        }

        foreach (KeyValuePair<int, float> item in Floats)
        {
            zdo.Set(item.Key, item.Value);
        }

        foreach (KeyValuePair<int, int> item in Ints)
        {
            zdo.Set(item.Key, item.Value);
        }

        foreach (KeyValuePair<int, long> item in Longs)
        {
            zdo.Set(item.Key, item.Value);
        }

        foreach (KeyValuePair<int, Vector3> item in Vecs)
        {
            zdo.Set(item.Key, item.Value);
        }

        foreach (KeyValuePair<int, Quaternion> item in Quats)
        {
            zdo.Set(item.Key, item.Value);
        }

        foreach (KeyValuePair<int, byte[]> item in ByteArrays)
        {
            zdo.Set(item.Key, item.Value.ToArray());
        }
    }

    private void Load(ZDO zdo)
    {
        Persistent = zdo.Persistent;
        Distant = zdo.Distant;
        ObjectType = zdo.Type;

        Strings = ZDOExtraData.GetStrings(zdo.m_uid).ToDictionary(item => item.Key, item => item.Value);
        Floats = ZDOExtraData.GetFloats(zdo.m_uid).ToDictionary(item => item.Key, item => item.Value);
        Ints = ZDOExtraData.GetInts(zdo.m_uid).ToDictionary(item => item.Key, item => item.Value);
        Longs = ZDOExtraData.GetLongs(zdo.m_uid).ToDictionary(item => item.Key, item => item.Value);
        Vecs = ZDOExtraData.GetVec3s(zdo.m_uid).ToDictionary(item => item.Key, item => item.Value);
        Quats = ZDOExtraData.GetQuaternions(zdo.m_uid).ToDictionary(item => item.Key, item => item.Value);
        ByteArrays = ZDOExtraData.GetByteArrays(zdo.m_uid).ToDictionary(item => item.Key, item => item.Value.ToArray());
    }

    private void Load(string payload)
    {
        if (!payload.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Unsupported zone bundle ZDO data format. Legacy bundles are not converted; create a new archive from the live world with the current ZoneSavior version.");
        }

        ZPackage package = new(payload.Substring(Prefix.Length));
        int version = package.ReadInt();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported ZoneSavior ZDO data version {version}.");
        }

        Persistent = package.ReadBool();
        Distant = package.ReadBool();
        ObjectType = (ZDO.ObjectType)package.ReadInt();

        Strings = ReadDictionary(package, () => package.ReadString());
        Floats = ReadDictionary(package, () => package.ReadSingle());
        Ints = ReadDictionary(package, () => package.ReadInt());
        Longs = ReadDictionary(package, () => package.ReadLong());
        Vecs = ReadDictionary(package, () => package.ReadVector3());
        Quats = ReadDictionary(package, () => package.ReadQuaternion());
        ByteArrays = ReadDictionary(package, () => package.ReadByteArray().ToArray());
    }

    private static void WriteDictionary<T>(ZPackage package, Dictionary<int, T> values, Action<T> writeValue)
    {
        package.Write(values.Count);
        foreach (KeyValuePair<int, T> item in values.OrderBy(item => item.Key))
        {
            package.Write(item.Key);
            writeValue(item.Value);
        }
    }

    private static Dictionary<int, T> ReadDictionary<T>(ZPackage package, Func<T> readValue)
    {
        int count = package.ReadInt();
        if (count < 0 || count > MaxDictionaryEntries)
        {
            throw new InvalidDataException($"Zone bundle ZDO dictionary count {count} is invalid.");
        }

        Dictionary<int, T> values = new(count);
        for (int i = 0; i < count; i++)
        {
            int key = package.ReadInt();
            values[key] = readValue();
        }

        return values;
    }
}

internal static class ZoneBundleZdoHelper
{
    public static ZDO? Init(GameObject prefab, Vector3 position, Quaternion rotation, Vector3? scale, ZoneBundleZdoData data)
    {
        if (ZDOMan.instance == null || !prefab)
        {
            return null;
        }

        int prefabHash = StringExtensionMethods.GetStableHashCode(prefab.name);
        ZDO zdo = ZDOMan.instance.CreateNewZDO(position, prefabHash);
        zdo.SetPrefab(prefabHash);
        zdo.SetRotation(rotation);

        ZNetView prefabView = prefab.GetComponent<ZNetView>();
        if (prefabView != null)
        {
            zdo.Persistent = prefabView.m_persistent;
            zdo.Distant = prefabView.m_distant;
            zdo.Type = prefabView.m_type;
        }

        data.ApplyTo(zdo);
        if (scale.HasValue)
        {
            zdo.Set(ZDOVars.s_scaleHash, scale.Value);
        }

        return zdo;
    }

    public static void Destroy(ZDO zdo)
    {
        if (zdo == null || ZDOMan.instance == null)
        {
            return;
        }

        List<ZDO> chain = [];
        HashSet<ZDOID> visited = [];
        ZDO? current = zdo;
        while (current != null)
        {
            ZDOID currentId = current.m_uid;
            if (!visited.Add(currentId) || !CanDestroyZdo(currentId))
            {
                break;
            }

            chain.Add(current);
            ZDOID spawnedConnection = current.GetConnectionZDOID(ZDOExtraData.ConnectionType.Spawned);
            if (spawnedConnection == ZDOID.None ||
                !ZDOMan.instance.m_objectsByID.TryGetValue(spawnedConnection, out ZDO connected) ||
                connected == current)
            {
                break;
            }

            current = connected;
        }

        for (int index = chain.Count - 1; index >= 0; index--)
        {
            DestroySingle(chain[index]);
        }
    }

    private static void DestroySingle(ZDO zdo)
    {
        ZDOID id = zdo.m_uid;
        zdo.SetOwner(ZDOMan.GetSessionID());

        ZNetScene? scene = ZNetScene.instance;
        if (scene != null)
        {
            ZNetView? instance = scene.FindInstance(zdo);
            if (instance != null)
            {
                scene.Destroy(instance.gameObject);
            }
        }

        ZDO? remaining = ZDOMan.instance.GetZDO(id);
        if (remaining != null && remaining.IsValid())
        {
            remaining.SetOwner(ZDOMan.GetSessionID());
            ZDOMan.instance.DestroyZDO(remaining);
            ZDOMan.instance.HandleDestroyedZDO(id);
        }
    }

    public static void FlushDestroyed()
    {
        ZDOMan.instance?.SendDestroyed();
    }

    private static bool CanDestroyZdo(ZDOID id)
    {
        if (Player.m_localPlayer != null && ((Character)Player.m_localPlayer).GetZDOID() == id)
        {
            return false;
        }

        return ZNet.instance == null || !ZNet.instance.m_peers.Any(peer => peer != null && peer.m_characterID == id);
    }
}

