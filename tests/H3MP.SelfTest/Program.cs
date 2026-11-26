using System.Buffers.Binary;
using H3MP.DedicatedServer;
using H3MP.DedicatedServer.Net;
using H3MP.DedicatedServer.Protocol;
using H3MP.SelfTest;

// End-to-end self test: starts the real server on loopback and drives it with hand-written clients
// that speak the wire protocol exactly as the mod's Client does.

int checks = 0, failures = 0;
void Check(bool ok, string what)
{
    checks++;
    if (!ok) failures++;
    Console.WriteLine((ok ? "  ok    " : "  FAIL  ") + what);
}
void Section(string s) => Console.WriteLine($"\n== {s}");

var logLines = new List<string>();
var log = new Logger { Min = LogLevel.Debug };
log.Sink = l => { lock (logLines) logLines.Add(l); };

static Task<T> OnLoop<T>(GameServer s, Func<T> f)
{
    var tcs = new TaskCompletionSource<T>();
    s.Post(() => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } });
    return tcs.Task;
}

static async Task<(TestClient c, List<(ServerPackets id, R r)> got)> Join(
    int port, string name, string scene, int instance = 0, int iff = 0, int color = 0, Action<R>? onWelcome = null)
{
    var c = await TestClient.ConnectAsync(port);
    var w = await c.ExpectTcp(ServerPackets.welcome);
    var copy = new R(w.Bytes);
    w.S(); c.Id = w.I();
    onWelcome?.Invoke(copy);
    await c.SendTcp(new W(ClientPackets.welcomeReceived).I(c.Id).S(name).S(scene).I(instance).I(iff).I(color).Framed());
    c.UdpHello();
    var got = new List<(ServerPackets, R)>();
    while (true)
    {
        var x = await TestClient.Next(c.Tcp, 2000) ?? throw new TestFailure($"{name}: join timed out");
        var id = (ServerPackets)x.id;
        got.Add((id, new R(x.body)));
        if (id == ServerPackets.connectionComplete) break;
    }
    return (c, got);
}

static W State(float health) => new W(ClientPackets.playerState)
    .V(1, 2, 3).Q(0, 0, 0, 1).V(4, 5, 6).Q(0, 0, 0, 1).V(7, 8, 9).Q(0, 0, 0, 1)
    .V(10, 11, 12).Q(0, 0, 0, 1).V(13, 14, 15).Q(0, 0, 0, 1).F(health).I(100).Sh(0);

static float ReadHealth(R r) { r.I(); for (int i = 0; i < 35; i++) r.F(); return r.F(); }

try
{
    // ------------------------------------------------------------------------------------------
    Section("Packet encoding (server Packet vs. independent raw encoding)");
    {
        Check(Packet.Create(5).Write(1).ToFramed().SequenceEqual(new byte[] { 8, 0, 0, 0, 5, 0, 0, 0, 1, 0, 0, 0 }),
              "frame = [int len][int id][fields], little-endian");

        var w = new W(ClientPackets.welcomeReceived).I(-7).S("h\u00e9llo").B(true).L(1L << 40).F(1.5f).V(1, 2, 3).Q(0, 0, 0, 1);
        var p = Packet.Parse(w.Payload());
        Check(p.ReadInt() == (int)ClientPackets.welcomeReceived, "id round-trips");
        Check(p.ReadInt() == -7, "int");
        Check(p.ReadString() == "h?llo", "string: length-prefixed ASCII, non-ASCII -> '?' (same as upstream)");
        Check(p.ReadBool() && p.ReadLong() == 1L << 40 && p.ReadFloat() == 1.5f, "bool/long/float");
        Check(p.ReadVector3() == new Vec3(1, 2, 3) && p.ReadQuaternion() == Quat.Identity, "Vector3/Quaternion");
        Check(p.UnreadLength == 0, "no trailing bytes");

        var r = new R(Packet.Create(ServerPackets.spawnPlayer).Write(3).Write("abc").Write(true).ToArray()[4..]);
        Check(r.I() == 3 && r.S() == "abc" && r.B() && r.Remaining == 0, "server-written packet decodes with independent reader");

        bool threw = false;
        try { var q = Packet.Parse(new byte[] { 1, 0, 0, 0 }); q.ReadInt(); q.ReadInt(); } catch (ProtocolException) { threw = true; }
        Check(threw, "underflow raises ProtocolException (not IndexOutOfRange)");
        threw = false;
        try { Packet.Parse(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }).ReadString(); } catch (ProtocolException) { threw = true; }
        Check(threw, "oversized string length raises ProtocolException");
    }

    // ------------------------------------------------------------------------------------------
    var cfg = new ServerConfig { Port = 0, MaxClientCount = 4, MaxConnectionsPerIp = 0, MaxConnectsPerIpPerMinute = 0, BanFile = null, TickRate = 20, HandshakeTimeoutSeconds = 2, ServerName = "Test Server" };
    var server = new GameServer(cfg, log);
    server.Start();
    int port = server.Port;

    // ------------------------------------------------------------------------------------------
    Section("Handshake: first player");
    var (A, aGot) = await Join(port, "alice", "Sandbox", 0, iff: 1, color: 3, onWelcome: w =>
    {
        Check(w.S().StartsWith("Welcome"), "welcome message");
        Check(w.I() == 1, "first client gets slot 1 (slot 0 is the absent host)");
        Check(w.By() == 20, "tick rate byte");
        Check(!w.B() && w.I() == 1 && w.I() == 0 && w.B(), "colorByIFF/nameplate/radar/radarColor defaults match upstream");
        Check(w.I() == 0 && w.I() == 0 && w.I() == 0 && w.Remaining == 0, "empty max-health / custom-packet / spectator lists, nothing trailing");
    });
    Check(aGot.Count == 2 && aGot[0].id == ServerPackets.connectSync && aGot[1].id == ServerPackets.connectionComplete,
          "after welcomeReceived: connectSync, connectionComplete (no spawns: nobody else here)");
    Check(aGot[0].r.B() == true, "connectSync.inControl = true when alone in the scene");

    Section("Handshake: second player in the same scene");
    var (B, bGot) = await Join(port, "bob", "Sandbox", 0, iff: 2, color: 4);
    Check(B.Id == 2, "second client gets slot 2");
    Check(bGot.Count == 3 && bGot[0].id == ServerPackets.spawnPlayer && bGot[1].id == ServerPackets.connectSync && bGot[2].id == ServerPackets.connectionComplete,
          "newcomer gets: spawnPlayer(existing), connectSync, connectionComplete");
    {
        var s = bGot[0].r;
        Check(s.I() == 1 && s.S() == "alice" && s.S() == "Sandbox" && s.I() == 0 && s.I() == 1 && s.I() == 3 && !s.B(),
              "spawnPlayer carries id/name/scene/instance/IFF/color, join=false");
        Check(bGot[1].r.B() == false, "connectSync.inControl = false when someone is already in the scene");
    }
    {
        var s = await A.ExpectTcp(ServerPackets.spawnPlayer);
        Check(s.I() == 2 && s.S() == "bob" && s.S() == "Sandbox" && s.I() == 0 && s.I() == 2 && s.I() == 4 && s.B(),
              "existing player is told about the newcomer, join=true");
        var request = await A.ExpectTcp(ServerPackets.updateRequest);
        Check(!request.B() && request.I() == 2,
              "existing player receives up-to-date object request for the newcomer");
        await A.SendTcp(new W(ClientPackets.DoneSendingUpdaToDateObjects).I(2).Framed());
        await B.SendTcp(new W(ClientPackets.DoneLoadingScene).Framed());
    }
    await Task.Delay(200);   // let both UDP endpoints bind

    // ------------------------------------------------------------------------------------------
    Section("Player state relay (UDP)");
    {
        var st = State(77f);
        A.SendUdp(st.Framed());
        var r = await B.ExpectUdp(ServerPackets.playerState);
        Check(r.I() == 1, "relayed packet is stamped with sender id");
        Check(r.F() == 1 && r.F() == 2 && r.F() == 3, "position preserved");
        Check(r.Bytes[4..].SequenceEqual(st.Payload()[4..]), "body relayed byte-for-byte");
        Check((await A.Drain(A.Udp, 300)).Count == 0, "sender does not receive its own state");
        Check(A.BadUdpFraming == 0 && B.BadUdpFraming == 0, "UDP batch framing valid");
    }

    Section("UDP coalescing (latest pose wins per tick)");
    {
        for (int i = 1; i <= 6; i++) A.SendUdpBatched(State(i).Framed());
        var got = await B.Drain(B.Udp, 400);
        Check(got.Count >= 1 && got.Count <= 3, $"6 rapid states collapsed to {got.Count} delivered");
        Check(got.Count > 0 && ReadHealth(new R(got[^1].body)) == 6f, "the newest state is the one delivered");
    }

    // ------------------------------------------------------------------------------------------
    Section("Client-style batched uploads (what the real mod sends)");
    {
        A.SendUdpBatched(State(55f).Framed(), new W(ClientPackets.ping).L(31337).Framed());
        Check(ReadHealth(await B.ExpectUdp(ServerPackets.playerState)) == 55f, "player state inside a client batch is relayed");
        Check((await A.ExpectTcp(ServerPackets.ping)).L() == 31337, "every item in a batch is dispatched");

        A.SendUdpBatched(new W(ClientPackets.batchedPacket).Framed());               // batch nested in a batch
        A.SendUdp(new W(ClientPackets.batchedPacket).I(9999).Raw(new byte[] { 1, 2 }).Framed());   // item length lies
        await Task.Delay(250);
        bool nested, badLen;
        lock (logLines)
        {
            nested = logLines.Any(l => l.Contains("Malformed batchedPacket") && l.Contains("nested"));
            badLen = logLines.Any(l => l.Contains("Malformed batchedPacket") && l.Contains("bad batch item length"));
        }
        Check(nested, "nested batch is rejected");
        Check(badLen, "batch item with a lying length is rejected");
        A.SendUdpBatched();
        await Task.Delay(50);
        bool loggedEmptyBatch;
        lock (logLines)
            loggedEmptyBatch = logLines.Any(l => l.Contains("packet in UDP client=1 id=189 name=batchedPacket bytes=4"));
        Check(!loggedEmptyBatch, "empty UDP keepalive batches do not flood Debug logs");
        await A.SendTcp(new W(ClientPackets.ping).L(1).Framed());
        Check((await A.ExpectTcp(ServerPackets.ping)).L() == 1, "connection survives malformed batches");
        await B.Drain(B.Udp, 100);
    }

    // ------------------------------------------------------------------------------------------
    Section("Scene/instance routing");
    {
        await A.SendTcp(new W(ClientPackets.playerScene).S("Arena").Framed());
        var r = await B.ExpectTcp(ServerPackets.playerScene);
        Check(r.I() == 1 && r.S() == "Arena", "other player is told of the scene change");
        A.SendUdpBatched(State(1).Framed());
        Check((await B.Drain(B.Udp, 300)).Count == 0, "states no longer reach players in a different scene");

        await B.SendTcp(new W(ClientPackets.playerScene).S("Arena").Framed());
        var r2 = await A.ExpectTcp(ServerPackets.playerScene);
        Check(r2.I() == 2 && r2.S() == "Arena", "scene change relayed the other way");
        var sceneRequest = await A.ExpectTcp(ServerPackets.updateRequest);
        Check(!sceneRequest.B() && sceneRequest.I() == 2, "new room member triggers an up-to-date object request");
        await A.SendTcp(new W(ClientPackets.DoneSendingUpdaToDateObjects).I(2).Framed());
        A.SendUdpBatched(State(2).Framed());
        await B.ExpectUdp(ServerPackets.playerState);
        Check(true, "states flow again once both are in the same scene");

        await B.SendTcp(new W(ClientPackets.playerInstance).I(7).B(false).Framed());
        var ri = await A.ExpectTcp(ServerPackets.playerInstance);
        Check(ri.I() == 2 && ri.I() == 7, "instance change relayed");
        A.SendUdpBatched(State(3).Framed());
        Check((await B.Drain(B.Udp, 300)).Count == 0, "states don't cross instances");
        await A.SendTcp(new W(ClientPackets.playerInstance).I(7).B(false).Framed());
        var ai = await B.ExpectTcp(ServerPackets.playerInstance);
        Check(ai.I() == 1 && ai.I() == 7, "instance change relayed to the other room member");
        var instanceRequest = await B.ExpectTcp(ServerPackets.updateRequest);
        Check(!instanceRequest.B() && instanceRequest.I() == 1, "instance change requests up-to-date objects");
        await B.SendTcp(new W(ClientPackets.DoneSendingUpdaToDateObjects).I(1).Framed());
        A.SendUdpBatched(State(4).Framed());
        await B.ExpectUdp(ServerPackets.playerState);
        Check(true, "states flow again once instances match");

        await A.SendTcp(new W(ClientPackets.weaponFire).I(123).F(1.25f).Framed());
        var weapon = await B.ExpectTcp(ServerPackets.weaponFire);
        Check(weapon.I() == 123 && weapon.F() == 1.25f, "experimental passthrough relays raw room packets");
        await A.SendTcp(new W(ClientPackets.sosigPickupItem).I(432).Framed());
        var pickup = await B.ExpectTcp(ServerPackets.sosigPickUpItem);
        Check(pickup.I() == 432, "case-variant client/server packet names map to the matching server packet ID");

        var inRoom = await OnLoop(server, () => server.Rooms.In("Arena", 7).OrderBy(x => x).ToArray());
        Check(inRoom.SequenceEqual(new[] { 1, 2 }), "server room index: both players in (Arena, 7)");
        Check(!await OnLoop(server, () => server.Rooms.AnyoneInScene("Sandbox")), "old scene emptied from the index");
    }

    Section("IFF / color / ping");
    {
        await A.SendTcp(new W(ClientPackets.playerIFF).I(3).Framed());
        var r = await B.ExpectTcp(ServerPackets.playerIFF);
        Check(r.I() == 1 && r.I() == 3, "IFF change relayed");

        await A.SendTcp(new W(ClientPackets.playerColor).I(1).I(5).Framed());
        var c = await B.ExpectTcp(ServerPackets.playerColor);
        Check(c.I() == 1 && c.I() == 5, "own color change relayed");

        await A.SendTcp(new W(ClientPackets.playerColor).I(2).I(9).Framed());
        Check((await B.Drain(B.Tcp, 300)).Count == 0, "a client cannot recolor someone else");

        await A.SendTcp(new W(ClientPackets.ping).L(123456789).Framed());
        Check((await A.ExpectTcp(ServerPackets.ping)).L() == 123456789, "ping echoes the client timestamp");
    }

    Section("Tracked object registration");
    {
        var request = new W(ClientPackets.trackedObject)
            .I(-1).S("ExampleObject").I(A.Id).I(A.Id).I(-1).I(17)
            .S("Arena").I(7).B(false).I(1234).B(true)
            .Raw(new byte[] { 0xAB, 0xCD });
        byte[] expected = request.Payload()[4..];

        await A.SendTcp(request.Framed());
        var ownerObject = await A.ExpectTcp(ServerPackets.trackedObject);
        int trackedId = ownerObject.I();
        BinaryPrimitives.WriteInt32LittleEndian(expected, trackedId);
        Check(trackedId == 99 && ownerObject.Bytes.SequenceEqual(expected),
              "creator receives the upstream-compatible server ID while retaining its local waiting index and payload");

        var peerObject = await B.ExpectTcp(ServerPackets.trackedObject);
        Check(peerObject.Bytes.SequenceEqual(expected), "object registration is relayed to peers in the same scene and instance");

        var cached = await OnLoop(server, () => server.Objects.Get(trackedId));
        bool replayClearsLocalIndex = false;
        if (cached is not null)
        {
            var replay = Packet.Parse(cached.LastPacket);
            replay.ReadInt();
            replay.ReadString();
            replay.ReadInt();
            replay.ReadInt();
            replay.ReadInt();
            replayClearsLocalIndex = replay.ReadInt() == -1;
        }
        Check(cached is { Controller: 1, Scene: "Arena", Instance: 7 } && replayClearsLocalIndex,
              "replay cache records the object without the creator-specific local tracked ID");
    }

    Section("TCP framing robustness");
    {
        var ping = new W(ClientPackets.ping).L(42).Framed();
        await A.SendTcpRaw(ping[..3]); await Task.Delay(40);
        await A.SendTcpRaw(ping[3..9]); await Task.Delay(40);
        await A.SendTcpRaw(ping[9..]);
        Check((await A.ExpectTcp(ServerPackets.ping)).L() == 42, "packet split across 3 TCP writes is reassembled");

        await A.SendTcpRaw(new W(ClientPackets.ping).L(1).Framed().Concat(new W(ClientPackets.ping).L(2).Framed()).ToArray());
        Check((await A.ExpectTcp(ServerPackets.ping)).L() == 1 && (await A.ExpectTcp(ServerPackets.ping)).L() == 2,
              "two packets in one TCP write are both processed, in order");
    }

    // ------------------------------------------------------------------------------------------
    Section("Hostile / unexpected input");
    {
        await A.SendTcp(new W(ClientPackets.playerScene).I(9999).Raw(new byte[] { 1, 2, 3 }).Framed());
        await A.SendTcp(new W(ClientPackets.ping).L(7).Framed());
        Check((await A.ExpectTcp(ServerPackets.ping)).L() == 7, "malformed packet is dropped; connection and server survive");
        bool warned; lock (logLines) warned = logLines.Any(l => l.Contains("Malformed") && l.Contains("playerScene"));
        Check(warned, "malformed packet was logged");

        await A.SendTcp(new W(ClientPackets.destroyObject).I(5).Framed());
        await A.SendTcp(new W(ClientPackets.ping).L(8).Framed());
        Check((await B.ExpectTcp(ServerPackets.destroyObject)).I() == 5,
              "object registry packets are relayed to the room");
        await A.ExpectTcp(ServerPackets.ping);
        var unhandled = await OnLoop(server, () => server.UnhandledCounts.ToDictionary(k => k.Key, v => v.Value));
        Check(!unhandled.ContainsKey((int)ClientPackets.destroyObject),
              "registered object packet is no longer counted as unhandled");

        var G = await TestClient.ConnectAsync(port);
        await G.ExpectTcp(ServerPackets.welcome);
        await G.SendTcpRaw(BitConverter.GetBytes(int.MaxValue));
        Check(await G.WaitClosed(1500), "absurd packet length -> that client is disconnected");

        var H = await TestClient.ConnectAsync(port);
        await H.ExpectTcp(ServerPackets.welcome);
        await H.SendTcp(new W(ClientPackets.playerScene).S("Evil").Framed());
        Check((await B.Drain(B.Tcp, 300)).Count == 0, "packets before the welcome handshake are ignored, not relayed");
        Check(await H.WaitClosed(3500), "client that never completes the handshake is dropped (timeout)");
        await A.SendTcp(new W(ClientPackets.ping).L(9).Framed());
        Check((await A.ExpectTcp(ServerPackets.ping)).L() == 9, "other clients unaffected by all of the above");
        G.Dispose(); H.Dispose();
    }

    // ------------------------------------------------------------------------------------------
    Section("Explicit clientDisconnect");
    {
        var (E, eGot) = await Join(port, "eve", "Arena", 7);
        Check(eGot.Any(packet => packet.id == ServerPackets.trackedObject),
              "joining client receives cached tracked objects during the handshake");
        var spawnA = await A.ExpectTcp(ServerPackets.spawnPlayer);
        var reqA = await A.ExpectTcp(ServerPackets.updateRequest);
        var spawnB = await B.ExpectTcp(ServerPackets.spawnPlayer);
        var reqB = await B.ExpectTcp(ServerPackets.updateRequest);
        Check(spawnA.I() == E.Id && spawnB.I() == E.Id,
              "existing players are told about the newcomer");
        Check(!reqA.B() && reqA.I() == E.Id && !reqB.B() && reqB.I() == E.Id,
              "joining a populated room requests up-to-date objects from existing players");
        await E.SendTcp(new W(ClientPackets.DoneLoadingScene).Framed());
        bool loadedBeforePeers = false;
        for (int attempt = 0; attempt < 100 && !loadedBeforePeers; attempt++)
        {
            lock (logLines)
                loadedBeforePeers = logLines.Any(line =>
                    line.Contains($"Client {E.Id} finished scene loading before peer object sync completed."));
            if (!loadedBeforePeers) await Task.Delay(10);
        }
        Check(loadedBeforePeers, "early scene-load completion keeps peer-sync state pending");
        await A.SendTcp(new W(ClientPackets.DoneSendingUpdaToDateObjects).I(E.Id).Framed());
        await B.SendTcp(new W(ClientPackets.DoneSendingUpdaToDateObjects).I(E.Id).Framed());
        Check((await E.ExpectTcp(ServerPackets.trackedObject)).I() >= 0,
              "cached objects are sent when peer sync completes after scene loading");
        await E.SendTcp(new W(ClientPackets.clientDisconnect).L(0).Framed());     // predates this connection
        await E.SendTcp(new W(ClientPackets.ping).L(5).Framed());
        Check((await E.ExpectTcp(ServerPackets.ping)).L() == 5, "stale clientDisconnect (older than the connection) is ignored");
        await E.SendTcp(new W(ClientPackets.clientDisconnect).L(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 1000).Framed());
        Check((await A.ExpectTcp(ServerPackets.clientDisconnect, 3000)).I() == E.Id, "real clientDisconnect removes the player and notifies others");
        Check(await E.WaitClosed(1500), "server closes that connection");
        E.Dispose();
    }

    Section("Disconnect and slot reuse");
    {
        B.Dispose();
        var r = await A.ExpectTcp(ServerPackets.clientDisconnect, 3000);
        Check(r.I() == 2, "remaining player is told who left");
        var inRoom = await OnLoop(server, () => server.Rooms.In("Arena", 7).ToArray());
        Check(inRoom.SequenceEqual(new[] { 1 }), "leaver removed from the room index");
        var (D, dGot) = await Join(port, "dave", "Arena", 7);
        Check(D.Id == 2, "freed slot is reused");
        Check(dGot[0].id == ServerPackets.spawnPlayer && dGot[0].r.I() == 1, "new player sees the one who stayed");
        await A.ExpectTcp(ServerPackets.spawnPlayer);

        Section("Server full");
        var fullCfg = new ServerConfig { Port = 0, MaxClientCount = 1, MaxConnectionsPerIp = 0, MaxConnectsPerIpPerMinute = 0, BanFile = null };
        var full = new GameServer(fullCfg, new Logger { Min = LogLevel.Error });
        full.Start();
        var (X, _) = await Join(full.Port, "x", "S");
        var Y = await TestClient.ConnectAsync(full.Port);
        Check(await Y.WaitClosed(1500), "connection beyond MaxClientCount is refused");
        Check(Y.Tcp.Reader.Count == 0, "...without ever receiving a welcome");
        X.Dispose(); Y.Dispose();
        await full.StopAsync();

        Section("Per-IP connection limit and bans");
        var limCfg = new ServerConfig { Port = 0, MaxClientCount = 4, MaxConnectionsPerIp = 2, MaxConnectsPerIpPerMinute = 0, BanFile = null };
        var lim = new GameServer(limCfg, new Logger { Min = LogLevel.Error });
        lim.Start();
        var (L1, _) = await Join(lim.Port, "l1", "S");
        var (L2, _) = await Join(lim.Port, "l2", "S");
        var L3 = await TestClient.ConnectAsync(lim.Port);
        Check(await L3.WaitClosed(1500), "third simultaneous connection from one IP is refused (MaxConnectionsPerIp = 2)");
        Check(L3.Tcp.Reader.Count == 0, "...without a welcome");
        L1.Dispose();
        await L2.ExpectTcp(ServerPackets.clientDisconnect, 3000);   // server has processed the leaver, slot is free
        var (L4, _) = await Join(lim.Port, "l4", "S");
        Check(L4.Id > 0, "a freed connection allowance can be used again");

        await OnLoop(lim, () => lim.Bans.Add(System.Net.IPAddress.Loopback));
        var L5 = await TestClient.ConnectAsync(lim.Port);
        Check(await L5.WaitClosed(1500), "banned IP is refused");
        await OnLoop(lim, () => lim.Bans.Remove(System.Net.IPAddress.Loopback));
        L4.Dispose();
        await Task.Delay(100);
        var (L6, _) = await Join(lim.Port, "l6", "S");
        Check(L6.Id > 0, "unbanned IP can join again");
        L2.Dispose(); L3.Dispose(); L4.Dispose(); L5.Dispose(); L6.Dispose();
        await lim.StopAsync();

        Section("Connection-rate limit");
        var rateCfg = new ServerConfig { Port = 0, MaxClientCount = 4, MaxConnectionsPerIp = 0, MaxConnectsPerIpPerMinute = 3, BanFile = null };
        var rate = new GameServer(rateCfg, new Logger { Min = LogLevel.Error });
        rate.Start();
        var rateClients = new List<TestClient>();
        for (int i = 0; i < 3; i++) rateClients.Add((await Join(rate.Port, "r" + i, "S")).c);
        var rateExtra = await TestClient.ConnectAsync(rate.Port);
        Check(await rateExtra.WaitClosed(1500), "fourth connection within a minute is refused (MaxConnectsPerIpPerMinute = 3)");
        foreach (var rc in rateClients) rc.Dispose();
        rateExtra.Dispose();
        await rate.StopAsync();

        Section("Packet flood cap");
        var floodCfg = new ServerConfig { Port = 0, MaxClientCount = 2, MaxConnectionsPerIp = 0, MaxConnectsPerIpPerMinute = 0, MaxPacketsPerSecond = 50, BanFile = null };
        var flood = new GameServer(floodCfg, new Logger { Min = LogLevel.Error });
        flood.Start();
        var (F1, _) = await Join(flood.Port, "f1", "S");
        var burst = Enumerable.Range(0, 200).SelectMany(i => new W(ClientPackets.ping).L(i).Framed()).ToArray();
        await F1.SendTcpRaw(burst);
        Check(await F1.WaitClosed(2000), "client sending more than MaxPacketsPerSecond is disconnected");
        F1.Dispose();
        await flood.StopAsync();

        Section("File logging and ban file");
        string logDir = Path.Combine(Path.GetTempPath(), "h3mp-selftest-" + Guid.NewGuid().ToString("N"));
        string logPath = Path.Combine(logDir, "sub", "server.log");
        var fileLog = new Logger { Sink = _ => { }, Min = LogLevel.Trace };
        fileLog.OpenFile(logPath);
        fileLog.Info("hello file");
        fileLog.Packet("in", 1, "welcomeReceived", 3, "TCP", 3,
                       new byte[] { 1, 2, 3 }, includePayload: true);
        string logText;
        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs)) logText = sr.ReadToEnd();
        Check(logText.Contains("hello file") && logText.Contains("[INFO ]") && System.Text.RegularExpressions.Regex.IsMatch(logText, @"^\d{4}-\d{2}-\d{2} "),
              "log lines are written to the file (created with its directory) and carry a date");
        Check(logText.Contains("packet in TCP client=3 id=1 name=welcomeReceived bytes=3") &&
              logText.Contains("hex=010203"),
              "trace packet logs include direction, transport, identity, size, and optional hex");
        fileLog.Dispose();

        string banPath = Path.Combine(logDir, "bans.txt");
        File.WriteAllText(banPath, "# comment\n10.1.2.3\n::ffff:10.9.9.9  # mapped form\nnot-an-ip\n");
        var bl = new BanList(banPath);
        Check(bl.Count == 2 && bl.Contains(System.Net.IPAddress.Parse("10.1.2.3")) && bl.Contains(System.Net.IPAddress.Parse("10.9.9.9")),
              "ban file loads (comments and junk ignored, IPv4-mapped form normalized)");
        bl.Add(System.Net.IPAddress.Parse("10.5.5.5"));
        Check(new BanList(banPath).Contains(System.Net.IPAddress.Parse("10.5.5.5")), "bans persist across restarts");
        try { Directory.Delete(logDir, true); } catch { }

        Section("Graceful shutdown");
        var stopTask = server.StopAsync();
        bool sawClosed = false;
        while (await TestClient.Next(D.Tcp, 2000) is { } x) { if (x.id == (int)ServerPackets.serverClosed) { sawClosed = true; break; } }
        Check(sawClosed, "clients receive serverClosed");
        await stopTask;
        Check(await D.WaitClosed(1000), "connections are closed after shutdown");
        D.Dispose(); A.Dispose();
    }
}
catch (TestFailure ex)
{
    failures++;
    Console.WriteLine("  FAIL  " + ex.Message);
}
catch (Exception ex)
{
    failures++;
    Console.WriteLine("  ERROR " + ex);
}

Console.WriteLine($"\n{checks - failures} of {checks} checks passed" + (failures > 0 ? $" ({failures} FAILED)" : ""));
return failures == 0 ? 0 : 1;
