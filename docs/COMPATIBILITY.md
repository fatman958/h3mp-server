# H3MP compatibility

## VIP H3MP 1.14.1

The attached package identifies itself as version `1.14.1` (author `VIP`). Its managed assembly's
`ClientPackets` (200 entries) and `ServerPackets` (209 entries) enums were compared with
`PacketIds.generated.cs`: names and numeric IDs match with no differences. The included `Config.json`
uses the keys supported by this server; H3MP-only settings such as `IP`, `Username`, `ISList`,
`Public`, `UPnP`, and `ModlistEnforcement` are intentionally ignored by the dedicated server.

Use the mod's direct-IP Join flow. The dedicated server implements the game-server TCP/UDP protocol,
not the separate H3MP public index-server protocol, so it will not be listed in the in-game public
server browser.

The dedicated server has not been validated with the actual game client. Initial tracked-object ID
allocation and registration now follow the mod's wire format, but object hand-off and opaque packet
relays remain experimental. The package README also explicitly warns that this mod build and its
Take & Hold patches may be untested.

For a failed attempt, run the server with `--log-level Debug`. For packet hex diagnostics, use
`MinimumLogLevel: "Trace"` and `LogPacketPayloads: true`. Logs are written to
`logs/h3mp-server.log` by default; see [OPERATIONS.md](OPERATIONS.md).
