using System.Net;
using System.Text;
using H3MP.DedicatedServer.Net;

namespace H3MP.DedicatedServer;

/// <summary>
/// Text commands for the server operator (stdin). <see cref="Execute"/> must run on the game-loop
/// thread (Program posts each line there), so it can touch server state directly.
/// </summary>
internal sealed class AdminConsole
{
    private readonly GameServer _server;
    private readonly Action _requestStop;

    public AdminConsole(GameServer server, Action requestStop)
    {
        _server = server;
        _requestStop = requestStop;
    }

    public const string Help =
        "Commands:\n" +
        "  status          list connected players\n" +
        "  kick <id>       disconnect the client in that slot\n" +
        "  ban <id|ip>     ban an IP (a slot id means that client's IP) and disconnect it\n" +
        "  unban <ip>      remove a ban\n" +
        "  bans            list banned IPs\n" +
        "  metrics         show server metrics\n" +
        "  stop            shut the server down";

    public string Execute(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "";

        switch (parts[0].ToLowerInvariant())
        {
            case "help": case "?": return Help;
            case "status": return Status();
            case "bans": return Bans();
            case "metrics": return Metrics();
            case "stop": case "quit": case "exit": _requestStop(); return "Stopping...";

            case "kick":
            {
                if (parts.Length != 2 || !int.TryParse(parts[1], out int id)) return "usage: kick <id>";
                var c = _server.GetConnection(id);
                if (c == null) return $"No client in slot {id}.";
                _server.Disconnect(c, "kicked by admin");
                return $"Kicked slot {id}.";
            }

            case "ban":
            {
                if (parts.Length != 2) return "usage: ban <id|ip>";
                IPAddress? ip = null;
                if (int.TryParse(parts[1], out int id))      // checked first: IPAddress.TryParse("5") would give 0.0.0.5
                {
                    ip = _server.GetConnection(id)?.RemoteAddress;
                    if (ip == null) return $"No client in slot {id}.";
                }
                else if (IPAddress.TryParse(parts[1], out var parsed)) ip = BanList.Norm(parsed);
                else return "usage: ban <id|ip>";

                bool added = _server.Bans.Add(ip);
                int dropped = _server.DisconnectAddress(ip, "banned by admin");
                return $"{(added ? "Banned" : "Already banned:")} {ip}. Disconnected {dropped} client(s).";
            }

            case "unban":
            {
                if (parts.Length != 2 || !IPAddress.TryParse(parts[1], out var ip)) return "usage: unban <ip>";
                return _server.Bans.Remove(BanList.Norm(ip)) ? $"Unbanned {BanList.Norm(ip)}." : $"{BanList.Norm(ip)} was not banned.";
            }

            default: return $"Unknown command \"{Logger.Clean(parts[0])}\". Type help.";
        }
    }

    private string Status()
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var c in _server.Registered())
        {
            var p = c.Player!;
            sb.AppendLine($"  [{c.Id}] \"{Logger.Clean(p.Username)}\"  {c.RemoteAddress}  scene \"{Logger.Clean(p.Scene)}\" instance {p.Instance}  IFF {p.IFF}");
            n++;
        }
        int joining = _server.Connections().Count() - n;
        sb.Append($"{n} player(s) online" + (joining > 0 ? $", {joining} still handshaking" : "") + $", max {_server.Config.MaxClientCount}.");
        if (_server.UnhandledCounts.Count > 0)
            sb.Append($"\nUnhandled client packet kinds seen so far: {string.Join(", ", _server.UnhandledCounts.OrderByDescending(kv => kv.Value).Take(8).Select(kv => $"{(Enum.IsDefined(typeof(Protocol.ClientPackets), kv.Key) ? ((Protocol.ClientPackets)kv.Key).ToString() : kv.Key.ToString())}x{kv.Value}"))}");
        return sb.ToString();
    }

    private string Bans()
    {
        if (_server.Bans.Count == 0) return "No bans.";
        return string.Join("\n", _server.Bans.All.Select(a => "  " + a));
    }

    private string Metrics()
    {
        int players = _server.PlayerCount;
        int objects = _server.Objects.Count;
        int unhandledKinds = _server.UnhandledCounts.Count;
        long unhandledPackets = _server.UnhandledCounts.Values.Sum();
        return $"players={players}\nobjects={objects}\nunhandled_packet_kinds={unhandledKinds}\nunhandled_packets={unhandledPackets}";
    }
}
