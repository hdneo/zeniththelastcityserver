# ZenFabric protocol (UDP)

Ramen VR replaced the SpatialOS runtime with their own runtime ("ZenFabric") and re-implemented the Worker SDK API (`Improbable.Worker.CInterop`) entirely in C#. All layouts here come from the disassembly of `Serialize()`/`Deserialize()` in `GameAssembly.dll`, are implemented and tested in `shared/Zenith.Protocol`, and have been confirmed against live captures.

## Layers

```
UDP
 └ LiteNetLib 1.0.1.1 (ProtocolId 13) WITH MODIFIED WINDOW SIZE 512 (original 64)
   ReliableOrdered, channel 0 (ChannelId 2), DisconnectTimeout 30 s
   ACK packets are therefore 69 instead of 13 bytes; large messages are fragmented (login world ≈ 510 KB = 363 fragments)
    └ connect key = joinToken (from /api/realm/join)
       └ message: [int32 payload length][payload]
          └ payload (XOR-encrypted once a key is set): [int32 opcode][body]
```

All numbers little-endian. `string` = `[int32 length][UTF-8]`, `bytes` = `[int32 length][data]`, `u8` = 1 byte.

Important for your own implementations: with the stock LiteNetLib (NuGet) the connection stalls at the first large message. The project therefore contains LiteNetLib 1.0.1.1 as source code in `shared/LiteNetLib` with `DefaultWindowSize = 512`.

## Handshake and encryption

```
Client                                   Server
  │──── LiteNetLib Connect(key=joinToken) ──►│
  │◄─── ProtocolSettings (plain text) ───────│  RSA-2048 modulus + exponent
  │ generates 2048 random bytes K             │
  │──── AuthWorker (plain text) ────────────►│  identityToken + K in 245-byte blocks,
  │      encrypted from now on                │  each RSA PKCS#1 v1.5 → 9 blocks of 256 bytes
  │◄─── InitWorker (encrypted) ──────────────│  WorkerEntityId
```

Encryption: `payload[i] ^= K[i % 2048]`; the index restarts at 0 for **every** message. The length prefix stays plain text.

## Operations

| Code | Name | Direction | Body |
|---:|---|---|---|
| 1 | ProtocolSettings | S→C | bytes modulus, bytes exponent |
| 2 | AuthWorker | C→S | string identityToken, int32 n, n×bytes keyPart |
| 3 | InitWorker | S→C | int64 workerEntityId |
| 4 | FlagUpdate | S→C | string name, string value |
| 5 | AddEntity | S→C | int64 entityId |
| 6 | RemoveEntity | S→C | int64 entityId |
| 7 | AddComponent | both | int64 entityId, uint32 componentId, bytes data |
| 8 | ComponentUpdate | both | int64 entityId, uint32 componentId, bytes data |
| 9 | ComponentSetAuthorityChange | S→C | int64 entityId, uint32 setId, u8 authority, int32 n, n×(uint32 compId, bytes data) |
| 10 | CreateEntityRequest | C→S | int64 requestId, int64 entityId (0 = server chooses), int32 n, n×(uint32, bytes) |
| 11 | CreateEntityResponse | S→C | int64 requestId, u8 status, string message, int64 entityId |
| 12 | DeleteEntityRequest | C→S | int64 requestId, int64 entityId |
| 13 | DeleteEntityResponse | S→C | int64 requestId, int64 entityId, u8 status, string message |
| 14 | CommandRequest | both | int64 requestId, int64 entityId, int64 callerWorkerEntityId, uint32 componentId, uint32 commandIndex, bytes data |
| 15 | CommandResponse | both | int64 requestId, int64 entityId, u8 status, string message, uint32 componentId, uint32 commandIndex, bytes data |
| 16 | EntityQueryRequest | C→S | int64 requestId, bytes QueryConstraint |
| 17 | EntityQueryResponse | S→C | int64 requestId, u8 status, string message, int32 n, n×Entity |
| 18 | SystemRequest | S→C | int64 requestId, string command, string args |
| 19 | SystemResponse | C→S | int64 requestId, string data |
| 20 / 21 | Ping / Pong | C→S / S→C | int64 requestId (the client pings every 3 s) |
| 90 | ReserveEntityIdsRequest | C→S | int64 requestId, uint32 count |
| 91 | ReserveEntityIdsResponse | S→C | int64 requestId, u8 status, string message, int64 firstId, int32 count |
| 100 | Log | C→S | u8 level (1–4), string logger, string message, int64 entityId |
| 200 | Disconnect | both | u8 code, string reason |

`Entity` = int64 entityId, int32 n, n×(uint32 componentId, bytes data).
Status: 1 Success, 2 Timeout, 3 NotFound, 4 AuthorityLost, 5 PermissionDenied, 6 ApplicationError, 7 InternalError. Authority: 0/1.
The client reports opcodes 22–89 and ≥ 202 as unknown.

## Component data (SchemaObject)

No protobuf, no type tags – not generically readable without the schema:

```
field        [uint16 FieldId][optional: u8 HasValue][value]
int/float    4 bytes, int64/double 8 bytes, bool 1 byte
string       field: [FieldId][u8 HasValue][int32 len][utf8]  (always with HasValue)
object       [FieldId][int32 len][nested SchemaObject]
list         [FieldId][int32 count][elements]; object lists: each [int32 len][bytes]
map<K,V>     object list of {1: key, 2: value}; empty = [FieldId][int32 0]
empty option [FieldId][u8 0]
```

Examples: Position(54) = `{1: object Coordinates {1 double x, 2 double y, 3 double z}}`, Metadata(53) = `{1: string}`, AuthorityDelegation(65) = `{1: map<uint32 setId, int64 workerEntityId>}`.

Reference data from the client:
- `data/component_ids.tsv` – all 174 component IDs with names
- `data/component_sets.txt` – 25 component sets (e.g. `PlayerClientSet = 70001`) with members
- `data/component_schemas.txt` – automatically extracted field IDs/types per component (raw, in serializer order; `?` = not statically resolvable; verify against captures before use)

## Authority

Who is responsible for which components is stored in `AuthorityDelegation` (set ID → worker entity ID). The runtime informs the worker about its authority via `ComponentSetAuthorityChange`, including the current data of the components in the set.
