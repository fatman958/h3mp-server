using System.Buffers.Binary;
using System.Text;

internal enum ServerPacket
{
    Welcome = 0,
    SpawnPlayer = 1,
    PlayerState = 2,
    ConnectSync = 8,
    UpdateRequest = 34,
    ClientDisconnect = 116,
    ServerClosed = 117,
    Ping = 123,
    TrackedObject = 156,
    IdConfirm = 171,
    ConnectionComplete = 179,
    BatchedPacket = 198
}

internal enum ClientPacket
{
    WelcomeReceived = 1,
    PlayerState = 2,
    TrackedObjects = 9,
    UpdateObjectRequest = 40,
    ClientDisconnect = 135,
    Ping = 140,
    IdConfirm = 165,
    DoneLoadingScene = 100,
    DoneSendingUpToDateObjects = 101,
    BatchedPacket = 189
}

internal sealed class PacketWriter
{
    private readonly MemoryStream _stream = new();
    private readonly BinaryWriter _writer;

    public PacketWriter(int id)
    {
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        _writer.Write(id);
    }

    public PacketWriter Write(byte value) { _writer.Write(value); return this; }
    public PacketWriter Write(bool value) { _writer.Write(value ? (byte)1 : (byte)0); return this; }
    public PacketWriter Write(short value) { _writer.Write(value); return this; }
    public PacketWriter Write(int value) { _writer.Write(value); return this; }
    public PacketWriter Write(long value) { _writer.Write(value); return this; }
    public PacketWriter Write(float value) { _writer.Write(value); return this; }
    public PacketWriter Write(string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        _writer.Write(bytes.Length);
        _writer.Write(bytes);
        return this;
    }
    public PacketWriter WriteRaw(ReadOnlySpan<byte> bytes) { _writer.Write(bytes); return this; }

    public byte[] ToPayload() => _stream.ToArray();

    public byte[] ToFramed()
    {
        byte[] payload = ToPayload();
        byte[] framed = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, payload.Length);
        payload.CopyTo(framed, 4);
        return framed;
    }
}

internal sealed class PacketReader
{
    private readonly byte[] _bytes;
    private int _offset;

    public PacketReader(byte[] bytes) => _bytes = bytes;
    public int Remaining => _bytes.Length - _offset;

    private ReadOnlySpan<byte> Take(int length)
    {
        if (length < 0 || length > Remaining)
            throw new InvalidDataException($"Malformed packet: expected {length} bytes, only {Remaining} remain.");
        ReadOnlySpan<byte> result = _bytes.AsSpan(_offset, length);
        _offset += length;
        return result;
    }

    public byte ReadByte() => Take(1)[0];
    public bool ReadBool() => ReadByte() != 0;
    public short ReadShort() => BinaryPrimitives.ReadInt16LittleEndian(Take(2));
    public int ReadInt() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public long ReadLong() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public float ReadFloat() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public string ReadString()
    {
        int length = ReadInt();
        return Encoding.ASCII.GetString(Take(length));
    }
    public byte[] ReadRemaining() => Take(Remaining).ToArray();
}

internal readonly record struct WirePacket(int Id, byte[] Body)
{
    public static WirePacket Parse(byte[] payload)
    {
        var reader = new PacketReader(payload);
        int id = reader.ReadInt();
        return new WirePacket(id, reader.ReadRemaining());
    }
}
