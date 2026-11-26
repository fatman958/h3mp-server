using System.Buffers.Binary;
using System.Text;

namespace H3MP.DedicatedServer.Protocol;

/// <summary>Thrown when a peer sends data that violates the wire format.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }
}

/// <summary>
/// Wire-compatible re-implementation of H3MP's Packet class (no Unity types).
///
/// Layout (all little-endian, matching BitConverter on x86/x64):
///   payload  = [int32 packetId][fields...]
///   on TCP   = [int32 payloadLength][payload]
///   string   = [int32 byteLength][ASCII bytes]      (non-ASCII becomes '?', same as upstream)
///   bool     = 1 byte
///   Vector3  = 3 x float, Quaternion = 4 x float (x,y,z,w)
///
/// A Packet is either in write mode (Create) or read mode (Parse); never both.
/// </summary>
public sealed class Packet
{
    private readonly MemoryStream? _out;
    private readonly byte[]? _in;
    private int _pos;

    private Packet(MemoryStream? o, byte[]? i) { _out = o; _in = i; }

    public static Packet Create(int id) { var p = new Packet(new MemoryStream(64), null); p.Write(id); return p; }
    public static Packet Create(ServerPackets id) => Create((int)id);
    public static Packet Parse(byte[] payload) => new(null, payload);

    // ---------------------------------------------------------------- writing

    private MemoryStream Out => _out ?? throw new InvalidOperationException("Packet is read-only.");

    public Packet Write(byte v) { Out.WriteByte(v); return this; }
    public Packet Write(bool v) { Out.WriteByte(v ? (byte)1 : (byte)0); return this; }
    public Packet Write(short v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteInt16LittleEndian(s, v); Out.Write(s); return this; }
    public Packet Write(ushort v) { Span<byte> s = stackalloc byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(s, v); Out.Write(s); return this; }
    public Packet Write(int v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(s, v); Out.Write(s); return this; }
    public Packet Write(uint v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(s, v); Out.Write(s); return this; }
    public Packet Write(long v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(s, v); Out.Write(s); return this; }
    public Packet Write(float v) { Span<byte> s = stackalloc byte[4]; BinaryPrimitives.WriteSingleLittleEndian(s, v); Out.Write(s); return this; }
    public Packet Write(double v) { Span<byte> s = stackalloc byte[8]; BinaryPrimitives.WriteDoubleLittleEndian(s, v); Out.Write(s); return this; }

    public Packet Write(string v)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(v);
        Write(bytes.Length);   // upstream writes v.Length; identical for ASCII, safer for surrogate pairs
        Out.Write(bytes);
        return this;
    }

    public Packet Write(Vec2 v) => Write(v.X).Write(v.Y);
    public Packet Write(Vec3 v) => Write(v.X).Write(v.Y).Write(v.Z);
    public Packet Write(Quat v) => Write(v.X).Write(v.Y).Write(v.Z).Write(v.W);

    /// <summary>Append raw bytes with no length prefix (used when relaying).</summary>
    public Packet WriteRaw(ReadOnlySpan<byte> bytes) { Out.Write(bytes); return this; }

    /// <summary>The payload without the TCP length prefix.</summary>
    public byte[] ToArray() => _out?.ToArray() ?? (byte[])_in!.Clone();

    /// <summary>[int32 length][payload] — what goes on a TCP stream or inside a UDP batch.</summary>
    public byte[] ToFramed()
    {
        var payload = Out;
        int len = checked((int)payload.Length);
        var framed = new byte[4 + len];
        BinaryPrimitives.WriteInt32LittleEndian(framed, len);
        payload.GetBuffer().AsSpan(0, len).CopyTo(framed.AsSpan(4));
        return framed;
    }

    // ---------------------------------------------------------------- reading

    private byte[] In => _in ?? throw new InvalidOperationException("Packet is write-only.");

    public int UnreadLength => In.Length - _pos;

    /// <summary>The not-yet-read bytes, without consuming them.</summary>
    public ReadOnlySpan<byte> PeekRemaining() => In.AsSpan(_pos);

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || n > In.Length - _pos)
            throw new ProtocolException($"Packet underflow: wanted {n} bytes, {In.Length - _pos} left.");
        var s = In.AsSpan(_pos, n);
        _pos += n;
        return s;
    }

    public byte ReadByte() => Take(1)[0];
    public bool ReadBool() => Take(1)[0] != 0;
    public short ReadShort() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public ushort ReadUShort() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint ReadUInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public double ReadDouble() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
    public byte[] ReadBytes(int n) => Take(n).ToArray();

    public string ReadString()
    {
        int len = ReadInt();
        if (len < 0) throw new ProtocolException($"Negative string length {len}.");
        return Encoding.ASCII.GetString(Take(len));
    }

    public Vec2 ReadVector2() => new(ReadFloat(), ReadFloat());
    public Vec3 ReadVector3() => new(ReadFloat(), ReadFloat(), ReadFloat());
    public Quat ReadQuaternion() => new(ReadFloat(), ReadFloat(), ReadFloat(), ReadFloat());
}
