# ZenithServer

C# / .NET 8. One process with:

- **HTTP/HTTPS** (Kestrel): Skywave login (Oculus `…rift` and Steam `…steam`), Nexus (status, realm list, join), PlayFab emulation, stubs for everything else. HTTPS certificates per host name from its own CA (`data/certs`).
- **ZenFabric runtime** (LiteNetLib 1.0.1.1 with window size 512, UDP): handshake, encryption, entity database, shared and worker-scoped visibility, authority via `AuthorityDelegation`, command routing, entity queries, ping/pong and logging.
- **Game logic** implements login status, player creation, clock sync, a persistent character roster and travel between Infinite Realms areas. Players in the same area and realm receive each other's public character and tracked body parts. The owner receives the full character state.

## Starting

```bash
dotnet run --project server/ZenithServer -c Release
```

Settings in `server/ZenithServer/appsettings.json` (section `Zenith`) or via environment variables, e.g. `Zenith__ZenFabric__Port=7777`.

| Setting | Meaning |
|---|---|
| `PublicHost` | address for UDP, written into the realm list |
| `Http.HttpPort` / `HttpsPort` | 80 / 443 (0 = off) |
| `ZenFabric.BindAddress` / `Port` | UDP IPv4 bind address and port (0.0.0.0 / 7777) |
| `ZenFabric.CapturePackets` | all packets decrypted to `data/logs/*.zfcap` (readable with `ZenithSniffer decode`) |
| `ZenFabric.WorkerFlags` | worker flags sent after login |
| `ZenFabric.CharacterSpawn` | optional three coordinates that override the Neo City or Classic starter position |
| `ZenFabric.GameLogicWorkerToken` | token an external logic worker can use to log in |
| `Auth.AutoRegister` | create accounts automatically |
| `Realms` | realm entries with `Tags` for filtered lookups and optional `WorkerHost`/`WorkerPort` |

Data in `data/`: `accounts.json`, `world.json` (entities with the Persistence component), `zenfabric_rsa.pem`, `titledata.json` (optional), `logs/http.jsonl` (every request; unimplemented ones are marked with `"handled": false`).

Connecting the game: [04_Server_Addresses.md](04_Server_Addresses.md).

## MySQL storage

Zenith uses JSON storage when `ConnectionStrings:ZenithMySql` is blank or unset.

Create the MySQL database and give the Zenith login `CREATE`, `SELECT`, `INSERT`, and `UPDATE` on it. Fill in the top-level `ConnectionStrings` section of `server/ZenithServer/appsettings.json`:

```json
"ConnectionStrings": {
  "ZenithMySql": "Server=127.0.0.1;Port=3306;Database=zenith;User ID=zenith_app;Password=YOUR_PASSWORD;SslMode=Required"
}
```

Set the `ConnectionStrings__ZenithMySql` environment variable to override the file value, for example to keep credentials outside the config file.

Use the same `Zenith:DataDirectory` as the JSON-backed server for the first MySQL startup. Zenith creates the `zenith_accounts` and `zenith_world` tables in the configured database, imports any `accounts.json` and `world.json` it finds in one transaction, and records completion in MySQL. A first startup without those files initializes empty storage. Existing database records take precedence over matching JSON records. The import leaves the original files available as backups. Subsequent starts read MySQL directly. If MySQL is configured and unavailable, startup fails so the server cannot silently load an older JSON copy.

MySQL stores one record per account, including characters, preferences, PlayFab data, reports, Zennies, and cosmetics. The world record contains the entity ID allocator and persistent entity snapshots. Use one running Zenith process per database because account and entity state are cached in memory. Session tokens are issued again at login after a restart. Certificates, RSA keys, operator configuration, and capture logs use the local data directory.

## Test without the game

```bash
# server on test ports
set Zenith__Http__HttpPort=18080
set Zenith__Http__HttpsPort=0
set Zenith__ZenFabric__Port=17777
dotnet run --project server/ZenithServer

# second window
dotnet run --project tools/ZenithTestClient -- --nexus http://127.0.0.1:18080
```

The test client exercises Skywave login, realm join and the ZenFabric connection through CreatePlayer. It reports `[OK]` for ProtocolSettings, InitWorker, Pong, EntityQuery and CreatePlayer.

Add `--user roster-test --character Wirecheck` to the test client arguments to exercise name validation, character creation and roster retrieval. Run it again with the same arguments to retrieve the existing character.

Also pass `--spawn` to check invalid tickets, unknown characters, entity links and authority, and duplicate requests. The same test acknowledges avatar and world readiness, checks the teleport event, and confirms the destination.

Use `--character IslesTest --challenges` to create a classless test character and check the starting stats, shoulder bow, Air Chops projectile and all three gate routes over UDP.

## Capture references

The six sanitized exports show the client requesting `prod-build40`, waiting for `synced=1`, querying component 2018 and calling `CheckPlayerStatus` before `PlayerCreator.CreatePlayer` on entity 2. The player then requests clock samples (5011) and lists characters on entity 4 (2010). In the November 4 export, requests 96, 101 and 113 show name validation, creation and roster retrieval. The November 8 export supplies the build 56557 flags and level tables in `ClientData/`. The private server keeps Unity logging enabled for connection diagnostics.

November 4 request 119 and November 8 request 13 show `CharacterSpawner.InstantiateCharacter` (2010 #3). The empty success response precedes the entity additions and the `PlayerCharacterComponent` link update on the player entity. The private server builds the rig from the local roster and the level-one stat table, then removes it when the worker disconnects.

November 4 requests 135, 143 and 156 show `TeleportComponent.PlayerInitialized` (2017 #3), `PlayerWorldComponent.WorldLoaded` (4007 #2) and `TeleportComponent.UpdatePosition` (2017 #1). The server sends the login teleport event after the avatar and world are ready.

The November 10 capture (`20261110-195218-proxy`, session 1, request 700) shows `PlayerLifecycleComponent` (2013) moving from downed to recovering to alive. The server now uses that sequence when an avatar falls below the world, restores health after the recovery teleport is confirmed and uses the realm's spawn as the recovery point. The `--spawn` test exercises both the client recovery command (2013 #1) and automatic recovery if the command is absent.

In the same session, `SpawnableLootComponent` (2014 #1) has six successful claims. Requests 2206, 3250, 3567, 4307 and 4705 are followed immediately by `InventoryComponent` (1015) updates. Request 3794 instead has an empty result list and removes the loot entity. These examples give us the claim and inventory sequence for the NPC loot implementation.

## Infinite Realms areas

Classless characters start in Neo City. The server publishes the city's bundled prefab and all three destination prefabs at login so the client can load the maps before the player reaches the gates. Walk or glide through a city gate to travel to Shifting Isles (Daily Session, east), Wasteland Grove (Adventure, west), or Trials of Telos (Boss Rush, north). At the gate, the server updates `PlayerWorldComponent` while the client continues moving along the path. Walk back along the entrance path or use the return command to go back to Neo City.

The November 10 capture supplies the area template IDs, prefab paths and city signage. The installed client supplies the prefab addresses and checkpoint transforms. Each realm has one shared space per area. Daily rotation, enemies, encounter waves, challenge scoring and rewards still require game logic.

## Neo City cosmetic kiosk

The wallet session, transaction start and cosmetic checkout commands support the 39 armor cosmetics in the build 56557 City Kiosk catalog. The server checks item IDs and prices against that catalog, saves purchases to the account and adds the items to the cosmetic bag. Ownership carries across characters and server restarts. Repeating a purchase keeps the existing item and balance.

Set `StartingZennies` under `Zenith` in `server/ZenithServer/appsettings.json` to change the starting balance, then restart the server. The default is 5,000. Saved balances take precedence after a purchase.

Run `--character KioskTest --shop` with `ZenithTestClient` to check the wallet handshake, transaction start, checkout, owned-item retry and inventory updates. This mode creates a classless character and purchases the Shin boots. Cosmetic equipment commands still need implementation and the kiosk UI needs a headset check.
