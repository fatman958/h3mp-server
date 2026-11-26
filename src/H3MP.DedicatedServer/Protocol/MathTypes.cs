namespace H3MP.DedicatedServer.Protocol;

// Minimal stand-ins for UnityEngine.Vector2/Vector3/Quaternion so the server has no
// dependency on Unity. Only the wire layout matters (little-endian floats, in this order).

public readonly record struct Vec2(float X, float Y);

public readonly record struct Vec3(float X, float Y, float Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);
}

public readonly record struct Quat(float X, float Y, float Z, float W)
{
    public static readonly Quat Identity = new(0, 0, 0, 1);
}
