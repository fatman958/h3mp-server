# Roadmap

Numbers come from `tools/analyze-handlers.py` (see `HANDLER_ANALYSIS.md` for the per-packet list).
200 client->server packets: **9 done**, 2 unused, **12 object registry**, **120 relays routed by object**,
6 simple relays, **47 TNH**, 4 other.

## Phase 2: object registry (12 packets) <- the real work
`trackedObject`, `trackedObjects`, `objectUpdate`, `giveObjectControl`, `destroyObject`, `objectParent`,
`objectScene`, `objectInstance`, `IDConfirm`, `updateObjectRequest`, `DoneSendingUpdaToDateObjects`, `DoneLoadingScene`,
plus the join-time flow (`RequestUpToDateObjects`, `SendRelevantTrackedObjects`, `loadingClientsWaitingFrom`).

* Server keeps `trackedID -> {controller, scene, instance, parent, children, type, last full blob}`;
  ID allocation with the confirm/recycle protocol (`availableObjectIndices`, `IDConfirm`).
* When a controller leaves: redistribute their objects (`GameManager.DistributeAllControl`) or destroy.
* **Open question / main risk:** how much of each `Tracked*Data` type the server must *decode* vs.
  store as an opaque blob. Routing needs only the base header; late joiners are served by asking
  current controllers for fresh data, which suggests opaque blobs suffice. Verify in
  `TrackedObjectData` (constructor + `Update`) before committing to it.
* Cross-check against two real clients; this is where "no host player" assumptions will surface
  (`controller == 0` meaning host, `firstInSceneInstance`).

## Phase 3: routed relays (120 + 6)
Most are `read trackedID` -> look up object -> forward to the right room/controller with the
`ServerPackets` ID. Make it table-driven (packet -> routing rule) and generate the table from the
analysis, then hand-review the ones with extra logic (damage goes to the *controller*, not the room;
`playerDamage` goes to one player). Drop every "host-local" block.

## Phase 4: Take & Hold + spectator hosts (47 + 4)
`TNHInstance` state, controller election (a player's game runs TNH; the server picks/reassigns),
`setTNHController`, hold/phase/token packets, spectator-host assignment.
Needs a design decision: with no host player, who may create/start TNH instances?

## Phase 5: operations
Index-server listing (`ISClient`, `h3mp.tommysoucy.vip`), mod-list enforcement, admin console/kick/ban,
metrics, Dockerfile/systemd unit.

## Always
Keep `gen-protocol.py` + `analyze-handlers.py` in the loop on every mod release, and extend
`tests/H3MP.SelfTest` with each packet you add (the batching gap above shows why the test client must
follow *upstream's* behavior, not just the server's).

## Status note
Experimental implementation is present for phases 2-5 as described below:
* Object and TNH packets use opaque passthrough plus a tracked-ID registry.
* Weapon, sosig, effect, and spectator packets use matching-name room relays.
* Optional Prometheus-style `/metrics` and `/healthz` endpoints are available.
* Index-server listing, mod-list enforcement, authentication, encryption, and UPnP still require protocol-specific upstream or external integration details.
