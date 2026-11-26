using System.Text.Json;

namespace H3MP.DedicatedServer;

/// <summary>
/// Server settings. Property names match the mod's Config.json so you can reuse that file as-is;
/// keys this server has no use for (IP, Username, ISList, UPnP, ...) are simply ignored.
/// </summary>
public sealed class ServerConfig
{
    public int Port { get; set; } = 7863;
    public int MaxClientCount { get; set; } = 5;
    public int TickRate { get; set; } = 20;
    public string ServerName { get; set; } = "Default Server Name";

    // In the mod these are wristmenu toggles that only the host can change. A dedicated server
    // has no host player, so they become static settings that are sent to every client on join.
    public bool ColorByIFF { get; set; } = false;
    /// <summary>0: All, 1: Friendly only (same IFF), 2: None</summary>
    public int NameplateMode { get; set; } = 1;
    /// <summary>0: All, 1: Friendly only (same IFF), 2: None</summary>
    public int RadarMode { get; set; } = 0;
    public bool RadarColor { get; set; } = true;

    /// <summary>
    /// Address to listen on. "0.0.0.0" = IPv4 only (default, same as before). "::" = dual-stack:
    /// IPv6 and IPv4 clients on the same port (needs IPv6 enabled on the host).
    /// </summary>
    public string ListenAddress { get; set; } = "0.0.0.0";
    /// <summary>Also write the log to this file (rotated to FILE.1 at 10 MB). Empty/null = console only.</summary>
    public string? LogFile { get; set; } = "logs/h3mp-server.log";
    /// <summary>Minimum log level: Trace, Debug, Info, Warn, or Error.</summary>
    public string MinimumLogLevel { get; set; } = "Info";
    /// <summary>Include truncated packet-body hex in Trace packet logs. Can expose gameplay data.</summary>
    public bool LogPacketPayloads { get; set; }
    /// <summary>One IP per line ('#' comments allowed). Loaded at start, rewritten by the ban/unban commands. Empty = not persisted.</summary>
    public string? BanFile { get; set; } = "bans.txt";
    /// <summary>Simultaneous connections allowed per source IP. 0 = unlimited.</summary>
    public int MaxConnectionsPerIp { get; set; } = 4;
    /// <summary>New TCP connections allowed per source IP per minute. 0 = unlimited.</summary>
    public int MaxConnectsPerIpPerMinute { get; set; } = 30;
    /// <summary>Packets (TCP, UDP, and each item of a batch) one client may send per second before it is dropped. 0 = unlimited.</summary>
    public int MaxPacketsPerSecond { get; set; } = 10000;

    /// <summary>Seconds a fresh TCP connection may take to complete the welcome handshake.</summary>
    public int HandshakeTimeoutSeconds { get; set; } = 15;
    /// <summary>Largest single packet accepted from a client (bytes).</summary>
    public int MaxPacketBytes { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// EXPERIMENTAL stopgap. Unhandled client packets are forwarded unparsed to other players in the
    /// sender's scene+instance when their name matches a ServerPackets entry.
    /// </summary>
    public bool PassthroughRelay { get; set; } = true;

    /// <summary>Optional loopback HTTP port for Prometheus-style server metrics. 0 disables it.</summary>
    public int MetricsPort { get; set; } = 0;
    /// <summary>Interface used by the optional metrics HTTP listener.</summary>
    public string MetricsListenAddress { get; set; } = "127.0.0.1";

    public static ServerConfig Load(string? path)
    {
        if (path == null || !File.Exists(path)) return new ServerConfig();
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path), opts) ?? new ServerConfig();
    }

    /// <summary>Clamp values into ranges the protocol can represent.</summary>
    public void Normalize()
    {
        Port = Math.Clamp(Port, 0, 65535);
        MaxClientCount = Math.Clamp(MaxClientCount, 1, 1024);
        TickRate = Math.Clamp(TickRate, 1, 255);          // sent to clients as a single byte
        HandshakeTimeoutSeconds = Math.Max(1, HandshakeTimeoutSeconds);
        MaxConnectionsPerIp = Math.Max(0, MaxConnectionsPerIp);
        MaxConnectsPerIpPerMinute = Math.Max(0, MaxConnectsPerIpPerMinute);
        MaxPacketsPerSecond = Math.Max(0, MaxPacketsPerSecond);
        if (string.IsNullOrWhiteSpace(ListenAddress)) ListenAddress = "0.0.0.0";
        MetricsPort = Math.Clamp(MetricsPort, 0, 65535);
        if (string.IsNullOrWhiteSpace(MetricsListenAddress)) MetricsListenAddress = "127.0.0.1";
        MaxPacketBytes = Math.Clamp(MaxPacketBytes, 1024, 64 * 1024 * 1024);
    }
}
