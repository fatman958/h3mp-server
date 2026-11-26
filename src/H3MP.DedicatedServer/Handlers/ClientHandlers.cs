using H3MP.DedicatedServer.Net;
using H3MP.DedicatedServer.Protocol;

namespace H3MP.DedicatedServer.Handlers;

internal delegate void PacketHandler(GameServer server, ClientConnection from, Packet packet, int depth);

/// <summary>
/// Client -> server packet dispatch, indexed by ClientPackets ID (upstream: Server.packetHandlers).
/// Phase 1 implements the player/session packets. Everything else is reported as "unhandled" —
/// see docs/ROADMAP.md for the order to add the rest in.
/// </summary>
internal sealed class ClientHandlers
{
    private readonly PacketHandler?[] _table;

    public ClientHandlers()
    {
        int max = Enum.GetValues<ClientPackets>().Max(v => (int)v);
        _table = new PacketHandler?[max + 1];

        Register(ClientPackets.welcomeReceived, WelcomeReceived);
        Register(ClientPackets.ping, Ping);
        Register(ClientPackets.playerState, PlayerState);
        Register(ClientPackets.playerScene, PlayerScene);
        Register(ClientPackets.playerInstance, PlayerInstance);
        Register(ClientPackets.playerIFF, PlayerIFF);
        Register(ClientPackets.playerColor, PlayerColor);
        Register(ClientPackets.batchedPacket, BatchedPackets);
        Register(ClientPackets.clientDisconnect, ClientDisconnect);
        Register(ClientPackets.trackedObject, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.trackedObject, p));
        Register(ClientPackets.trackedObjects, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.trackedObjects, p));
        Register(ClientPackets.objectUpdate, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.objectUpdate, p));
        Register(ClientPackets.giveObjectControl, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.giveObjectControl, p));
        Register(ClientPackets.destroyObject, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.destroyObject, p));
        Register(ClientPackets.objectParent, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.objectParent, p));
        Register(ClientPackets.objectScene, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.objectScene, p));
        Register(ClientPackets.objectInstance, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.objectInstance, p));
        Register(ClientPackets.IDConfirm, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.IDConfirm, p));
        Register(ClientPackets.updateObjectRequest, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.updateObjectRequest, p));
        Register(ClientPackets.DoneSendingUpdaToDateObjects, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.DoneSendingUpdaToDateObjects, p));
        Register(ClientPackets.DoneLoadingScene, (s, c, p) => s.HandleRegistryPacket(c, ClientPackets.DoneLoadingScene, p));
    }

    private void Register(ClientPackets id, Action<GameServer, ClientConnection, Packet> h)
        => _table[(int)id] = (s, c, p, _) => h(s, c, p);

    private void Register(ClientPackets id, PacketHandler h) => _table[(int)id] = h;

    public bool TryGet(int id, out PacketHandler handler)
    {
        handler = null!;
        if (id < 0 || id >= _table.Length || _table[id] == null) return false;
        handler = _table[id]!;
        return true;
    }

    // Sanity limits on untrusted strings (they end up in logs and are rebroadcast to other players).
    private const int MaxName = 64, MaxScene = 256;

    private static string ReadBounded(Packet p, int max, string what)
    {
        string s = p.ReadString();
        if (s.Length > max) throw new ProtocolException($"{what} too long ({s.Length} > {max}).");
        return s;
    }

    // ------------------------------------------------------------------------------------

    /// <summary>[int id][string username][string scene][int instance][int IFF][int colorIndex]</summary>
    private static void WelcomeReceived(GameServer s, ClientConnection c, Packet p)
    {
        if (c.Player != null) { s.Log.Warn($"Client {c.Id} sent welcomeReceived twice; ignoring."); return; }

        int claimedId = p.ReadInt();
        string username = ReadBounded(p, MaxName, "username");
        string scene = ReadBounded(p, MaxScene, "scene");
        int instance = p.ReadInt();
        int iff = p.ReadInt();
        int color = p.ReadInt();

        if (claimedId != c.Id)
            s.Log.Warn($"Client in slot {c.Id} claims ID {claimedId}; using {c.Id}.");

        s.RegisterPlayer(c, username, scene, instance, iff, color);
    }

    /// <summary>[long clientTimeMs] — echoed back so the client can compute round-trip time.</summary>
    private static void Ping(GameServer s, ClientConnection c, Packet p)
        => ServerSend.Ping(c, p.ReadLong());

    /// <summary>
    /// 10 x (Vector3|Quaternion) pose, float health, int maxHealth, short extraLen, extra bytes.
    /// Parsed fully (so malformed packets are dropped, not relayed), then the original body is relayed.
    /// </summary>
    private static void PlayerState(GameServer s, ClientConnection c, Packet p)
    {
        var player = c.Player!;
        byte[] body = p.PeekRemaining().ToArray();

        var pos = p.ReadVector3(); var rot = p.ReadQuaternion();
        var headPos = p.ReadVector3(); var headRot = p.ReadQuaternion();
        var torsoPos = p.ReadVector3(); var torsoRot = p.ReadQuaternion();
        var lPos = p.ReadVector3(); var lRot = p.ReadQuaternion();
        var rPos = p.ReadVector3(); var rRot = p.ReadQuaternion();
        float health = p.ReadFloat();
        int maxHealth = p.ReadInt();
        short extraLen = p.ReadShort();
        if (extraLen < 0 || extraLen > p.UnreadLength) throw new ProtocolException($"bad additional-data length {extraLen}.");

        player.Position = pos; player.Rotation = rot;
        player.HeadPos = headPos; player.HeadRot = headRot;
        player.TorsoPos = torsoPos; player.TorsoRot = torsoRot;
        player.LeftHandPos = lPos; player.LeftHandRot = lRot;
        player.RightHandPos = rPos; player.RightHandRot = rRot;
        player.Health = health; player.MaxHealth = maxHealth;

        ServerSend.RelayPlayerState(s, player, body);
    }

    /// <summary>[string scene]</summary>
    private static void PlayerScene(GameServer s, ClientConnection c, Packet p)
    {
        var player = c.Player!;
        string scene = ReadBounded(p, MaxScene, "scene");
        s.MovePlayer(player, scene, player.Instance);
        ServerSend.PlayerScene(s, player.Id, scene);
        s.RequestObjectSync(c);
    }

    /// <summary>[int instance][bool wasLoading]</summary>
    private static void PlayerInstance(GameServer s, ClientConnection c, Packet p)
    {
        var player = c.Player!;
        int instance = p.ReadInt();
        bool wasLoading = p.ReadBool();
        s.MovePlayer(player, player.Scene, instance);
        ServerSend.PlayerInstance(s, player.Id, instance);
        if (!wasLoading)
            s.RequestObjectSync(c);
    }

    /// <summary>[int IFF]</summary>
    private static void PlayerIFF(GameServer s, ClientConnection c, Packet p)
    {
        int iff = p.ReadInt();
        c.Player!.IFF = iff;
        ServerSend.PlayerIFF(s, c.Id, iff);
    }

    /// <summary>[int playerId][int colorIndex]</summary>
    private static void PlayerColor(GameServer s, ClientConnection c, Packet p)
    {
        int id = p.ReadInt();
        int index = p.ReadInt();
        // Upstream lets the host recolor anyone; with no host here, a client may only set its own.
        if (id != c.Id) { s.Log.Debug($"Client {c.Id} tried to set color of player {id}; ignored."); return; }
        c.Player!.ColorIndex = index;
        ServerSend.PlayerColor(s, c.Id, index);
    }

    /// <summary>
    /// Clients upload most UDP traffic (player state included) as batches:
    /// [int len][int id][payload] repeated until the packet ends. Each item is dispatched as if it had
    /// arrived on its own. Nesting is refused so a hostile client can't build a recursion bomb.
    /// </summary>
    private static void BatchedPackets(GameServer s, ClientConnection c, Packet p, int depth)
    {
        if (depth > 0) throw new ProtocolException("nested batchedPacket.");
        while (p.UnreadLength > 0)
        {
            int length = p.ReadInt();
            if (length <= 0 || length > p.UnreadLength) throw new ProtocolException($"bad batch item length {length}.");
            byte[] item = p.ReadBytes(length);
            s.HandlePacket(c, item, udp: true, depth: depth + 1);
            if (c.Closed) return;
        }
    }

    /// <summary>
    /// [long unixMs] — the client's goodbye. Ignored if it predates this TCP connection, so a late
    /// disconnect from a previous session can't kick whoever reconnected into the same slot.
    /// </summary>
    private static void ClientDisconnect(GameServer s, ClientConnection c, Packet p)
    {
        long sentAt = p.ReadLong();
        if (c.OpenedAtUnixMs < sentAt) s.Disconnect(c, "client requested disconnect");
        else s.Log.Debug($"Ignoring stale clientDisconnect from client {c.Id}");
    }

}
