using System.Globalization;
using System.Text;

if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
{
    EmulatorOptions.PrintHelp();
    return 0;
}

try
{
    var options = EmulatorOptions.Parse(args);
    using var stopped = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        stopped.Cancel();
    };

    await RunClientsAsync(options, stopped.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopped.");
    return 0;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    EmulatorOptions.PrintHelp();
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Emulator failed: {ex.Message}");
    return 1;
}

static async Task RunClientsAsync(EmulatorOptions options, CancellationToken stopToken)
{
    using var run = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
    var clients = new List<H3mpClientEmulator>();
    var tasks = new List<Task>();

    try
    {
        for (int i = 0; i < options.ClientCount; i++)
        {
            string name = options.ClientCount == 1
                ? options.Name
                : $"{options.Name}-{i + 1:D2}";
            var clientOptions = options with { Name = name, X = options.X + i * 1.5f };
            var client = new H3mpClientEmulator(clientOptions, options.ClientCount - i - 1,
                                                options.ClientCount - 1, i * 1.5f);
            clients.Add(client);
            tasks.Add(client.RunAsync(run.Token));
            await client.Joined.WaitAsync(run.Token);
        }

        await Task.WhenAll(clients.Select(client =>
            client.ExpectedSyncComplete.WaitAsync(TimeSpan.FromSeconds(5), run.Token)));
        await Task.WhenAll(clients.Select(client =>
            client.AllPeersSeen.WaitAsync(TimeSpan.FromSeconds(5), run.Token)));
        await Task.WhenAll(clients.Select(client =>
            client.AllPeerStatesSeen.WaitAsync(TimeSpan.FromSeconds(5), run.Token)));

        Console.WriteLine($"Sync check passed: {clients.Count} clients joined the same room, saw all peers, and received their pose updates.");
        if (options.DurationSeconds == 0)
            await Task.Delay(Timeout.InfiniteTimeSpan, stopToken);
        else
            await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds), stopToken);

        Console.WriteLine($"Run complete after {options.DurationSeconds} seconds.");
    }
    finally
    {
        run.Cancel();
        try { await Task.WhenAll(tasks); }
        catch (OperationCanceledException) when (run.IsCancellationRequested) { }
    }

    foreach (var client in clients)
        Console.WriteLine(client.GetSummary());
}

internal sealed record EmulatorOptions(
    string Host,
    int Port,
    string Name,
    string Scene,
    int Instance,
    int Iff,
    int Color,
    int ClientCount,
    int DurationSeconds,
    int StateHz,
    float X,
    float Y,
    float Z,
    bool PositionSpecified,
    bool Verbose)
{
    public static EmulatorOptions Parse(string[] args)
    {
        string host = "127.0.0.1";
        int port = 7863, instance = 0, iff = 0, color = 0, clients = 1, duration = 0, stateHz = 20;
        float x = 0f, y = 0f, z = 0f;
        string name = "H3MP-TestClient", scene = "IndoorRange_Updated";
        bool positionSpecified = false, verbose = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--host": host = Next(); break;
                case "--port": port = ParseInt(Next(), "--port"); break;
                case "--name": name = Next(); break;
                case "--scene": scene = Next(); break;
                case "--instance": instance = ParseInt(Next(), "--instance"); break;
                case "--iff": iff = ParseInt(Next(), "--iff"); break;
                case "--color": color = ParseInt(Next(), "--color"); break;
                case "--clients": clients = ParseInt(Next(), "--clients"); break;
                case "--state-hz": stateHz = ParseInt(Next(), "--state-hz"); break;
                case "--x": x = ParseFloat(Next(), "--x"); positionSpecified = true; break;
                case "--y": y = ParseFloat(Next(), "--y"); positionSpecified = true; break;
                case "--z": z = ParseFloat(Next(), "--z"); positionSpecified = true; break;
                case "--duration-seconds": duration = ParseInt(Next(), "--duration-seconds"); break;
                case "--verbose": verbose = true; break;
                default: throw new ArgumentException($"Unknown argument: {args[i]}");
            }

            string Next()
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"{args[i]} needs a value.");
                return args[++i];
            }
        }

        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("--host cannot be empty.");
        if (port is < 1 or > 65535) throw new ArgumentException("--port must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(name) || Encoding.ASCII.GetByteCount(name) > 64)
            throw new ArgumentException("--name must contain 1-64 ASCII bytes.");
        if (string.IsNullOrWhiteSpace(scene) || Encoding.ASCII.GetByteCount(scene) > 256)
            throw new ArgumentException("--scene must contain 1-256 ASCII bytes.");
        if (clients is < 1 or > 32) throw new ArgumentException("--clients must be between 1 and 32.");
        if (clients > 1 && Encoding.ASCII.GetByteCount(name) + 3 > 64)
            throw new ArgumentException("--name must leave room for the per-client suffix when --clients is greater than 1.");
        if (stateHz is < 1 or > 120) throw new ArgumentException("--state-hz must be between 1 and 120.");
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new ArgumentException("--x, --y, and --z must be finite numbers.");
        if (duration < 0) throw new ArgumentException("--duration-seconds cannot be negative.");

        return new(host, port, name, scene, instance, iff, color, clients, duration, stateHz,
                   x, y, z, positionSpecified, verbose);
    }

    private static int ParseInt(string value, string option)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : throw new ArgumentException($"{option} requires an integer.");

    private static float ParseFloat(string value, string option)
        => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
            ? result
            : throw new ArgumentException($"{option} requires a number.");

    public static void PrintHelp()
    {
        Console.WriteLine(
            "H3MP.ClientEmulator [options]\n" +
            "  --host HOST                 Server address (default 127.0.0.1)\n" +
            "  --port PORT                 TCP+UDP server port (default 7863)\n" +
            "  --name NAME                 Base emulated username\n" +
            "  --scene SCENE               Scene sent in the join handshake (default IndoorRange_Updated)\n" +
            "  --instance N                Scene instance (default 0)\n" +
            "  --iff N                     Player IFF (default 0)\n" +
            "  --color N                   Player color index (default 0)\n" +
            "  --clients N                 Join sequentially, then test peer/object sync (1-32)\n" +
            "  --x/--y/--z N               Set explicit position; otherwise auto-place near a real H3VR player\n" +
            "  --state-hz N                Stationary pose updates per second (default 20)\n" +
            "  --duration-seconds N        Stay connected after sync; 0 runs until Ctrl+C (default 0)\n" +
            "  --verbose                   Print otherwise-summarized packets\n\n" +
            "This wire-level test client sends a stationary player pose; it does not load Unity or create game objects.");
    }
}
