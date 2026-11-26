# Build:  docker build -t h3mp-server .
# Run:    docker run -d --name h3mp -p 7863:7863/tcp -p 7863/udp:7863/udp -v h3mp-data:/data h3mp-server
#   (publish BOTH tcp and udp; the game needs both on the same port)
# Admin console: `docker attach h3mp` only works if started with -i; otherwise manage via config + bans.txt in /data.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY nuget.config ./
COPY src/H3MP.DedicatedServer/ src/H3MP.DedicatedServer/
RUN dotnet publish src/H3MP.DedicatedServer/H3MP.DedicatedServer.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /out/ ./
RUN mkdir -p /data && chown 1654 /data
USER 1654
VOLUME /data
EXPOSE 7863/tcp 7863/udp
# Config.json (optional), bans.txt and logs/server.log all live in the /data volume.
WORKDIR /data
ENTRYPOINT ["dotnet", "/app/H3MP.DedicatedServer.dll", "--log-file", "/data/logs/server.log", "--no-console"]
