# Client analysis

Examined: the Oculus/Rift version (`C:\Program Files\Oculus\Software\Software\ramen-vr-zenith`, ~95 GB) and later the Steam version (`C:\Program Files (x86)\Steam\steamapps\common\Zenith MMO`). Both have the same structure.

## Key facts

| | |
|---|---|
| Engine | Unity 2022.3.12f1, IL2CPP, Windows x64 |
| Code | `GameAssembly.dll` (212 MB), metadata `global-metadata.dat` version 29, not encrypted |
| Assets | `resources.assets`, Addressables in `StreamingAssets/aa` (≈ 93 GB) |
| Network | LiteNetLib 1.0.1.1 **with modified window size 512** (UDP), a custom re-implementation of the SpatialOS Worker SDK (`Improbable.Worker.Sdk.dll` → "ZenFabric"), SpatialOS GDK (`Improbable.Gdk.*`, 9,932 generated types) |
| Backend | Skywave (login), Nexus/Noodle (realms), Miso (matchmaking), PlayFab (title `4BB7B`), Vivox (voice), Sentry |
| Anti-cheat | ACTk (memory/injection detection only, no file integrity check found) |

## Architecture

The game is a former SpatialOS game. The game world consists of entities with components; clients ("UnityClient" workers) and server logic ("UnityGameLogic" workers) connect to a central runtime that distributes entities, updates, commands and authority. Ramen VR rebuilt this runtime themselves (ZenFabric). The client also contains parts of the server logic (`ZenithGameLogicWorkerConnector`, IAP server, Npgsql, RabbitMQ) and an `entityassets_gamelogic` bundle – but the actual server scenes are not in the build. **The server's game logic therefore has to be rebuilt.**

## Method (reproducible)

All scripts are in `tools/il2cpp-analysis` (see the README there):

1. `meta.py` – types, fields, methods and 40,331 string literals from the metadata.
2. `binmap.py` – address of every method in `GameAssembly.dll` (via the CodeGenModules).
3. `il2.py` / `dis.sh` – disassembly with objdump, automatically annotated (method names, strings, types, fields).
4. `enums.py` – constants (e.g. opcodes, status codes, endpoint names).
5. `xref.py` – caller search.
6. `schema.py`, `compsets.py` – component schemas and component sets.

## Most important locations

| Class / method | Meaning |
|---|---|
| `Improbable.Worker.CInterop.Connection` | ZenFabric connection: `ConnectAsync`, `OnNetworkReceive` (handshake/decryption), `SendMessage` (framing/encryption) |
| `Improbable.Worker.CInterop.*Op` | all operations with `Serialize`/`Deserialize` |
| `Improbable.Worker.CInterop.SchemaObject` | binary format of the component data |
| `LiteNetLib.ReliableChannel..ctor` | window size `0x200` = 512 (stock LiteNetLib: 64) |
| `Zenith.ZenithClientWorkerConnector.Connect` | fetch realm list → ReceptionistFlow → UDP |
| `Zenith.NoodleAuthClient`, `Zenith.NoodleAuthConfig` | realm list and join |
| `Zenith.Auth.Skywave.AuthClientSkywave` | login endpoints (`…rift` on Oculus, `…steam` on Steam) |
| `Zenith.Auth.ReleaseConfiguration` | base URLs per environment |
| `Zenith.AuthCredentials` | static login data (tokens, deploymentID) |
| `Improbable.Generated.ComponentSets` | component sets for authority |

Details: [02 Protocol](02_ZenFabric_Protocol.md), [03 HTTP APIs](03_HTTP_APIs.md), [04 Server addresses](04_Server_Addresses.md).

## Confirmed by live captures

- Handshake, encryption, opcodes and data layouts match the real server exactly.
- The game accepts certificates from a CA imported into the Windows user certificate store (proxy capture worked with the real game).
- The real server sends a ~510 KB initial message (363 fragments) right after login – that is what exposed the modified LiteNetLib window size.

## Open points

- Content of the real player entity and of the Zenith commands (character selection, inventory, combat …) → record with the sniffer on the live server.
- Exact response formats of PlayFab calls, news, character API.
- Component updates cannot be merged without the schema; the server stores them as a log for now.
