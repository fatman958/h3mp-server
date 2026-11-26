namespace H3MP.DedicatedServer;

public enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4 }

public sealed class Logger : IDisposable
{
    public LogLevel Min { get; set; } = LogLevel.Info;
    private readonly object _gate = new();
    public Action<string>? Sink { get; set; }   // tests can capture output

    private const long MaxFileBytes = 10L * 1024 * 1024;
    private StreamWriter? _file;
    private string? _filePath;
    private long _fileBytes;

    /// <summary>Also append every log line (with date) to <paramref name="path"/>. Keeps one backup (path + ".1").</summary>
    public void OpenFile(string path)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        lock (_gate)
        {
            _filePath = path;
            OpenWriter();
            Console.WriteLine($"Logging to {Path.GetFullPath(path)} (rotates at 10 MB).");
        }
    }

    private void OpenWriter()
    {
        _file?.Dispose();
        var fs = new FileStream(_filePath!, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _fileBytes = fs.Length;
        _file = new StreamWriter(fs) { AutoFlush = true };
    }

    private void WriteFile(DateTime now, LogLevel lvl, string m)
    {
        if (_file == null) return;
        try
        {
            string line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{lvl.ToString().ToUpperInvariant(),-5}] {m}";
            _file.WriteLine(line);
            _fileBytes += line.Length + 1;
            if (_fileBytes > MaxFileBytes)
            {
                _file.Dispose();
                _file = null;
                File.Move(_filePath!, _filePath + ".1", overwrite: true);
                OpenWriter();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _file?.Dispose();
            _file = null;
            Console.Error.WriteLine($"[{DateTimeOffset.Now:O}] [ERROR] File logging disabled for \"{_filePath}\": {ex.Message}");
        }
    }

    public void Trace(string m) => Write(LogLevel.Trace, m);
    public void Debug(string m) => Write(LogLevel.Debug, m);
    public void Info(string m) => Write(LogLevel.Info, m);
    public void Warn(string m) => Write(LogLevel.Warn, m);
    public void Error(string m) => Write(LogLevel.Error, m);

    public void Packet(string direction, int id, string name, int clientId, string transport,
                       int payloadBytes, ReadOnlySpan<byte> payload, bool includePayload)
    {
        if (Min > (includePayload ? LogLevel.Trace : LogLevel.Debug)) return;
        string line = $"packet {direction} {transport} client={clientId} id={id} name={name} bytes={payloadBytes}";
        if (includePayload)
        {
            int count = Math.Min(payload.Length, 48);
            line += $" hex={Convert.ToHexString(payload[..count])}";
            if (payload.Length > count) line += "...";
            Trace(line);
        }
        else
        {
            Debug(line);
        }
    }

    private void Write(LogLevel lvl, string m)
    {
        if (lvl < Min) return;
        var now = DateTime.Now;
        string line = $"{now:HH:mm:ss.fff} [{lvl.ToString().ToUpperInvariant(),-5}] {m}";
        lock (_gate)
        {
            if (Sink != null) Sink(line); else Console.WriteLine(line);
            WriteFile(now, lvl, m);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>Make untrusted text (usernames, scene names) safe to print on one log line.</summary>
    public static string Clean(string s, int max = 64)
    {
        var chars = s.Length > max ? s[..max] : s;
        return string.Create(chars.Length, chars, (span, src) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = char.IsControl(src[i]) ? '?' : src[i];
        });
    }
}
