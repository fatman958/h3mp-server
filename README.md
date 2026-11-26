# H3MP Dedicated Server (phase 1)

A standalone .NET 8 server for the [H3MP](https://github.com/TommySoucy/H3MP) multiplayer mod for
*Hot Dogs, Horseshoes & Hand Grenades*. No Unity, no game install, no VR: it speaks the mod's wire
protocol directly, so it can run headless on Linux or Windows.

Targets **H3MP 1.14.1**, including the attached VIP 1.14.1 package: all 209 server and 200 client
packet enum names/IDs were compared and match. Licensed **GPLv3** (derived from upstream H3MP, see `LICENSE`).

> **Status:** players can connect and move, and the server includes experimental opaque relays for
> objects, weapons, sosigs, effects, and Take & Hold. Set `"PassthroughRelay": false` to disable
> unverified passthrough (see [docs/PASSTHROUGH.md](docs/PASSTHROUGH.md)).
> It has been tested against a hand-written client that follows the protocol, **not yet against the
> real game**. The VIP package README itself warns its release is untested. Use direct-IP Join; this
> dedicated server does not implement H3MP's public index-server/browser protocol.

## Run

```sh
dotnet run --project src/H3MP.DedicatedServer -- --port 7863 --max-clients 5 --name "My Server"
# or publish a build:
dotnet publish src/H3MP.DedicatedServer -c Release -o out && dotnet out/H3MP.DedicatedServer.dll
```

Open **TCP and UDP** on the chosen port (default 7863) in your firewall/router.
Clients connect with the mod's normal "join" using the server's IP and port.
`Ctrl+C` / `SIGTERM` shuts down gracefully (clients are told the server closed).

Options: `--config <file>` `--port` `--max-clients` `--tick-rate` `--name` `--metrics-port`
`--log-file` `--log-level` (`Trace|Debug|Info|Warn|Error`) `-v` (Debug).
Detailed logs go to `logs/h3mp-server.log` by default. For packet hex, set `MinimumLogLevel` to `Trace`
and `LogPacketPayloads` to `true` in config.
The mod's own `Config.json` works as a config file; unknown keys are ignored. Extra keys:
`ColorByIFF`, `NameplateMode`, `RadarMode`, `RadarColor` (host-only toggles in the mod, static here),
`HandshakeTimeoutSeconds`, `MaxPacketBytes`.

## Test

```sh
dotnet run --project tests/H3MP.SelfTest      # end-to-end checks over real sockets, exit code 0 = pass
```

## Emulate a client

The independent wire-level client can test multi-user join and object-sync behavior without Unity
or a VR headset:

```sh
dotnet run --project tools/H3MP.ClientEmulator -- --host 127.0.0.1 --port 7863 --clients 3
```

Clients join sequentially in the same scene, stream stationary poses, respond to peer object-sync
requests with empty snapshots, and verify that each receives every other's pose. Without explicit
coordinates, they move near the first real H3VR player's pose; use `--x`, `--y`, and `--z` to set a
fixed position. They stay connected until Ctrl+C by
default; `--duration-seconds N` stops them after N seconds. This does not emulate H3VR
rendering, physics, or valid game-object serialization. To see the avatars in-game, use the same
`--scene` and `--instance` as the H3VR client; see
[tools/H3MP.ClientEmulator/README.md](tools/H3MP.ClientEmulator/README.md).

## What works today

| Area | Status |
|---|---|
| TCP+UDP transport, framing, UDP batching (both directions), tick-rate batching | done |
| Welcome handshake, slot assignment (1..N), spawn/connectSync/connectionComplete flow | done |
| Player state relay (pose, health) per scene+instance, latest-wins coalescing | done |
| Scene / instance / IFF / color / ping / clean disconnect | done |
| Hardening: bounds-checked reads, packet-size cap, handshake timeout, pre-welcome gating, UDP endpoint bound only from the TCP peer's IP, slow-client cutoff | done |
| Weapon/sosig/effect relays | **experimental passthrough** (unparsed, room-wide, unverified) |
| Object registry, join sync, controller hand-off | **experimental opaque registry** (raw relay plus tracked-ID index) |
| Take & Hold and spectator hosts | **experimental passthrough** (room-wide, unverified) |
| Public index-server listing, mod-list enforcement, authentication | requires upstream index/mod protocol details |
| Prometheus-style metrics and health endpoint | optional `MetricsPort` |

## Layout

```
src/H3MP.DedicatedServer/
  Protocol/   Packet (wire-compatible, no Unity), math structs, PacketIds.generated.cs (from upstream)
  Net/        GameServer (loop, listeners, tick, UDP batching), ClientConnection, Rooms, PlayerInfo
  Handlers/   ClientHandlers (client->server), ServerSend (server->client)
tests/H3MP.SelfTest/   independent wire-level test client + scenarios
tools/H3MP.ClientEmulator/  standalone multi-client join/object-sync test client
tools/        gen-protocol.py (regenerate packet IDs), analyze-handlers.py (what's left to port)
docs/         ARCHITECTURE.md, ROADMAP.md, HANDLER_ANALYSIS.md (generated)
```

## When the mod updates

```sh
git clone https://github.com/TommySoucy/H3MP && cd h3mp-server
python3 tools/gen-protocol.py ../H3MP/src/networking/Packet.cs
python3 tools/analyze-handlers.py ../H3MP/src/networking
dotnet run --project tests/H3MP.SelfTest
```
Packet IDs are copied from upstream, so they follow the mod; field *layouts* in `ServerSend.cs` /
`ClientHandlers.cs` must be re-checked by hand against upstream's `ServerSend.cs` / `ServerHandle.cs`.


## Operations
See `docs/OPERATIONS.md` (admin console, bans, per-IP limits, file logging, Docker, systemd).
For Pterodactyl, import `deploy/pterodactyl/egg-h3mp-dedicated.json`; the setup notes are in
[`deploy/pterodactyl/README.md`](deploy/pterodactyl/README.md). Allocate the game port for both TCP
and UDP.

## Python tools (optional)
The server itself is pure .NET and needs no Python. `tools/` holds two helper scripts for re-checking
packet IDs and handlers after a mod update:

```
python3 -m venv .venv
source .venv/bin/activate          # Windows: .venv\Scripts\activate
pip install -r tools/requirements.txt   # no third-party packages needed
python tools/gen-protocol.py <path/to/H3MP/src/networking/Packet.cs>
```
