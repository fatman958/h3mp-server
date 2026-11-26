using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using H3MP.DedicatedServer.Protocol;

namespace H3MP.SelfTest;

// Deliberately independent of the server's Packet class: raw BinaryWriter/BinaryReader (little-endian),
// following the *client's* upstream encoding, so a symmetric bug in Packet can't hide.

/// <summary>Writes a client -> server packet.</summary>
sealed class W
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _bw;
    public W(ClientPackets id) { _bw = new BinaryWriter(_ms); _bw.Write((int)id); }
    public W I(int v) { _bw.Write(v); return this; }
    public W L(long v) { _bw.Write(v); return this; }
    public W F(float v) { _bw.Write(v); return this; }
    public W Sh(short v) { _bw.Write(v); return this; }
    public W B(bool v) { _bw.Write(v ? (byte)1 : (byte)0); return this; }
    public W S(string s) { var b = Encoding.ASCII.GetBytes(s); _bw.Write(b.Length); _bw.Write(b); return this; }
    public W V(float x, float y, float z) => F(x).F(y).F(z);
    public W Q(float x, float y, float z, float w) => F(x).F(y).F(z).F(w);
    public W Raw(byte[] b) { _bw.Write(b); return this; }
    public byte[] Payload() => _ms.ToArray();
    public byte[] Framed() { var p = Payload(); return BitConverter.GetBytes(p.Length).Concat(p).ToArray(); }
}

/// <summary>Reads a server -> client packet body (after the id).</summary>
sealed class R
{
    private readonly BinaryReader _br;
    public byte[] Bytes { get; }
    public R(byte[] body) { Bytes = body; _br = new BinaryReader(new MemoryStream(body)); }
    public int I() => _br.ReadInt32();
    public long L() => _br.ReadInt64();
    public float F() => _br.ReadSingle();
    public short Sh() => _br.ReadInt16();
    public byte By() => _br.ReadByte();
    public bool B() => _br.ReadByte() != 0;
    public string S() { int n = _br.ReadInt32(); return Encoding.ASCII.GetString(_br.ReadBytes(n)); }
    public long Remaining => _br.BaseStream.Length - _br.BaseStream.Position;
}

sealed class TestClient : IDisposable
{
    public int Id;
    public readonly Channel<(int id, byte[] body)> Tcp = Channel.CreateUnbounded<(int, byte[])>();
    public readonly Channel<(int id, byte[] body)> Udp = Channel.CreateUnbounded<(int, byte[])>();
    public int BadUdpFraming;
    public bool TcpClosed;

    private readonly TcpClient _tcp = new();
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _cts = new();
    private NetworkStream _ns = null!;

    public static async Task<TestClient> ConnectAsync(int port)
    {
        var c = new TestClient();
        await c._tcp.ConnectAsync(IPAddress.Loopback, port);
        c._tcp.NoDelay = true;
        c._ns = c._tcp.GetStream();
        c._udp.Connect(IPAddress.Loopback, port);
        _ = Task.Run(c.TcpLoop);
        _ = Task.Run(c.UdpLoop);
        return c;
    }

    private async Task<bool> ReadExact(byte[] buf, int n)
    {
        int got = 0;
        while (got < n)
        {
            int r;
            try { r = await _ns.ReadAsync(buf.AsMemory(got, n - got), _cts.Token); }
            catch { return false; }
            if (r == 0) return false;
            got += r;
        }
        return true;
    }

    private async Task TcpLoop()
    {
        var lenBuf = new byte[4];
        while (await ReadExact(lenBuf, 4))
        {
            int len = BitConverter.ToInt32(lenBuf);
            var body = new byte[len];
            if (!await ReadExact(body, len)) break;
            Tcp.Writer.TryWrite((BitConverter.ToInt32(body, 0), body[4..]));
        }
        TcpClosed = true;
        Tcp.Writer.TryComplete();
    }

    private async Task UdpLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            byte[] d;
            try { d = (await _udp.ReceiveAsync(_cts.Token)).Buffer; } catch { return; }
            if (d.Length < 8 || BitConverter.ToInt32(d, 0) != d.Length - 4 ||
                BitConverter.ToInt32(d, 4) != (int)ServerPackets.batchedPacket) { BadUdpFraming++; continue; }
            int off = 8;
            while (off < d.Length)
            {
                int len = BitConverter.ToInt32(d, off); off += 4;
                var item = d[off..(off + len)]; off += len;
                Udp.Writer.TryWrite((BitConverter.ToInt32(item, 0), item[4..]));
            }
        }
    }

    public async Task SendTcp(byte[] framed) { await _ns.WriteAsync(framed); await _ns.FlushAsync(); }
    public async Task SendTcpRaw(byte[] bytes) { await _ns.WriteAsync(bytes); await _ns.FlushAsync(); }

    /// <summary>Client -> server UDP: [int clientId][int len][payload]</summary>
    public void SendUdp(byte[] framedPacket)
    {
        var d = BitConverter.GetBytes(Id).Concat(framedPacket).ToArray();
        _udp.Send(d, d.Length);
    }
    /// <summary>
    /// What the real mod's client sends for most UDP traffic:
    /// [int clientId][int len][int batchedPacket][ ([int len][payload])... ]
    /// </summary>
    public void SendUdpBatched(params byte[][] framedItems)
    {
        var ms = new MemoryStream();
        var bw = new BinaryWriter(ms);
        bw.Write(Id);
        bw.Write(4 + framedItems.Sum(i => i.Length));
        bw.Write((int)ClientPackets.batchedPacket);
        foreach (var it in framedItems) bw.Write(it);
        _udp.Send(ms.ToArray(), (int)ms.Length);
    }

    public void UdpHello() { var d = BitConverter.GetBytes(Id); _udp.Send(d, d.Length); }

    public static async Task<(int id, byte[] body)?> Next(Channel<(int, byte[])> ch, int ms)
    {
        using var cts = new CancellationTokenSource(ms);
        try { return await ch.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return null; }
    }

    public async Task<R> ExpectTcp(ServerPackets id, int ms = 2000)
    {
        var x = await Next(Tcp, ms);
        if (x == null) throw new TestFailure($"client {Id}: timed out waiting for TCP {id}");
        if (x.Value.id != (int)id) throw new TestFailure($"client {Id}: expected TCP {id}, got id {x.Value.id} ({(ServerPackets)x.Value.id})");
        return new R(x.Value.body);
    }

    public async Task<R> ExpectUdp(ServerPackets id, int ms = 2000)
    {
        var x = await Next(Udp, ms);
        if (x == null) throw new TestFailure($"client {Id}: timed out waiting for UDP {id}");
        if (x.Value.id != (int)id) throw new TestFailure($"client {Id}: expected UDP {id}, got id {x.Value.id}");
        return new R(x.Value.body);
    }

    public async Task<List<(int id, byte[] body)>> Drain(Channel<(int, byte[])> ch, int ms)
    {
        var list = new List<(int, byte[])>();
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (true)
        {
            int left = (int)(end - DateTime.UtcNow).TotalMilliseconds;
            if (left <= 0) break;
            var x = await Next(ch, left);
            if (x == null) break;
            list.Add(x.Value);
        }
        return list;
    }

    /// <summary>Wait for the server to close the TCP connection.</summary>
    public async Task<bool> WaitClosed(int ms)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { if (TcpClosed) return true; await Task.Delay(20); }
        return TcpClosed;
    }

    public void Dispose() { _cts.Cancel(); try { _tcp.Close(); } catch { } try { _udp.Close(); } catch { } }
}

sealed class TestFailure : Exception { public TestFailure(string m) : base(m) { } }
