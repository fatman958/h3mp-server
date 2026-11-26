using System.Net;

namespace H3MP.DedicatedServer.Net;

/// <summary>
/// Set of banned source IPs, persisted as one address per line. Only touched from the game-loop
/// thread (the admin console posts its commands there), so it needs no locking.
/// Note: rewriting the file drops any '#' comments that were in it.
/// </summary>
public sealed class BanList
{
    private readonly HashSet<IPAddress> _set = new();
    private readonly string? _path;

    public BanList(string? path)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        Load();
    }

    public int Count => _set.Count;
    public IEnumerable<IPAddress> All => _set.OrderBy(a => a.ToString(), StringComparer.Ordinal);

    /// <summary>IPv4-mapped IPv6 addresses (::ffff:1.2.3.4) become plain IPv4 so one ban covers both forms.</summary>
    public static IPAddress Norm(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    public bool Contains(IPAddress a) => _set.Contains(Norm(a));

    public bool Add(IPAddress a)
    {
        bool added = _set.Add(Norm(a));
        if (added) Save();
        return added;
    }

    public bool Remove(IPAddress a)
    {
        bool removed = _set.Remove(Norm(a));
        if (removed) Save();
        return removed;
    }

    private void Load()
    {
        if (_path == null || !File.Exists(_path)) return;
        foreach (string raw in File.ReadAllLines(_path))
        {
            string line = raw;
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length > 0 && IPAddress.TryParse(line, out var ip)) _set.Add(Norm(ip));
        }
    }

    private void Save()
    {
        if (_path == null) return;
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllLines(tmp, All.Select(a => a.ToString()));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The ban still applies for this run; it just won't survive a restart.
        }
    }
}
