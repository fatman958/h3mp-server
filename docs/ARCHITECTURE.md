# Architecture notes

## How upstream H3MP is built (what we reverse-engineered)

* **Listen-server.** One player's game is the server *and* client 0. `Server`, `ServerHandle`,
  `ServerSend` live inside the game process and call `GameManager`, `Mod` and `FistVR` types directly.
* **Transport.** One TCP listener + one UDP socket on the same port. Slot/client IDs are 1..N; 0 = host.
  * TCP frame: `[int32 len][int32 packetId][fields]`, little-endian.
  * Strings: `[int32 byteLen][ASCII]`. Bools: 1 byte. Vector3 = 3 floats, Quaternion = 4 (x,y,z,w).
  * UDP client->server: `[int32 clientId][int32 len][payload]`. The first datagram is just the 4-byte ID
    and tells the server the client's UDP endpoint.
  * UDP batching, **both directions**: `[int32 len][int32 batchedPacket][ ([int32 len][payload])... ]`,
    flushed every tick, ~1300-byte budget, latest-wins per key. (Missing the client->server batching
    would silently drop every player state; the handler analysis tool is what exposed that.)
* **Packet IDs differ by direction.** `ServerPackets` and `ClientPackets` are separate enums. A relayed
  packet must be re-stamped with the *ServerPackets* ID, and often gets the sender's ID inserted
  after it (e.g. playerState).
* **Dispatch.** `Server.packetHandlers[]` is positional: element *i* handles `ClientPackets` value *i*.
* **Ownership model.** Every synced object has `trackedID`, `controller` (client ID that simulates it),
  `scene`, `instance`. The controller's game runs the physics/AI; everyone else receives updates.
  The server keeps the registry and routes. TNH progression and enemy AI run on a player's game
  (the TNH "controller"), never on the server itself.
* **Join flow.** `welcome` -> client `welcomeReceived` -> server `SendIntoGame`: spawnPlayer for each
  existing player, spawnPlayer(join) to everyone else, `connectSync(inControl)`, then
  `connectionComplete`. If others share the scene, the server asks *them* for up-to-date objects
  (`requestUpToDateObjects`) and forwards the result to the newcomer.

## How this server differs

| Upstream | Here |
|---|---|
| Handlers run on Unity's main thread | One game-loop task; socket tasks only `Post()` events. No locks around game state. |
| Host is player 0 and applies effects to its own physical objects | No host player; the "apply locally" half of each handler simply doesn't exist. |
| Anyone's first UDP datagram binds that slot's endpoint | Bound only if it comes from the TCP peer's IP. |
| Reads trust the peer (IndexOutOfRange etc.) | Reads are bounds-checked (`ProtocolException`); bad packets are dropped and logged, repeat offenders disconnected. |
| Empty UDP batches sent every tick | Only non-empty batches. Player pose packets are coalesced per sender. |
| Mod-API custom packet IDs | Advertised as none; custom packets are ignored. |

## Concurrency contract

`GameServer`, `Rooms`, `PlayerInfo`, `ClientConnection.Player/UdpQueue` are **loop-thread only**.
From any other thread use `server.Post(() => ...)`. `ClientConnection.SendTcp` is thread-safe.
