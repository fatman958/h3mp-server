# Operations

## Config keys (all optional; Config.json from the mod still works)
| Key | Default | Meaning |
|---|---|---|
| `ListenAddress` | `0.0.0.0` | `::` = dual-stack IPv6 + IPv4 on one port (needs IPv6 enabled on the host) |
| `LogFile` | `logs/h3mp-server.log` | also log to this file (dated lines, rotates to `FILE.1` at 10 MB); set `null` to disable |
| `MinimumLogLevel` | `Info` | `Trace`, `Debug`, `Info`, `Warn`, or `Error` |
| `LogPacketPayloads` | `false` | at `Trace`, include up to 48 bytes of packet-body hex (can expose gameplay data) |
| `BanFile` | `bans.txt` | one IP per line, `#` comments; rewritten by `ban`/`unban` (comments are lost on rewrite) |
| `MaxConnectionsPerIp` | 4 | simultaneous connections per source IP, 0 = off |
| `MaxConnectsPerIpPerMinute` | 30 | new connections per source IP per minute, 0 = off |
| `MaxPacketsPerSecond` | 10000 | per client, counts batch items and unimplemented packets, 0 = off |
| `MetricsPort` | 0 | optional HTTP port for `/metrics` and `/healthz`; 0 = disabled |

CLI: `--listen`, `--log-file`, `--log-level`, `--metrics-port`, `--no-console`, plus the existing options.
Players behind one NAT share an IP: raise `MaxConnectionsPerIp` if a household needs more than 4 slots.

## Diagnostics

Logs are written to the console and `logs/h3mp-server.log` by default. To capture detailed connection
and packet metadata, start with `--log-level Debug` (or `-v`). For truncated packet hex and trace-level
events, set `MinimumLogLevel` to `Trace` and `LogPacketPayloads` to `true`. Packet payloads may contain
gameplay state; only enable payload logging for troubleshooting and share logs intentionally.

The logs include connection attempts/rejections, assigned player slots, room transitions, TCP and UDP
packet direction/ID/name/size, sync requests and completions, malformed UDP counters, and disconnect
reasons. Empty UDP batch keepalives are omitted to avoid flooding Debug logs. `MetricsPort` also exposes
rejected UDP datagram counters. Send the log covering the failed attempt along with the exact server
startup arguments/config (remove public IPs if desired).

For a client stuck while loading indexes, look for incoming `trackedObject` packets followed by
outgoing `trackedObject` packets and `Registered tracked object` debug lines. When joining a populated
room, `Object sync complete` should appear for each existing player before cached objects are replayed.

For the VIP H3MP 1.14.1 package, direct-IP connection uses this server's game protocol. The dedicated
server does not implement the H3MP public index-server protocol, so it cannot appear in the mod's public
server browser; use the mod's direct Join option with the server IP and port.

## Admin console (stdin)
`status`, `kick <id>`, `ban <id|ip>`, `unban <ip>`, `bans`, `stop`, `help`.
Under systemd/Docker there is no stdin: edit `bans.txt` and restart, or run interactively when you need it.

## Deploy
* Docker: see `Dockerfile` header (publish TCP **and** UDP).
* systemd: `deploy/h3mp-server.service`.

## Still not done
Authentication/encryption, index-server listing, mod-list enforcement, UPnP, benchmarks. Bans are by IP only and are not
a substitute for authentication.
