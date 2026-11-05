# ZenithServer

C# / .NET 8. One process with:

- **HTTP/HTTPS** (Kestrel): Skywave login (Oculus `…rift` and Steam `…steam`), Nexus (status, realm list, join), PlayFab emulation, stubs for everything else. HTTPS certificates per host name from its own CA (`data/certs`).
- **ZenFabric runtime** (LiteNetLib 1.0.1.1 with window size 512, UDP): handshake, encryption, entity database, visibility (currently: everyone sees everything), authority via `AuthorityDelegation`, command routing, entity queries, ping/pong, log.
- **Game logic** (`ZenFabric/GameLogic.cs`): creates a PlayerCreator entity and answers `PlayerCreator.CreatePlayer` with a minimal player entity. Everything else is logged and answered with "Not implemented" – this is where the rebuild continues.

## Starting

```bash
dotnet run --project server/ZenithServer -c Release
```

Settings in `server/ZenithServer/appsettings.json` (section `Zenith`) or via environment variables, e.g. `Zenith__ZenFabric__Port=7777`.

| Setting | Meaning |
|---|---|
| `PublicHost` | address for UDP, written into the realm list |
| `Http.HttpPort` / `HttpsPort` | 80 / 443 (0 = off) |
| `ZenFabric.Port` | UDP port (7777) |
| `ZenFabric.CapturePackets` | all packets decrypted to `data/logs/*.zfcap` (readable with `ZenithSniffer decode`) |
| `ZenFabric.WorkerFlags` | worker flags sent after login |
| `ZenFabric.GameLogicWorkerToken` | token an external logic worker can use to log in |
| `Auth.AutoRegister` | create accounts automatically |
| `Realms` | realms shown to the client (`Id`, `Name`, `Region`, `Status`, `Load`, optional `WorkerHost`/`WorkerPort`) |

Data in `data/`: `accounts.json`, `world.json` (entities with the Persistence component), `zenfabric_rsa.pem`, `titledata.json` (optional), `logs/http.jsonl` (every request; unimplemented ones are marked with `"handled": false`).

Connecting the game: [04_Server_Addresses.md](04_Server_Addresses.md).

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

The test client replays the complete flow of the real client and reports `[OK]` at the end for ProtocolSettings, InitWorker, Pong, EntityQuery and CreatePlayer. Tested directly against the server and through the sniffer proxy.

## Next steps

1. Record real sessions with the sniffer (until December 18).
2. Read from `zenfabric.log`/`zenfabric.jsonl` which entities/components the server sends after login and which commands the client sends.
3. Rebuild that logic in `GameLogic.cs` (or your own `IGameLogic` classes) and take over the PlayFab responses from `http.log`.
