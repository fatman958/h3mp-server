# Passthrough relay (experimental stopgap)

`PassthroughRelay` defaults to `true`. Set it to `false` in `Config.json` to disable the relay.

For any unhandled client packet whose name has a matching `ServerPackets` entry, the server forwards
the raw body over TCP to the other players in the sender's scene and instance. Session packets and custom packets remain excluded. Object-registry and TNH
packets are included as unverified opaque relays.

This relay is not verified against upstream:

- Upstream may insert fields such as the sender ID or route packets to a controller or one player.
- The object registry is opaque: tracked payload layouts and controller hand-off rules are not decoded.
- Packets sent over UDP upstream are currently forwarded over TCP.

Test with two real clients using `-v`, and replace relay entries with real handlers incrementally.
