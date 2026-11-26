using H3MP.DedicatedServer.Net;
using H3MP.DedicatedServer.Protocol;

namespace H3MP.DedicatedServer.Handlers;

/// <summary>
/// Builders for server -> client packets. Field order mirrors upstream networking/ServerSend.cs;
/// change nothing here without checking the matching ClientHandle method in the mod.
/// </summary>
internal static class ServerSend
{
    // ---- helpers -------------------------------------------------------------------------

    private static void To(ClientConnection c, Packet p) => c.SendTcp(p.ToFramed());

    /// <summary>All joined players except <paramref name="exceptId"/> (use -1 for everyone).</summary>
    private static void ToAll(GameServer s, Packet p, int exceptId = -1)
    {
        byte[] framed = p.ToFramed();
        foreach (var c in s.Registered())
            if (c.Id != exceptId) c.SendTcp(framed);
    }

    // ---- connection / handshake ----------------------------------------------------------

    public static void Welcome(GameServer s, ClientConnection to)
    {
        var p = Packet.Create(ServerPackets.welcome)
            .Write($"Welcome to {Logger.Clean(s.Config.ServerName)}")
            .Write(to.Id)
            .Write((byte)s.Config.TickRate)
            .Write(s.Config.ColorByIFF)
            .Write(s.Config.NameplateMode)
            .Write(s.Config.RadarMode)
            .Write(s.Config.RadarColor)
            .Write(0)    // max-health entries (scene -> instance -> value); none yet
            .Write(0)    // registered custom packet IDs (mod API); none advertised
            .Write(0);   // spectator hosts; none
        To(to, p);
    }

    public static void SpawnPlayer(ClientConnection to, PlayerInfo player, bool join)
    {
        var p = Packet.Create(ServerPackets.spawnPlayer)
            .Write(player.Id)
            .Write(player.Username)
            .Write(player.Scene)
            .Write(player.Instance)
            .Write(player.IFF)
            .Write(player.ColorIndex)
            .Write(join);
        To(to, p);
    }

    public static void ConnectSync(ClientConnection to, bool inControl)
        => To(to, Packet.Create(ServerPackets.connectSync).Write(inControl));

    public static void ConnectionComplete(ClientConnection to)
        => To(to, Packet.Create(ServerPackets.connectionComplete));

    public static void Ping(ClientConnection to, long clientTime)
        => To(to, Packet.Create(ServerPackets.ping).Write(clientTime));

    public static void TrackedObject(GameServer server, int sourceId, string scene, int instance, byte[] body)
    {
        byte[] framed = Packet.Create(ServerPackets.trackedObject).WriteRaw(body).ToFramed();
        var recipients = new HashSet<int>(server.Rooms.In(scene, instance)) { sourceId };
        foreach (int id in recipients)
            server.GetConnection(id)?.SendTcp(framed);
    }

    public static void ClientDisconnect(GameServer s, int clientId)
        => ToAll(s, Packet.Create(ServerPackets.clientDisconnect).Write(clientId));

    public static void ServerClosed(GameServer s)
        => ToAll(s, Packet.Create(ServerPackets.serverClosed));

    // ---- player state relays -------------------------------------------------------------

    public static void PlayerScene(GameServer s, int clientId, string scene)
        => ToAll(s, Packet.Create(ServerPackets.playerScene).Write(clientId).Write(scene), exceptId: clientId);

    public static void PlayerInstance(GameServer s, int clientId, int instance)
        => ToAll(s, Packet.Create(ServerPackets.playerInstance).Write(clientId).Write(instance), exceptId: clientId);

    public static void PlayerIFF(GameServer s, int clientId, int iff)
        => ToAll(s, Packet.Create(ServerPackets.playerIFF).Write(clientId).Write(iff), exceptId: clientId);

    public static void PlayerColor(GameServer s, int clientId, int colorIndex)
        => ToAll(s, Packet.Create(ServerPackets.playerColor).Write(clientId).Write(colorIndex), exceptId: clientId);

    /// <summary>
    /// Relay a client's pose to everyone in its scene+instance over UDP. Upstream re-stamps the packet
    /// with the ServerPackets ID and inserts the sender's ID right after it; we rebuild the same bytes.
    /// </summary>
    public static void RelayPlayerState(GameServer s, PlayerInfo from, ReadOnlySpan<byte> body)
    {
        var room = s.Rooms.In(from.Scene, from.Instance);
        if (room.Count == 0 || (room.Count == 1 && room[0] == from.Id)) return;

        byte[] framed = Packet.Create(ServerPackets.playerState).Write(from.Id).WriteRaw(body).ToFramed();
        foreach (int id in room)
        {
            if (id == from.Id) continue;
            var target = s.GetConnection(id);
            if (target != null) s.QueueUdp(target, framed, from.StateKey);
        }
    }
}
