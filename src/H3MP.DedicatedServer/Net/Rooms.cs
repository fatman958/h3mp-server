namespace H3MP.DedicatedServer.Net;

/// <summary>
/// Index of which players are in which (scene, instance) pair — upstream's
/// GameManager.playersByInstanceByScene. Almost every relay decision is "send to everyone
/// in the sender's scene+instance", so this is the hot lookup. Loop-thread only.
/// </summary>
public sealed class Rooms
{
    private readonly Dictionary<string, Dictionary<int, List<int>>> _byScene = new();
    private static readonly List<int> Empty = new();

    public void Add(string scene, int instance, int playerId)
    {
        if (!_byScene.TryGetValue(scene, out var instances))
            _byScene[scene] = instances = new Dictionary<int, List<int>>();
        if (!instances.TryGetValue(instance, out var players))
            instances[instance] = players = new List<int>();
        if (!players.Contains(playerId)) players.Add(playerId);
    }

    public void Remove(string scene, int instance, int playerId)
    {
        if (!_byScene.TryGetValue(scene, out var instances)) return;
        if (!instances.TryGetValue(instance, out var players)) return;
        players.Remove(playerId);
        if (players.Count == 0) instances.Remove(instance);
        if (instances.Count == 0) _byScene.Remove(scene);
    }

    /// <summary>Everyone in the room, including the asker. Never null.</summary>
    public IReadOnlyList<int> In(string scene, int instance)
        => _byScene.TryGetValue(scene, out var i) && i.TryGetValue(instance, out var p) ? p : Empty;

    /// <summary>True if anyone at all is in this scene, in any instance.</summary>
    public bool AnyoneInScene(string scene) => _byScene.ContainsKey(scene);
}
