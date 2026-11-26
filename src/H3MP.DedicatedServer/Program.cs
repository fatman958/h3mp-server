using System.Runtime.InteropServices;
using H3MP.DedicatedServer;
using H3MP.DedicatedServer.Net;

// Usage: H3MP.DedicatedServer [--config Config.json] [--port N] [--max-clients N]
//                             [--tick-rate N] [--name "My Server"] [-v|--verbose]

string? configPath = null;
int? port = null, maxClients = null, tickRate = null, metricsPort = null;
string? name = null, listen = null, logFile = null, logLevel = null;
bool noConsole = false;
var log = new Logger();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--config": configPath = Next(); break;
        case "--port": port = int.Parse(Next()); break;
        case "--max-clients": maxClients = int.Parse(Next()); break;
        case "--tick-rate": tickRate = int.Parse(Next()); break;
        case "--name": name = Next(); break;
        case "--listen": listen = Next(); break;          // "0.0.0.0" (default, IPv4) or "::" (IPv6 + IPv4)
        case "--log-file": logFile = Next(); break;
        case "--log-level": logLevel = Next(); break;
        case "--metrics-port": metricsPort = int.Parse(Next()); break;
        case "--no-console": noConsole = true; break;     // don't read admin commands from stdin
        case "-v": case "--verbose": log.Min = LogLevel.Debug; break;
        case "-h": case "--help":
            Console.WriteLine("H3MP.DedicatedServer [--config Config.json] [--port N] [--max-clients N] [--tick-rate N] [--name S]\n" +
                              "                      [--listen ADDR] [--log-file PATH] [--log-level LEVEL] [--metrics-port N] [--no-console] [-v]\n" +
                              "Admin commands are read from stdin; type 'help' while running.");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]}");
            return 2;
    }
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
}

// Config.json from the mod works as-is; if no path is given, pick up ./Config.json when present.
configPath ??= File.Exists("Config.json") ? "Config.json" : null;
var config = ServerConfig.Load(configPath);
if (port != null) config.Port = port.Value;
if (maxClients != null) config.MaxClientCount = maxClients.Value;
if (tickRate != null) config.TickRate = tickRate.Value;
if (name != null) config.ServerName = name;
if (listen != null) config.ListenAddress = listen;
if (logFile != null) config.LogFile = logFile;
if (metricsPort != null) config.MetricsPort = metricsPort.Value;
if (logLevel != null) config.MinimumLogLevel = logLevel;
if (!Enum.TryParse(config.MinimumLogLevel, true, out LogLevel minimumLogLevel) ||
    !Enum.IsDefined(minimumLogLevel))
{
    Console.Error.WriteLine($"Invalid MinimumLogLevel \"{config.MinimumLogLevel}\". Use Trace, Debug, Info, Warn, or Error.");
    return 2;
}
log.Min = minimumLogLevel;
if (args.Contains("-v") || args.Contains("--verbose")) log.Min = LogLevel.Debug;
if (!string.IsNullOrWhiteSpace(config.LogFile))
{
    try { log.OpenFile(config.LogFile); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        log.Warn($"Could not open log file {config.LogFile}: {ex.Message}. Logging to console only.");
    }
}
log.Info(configPath != null ? $"Loaded config from {configPath}" : "No Config.json found, using defaults.");

var server = new GameServer(config, log);
try { server.Start(); }
catch (System.Net.Sockets.SocketException ex)
{
    log.Error($"Could not bind {config.ListenAddress}:{config.Port}: {ex.Message}");
    return 1;
}
catch (ArgumentException ex)
{
    log.Error(ex.Message);
    return 1;
}

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
// Cancel = true stops the runtime from terminating the process the moment this handler returns,
// so StopAsync gets to finish (notify clients, close sockets).
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; done.TrySetResult(); });

if (!noConsole)
{
    // Admin commands. At EOF (e.g. `docker run` without -i, or a systemd service) the reader just ends.
    var admin = new AdminConsole(server, () => done.TrySetResult());
    _ = Task.Run(async () =>
    {
        try
        {
            string? line;
            while ((line = await Console.In.ReadLineAsync()) != null)
            {
                string cmd = line;
                server.Post(() =>
                {
                    string output = admin.Execute(cmd);
                    if (output.Length > 0) Console.WriteLine(output);
                });
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    });
}

await done.Task;
await server.StopAsync();
log.Dispose();
return 0;
