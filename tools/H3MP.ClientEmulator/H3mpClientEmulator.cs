using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

internal sealed class H3mpClientEmulator
{
    private const int MaxFrameBytes = 4 * 1024 * 1024;
    private static readonly ConcurrentDictionary<int, byte> EmulatedClientIds = new();
    private readonly EmulatorOptions _options;
    private readonly int _expectedSyncRequests;
    private readonly int _expectedPeerNotifications;
    private readonly float _peerSpacing;
    private readonly Channel<WirePacket> _incoming = Channel.CreateUnbounded<WirePacket>();
    private readonly TaskCompletionSource _joined = NewSignal();
    private readonly TaskCompletionSource _expectedSyncComplete;
    private readonly TaskCompletionSource _allPeersSeen;
    private readonly TaskCompletionSource _allPeerStatesSeen;
    private readonly ConcurrentQueue<int> _syncTargets = new();
    private readonly ConcurrentDictionary<int, byte> _peerStateSenders = new();
    private StandingPose _pose;
    private int _clientId;
    private int _tickRate = 20;
    private int _syncRequestsReceived;
    private int _peerNotificationsReceived;
    private int _posePacketsSent;
    private int _posePacketsReceived;
    private int _trackedObjectsReceived;
    private int _autoPlaced;
    private bool _serverClosed;

    public H3mpClientEmulator(
        EmulatorOptions options,
        int expectedSyncRequests,
        int expectedPeerNotifications,
        float peerSpacing)
    {
        _options = options;
        _expectedSyncRequests = expectedSyncRequests;
        _expectedPeerNotifications = expectedPeerNotifications;
        _peerSpacing = peerSpacing;
        _pose = new StandingPose(options.X, options.Y, options.Z, 0f);
        _expectedSyncComplete = NewSignal();
        _allPeersSeen = NewSignal();
        _allPeerStatesSeen = NewSignal();
        if (expectedSyncRequests == 0) _expectedSyncComplete.TrySetResult();
        if (expectedPeerNotifications == 0) _allPeersSeen.TrySetResult();
        if (expectedPeerNotifications == 0) _allPeerStatesSeen.TrySetResult();
    }

    public Task Joined => _joined.Task;
    public Task ExpectedSyncComplete => _expectedSyncComplete.Task;
    public Task AllPeersSeen => _allPeersSeen.Task;
    public Task AllPeerStatesSeen => _allPeerStatesSeen.Task;

    public async Task RunAsync(CancellationToken stopToken)
    {
        TcpClient? tcp = null;
        UdpClient? udp = null;
        NetworkStream? stream = null;
        Task? tcpReader = null;
        Task? udpReader = null;

        try
        {
            tcp = await ConnectAsync(_options.Host, _options.Port, stopToken);
            udp = new UdpClient(tcp.Client.AddressFamily);
            if (tcp.Client.RemoteEndPoint is not IPEndPoint remote)
                throw new IOException("Connected TCP socket has no remote endpoint.");
            udp.Connect(remote);
            stream = tcp.GetStream();

            using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stopToken))
            {
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await JoinAsync(stream, udp, handshakeTimeout.Token);
            }

            _joined.TrySetResult();
            tcpReader = ReadTcpLoopAsync(stream, stopToken);
            udpReader = ReadUdpLoopAsync(udp, stopToken);
            await RunSessionAsync(stream, udp, stopToken);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            _joined.TrySetCanceled(stopToken);
        }
        catch (Exception ex)
        {
            _joined.TrySetException(ex);
            throw;
        }
        finally
        {
            if (!_serverClosed && stream != null)
            {
                try
                {
                    await SendTcpAsync(stream,
                        new PacketWriter((int)ClientPacket.ClientDisconnect)
                            .Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).ToFramed(),
                        CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    Console.Error.WriteLine($"Client {_clientId}: could not send graceful disconnect: {ex.Message}");
                }
            }

            tcp?.Close();
            udp?.Dispose();
            if (tcpReader != null) await ObserveShutdownAsync(tcpReader);
            if (udpReader != null) await ObserveShutdownAsync(udpReader);
            if (_clientId != 0) EmulatedClientIds.TryRemove(_clientId, out _);
        }
    }

    public string GetSummary()
        => $"Client {_clientId} ({_options.Name}): peers={Volatile.Read(ref _peerNotificationsReceived)}/{_expectedPeerNotifications}, " +
           $"object-sync responses={Volatile.Read(ref _syncRequestsReceived)}/{_expectedSyncRequests}, " +
           $"pose updates sent={Volatile.Read(ref _posePacketsSent)}, received={Volatile.Read(ref _posePacketsReceived)}, " +
           $"distinct peer poses={_peerStateSenders.Count}/{_expectedPeerNotifications}, " +
           $"auto-placed={Volatile.Read(ref _autoPlaced) == 1}, " +
           $"tracked objects received={Volatile.Read(ref _trackedObjectsReceived)}, " +
           $"sync targets=[{string.Join(",", _syncTargets)}].";

    private async Task JoinAsync(NetworkStream stream, UdpClient udp, CancellationToken cancellationToken)
    {
        Console.WriteLine($"Connecting {_options.Name} to {_options.Host}:{_options.Port}...");
        byte[] welcomePayload = await ReadFrameAsync(stream, cancellationToken);
        var welcome = new PacketReader(welcomePayload);
        int packetId = welcome.ReadInt();
        if (packetId != (int)ServerPacket.Welcome)
            throw new InvalidDataException($"Expected welcome packet, received ID {packetId}.");

        string greeting = welcome.ReadString();
        _clientId = welcome.ReadInt();
        EmulatedClientIds.TryAdd(_clientId, 0);
        _tickRate = Math.Max(1, (int)welcome.ReadByte());
        Console.WriteLine($"Server: {greeting}; assigned client ID {_clientId}; tick rate {_tickRate} Hz.");

        byte[] response = new PacketWriter((int)ClientPacket.WelcomeReceived)
            .Write(_clientId).Write(_options.Name).Write(_options.Scene)
            .Write(_options.Instance).Write(_options.Iff).Write(_options.Color).ToFramed();
        await SendTcpAsync(stream, response, cancellationToken);
        udp.Send(BitConverter.GetBytes(_clientId), sizeof(int));

        while (true)
        {
            WirePacket packet = WirePacket.Parse(await ReadFrameAsync(stream, cancellationToken));
            await HandleServerPacketAsync(stream, packet, cancellationToken);
            if (packet.Id == (int)ServerPacket.ConnectionComplete)
            {
                await SendTcpAsync(stream,
                    new PacketWriter((int)ClientPacket.DoneLoadingScene).ToFramed(),
                    cancellationToken);
                Console.WriteLine($"Joined {_options.Scene}/{_options.Instance} as \"{_options.Name}\".");
                return;
            }
            if (_serverClosed)
                throw new IOException("Server closed during the join handshake.");
        }
    }

    private async Task RunSessionAsync(NetworkStream stream, UdpClient udp, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        long nextPing = 0;
        long nextState = 0;
        long stateIntervalTicks = Math.Max(1, Stopwatch.Frequency / _options.StateHz);

        while (!cancellationToken.IsCancellationRequested)
        {
            while (_incoming.Reader.TryRead(out WirePacket packet))
            {
                if (!await HandleServerPacketAsync(stream, packet, cancellationToken))
                    return;
            }

            if (_incoming.Reader.Completion.IsCompleted && !_incoming.Reader.TryPeek(out _))
            {
                await _incoming.Reader.Completion;
                Console.WriteLine($"Client {_clientId}: server TCP connection closed.");
                return;
            }

            long now = clock.ElapsedTicks;
            if (now >= nextPing)
            {
                long timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                await SendTcpAsync(stream,
                    new PacketWriter((int)ClientPacket.Ping).Write(timestamp).ToFramed(),
                    cancellationToken);
                nextPing = now + Stopwatch.Frequency;
            }

            if (now >= nextState)
            {
                SendPlayerState(udp);
                nextState = now + stateIntervalTicks;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private void SendPlayerState(UdpClient udp)
    {
        StandingPose pose = Volatile.Read(ref _pose);
        var state = new PacketWriter((int)ClientPacket.PlayerState);
        WritePose(state, pose, 0f, 0f, 0f);
        WritePose(state, pose, 0f, 1.7f, 0f);
        WritePose(state, pose, 0f, 1.2f, 0f);
        WritePose(state, pose, -0.3f, 1.4f, 0f);
        WritePose(state, pose, 0.3f, 1.4f, 0f);
        byte[] framedState = state.Write(100f).Write(100).Write((short)0).ToFramed();

        // The batch contains the individually framed packet directly; its length is not repeated.
        byte[] framedBatch = new PacketWriter((int)ClientPacket.BatchedPacket)
            .WriteRaw(framedState).ToFramed();
        byte[] datagram = new byte[sizeof(int) + framedBatch.Length];
        BinaryPrimitives.WriteInt32LittleEndian(datagram, _clientId);
        framedBatch.CopyTo(datagram, sizeof(int));
        udp.Send(datagram, datagram.Length);
        Interlocked.Increment(ref _posePacketsSent);
    }

    private static void WritePose(PacketWriter packet, StandingPose pose, float localX, float localY, float localZ)
    {
        float sin = MathF.Sin(pose.Yaw);
        float cos = MathF.Cos(pose.Yaw);
        float x = pose.X + cos * localX + sin * localZ;
        float y = pose.Y + localY;
        float z = pose.Z - sin * localX + cos * localZ;
        float rotationY = MathF.Sin(pose.Yaw / 2f);
        float rotationW = MathF.Cos(pose.Yaw / 2f);
        packet.Write(x).Write(y).Write(z)
            .Write(0f).Write(rotationY).Write(0f).Write(rotationW);
    }

    private async Task<bool> HandleServerPacketAsync(
        NetworkStream stream,
        WirePacket packet,
        CancellationToken cancellationToken)
    {
        var reader = new PacketReader(packet.Body);
        switch ((ServerPacket)packet.Id)
        {
            case ServerPacket.SpawnPlayer:
            {
                int id = reader.ReadInt();
                string name = reader.ReadString();
                string scene = reader.ReadString();
                int instance = reader.ReadInt();
                int iff = reader.ReadInt();
                int color = reader.ReadInt();
                bool joined = reader.ReadBool();
                int peers = Interlocked.Increment(ref _peerNotificationsReceived);
                if (peers >= _expectedPeerNotifications) _allPeersSeen.TrySetResult();
                Console.WriteLine($"Player {name} ({id}) {(joined ? "joined" : "is in")} {scene}/{instance}, IFF={iff}, color={color}.");
                break;
            }
            case ServerPacket.ConnectSync:
                Console.WriteLine($"Connect sync: in control={reader.ReadBool()}.");
                break;
            case ServerPacket.ConnectionComplete:
                Console.WriteLine("Connection handshake complete.");
                break;
            case ServerPacket.Ping:
            {
                long sentAt = reader.ReadLong();
                long rtt = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sentAt);
                if (_options.Verbose) Console.WriteLine($"Ping reply: approximately {rtt} ms.");
                break;
            }
            case ServerPacket.UpdateRequest:
            {
                bool instantiate = reader.ReadBool();
                int forClient = reader.ReadInt();
                await SendTcpAsync(stream,
                    new PacketWriter((int)ClientPacket.DoneSendingUpToDateObjects)
                        .Write(forClient).ToFramed(),
                    cancellationToken);
                _syncTargets.Enqueue(forClient);
                int requests = Interlocked.Increment(ref _syncRequestsReceived);
                if (requests >= _expectedSyncRequests) _expectedSyncComplete.TrySetResult();
                Console.WriteLine($"Object sync requested for client {forClient} (instantiate={instantiate}); sent empty snapshot.");
                break;
            }
            case ServerPacket.TrackedObject:
            {
                int trackedId = reader.ReadInt();
                string type = reader.ReadString();
                Interlocked.Increment(ref _trackedObjectsReceived);
                Console.WriteLine($"Tracked object {trackedId}: {type}.");
                break;
            }
            case ServerPacket.IdConfirm:
            {
                int trackedId = reader.ReadInt();
                await SendTcpAsync(stream,
                    new PacketWriter((int)ClientPacket.IdConfirm).Write(trackedId).ToFramed(),
                    cancellationToken);
                if (_options.Verbose) Console.WriteLine($"Confirmed tracked object ID {trackedId}.");
                break;
            }
            case ServerPacket.ClientDisconnect:
                Console.WriteLine($"Player {reader.ReadInt()} disconnected.");
                break;
            case ServerPacket.ServerClosed:
                _serverClosed = true;
                Console.WriteLine("Server is shutting down.");
                return false;
            default:
                if (_options.Verbose)
                    Console.WriteLine($"Received server packet {packet.Id} ({packet.Body.Length} body bytes).");
                break;
        }
        return true;
    }

    private async Task ReadTcpLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                WirePacket packet = WirePacket.Parse(await ReadFrameAsync(stream, cancellationToken));
                await _incoming.Writer.WriteAsync(packet, cancellationToken);
            }
            _incoming.Writer.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _incoming.Writer.TryComplete();
        }
        catch (Exception ex)
        {
            _incoming.Writer.TryComplete(ex);
        }
    }

    private async Task ReadUdpLoopAsync(UdpClient udp, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                byte[] datagram = (await udp.ReceiveAsync(cancellationToken)).Buffer;
                HandleUdpDatagram(datagram);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Client {_clientId}: UDP receive failed: {ex.Message}");
        }
    }

    private void HandleUdpDatagram(byte[] datagram)
    {
        if (datagram.Length < 8 ||
            BinaryPrimitives.ReadInt32LittleEndian(datagram) != datagram.Length - sizeof(int) ||
            BinaryPrimitives.ReadInt32LittleEndian(datagram.AsSpan(sizeof(int))) != (int)ServerPacket.BatchedPacket)
            throw new InvalidDataException("Received malformed server UDP framing.");

        int offset = 2 * sizeof(int);
        while (offset < datagram.Length)
        {
            if (datagram.Length - offset < sizeof(int))
                throw new InvalidDataException("Received a truncated UDP batch item length.");
            int length = BinaryPrimitives.ReadInt32LittleEndian(datagram.AsSpan(offset));
            offset += sizeof(int);
            if (length < sizeof(int) || length > datagram.Length - offset)
                throw new InvalidDataException("Received a malformed UDP batch item.");

            WirePacket packet = WirePacket.Parse(datagram.AsSpan(offset, length).ToArray());
            offset += length;
            if (packet.Id == (int)ServerPacket.PlayerState)
                HandlePeerPlayerState(packet);
            else if (_options.Verbose)
                Console.WriteLine($"Received UDP server packet {packet.Id} ({packet.Body.Length} body bytes).");
        }
    }

    private void HandlePeerPlayerState(WirePacket packet)
    {
        var reader = new PacketReader(packet.Body);
        int senderId = reader.ReadInt();
        float x = reader.ReadFloat();
        float y = reader.ReadFloat();
        float z = reader.ReadFloat();
        float qx = reader.ReadFloat();
        float qy = reader.ReadFloat();
        float qz = reader.ReadFloat();
        float qw = reader.ReadFloat();
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
            !float.IsFinite(qx) || !float.IsFinite(qy) || !float.IsFinite(qz) || !float.IsFinite(qw))
            throw new InvalidDataException($"Peer {senderId} sent a non-finite player position.");

        Interlocked.Increment(ref _posePacketsReceived);
        if (_peerStateSenders.TryAdd(senderId, 0))
        {
            Console.WriteLine($"Received standing pose from peer {senderId} at ({x:0.##}, {y:0.##}, {z:0.##}).");
            if (_peerStateSenders.Count >= _expectedPeerNotifications)
                _allPeerStatesSeen.TrySetResult();
        }

        if (_options.PositionSpecified || EmulatedClientIds.ContainsKey(senderId) ||
            Interlocked.CompareExchange(ref _autoPlaced, 1, 0) != 0)
            return;

        float yaw = MathF.Atan2(2f * (qw * qy + qx * qz), 1f - 2f * (qy * qy + qx * qx));
        float forwardX = MathF.Sin(yaw);
        float forwardZ = MathF.Cos(yaw);
        float rightX = MathF.Cos(yaw);
        float rightZ = -MathF.Sin(yaw);
        var placedPose = new StandingPose(
            x + 2f * forwardX + _peerSpacing * rightX,
            y,
            z + 2f * forwardZ + _peerSpacing * rightZ,
            yaw + MathF.PI);
        Volatile.Write(ref _pose, placedPose);
        Console.WriteLine($"Auto-placed emulator player near H3VR peer {senderId} at " +
                          $"({placedPose.X:0.##}, {placedPose.Y:0.##}, {placedPose.Z:0.##}).");
    }

    private static async Task<TcpClient> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        SocketException? lastError = null;
        foreach (IPAddress address in addresses)
        {
            var candidate = new TcpClient(address.AddressFamily) { NoDelay = true };
            try
            {
                await candidate.ConnectAsync(address, port, cancellationToken);
                return candidate;
            }
            catch (SocketException ex)
            {
                candidate.Dispose();
                lastError = ex;
            }
        }
        throw new IOException($"Could not connect to {host}:{port}.", lastError);
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        byte[] size = new byte[4];
        await ReadExactlyAsync(stream, size, cancellationToken);
        int length = BitConverter.ToInt32(size);
        if (length is < 4 or > MaxFrameBytes)
            throw new InvalidDataException($"Invalid TCP frame length {length}.");
        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return payload;
    }

    private static async Task ReadExactlyAsync(
        NetworkStream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (count == 0) throw new EndOfStreamException("Server closed the TCP connection.");
            offset += count;
        }
    }

    private static async Task SendTcpAsync(
        NetworkStream stream,
        byte[] packet,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(packet, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed record StandingPose(float X, float Y, float Z, float Yaw);
}
