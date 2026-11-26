using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using H3MP.DedicatedServer.Handlers;
using H3MP.DedicatedServer.Protocol;

namespace H3MP.DedicatedServer.Net;

/// <summary>
/// Dedicated H3MP server core. Same transport as the mod's in-game host (one TCP listener and one
/// UDP socket on the same port; slot IDs 1..MaxClientCount, ID 0 is the absent "host player"), but
/// with no Unity/game dependency.
///
/// Threading: socket tasks post work onto a single-reader channel; ONE loop task runs all handlers
/// and the tick, so server state (rooms, slots, queues) needs no locking.
/// </summary>
public sealed class GameServer
{
    public const int UdpMtu = 1300;   // same batching budget as upstream ServerSend.SendBatchedPackets

    public ServerConfig Config { get; }
    public Logger Log { get; }
    public Rooms Rooms { get; } = new();
    public ObjectRegistry Objects { get; } = new();
    public BanList Bans { get; }
    /// <summary>The port actually bound (differs from Config.Port when that is 0).</summary>
    public int Port { get; private set; }

    private readonly ClientConnection?[] _slots;                       // index 0 unused (host slot upstream)
    private readonly Dictionary<int, int> _activeInstances = new();    // instance -> player count (needed for TNH later)
    private readonly Dictionary<int, List<int>> _clientsWaitingUpDate = new();
    private readonly Dictionary<int, List<int>> _loadingClientsWaitingFrom = new();    // instance -> player count (needed for TNH later)
    private readonly Dictionary<int, long> _unhandled = new();
    private readonly Dictionary<string, long> _udpDrops = new(StringComparer.Ordinal);
    private readonly ClientHandlers _handlers = new();
    private readonly Channel<Action> _loop = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<Task> _tasks = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private HttpListener? _metrics;
    private int _tickQueued;
    private readonly Dictionary<System.Net.IPAddress, Queue<long>> _recentConnects = new();   // source IP -> recent connect times (ms)
    private long _lastSweepMs;
    private int _rejectedSinceSweep;

    public GameServer(ServerConfig config, Logger? log = null)
    {
        config.Normalize();
        Config = config;
        Log = log ?? new Logger();
        Bans = new BanList(config.BanFile);
        _slots = new ClientConnection?[config.MaxClientCount + 1];
    }

    internal void LogOutgoingPacket(ClientConnection connection, int id, ReadOnlySpan<byte> payload)
    {
        string name = Enum.IsDefined(typeof(ServerPackets), id)
            ? ((ServerPackets)id).ToString()
            : "unknown";
        Log.Packet("out", id, name, connection.Id, "TCP", payload.Length,
                   payload, Config.LogPacketPayloads);
    }

    public long NowMs => _clock.ElapsedMilliseconds;
    public int PlayerCount => Registered().Count();

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (!IPAddress.TryParse(Config.ListenAddress, out var listen))
            throw new ArgumentException($"ListenAddress \"{Config.ListenAddress}\" is not an IP address.");
        bool v6 = listen.AddressFamily == AddressFamily.InterNetworkV6;

        _tcp = new TcpListener(listen, Config.Port);
        if (v6) _tcp.Server.DualMode = true;       // "::" accepts IPv4 clients too (as IPv4-mapped addresses)
        _tcp.Start();
        Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
        if (v6)
        {
            _udp = new UdpClient(AddressFamily.InterNetworkV6);
            _udp.Client.DualMode = true;
            _udp.Client.Bind(new IPEndPoint(listen, Port));
        }
        else _udp = new UdpClient(new IPEndPoint(listen, Port));

        Log.Info($"\"{Logger.Clean(Config.ServerName)}\" listening on port {Port} (TCP+UDP), " +
                 $"max {Config.MaxClientCount} players, tick rate {Config.TickRate}.");
        Log.Info($"Diagnostics: level={Logger.Clean(Config.MinimumLogLevel, 16)}, " +
                 $"file={(string.IsNullOrWhiteSpace(Config.LogFile) ? "disabled" : Path.GetFullPath(Config.LogFile))}, " +
                 $"packetPayloads={(Config.LogPacketPayloads ? "enabled" : "disabled")}.");

        _tasks.Add(Task.Run(RunLoopAsync));
        _tasks.Add(Task.Run(AcceptLoopAsync));
        _tasks.Add(Task.Run(UdpReceiveLoopAsync));
        _tasks.Add(Task.Run(TickLoopAsync));
        if (Config.MetricsPort > 0) _tasks.Add(Task.Run(MetricsLoopAsync));
    }

    public async Task StopAsync()
    {
        Log.Info("Shutting down...");
        Post(() => ServerSend.ServerClosed(this));
        await Task.Delay(250);                       // give the serverClosed packets a moment to flush
        Post(() => { foreach (var c in _slots) c?.Close(); });
        await Task.Delay(50);

        _cts.Cancel();
        try { _tcp?.Stop(); } catch { }
        try { _udp?.Close(); } catch { }
        try { _metrics?.Stop(); } catch { }
        _loop.Writer.TryComplete();
        await Task.WhenAny(Task.WhenAll(_tasks), Task.Delay(2000));
        Log.Info("Server stopped.");
    }

    /// <summary>Run an action on the game-loop thread. Safe to call from any thread.</summary>
    public void Post(Action action) => _loop.Writer.TryWrite(action);

    // ------------------------------------------------------------------ background tasks

    private async Task MetricsLoopAsync()
    {
        if (!IPAddress.TryParse(Config.MetricsListenAddress, out var address))
        {
            Log.Error($"Could not start metrics listener: invalid address {Config.MetricsListenAddress}");
            return;
        }

        _metrics = new HttpListener();
        string prefix = $"http://{address}:{Config.MetricsPort}/";
        _metrics.Prefixes.Add(prefix);
        try { _metrics.Start(); }
        catch (HttpListenerException ex)
        {
            Log.Error($"Could not start metrics listener on {prefix}: {ex.Message}");
            return;
        }
        Log.Info($"Metrics listener started on {prefix}.");

        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _metrics.GetContextAsync().ConfigureAwait(false); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => ServeMetricsContext(context));
        }
    }

    private async void ServeMetricsContext(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string body;
            string contentType;
            if (path == "/healthz")
            {
                body = "ok\n";
                contentType = "text/plain";
            }
            else
            {
                var tcs = new TaskCompletionSource<string>();
                Post(() => tcs.SetResult(BuildMetrics()));
                body = await tcs.Task.ConfigureAwait(false);
                contentType = "text/plain; version=0.0.4";
            }
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = 200;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        }
        catch (HttpListenerException) { }
        catch (ObjectDisposedException) { }
        finally { context.Response.Close(); }
    }

    private string BuildMetrics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# HELP h3mp_players_connected Current connected players.");
        sb.AppendLine("# TYPE h3mp_players_connected gauge");
        sb.AppendLine($"h3mp_players_connected {PlayerCount}");
        sb.AppendLine("# HELP h3mp_object_registry_entries Objects currently tracked.");
        sb.AppendLine("# TYPE h3mp_object_registry_entries gauge");
        sb.AppendLine($"h3mp_object_registry_entries {Objects.Count}");
        sb.AppendLine("# HELP h3mp_connections_open Open TCP connections.");
        sb.AppendLine("# TYPE h3mp_connections_open gauge");
        sb.AppendLine($"h3mp_connections_open {Connections().Count()}");
        sb.AppendLine("# HELP h3mp_udp_datagrams_dropped_total UDP datagrams discarded by validation.");
        sb.AppendLine("# TYPE h3mp_udp_datagrams_dropped_total counter");
        foreach (var pair in _udpDrops)
            sb.AppendLine($"h3mp_udp_datagrams_dropped_total{{reason=\"{pair.Key}\"}} {pair.Value}");
        sb.AppendLine("# HELP h3mp_unhandled_packets_total Unhandled client packet occurrences.");
        sb.AppendLine("# TYPE h3mp_unhandled_packets_total counter");
        foreach (var pair in _unhandled)
            sb.AppendLine($"h3mp_unhandled_packets_total{{packet=\"{pair.Key}\"}} {pair.Value}");
        return sb.ToString();
    }

    private async Task RunLoopAsync()
    {
        try
        {
            await foreach (var action in _loop.Reader.ReadAllAsync())
            {
                try { action(); }
                catch (Exception ex) { Log.Error("Unhandled error on game loop: " + ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tcp!.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex) { Log.Debug("Accept failed: " + ex.Message); await Task.Delay(10); continue; }
            Post(() => OnAccepted(client));
        }
    }

    private async Task UdpReceiveLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp!.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                // e.g. Windows reports ICMP "port unreachable" from a vanished client as a receive error.
                Log.Debug("UDP receive error: " + ex.Message);
                await Task.Delay(10);
                continue;
            }
            Post(() => OnUdpDatagram(r.Buffer, r.RemoteEndPoint));
        }
    }

    private async Task TickLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / Config.TickRate));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token))
            {
                if (Interlocked.Exchange(ref _tickQueued, 1) == 1) continue;   // loop is behind; don't pile up ticks
                Post(() => { Volatile.Write(ref _tickQueued, 0); Tick(); });
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------------ loop-thread handlers

    private void OnAccepted(TcpClient tcp)
    {
        if (tcp.Client.RemoteEndPoint is not IPEndPoint remoteEp) { try { tcp.Close(); } catch { } return; }   // already gone
        string remote = remoteEp.ToString();

        string? reject = RejectReason(BanList.Norm(remoteEp.Address), NowMs);
        if (reject != null)
        {
            _rejectedSinceSweep++;
            Log.Debug($"{remote} refused: {reject}.");
            try { tcp.Close(); } catch { }
            return;
        }

        for (int id = 1; id < _slots.Length; id++)
        {
            if (_slots[id] != null) continue;
            tcp.NoDelay = true;
            var conn = new ClientConnection(this, id, tcp, NowMs);
            _slots[id] = conn;
            conn.Start();
            Log.Info($"Incoming connection from {remote} assigned slot {id}.");
            ServerSend.Welcome(this, conn);
            return;
        }
        Log.Warn($"{remote} refused: server full.");
        try { tcp.Close(); } catch { }
    }

    /// <summary>Why a new connection from <paramref name="ip"/> must be refused, or null to accept it.</summary>
    private string? RejectReason(IPAddress ip, long now)
    {
        if (Bans.Contains(ip)) return "banned";

        if (Config.MaxConnectionsPerIp > 0)
        {
            int open = 0;
            for (int id = 1; id < _slots.Length; id++)
                if (_slots[id] is { Closed: false } c && c.RemoteAddress.Equals(ip)) open++;
            if (open >= Config.MaxConnectionsPerIp) return "too many simultaneous connections from this address";
        }

        if (Config.MaxConnectsPerIpPerMinute > 0)
        {
            if (!_recentConnects.TryGetValue(ip, out var q)) _recentConnects[ip] = q = new Queue<long>();
            while (q.Count > 0 && now - q.Peek() > 60_000) q.Dequeue();
            if (q.Count >= Config.MaxConnectsPerIpPerMinute) return "connecting too fast";
            q.Enqueue(now);
        }
        return null;
    }

    private void SweepConnectTracking(long now)
    {
        foreach (var ip in _recentConnects.Keys.ToList())
        {
            var q = _recentConnects[ip];
            while (q.Count > 0 && now - q.Peek() > 60_000) q.Dequeue();
            if (q.Count == 0) _recentConnects.Remove(ip);
        }
        if (_rejectedSinceSweep > 0)
        {
            Log.Info($"Refused {_rejectedSinceSweep} connection attempt(s) in the last minute (bans / per-IP limits). Run with -v for details.");
            _rejectedSinceSweep = 0;
        }
    }

    private void OnUdpDatagram(byte[] data, IPEndPoint ep)
    {
        // Client -> server UDP datagram: [int32 clientId][int32 payloadLen][payload]
        // The very first datagram (just the 4-byte id) is how the client announces its UDP endpoint.
        if (data.Length < 4) { RecordUdpDrop("short_header", ep); return; }
        int id = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (id <= 0 || id >= _slots.Length) { RecordUdpDrop("invalid_client_id", ep); return; }
        var conn = _slots[id];
        if (conn == null || conn.Closed) { RecordUdpDrop("unknown_client", ep); return; }

        if (conn.UdpEndPoint == null)
        {
            // Hardening vs. upstream: only accept the binding from the same IP as the TCP connection,
            // so a third party can't claim another client's UDP slot by guessing its ID.
            if (!BanList.Norm(ep.Address).Equals(conn.RemoteAddress)) { RecordUdpDrop("endpoint_ip_mismatch", ep); return; }
            conn.UdpEndPoint = ep;
            Log.Debug($"UDP bound for client {id} at {ep}");
            return;
        }
        if (!conn.UdpEndPoint.Equals(ep)) { RecordUdpDrop("endpoint_mismatch", ep); return; }
        if (data.Length < 8) { RecordUdpDrop("short_payload_header", ep); return; }

        int len = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        if (len <= 0 || len != data.Length - 8) { RecordUdpDrop("invalid_payload_length", ep); return; }
        HandlePacket(conn, data.AsSpan(8, len).ToArray(), udp: true);
    }

    private void RecordUdpDrop(string reason, IPEndPoint endpoint)
    {
        _udpDrops.TryGetValue(reason, out long count);
        _udpDrops[reason] = ++count;
        if (count == 1 || count % 100 == 0)
            Log.Debug($"Dropped UDP datagram reason={reason} remote={endpoint} count={count}.");
    }

    internal void HandlePacket(ClientConnection conn, byte[] payload, bool udp, int depth = 0)
    {
        if (conn.Closed) return;

        if (Config.MaxPacketsPerSecond > 0)
        {
            // Fixed one-second window. Counts every packet, including each item inside a batch and
            // packets this build does not implement (those cost CPU and were previously unlimited).
            long now = NowMs;
            if (now - conn.RateWindowStartMs >= 1000) { conn.RateWindowStartMs = now; conn.PacketsInWindow = 0; }
            if (++conn.PacketsInWindow > Config.MaxPacketsPerSecond)
            {
                Disconnect(conn, $"packet flood (> {Config.MaxPacketsPerSecond}/s)");
                return;
            }
        }

        Packet p = Packet.Parse(payload);
        int id;
        try { id = p.ReadInt(); }
        catch (ProtocolException) { return; }

        string packetName = Enum.IsDefined(typeof(ClientPackets), id)
            ? ((ClientPackets)id).ToString()
            : "unknown";
        bool emptyUdpBatch = udp && id == (int)ClientPackets.batchedPacket && p.UnreadLength == 0;
        if (!emptyUdpBatch)
            Log.Packet("in", id, packetName, conn.Id, udp ? "UDP" : "TCP",
                       payload.Length, payload, Config.LogPacketPayloads);

        if (id < 0)
        {
            // Custom (mod-API) packet IDs are negotiated in the welcome packet; we advertise none.
            Log.Debug($"Ignoring custom packet {id} from client {conn.Id}");
            return;
        }

        // Before the welcome handshake completes only welcomeReceived / ping make sense.
        if (conn.Player == null && id != (int)ClientPackets.welcomeReceived && id != (int)ClientPackets.ping)
        {
            Log.Debug($"Dropping pre-welcome packet {id} from client {conn.Id}");
            return;
        }

        if (!_handlers.TryGet(id, out var handler))
        {
            if (Config.PassthroughRelay && TryPassthrough(conn, id, p)) return;
            NoteUnhandled(conn, id);
            return;
        }

        try { handler(this, conn, p, depth); }
        catch (ProtocolException ex)
        {
            Log.Warn($"Malformed {(ClientPackets)id} from client {conn.Id}: {ex.Message}");
            if (++conn.BadPackets >= 20) Disconnect(conn, "too many malformed packets");
        }
    }

    private static readonly Dictionary<int, int> _passMap = BuildPassMap();

    private static Dictionary<int, int> BuildPassMap()
    {
        var map = new Dictionary<int, int>();
        var serverByName = Enum.GetValues<ServerPackets>()
            .GroupBy(v => v.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (int)g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var cp in Enum.GetValues<ClientPackets>())
        {
            string name = cp.ToString();
            if (name is "registerCustomPacketType" or "addNonSyncScene" or "minigunFire" or
                "welcomeReceived" or "clientDisconnect" or "batchedPacket")
                continue;
            if (serverByName.TryGetValue(name, out int serverId))
                map[(int)cp] = serverId;
        }
        return map;
    }

    /// <summary>Experimental raw relay for packets that have no real handler.</summary>
    private bool TryPassthrough(ClientConnection conn, int id, Packet p)
    {
        if (conn.Player == null || !_passMap.TryGetValue(id, out int serverId))
            return false;

        var room = Rooms.In(conn.Player.Scene, conn.Player.Instance);
        if (room.Count <= 1)
            return true;

        byte[] framed = Packet.Create(serverId).WriteRaw(p.PeekRemaining()).ToFramed();
        foreach (int other in room)
        {
            if (other != conn.Player.Id)
                GetConnection(other)?.SendTcp(framed);
        }
        return true;
    }

    internal void RequestObjectSync(ClientConnection destination)
    {
        if (destination.Player == null) return;
        var sources = Rooms.In(destination.Player.Scene, destination.Player.Instance)
            .Where(id => id != destination.Id)
            .ToArray();

        if (sources.Length == 0)
        {
            Log.Info($"Object sync client={destination.Id} room={Logger.Clean(destination.Player.Scene)}/{destination.Player.Instance}: no peers; sending cached objects.");
            SendRelevantObjects(destination, null);
            return;
        }

        var waiting = _loadingClientsWaitingFrom.ContainsKey(destination.Id)
            ? _loadingClientsWaitingFrom[destination.Id]
            : (_loadingClientsWaitingFrom[destination.Id] = new List<int>());
        waiting.Clear();

        foreach (int source in sources)
        {
            waiting.Add(source);
            if (!_clientsWaitingUpDate.TryGetValue(source, out var destinations))
                _clientsWaitingUpDate[source] = destinations = new List<int>();
            if (!destinations.Contains(destination.Id))
                destinations.Add(destination.Id);

            var sourceConnection = GetConnection(source);
            if (sourceConnection != null)
            {
                byte[] framed = Packet.Create(ServerPackets.updateRequest)
                    .Write(false).Write(destination.Id).ToFramed();
                sourceConnection.SendTcp(framed);
                Log.Info($"Object sync requested source={source} destination={destination.Id} instantiate=false.");
            }
            else
            {
                waiting.Remove(source);
            }
        }

        if (waiting.Count == 0)
        {
            _loadingClientsWaitingFrom.Remove(destination.Id);
            SendRelevantObjects(destination, null);
        }
    }

    private IEnumerable<int> GetWaitingClients(int source)
    {
        if (_clientsWaitingUpDate.TryGetValue(source, out var destinations))
            return destinations.ToArray();
        return Array.Empty<int>();
    }

    private void SendRelevantObjects(ClientConnection destination, int? fromController)
    {
        if (destination.Player == null) return;
        int sent = 0;
        foreach (var entry in Objects.Entries)
        {
            if (entry.Scene != destination.Player.Scene || entry.Instance != destination.Player.Instance)
                continue;
            if (fromController.HasValue && entry.Controller != fromController.Value)
                continue;
            byte[] framed = Packet.Create(ServerPackets.trackedObject)
                .WriteRaw(entry.LastPacket).ToFramed();
            destination.SendTcp(framed);
            sent++;
        }
        Log.Info($"Object sync replay destination={destination.Id} count={sent} controller={(fromController?.ToString() ?? "any")}.");
    }

    private void DoneSendingUpToDateObjects(int source, int forClient)
    {
        Log.Info($"Object sync complete source={source} destination={forClient}.");
        if (_clientsWaitingUpDate.TryGetValue(source, out var destinations))
        {
            destinations.Remove(forClient);
            if (destinations.Count == 0)
                _clientsWaitingUpDate.Remove(source);
        }

        if (_loadingClientsWaitingFrom.TryGetValue(forClient, out var sources))
        {
            sources.Remove(source);
            if (sources.Count == 0)
            {
                _loadingClientsWaitingFrom.Remove(forClient);
                if (GetConnection(forClient) is { } destination)
                    SendRelevantObjects(destination, null);
            }
        }
    }

    private void DoneLoadingScene(int destination)
    {
        Log.Info($"Client {destination} reported scene load complete.");
        if (_loadingClientsWaitingFrom.TryGetValue(destination, out var sources))
        {
            bool stillWaiting = sources.Any(source =>
                _clientsWaitingUpDate.TryGetValue(source, out var waiting) &&
                waiting.Contains(destination));
            if (stillWaiting)
            {
                Log.Debug($"Client {destination} finished scene loading before peer object sync completed.");
                return;
            }

            if (GetConnection(destination) is { } destinationConnection)
                SendRelevantObjects(destinationConnection, null);
            _loadingClientsWaitingFrom.Remove(destination);
        }
    }

    internal void HandleRegistryPacket(ClientConnection conn, ClientPackets packet, Packet p)
    {
        if (conn.Player == null) return;
        byte[] body = p.PeekRemaining().ToArray();

        if (packet == ClientPackets.trackedObject)
        {
            HandleTrackedObject(conn, body);
            return;
        }

        if (packet == ClientPackets.destroyObject)
        {
            if (body.Length >= 4)
            {
                int trackedId = BinaryPrimitives.ReadInt32LittleEndian(body);
                Objects.Remove(trackedId);
                Log.Debug($"Object registry destroy id={trackedId} controller={conn.Id}.");
            }
        }
        else if (packet == ClientPackets.updateObjectRequest)
        {
            if (body.Length >= 5) // bool + full TrackedObjectData
            {
                Packet full = Packet.Parse(body);
                bool instantiate = full.ReadBool();
                int trackedId = full.ReadInt();
                string typeId = full.ReadString();
                int initTracker = full.ReadInt();
                int controller = full.ReadInt();
                int parent = full.ReadInt();
                int localTrackedId = full.ReadInt();
                string scene = full.ReadString();
                int instance = full.ReadInt();

                byte[] fullObject = body.AsSpan(1).ToArray();
                Objects.Update(trackedId, conn.Id, scene, instance,
                               (int)ServerPackets.trackedObject, fullObject);
                Log.Debug($"Object registry update id={trackedId} controller={controller} scene={Logger.Clean(scene)}/{instance} source={conn.Id} type={Logger.Clean(typeId)}.");
                foreach (int waitingClient in GetWaitingClients(conn.Id).ToArray())
                {
                    if (GetConnection(waitingClient) is { } destination)
                    {
                        byte[] framed = Packet.Create(ServerPackets.trackedObject)
                            .WriteRaw(fullObject).ToFramed();
                        destination.SendTcp(framed);
                    }
                }
                _ = instantiate;
                _ = typeId;
                _ = initTracker;
                _ = parent;
                _ = localTrackedId;
            }
        }
        else if (packet != ClientPackets.trackedObjects &&
                 packet != ClientPackets.DoneLoadingScene &&
                 packet != ClientPackets.DoneSendingUpdaToDateObjects &&
                 body.Length >= 4)
        {
            int trackedId;
            if (packet == ClientPackets.trackedObjects)
                trackedId = body.Length >= 5 ? BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1)) : -1;
            else if (packet == ClientPackets.objectUpdate)
                trackedId = body.Length >= 5 ? BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(1)) : -1;
            else
                trackedId = BinaryPrimitives.ReadInt32LittleEndian(body);
            Enum.TryParse<ServerPackets>(packet.ToString(), out var serverPacket);
            if (trackedId >= 0)
                Objects.Update(trackedId, conn.Id, conn.Player.Scene, conn.Player.Instance,
                               (int)serverPacket, body);
        }

        if (packet == ClientPackets.updateObjectRequest)
        {
            return;
        }

        if (packet == ClientPackets.DoneSendingUpdaToDateObjects)
        {
            int forClient = body.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(body) : 0;
            DoneSendingUpToDateObjects(conn.Id, forClient);
            return;
        }

        if (packet == ClientPackets.DoneLoadingScene)
        {
            DoneLoadingScene(conn.Id);
            return;
        }

        TryPassthrough(conn, (int)packet, p);
    }

    private void HandleTrackedObject(ClientConnection conn, byte[] body)
    {
        var packet = Packet.Parse(body);
        packet.ReadInt(); // Client-local tracked ID is replaced with the server ID.
        string typeId = packet.ReadString();
        int initTracker = packet.ReadInt();
        int controller = packet.ReadInt();
        int parent = packet.ReadInt();
        int localTrackedIdOffset = body.Length - packet.UnreadLength;
        packet.ReadInt();
        string scene = packet.ReadString();
        int instance = packet.ReadInt();
        packet.ReadBool(); // sceneInit
        packet.ReadUInt(); // localWaitingIndex
        packet.ReadBool(); // active

        int trackedId = Objects.AllocateTrackedId();
        byte[] responseBody = (byte[])body.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(responseBody, trackedId);

        // The creator needs its local index in the first response to resolve its pending object.
        // Future replays must not carry that client-specific index.
        byte[] cachedBody = (byte[])responseBody.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(cachedBody.AsSpan(localTrackedIdOffset), -1);
        Objects.Update(trackedId, controller, scene, instance,
                       (int)ServerPackets.trackedObject, cachedBody);
        ServerSend.TrackedObject(this, conn.Id, scene, instance, responseBody);
        Log.Debug($"Registered tracked object id={trackedId} controller={controller} source={conn.Id} " +
                  $"scene={Logger.Clean(scene)}/{instance} type={Logger.Clean(typeId)} initTracker={initTracker} parent={parent}.");
    }

    private void NoteUnhandled(ClientConnection conn, int id)
    {
        _unhandled.TryGetValue(id, out long n);
        _unhandled[id] = n + 1;
        if (n == 0)
        {
            string name = Enum.IsDefined(typeof(ClientPackets), id) ? ((ClientPackets)id).ToString() : "unknown";
            Log.Info($"Unhandled client packet {id} ({name}) from client {conn.Id}: not implemented in this build. " +
                     "Further occurrences are counted silently.");
        }
    }

    public IReadOnlyDictionary<int, long> UnhandledCounts => _unhandled;

    private void Tick()
    {
        long now = NowMs;
        long handshakeMs = Config.HandshakeTimeoutSeconds * 1000L;
        if (now - _lastSweepMs >= 60_000) { _lastSweepMs = now; SweepConnectTracking(now); }
        for (int id = 1; id < _slots.Length; id++)
        {
            var conn = _slots[id];
            if (conn == null) continue;
            if (conn.Player == null && now - conn.ConnectedAtMs > handshakeMs)
            {
                Disconnect(conn, "handshake timeout");
                continue;
            }
            FlushUdp(conn);
        }
    }

    // ------------------------------------------------------------------ UDP queue / batching

    /// <summary>
    /// Queue a framed packet for the next tick's UDP batch. A non-null key coalesces: a newer packet
    /// with the same key replaces the queued one (right for "latest state wins" data like player poses).
    /// </summary>
    internal void QueueUdp(ClientConnection c, byte[] framed, string? key = null)
    {
        if (c.Closed || c.UdpEndPoint == null) return;
        if (framed.Length >= 8)
        {
            int packetId = BinaryPrimitives.ReadInt32LittleEndian(framed.AsSpan(4));
            string packetName = Enum.IsDefined(typeof(ServerPackets), packetId)
                ? ((ServerPackets)packetId).ToString()
                : "unknown";
            Log.Packet("out", packetId, packetName, c.Id, "UDP", framed.Length - 4,
                       framed.AsSpan(4), Config.LogPacketPayloads);
        }
        var q = c.UdpQueue;
        if (key != null)
        {
            for (int i = 0; i < q.Count; i++)
                if (q[i].Key == key) { q[i] = new QueuedUdp(key, framed); return; }
        }
        if (q.Count >= 4096) return;   // pathological backlog; UDP is lossy by design
        q.Add(new QueuedUdp(key, framed));
    }

    private void FlushUdp(ClientConnection c)
    {
        var q = c.UdpQueue;
        if (q.Count == 0) return;
        if (c.UdpEndPoint == null || _udp == null) { q.Clear(); return; }

        // Server -> client UDP datagram: [int32 len][int32 batchedPacket][ ([int32 len][payload])... ]
        int i = 0;
        while (i < q.Count)
        {
            int total = 4;                       // the batchedPacket id
            int j = i;
            while (j < q.Count)
            {
                int size = q[j].Framed.Length;
                if (j > i && total + size > UdpMtu) break;
                total += size;
                j++;
            }

            var datagram = new byte[4 + total];
            BinaryPrimitives.WriteInt32LittleEndian(datagram, total);
            BinaryPrimitives.WriteInt32LittleEndian(datagram.AsSpan(4), (int)ServerPackets.batchedPacket);
            int off = 8;
            for (int k = i; k < j; k++)
            {
                q[k].Framed.CopyTo(datagram, off);
                off += q[k].Framed.Length;
            }

            try { _udp.Send(datagram, datagram.Length, c.UdpEndPoint); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                Log.Debug($"UDP send to client {c.Id} failed: {ex.Message}");
            }
            i = j;
        }
        q.Clear();
    }

    // ------------------------------------------------------------------ players

    internal IEnumerable<ClientConnection> Registered()
    {
        for (int id = 1; id < _slots.Length; id++)
        {
            var c = _slots[id];
            if (c != null && c.Player != null && !c.Closed) yield return c;
        }
    }

    /// <summary>Every open connection, including ones that have not finished the welcome handshake.</summary>
    internal IEnumerable<ClientConnection> Connections()
    {
        for (int id = 1; id < _slots.Length; id++)
            if (_slots[id] is { Closed: false } c) yield return c;
    }

    /// <summary>Disconnect every open connection from <paramref name="ip"/>. Returns how many.</summary>
    internal int DisconnectAddress(IPAddress ip, string reason)
    {
        int n = 0;
        foreach (var c in Connections().ToList())
            if (c.RemoteAddress.Equals(ip)) { Disconnect(c, reason); n++; }
        return n;
    }

    internal ClientConnection? GetConnection(int id)
        => id > 0 && id < _slots.Length && _slots[id] is { Closed: false } c ? c : null;

    internal void RegisterPlayer(ClientConnection c, string username, string scene, int instance, int iff, int color)
    {
        var player = new PlayerInfo { Id = c.Id, Username = username, IFF = iff, ColorIndex = color, Scene = scene, Instance = instance };
        var others = Registered().Where(o => o != c).ToList();

        // Same order as upstream ServerClient.SendIntoGame:
        // 1. tell the newcomer about everyone already here (inControl = nobody else in this scene)
        bool inControl = true;
        foreach (var o in others)
        {
            ServerSend.SpawnPlayer(c, o.Player!, join: false);
            inControl &= o.Player!.Scene != scene;
        }
        // 2. register, then tell everyone else about the newcomer
        c.Player = player;
        Rooms.Add(scene, instance, c.Id);
        BumpInstance(instance, +1);
        foreach (var entry in Objects.Entries)
        {
            if (entry.Scene == scene && entry.Instance == instance)
            {
                byte[] framed = Packet.Create(entry.PacketId).WriteRaw(entry.LastPacket).ToFramed();
                c.SendTcp(framed);
            }
        }
        foreach (var o in others) ServerSend.SpawnPlayer(o, player, join: true);
        // 3. ask existing controllers for up-to-date objects, let the client sync, then signal completion
        RequestObjectSync(c);
        ServerSend.ConnectSync(c, inControl);
        ServerSend.ConnectionComplete(c);

        Log.Info($"Player {c.Id} \"{Logger.Clean(username)}\" from {c.RemoteAddress} joined scene \"{Logger.Clean(scene)}\" " +
                 $"instance {instance}. {PlayerCount}/{Config.MaxClientCount} online.");
    }

    internal void MovePlayer(PlayerInfo p, string scene, int instance)
    {
        string oldScene = p.Scene;
        int oldInstance = p.Instance;
        Rooms.Remove(p.Scene, p.Instance, p.Id);
        if (p.Instance != instance)
        {
            BumpInstance(p.Instance, -1);
            BumpInstance(instance, +1);
        }
        p.Scene = scene;
        p.Instance = instance;
        Rooms.Add(scene, instance, p.Id);
        Objects.MoveController(p.Id, scene, instance);
        Log.Info($"Player {p.Id} room changed {Logger.Clean(oldScene)}/{oldInstance} -> {Logger.Clean(scene)}/{instance}.");
    }

    private void BumpInstance(int instance, int delta)
    {
        _activeInstances.TryGetValue(instance, out int n);
        n += delta;
        if (n <= 0) _activeInstances.Remove(instance); else _activeInstances[instance] = n;
    }

    internal void Disconnect(ClientConnection conn, string reason)
    {
        if (conn.Closed) return;
        conn.Close();
        if (_slots[conn.Id] == conn) _slots[conn.Id] = null;

        var p = conn.Player;
        conn.Player = null;
        if (p == null)
        {
            Log.Info($"Client {conn.Id} ({conn.Remote}) dropped before joining ({reason}).");
            return;
        }
        Rooms.Remove(p.Scene, p.Instance, p.Id);
        BumpInstance(p.Instance, -1);
        Objects.RemoveController(p.Id);
        Log.Info($"Player {p.Id} \"{Logger.Clean(p.Username)}\" left ({reason}). {PlayerCount} online.");
        ServerSend.ClientDisconnect(this, p.Id);
    }
}
