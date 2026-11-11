# Zenith: The Last City – community server & sniffer

Preservation project for Zenith: The Last City (official server shutdown on December 18, 2026).

```
docs/      client analysis, protocol, HTTP APIs, server addresses, guides
  data/    tables extracted from the client (component IDs, component sets, schemas)
shared/    Zenith.Protocol – protocol library (ops, SchemaObject, encryption, CA)
           LiteNetLib – LiteNetLib 1.0.1.1 source with the window size used by the game (512)
server/    ZenithServer – HTTP APIs + ZenFabric runtime (C#/.NET 8)
sniffer/   ZenithSniffer – capture tool (MITM proxy, passive, decoder, sanitized export)
tools/     ZenithTestClient (protocol test) and il2cpp-analysis (Python analysis scripts)
```

## Quick start

Requirement: .NET 8 SDK.

```bash
dotnet build ZenithEmu.sln -c Release
```

- **Capture now, while the original servers are still running:** [docs/05_Sniffer.md](docs/05_Sniffer.md)
- **Share captures safely:** `ZenithSniffer export <capture-folder>` – see [docs/05_Sniffer.md](docs/05_Sniffer.md#sharing-a-capture-community)
- **Run your own server and point the game at it:** [docs/06_Server.md](docs/06_Server.md), [docs/04_Server_Addresses.md](docs/04_Server_Addresses.md)

## Sniffer release

The GitHub Action [`.github/workflows/sniffer-release.yml`](.github/workflows/sniffer-release.yml) builds a self-contained `ZenithSniffer.exe` (Windows x64, no .NET needed) and publishes `ZenithSniffer-<version>-win-x64.zip` as a GitHub release. The ZIP contains the EXE, `sniffersettings.json`, `docs/05_Sniffer.md` as `README.md` and the LiteNetLib license.

Start it either way:

```bash
git tag sniffer-v1.0.0
git push origin sniffer-v1.0.0
```

or on GitHub: **Actions → Sniffer release → Run workflow** and enter a version (e.g. `1.0.0`). Versions with a suffix (`1.0.0-beta1`) become pre-releases.

## Documentation

1. [Client analysis](docs/01_Client_Analysis.md)
2. [ZenFabric protocol (UDP)](docs/02_ZenFabric_Protocol.md)
3. [HTTP APIs](docs/03_HTTP_APIs.md)
4. [Server addresses](docs/04_Server_Addresses.md)
5. [Sniffer](docs/05_Sniffer.md)
6. [Server](docs/06_Server.md)

## Status

| Part | Status |
|---|---|
| Protocol (transport, handshake, all 28 ops, SchemaObject) | reconstructed, implemented, confirmed against live captures |
| LiteNetLib compatibility | modified window size (512) found and bundled |
| Sniffer | proxy mode works against the live servers; MxO-style text logs, Wireshark pcaps, sanitized export |
| Login/realm/join | stock Windows client verified through HTTPS sign-in and ZenFabric UDP authentication on a loopback test realm |
| PlayFab | basic emulation, the rest as logged stubs |
| Game world / game logic | player setup, clock sync, persistent characters and Neo City spawn for Infinite Realms. World population and gameplay are pending. |

Language: all docs, code comments and console output are in English (only some output inside the Python analysis scripts is still German).
