namespace H3MP.DedicatedServer.Net;

/// <summary>
/// Loop-thread-only object registry. Tracked-object envelopes are parsed for ID/routing metadata;
/// type-specific payloads remain opaque and are retained for replay.
/// </summary>
public sealed class ObjectRegistry
{
    public sealed record Entry(int TrackedId, int Controller, string Scene, int Instance, int PacketId, byte[] LastPacket);

    private readonly Dictionary<int, Entry> _objects = new();
    private int _allocationCount;

    public int Count => _objects.Count;
    public IReadOnlyCollection<Entry> Entries => _objects.Values;

    public int AllocateTrackedId()
    {
        int ordinal = _allocationCount;
        _allocationCount = checked(_allocationCount + 1);
        return checked((ordinal / 100) * 100 + 99 - ordinal % 100);
    }

    public void Update(int trackedId, int controller, string scene, int instance, int packetId, byte[] body)
    {
        _objects[trackedId] = new Entry(trackedId, controller, scene, instance, packetId, (byte[])body.Clone());
    }

    public void Remove(int trackedId) => _objects.Remove(trackedId);

    public void RemoveController(int controller)
    {
        foreach (var id in _objects.Where(o => o.Value.Controller == controller).Select(o => o.Key).ToArray())
            _objects.Remove(id);
    }

    public void MoveController(int controller, string scene, int instance)
    {
        foreach (var id in _objects.Where(o => o.Value.Controller == controller).Select(o => o.Key).ToArray())
        {
            var entry = _objects[id];
            _objects[id] = entry with { Scene = scene, Instance = instance };
        }
    }

    public Entry? Get(int trackedId) => _objects.TryGetValue(trackedId, out var entry) ? entry : null;
}
