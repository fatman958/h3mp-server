using H3MP.DedicatedServer.Protocol;

namespace H3MP.DedicatedServer.Net;

/// <summary>Server-side record of a connected player (mirrors upstream's networking/Player.cs).</summary>
public sealed class PlayerInfo
{
    public int Id { get; init; }
    public string Username { get; init; } = "";
    public int IFF { get; set; }
    public int ColorIndex { get; set; }
    public string Scene { get; set; } = "";
    public int Instance { get; set; }

    // Latest state received from the client's playerState packets.
    public Vec3 Position, HeadPos, TorsoPos, LeftHandPos, RightHandPos;
    public Quat Rotation = Quat.Identity, HeadRot = Quat.Identity, TorsoRot = Quat.Identity,
                LeftHandRot = Quat.Identity, RightHandRot = Quat.Identity;
    public float Health;
    public int MaxHealth;

    private string? _stateKey;
    /// <summary>UDP coalescing key: only the newest pose per player needs to be delivered each tick.</summary>
    internal string StateKey => _stateKey ??= "ps" + Id;
}
