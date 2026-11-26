using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace H3MP.DedicatedServer.Net;

internal readonly record struct QueuedUdp(string? Key, byte[] Framed);

/// <summary>
/// One connected client. Socket I/O runs on background tasks; everything they learn is handed to
/// the server's single game-loop thread via GameServer.Post, so game state is never touched
/// concurrently. A new ClientConnection object is created per connection (slot IDs are reused,
/// objects are not), so stale events from a dead connection can be recognised and ignored.
/// </summary>
public sealed class ClientConnection
{
    private const long MaxQueuedBytes = 16L * 1024 * 1024;

    private readonly GameServer _server;
    private readonly NetworkStream _stream;
    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private long _queuedBytes;
    private int _closed;

    public int Id { get; }
    public TcpClient Tcp { get; }
    public IPAddress RemoteAddress { get; }
    public string Remote { get; }
    public long ConnectedAtMs { get; }
    /// <summary>Wall-clock connect time (Unix ms); a client's clientDisconnect only counts if sent after this.</summary>
    public long OpenedAtUnixMs { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // --- loop-thread-only state below ---
    public IPEndPoint? UdpEndPoint { get; internal set; }
    public PlayerInfo? Player { get; internal set; }
    public int BadPackets { get; internal set; }
    internal long RateWindowStartMs { get; set; }
    internal int PacketsInWindow { get; set; }
    internal List<QueuedUdp> UdpQueue { get; } = new();

    public bool Closed => Volatile.Read(ref _closed) == 1;

    internal ClientConnection(GameServer server, int id, TcpClient tcp, long nowMs)
    {
        _server = server;
        Id = id;
        Tcp = tcp;
        _stream = tcp.GetStream();
        var ep = (IPEndPoint)tcp.Client.RemoteEndPoint!;
        RemoteAddress = BanList.Norm(ep.Address);   // IPv4-mapped IPv6 -> plain IPv4, so bans/limits match either way
        Remote = ep.ToString();
        ConnectedAtMs = nowMs;
    }

    internal void Start()
    {
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>Queue an already-framed ([len][payload]) buffer for TCP delivery. Thread-safe.</summary>
    public void SendTcp(byte[] framed)
    {
        if (Closed) return;
        if (framed.Length >= 8)
        {
            int id = BinaryPrimitives.ReadInt32LittleEndian(framed.AsSpan(4));
            _server.LogOutgoingPacket(this, id, framed.AsSpan(4));
        }
        if (Interlocked.Add(ref _queuedBytes, framed.Length) > MaxQueuedBytes)
        {
            _server.Post(() => _server.Disconnect(this, "send queue overflow (client too slow)"));
            return;
        }
        _out.Writer.TryWrite(framed);
    }

    /// <summary>Idempotent. Stops I/O and closes the socket.</summary>
    internal void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _out.Writer.TryComplete();
        try { Tcp.Close(); } catch { /* already gone */ }
    }

    // [int32 length][payload] frames. Partial reads are accumulated; one read may hold many frames.
    private async Task ReadLoopAsync()
    {
        byte[] acc = new byte[8192];
        int start = 0, end = 0;
        try
        {
            while (!Closed)
            {
                if (end == acc.Length)
                {
                    if (start > 0)
                    {
                        Buffer.BlockCopy(acc, start, acc, 0, end - start);
                        end -= start;
                        start = 0;
                    }
                    else Array.Resize(ref acc, acc.Length * 2);   // one frame bigger than the buffer
                }

                int n = await _stream.ReadAsync(acc.AsMemory(end)).ConfigureAwait(false);
                if (n == 0)
                {
                    _server.Post(() => _server.Disconnect(this, "connection closed by client"));
                    return;
                }
                end += n;

                while (end - start >= 4)
                {
                    int len = BinaryPrimitives.ReadInt32LittleEndian(acc.AsSpan(start));
                    if (len <= 0 || len > _server.Config.MaxPacketBytes)
                    {
                        _server.Post(() => _server.Disconnect(this, $"invalid packet length {len}"));
                        return;
                    }
                    if (end - start - 4 < len) break;   // wait for the rest of this frame

                    byte[] payload = acc.AsSpan(start + 4, len).ToArray();
                    start += 4 + len;
                    _server.Post(() => _server.HandlePacket(this, payload, udp: false));
                }
                if (start == end) start = end = 0;
            }
        }
        catch (Exception ex)
        {
            if (!Closed) _server.Post(() => _server.Disconnect(this, "read error: " + ex.Message));
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (byte[] data in _out.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await _stream.WriteAsync(data).ConfigureAwait(false);
                Interlocked.Add(ref _queuedBytes, -data.Length);
            }
        }
        catch (Exception ex)
        {
            if (!Closed) _server.Post(() => _server.Disconnect(this, "send failed: " + ex.Message));
        }
    }
}
