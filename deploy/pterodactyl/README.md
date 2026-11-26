# Pterodactyl egg

Import [`egg-h3mp-dedicated.json`](./egg-h3mp-dedicated.json) in the Pterodactyl panel under
**Nests → Import Egg**. Create a server using the imported egg and its default Pterodactyl Debian
image. During server creation, set **Server source ZIP URL** to a direct HTTPS download URL for a
ZIP of this server's source tree. The ZIP must contain
`src/H3MP.DedicatedServer/H3MP.DedicatedServer.csproj`; the installer builds a self-contained Linux
binary with the .NET 8 SDK. The install node needs outbound access to NuGet.org for runtime packs.
The source archive is [`h3mp-dedicated-source.zip`](./h3mp-dedicated-source.zip). Upload it to
HTTPS hosting reachable by the panel's installer container (for example, a GitHub release), then
paste the direct download URL into **Server source ZIP URL**. The local archive itself is not a
download URL and may not be reachable from a remote Pterodactyl host. Do not include credentials in
the URL.

The install script targets `linux-x64` or `linux-arm64` based on the install container architecture.
At runtime it uses the Pterodactyl Debian yolk, so the game container does not need the .NET runtime.

Allocate one port for the server and make that same port available over **both TCP and UDP** in
Pterodactyl/Wings and in the host firewall. H3MP clients use both transports on the same port.
The default server port is `7863`; the egg uses the allocated `SERVER_PORT`.

The server reads `Config.json` from its server directory when present. Logs and `bans.txt` are
written under the server directory (`logs/h3mp-server.log` by default), so they remain in the
Pterodactyl server files. Configure settings in the Panel's file manager or upload a `Config.json`.
The egg disables the stdin admin console; use the panel to stop/restart the server and edit
`bans.txt` for persistent bans.

The startup server-name variable accepts letters, numbers, underscores, periods, and hyphens (no
spaces), because Pterodactyl's startup command is shell-split.

The server does not select or load an H3VR world. Players should choose the same scene and instance
in H3VR when joining the server by direct IP.
